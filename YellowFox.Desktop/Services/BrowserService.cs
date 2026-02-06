using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using YellowFox.Desktop.Models;
using ModelProxy = YellowFox.Desktop.Models.Proxy;

namespace YellowFox.Desktop.Services;

public class BrowserService
{
    private readonly DatabaseService _databaseService;
    private readonly SettingsService _settingsService;
    private readonly ProxyValidatorService _proxyValidatorService;
    private readonly Dictionary<string, RunningInstance> _runningInstances = new();
    
    public BrowserService(DatabaseService databaseService, SettingsService settingsService, ProxyValidatorService proxyValidatorService)
    {
        _databaseService = databaseService;
        _settingsService = settingsService;
        _proxyValidatorService = proxyValidatorService;
    }
    
    public bool IsRunning(string profileId) => _runningInstances.ContainsKey(profileId);
    
    public async Task<bool> StartProfileAsync(string profileId)
    {
        if (IsRunning(profileId))
            return false; // Already running
        
        var profile = _databaseService.GetProfile(profileId);
        if (profile == null)
            throw new InvalidOperationException($"Profile {profileId} not found");

        var logPath = _databaseService.GetProfileLogFilePath(profileId, profile.Name);
        await WriteLogAsync(logPath, "INFO", $"Start requested for profile '{profile.Name}' ({profile.Id}).");
        
        try
        {
            ModelProxy? proxy = null;
            if (!string.IsNullOrWhiteSpace(profile.ProxyId))
            {
                proxy = _databaseService.GetProxy(profile.ProxyId);
                if (proxy == null)
                    throw new InvalidOperationException("Selected proxy not found.");

                await WriteLogAsync(logPath, "INFO", $"Validating proxy '{proxy.Name}' ({proxy.Type} {proxy.Host}:{proxy.Port}).");
                var validation = await _proxyValidatorService.ValidateAsync(proxy);
                if (!validation.IsSuccess)
                    throw new InvalidOperationException($"Proxy check failed: {validation.Error}");
                await WriteLogAsync(logPath, "INFO", $"Proxy validation passed. IP={validation.ExternalIp ?? "unknown"}, latency={validation.LatencyMs}ms.");
            }

            // Get profile data directory
            var userDataDir = _databaseService.GetProfileDataDirectory(profileId);
            PrepareSharedBookmarks(userDataDir, _databaseService.GetAllBookmarks());
            await WriteLogAsync(logPath, "INFO", $"Prepared profile directory and shared bookmarks: {userDataDir}");

            var enabledExtensions = _databaseService.GetEnabledExtensions()
                .Select(e => e.Path)
                .Where(File.Exists)
                .ToList();
            await WriteLogAsync(logPath, "INFO", $"Enabled extensions attached: {enabledExtensions.Count}");
            
            // Create config JSON with user_data_dir
            var config = new
            {
                os = profile.FingerprintConfig.Os,
                screen = new
                {
                    maxWidth = profile.FingerprintConfig.Screen.MaxWidth,
                    maxHeight = profile.FingerprintConfig.Screen.MaxHeight
                },
                user_data_dir = userDataDir,
                proxy = BuildCamoufoxProxy(proxy),
                addons = enabledExtensions
            };
            
            var configJson = JsonSerializer.Serialize(config);
            
            // Create temp config file
            var tempConfigPath = Path.GetTempFileName();
            await File.WriteAllTextAsync(tempConfigPath, configJson);
            await WriteLogAsync(logPath, "INFO", $"Generated temporary launch config: {tempConfigPath}");
            
            // Get paths from settings
            var pythonDir = ResolvePythonScriptsPath();
            
            // On Windows, use batch file that activates venv
            // On Linux/macOS, use shell script that activates venv
            string fileName;
            string arguments;
            
            if (OperatingSystem.IsWindows())
            {
                // Use batch file that handles venv activation
                var batchFile = Path.Combine(pythonDir, "start-server.bat");
                if (!File.Exists(batchFile))
                {
                    throw new FileNotFoundException($"Python launcher script not found: {batchFile}");
                }

                // Batch files must be launched via cmd.exe when UseShellExecute=false.
                fileName = "cmd.exe";
                arguments = $"/c \"\"{batchFile}\" \"{tempConfigPath}\"\"";
            }
            else
            {
                // Linux/macOS: use shell script that handles venv activation
                var shellScript = Path.Combine(pythonDir, "start-server.sh");
                if (!File.Exists(shellScript))
                {
                    throw new FileNotFoundException($"Python launcher script not found: {shellScript}");
                }
                fileName = "/bin/bash";
                arguments = $"\"{shellScript}\" \"{tempConfigPath}\"";
            }
            
            // Start Python process
            var processStartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            var process = Process.Start(processStartInfo);
            if (process == null)
                throw new InvalidOperationException("Failed to start Python process");
            await WriteLogAsync(logPath, "INFO", $"Python launcher process started. PID={process.Id}");
            
            // Read CDP URL from stdout - read lines until we find the URL
            string? cdpUrl = null;
            var timeout = TimeSpan.FromSeconds(30);
            var startTime = DateTime.Now;
            
            while (cdpUrl == null && (DateTime.Now - startTime) < timeout)
            {
                // Check if process has exited (error case)
                if (process.HasExited)
                {
                    var errorOutput = await process.StandardError.ReadToEndAsync();
                    throw new InvalidOperationException($"Python process exited unexpectedly. Error: {errorOutput}");
                }
                
                var line = await process.StandardOutput.ReadLineAsync();
                if (line == null)
                {
                    // No data available yet, wait a bit and continue
                    await Task.Delay(100);
                    continue;
                }
                
                // Look for CDP URL pattern (contains ws:// or http://)
                if (line.Contains("ws://") || line.Contains("http://"))
                {
                    // Extract URL using regex to handle ANSI color codes
                    // Pattern matches: ws://host:port/path or http://host:port/path
                    var match = Regex.Match(line, @"(wss?://[^\s\[\]]+|https?://[^\s\[\]]+)");
                    if (match.Success)
                    {
                        cdpUrl = match.Groups[1].Value.Trim();
                        break;
                    }
                }
            }
            
            if (string.IsNullOrEmpty(cdpUrl))
            {
                // Read error output to get actual error message
                var errorOutput = await process.StandardError.ReadToEndAsync();
                process.Kill();
                
                var errorMessage = string.IsNullOrWhiteSpace(errorOutput)
                    ? "Failed to get CDP URL from Python process (no error details available)"
                    : $"Failed to get CDP URL from Python process. Error: {errorOutput.Trim()}";
                
                throw new InvalidOperationException(errorMessage);
            }
            await WriteLogAsync(logPath, "INFO", $"Received CDP endpoint: {cdpUrl}");
            
            var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Firefox.ConnectAsync(cdpUrl);
            await WriteLogAsync(logPath, "INFO", "Playwright connected to browser.");
            await EnsureVisibleWindowAsync(browser, logPath);
            
            // Store running instance
            _runningInstances[profileId] = new RunningInstance
            {
                Process = process,
                CdpUrl = cdpUrl,
                TempConfigPath = tempConfigPath,
                Playwright = playwright,
                Browser = browser
            };
            await WriteLogAsync(logPath, "INFO", $"Profile '{profile.Name}' started successfully.");
            
            return true;
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "ERROR", $"Start failed: {ex.Message}");
            Debug.WriteLine($"Error starting profile: {ex.Message}");
            return false;
        }
    }
    
    public async Task<bool> StopProfileAsync(string profileId)
    {
        if (!_runningInstances.TryGetValue(profileId, out var instance))
            return false; // Not running

        var profile = _databaseService.GetProfile(profileId);
        var profileName = profile?.Name ?? profileId;
        var logPath = _databaseService.GetProfileLogFilePath(profileId, profileName);
        await WriteLogAsync(logPath, "INFO", $"Stop requested for profile '{profileName}' ({profileId}).");
        
        try
        {
            // Close browser connection
            if (instance.Browser != null)
            {
                await instance.Browser.CloseAsync();
            }
            
            // Dispose Playwright
            instance.Playwright?.Dispose();
            
            // Kill Python process
            if (instance.Process != null && !instance.Process.HasExited)
            {
                instance.Process.Kill();
                await instance.Process.WaitForExitAsync();
            }
            
            // Clean up temp config file
            if (File.Exists(instance.TempConfigPath))
            {
                File.Delete(instance.TempConfigPath);
            }
            
            _runningInstances.Remove(profileId);
            await WriteLogAsync(logPath, "INFO", $"Profile '{profileName}' stopped.");
            return true;
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "ERROR", $"Stop failed: {ex.Message}");
            Debug.WriteLine($"Error stopping profile: {ex.Message}");
            return false;
        }
    }
    
    public async Task StopAllAsync()
    {
        var profileIds = new List<string>(_runningInstances.Keys);
        foreach (var profileId in profileIds)
        {
            await StopProfileAsync(profileId);
        }
    }

    public async Task<(bool Success, string Message)> ExportCookiesAsync(string profileId, string filePath)
    {
        var profile = _databaseService.GetProfile(profileId);
        var profileName = profile?.Name ?? profileId;
        var logPath = _databaseService.GetProfileLogFilePath(profileId, profileName);
        await WriteLogAsync(logPath, "INFO", $"Cookie export requested to '{filePath}'.");

        var startedTemporarily = false;
        if (!_runningInstances.ContainsKey(profileId))
        {
            var startSuccess = await StartProfileAsync(profileId);
            if (!startSuccess)
                return (false, "Failed to start profile for cookie export.");
            startedTemporarily = true;
        }

        try
        {
            if (!_runningInstances.TryGetValue(profileId, out var instance) || instance.Browser == null)
                return (false, "No running browser instance found.");

            var context = instance.Browser.Contexts.FirstOrDefault();
            if (context == null)
                return (false, "No browser context found.");

            var cookies = await context.CookiesAsync();
            var json = JsonSerializer.Serialize(cookies, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(filePath, json);
            await WriteLogAsync(logPath, "INFO", $"Cookie export completed. Count={cookies.Count}");
            return (true, $"Exported {cookies.Count} cookies.");
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "ERROR", $"Cookie export failed: {ex.Message}");
            return (false, $"Export failed: {ex.Message}");
        }
        finally
        {
            if (startedTemporarily)
            {
                await StopProfileAsync(profileId);
            }
        }
    }

    public async Task<(bool Success, string Message)> ImportCookiesAsync(string profileId, string filePath)
    {
        var profile = _databaseService.GetProfile(profileId);
        var profileName = profile?.Name ?? profileId;
        var logPath = _databaseService.GetProfileLogFilePath(profileId, profileName);
        await WriteLogAsync(logPath, "INFO", $"Cookie import requested from '{filePath}'.");

        if (!File.Exists(filePath))
            return (false, "Cookie file not found.");

        var startedTemporarily = false;
        if (!_runningInstances.ContainsKey(profileId))
        {
            var startSuccess = await StartProfileAsync(profileId);
            if (!startSuccess)
                return (false, "Failed to start profile for cookie import.");
            startedTemporarily = true;
        }

        try
        {
            if (!_runningInstances.TryGetValue(profileId, out var instance) || instance.Browser == null)
                return (false, "No running browser instance found.");

            var context = instance.Browser.Contexts.FirstOrDefault();
            if (context == null)
                return (false, "No browser context found.");

            var json = await File.ReadAllTextAsync(filePath);
            var cookies = JsonSerializer.Deserialize<List<Cookie>>(json) ?? new List<Cookie>();
            if (cookies.Count == 0)
                return (false, "No cookies found in file.");

            await context.AddCookiesAsync(cookies);
            await WriteLogAsync(logPath, "INFO", $"Cookie import completed. Count={cookies.Count}");
            return (true, $"Imported {cookies.Count} cookies.");
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "ERROR", $"Cookie import failed: {ex.Message}");
            return (false, $"Import failed: {ex.Message}");
        }
        finally
        {
            if (startedTemporarily)
            {
                await StopProfileAsync(profileId);
            }
        }
    }

    public string GetOrCreateProfileLogPath(string profileId)
    {
        var profile = _databaseService.GetProfile(profileId);
        var profileName = profile?.Name ?? profileId;
        var logPath = _databaseService.GetProfileLogFilePath(profileId, profileName);

        if (!File.Exists(logPath))
        {
            File.WriteAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | INFO  | Log created for profile '{profileName}' ({profileId}).{Environment.NewLine}");
        }
        
        return logPath;
    }
    
    private class RunningInstance
    {
        public Process? Process { get; set; }
        public string CdpUrl { get; set; } = string.Empty;
        public string TempConfigPath { get; set; } = string.Empty;
        public IPlaywright? Playwright { get; set; }
        public IBrowser? Browser { get; set; }
    }

    private static void PrepareSharedBookmarks(string profileDir, IReadOnlyCollection<BookmarkItem> bookmarks)
    {
        var bookmarksFilePath = Path.Combine(profileDir, "bookmarks.html");
        var bookmarksHtml = BuildBookmarksHtml(bookmarks);
        File.WriteAllText(bookmarksFilePath, bookmarksHtml);

        var userJsPath = Path.Combine(profileDir, "user.js");
        var userJsLines = File.Exists(userJsPath)
            ? File.ReadAllLines(userJsPath).ToList()
            : new List<string>();

        EnsureUserPref(userJsLines, "browser.places.importBookmarksHTML", "true");
        EnsureUserPref(userJsLines, "browser.bookmarks.restore_default_bookmarks", "false");

        File.WriteAllLines(userJsPath, userJsLines);
    }

    private static string BuildBookmarksHtml(IReadOnlyCollection<BookmarkItem> bookmarks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
        sb.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
        sb.AppendLine("<TITLE>Bookmarks</TITLE>");
        sb.AppendLine("<H1>Bookmarks</H1>");
        sb.AppendLine("<DL><p>");

        foreach (var group in bookmarks.GroupBy(b => string.IsNullOrWhiteSpace(b.Folder) ? null : b.Folder))
        {
            if (!string.IsNullOrWhiteSpace(group.Key))
            {
                var folder = System.Net.WebUtility.HtmlEncode(group.Key);
                sb.AppendLine($"    <DT><H3>{folder}</H3>");
                sb.AppendLine("    <DL><p>");
                foreach (var bookmark in group)
                {
                    AppendBookmark(sb, bookmark, 8);
                }
                sb.AppendLine("    </DL><p>");
            }
            else
            {
                foreach (var bookmark in group)
                {
                    AppendBookmark(sb, bookmark, 4);
                }
            }
        }

        sb.AppendLine("</DL><p>");
        return sb.ToString();
    }

    private static void AppendBookmark(StringBuilder sb, BookmarkItem bookmark, int indent)
    {
        var spaces = new string(' ', indent);
        var title = System.Net.WebUtility.HtmlEncode(bookmark.Title);
        var url = System.Net.WebUtility.HtmlEncode(bookmark.Url);
        sb.AppendLine($"{spaces}<DT><A HREF=\"{url}\">{title}</A>");
    }

    private static void EnsureUserPref(List<string> lines, string key, string value)
    {
        var prefPrefix = $"user_pref(\"{key}\"";
        if (lines.Any(l => l.Contains(prefPrefix, StringComparison.Ordinal)))
            return;

        lines.Add($"user_pref(\"{key}\", {value});");
    }

    private static async Task WriteLogAsync(string logPath, string level, string message)
    {
        try
        {
            var logDir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrWhiteSpace(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {level,-5} | {message}{Environment.NewLine}";
            await File.AppendAllTextAsync(logPath, line);
        }
        catch
        {
            // Logging must never break runtime flow.
        }
    }

    private static Dictionary<string, object>? BuildCamoufoxProxy(ModelProxy? proxy)
    {
        if (proxy == null)
            return null;

        var server = $"{NormalizeProxyScheme(proxy.Type)}://{proxy.Host}:{proxy.Port}";
        var result = new Dictionary<string, object>
        {
            ["server"] = server
        };

        if (!string.IsNullOrWhiteSpace(proxy.Username))
            result["username"] = proxy.Username;

        if (!string.IsNullOrWhiteSpace(proxy.Password))
            result["password"] = proxy.Password;

        return result;
    }

    private static async Task EnsureVisibleWindowAsync(IBrowser browser, string logPath)
    {
        try
        {
            var context = browser.Contexts.FirstOrDefault();
            if (context == null)
            {
                context = await browser.NewContextAsync();
                await WriteLogAsync(logPath, "INFO", "No browser context found. Created a new context.");
            }

            var page = context.Pages.FirstOrDefault();
            if (page == null)
            {
                page = await context.NewPageAsync();
                await page.GotoAsync("about:blank");
                await WriteLogAsync(logPath, "INFO", "Created initial tab for profile window.");
            }

            await page.BringToFrontAsync();
            await WriteLogAsync(logPath, "INFO", "Browser window brought to front.");
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "WARN", $"Could not force browser window visibility: {ex.Message}");
        }
    }

    private static string NormalizeProxyScheme(string? proxyType)
    {
        if (string.IsNullOrWhiteSpace(proxyType))
            return "http";

        var type = proxyType.Trim().ToLowerInvariant();
        return type switch
        {
            "socks5" => "socks5",
            "socks4" => "socks4",
            _ => "http"
        };
    }

    private string ResolvePythonScriptsPath()
    {
        var configured = _settingsService.GetPythonScriptsPath();
        if (Directory.Exists(configured))
            return configured;

        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appDir, "python"),
            Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "python")),
            Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "python")),
            Path.Combine(Directory.GetCurrentDirectory(), "python")
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        return configured;
    }
}
