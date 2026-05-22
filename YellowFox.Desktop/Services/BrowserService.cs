using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using YellowFox.Desktop.Models;
using ModelProxy = YellowFox.Desktop.Models.Proxy;

namespace YellowFox.Desktop.Services;

public class BrowserService
{
    private static readonly TimeSpan TabsSnapshotInterval = TimeSpan.FromSeconds(2);

    private readonly DatabaseService _databaseService;
    private readonly SettingsService _settingsService;
    private readonly ProxyValidatorService _proxyValidatorService;
    private readonly Dictionary<string, RunningInstance> _runningInstances = new();
    private readonly object _runningInstancesLock = new();

    public event EventHandler<ProfileRunningStateChangedEventArgs>? ProfileRunningStateChanged;

    public BrowserService(DatabaseService databaseService, SettingsService settingsService, ProxyValidatorService proxyValidatorService)
    {
        _databaseService = databaseService;
        _settingsService = settingsService;
        _proxyValidatorService = proxyValidatorService;
    }

    public bool IsRunning(string profileId)
    {
        lock (_runningInstancesLock)
        {
            return _runningInstances.ContainsKey(profileId);
        }
    }

    public string? GetEndpoint(string profileId)
    {
        lock (_runningInstancesLock)
        {
            return _runningInstances.TryGetValue(profileId, out var instance)
                ? instance.CdpUrl
                : null;
        }
    }

    public async Task<bool> StartProfileAsync(string profileId)
    {
        if (IsRunning(profileId))
            return false;

        var profile = _databaseService.GetProfile(profileId);
        if (profile == null)
            throw new InvalidOperationException($"Profile {profileId} not found");

        var logPath = _databaseService.GetProfileLogFilePath(profileId, profile.Name);
        await WriteLogAsync(logPath, "INFO", $"Start requested for profile '{profile.Name}' ({profile.Id}).");

        Process? proxyBridgeProcess = null;
        string? proxyBridgeConfigPath = null;

        try
        {
            ModelProxy? proxy = null;
            ModelProxy? browserProxy = null;
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
                browserProxy = proxy;
                if (IsAuthenticatedSocks5(proxy))
                {
                    var bridge = await StartSocks5AuthBridgeAsync(proxy, logPath);
                    proxyBridgeProcess = bridge.Process;
                    proxyBridgeConfigPath = bridge.ConfigPath;
                    browserProxy = bridge.BrowserProxy;
                }
            }

            var userDataDir = _databaseService.GetProfileDataDirectory(profileId);
            var sharedBookmarks = _databaseService.GetAllBookmarks();
            var sharedBookmarksExtensionPath = PrepareSharedBookmarks(userDataDir, sharedBookmarks);
            await WriteLogAsync(logPath, "INFO", $"Prepared profile directory and shared bookmarks: {userDataDir}");

            var enabledExtensions = _databaseService.GetEnabledExtensions()
                .Select(e => e.Path)
                .Where(IsExtensionPathUsable)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (IsExtensionPathUsable(sharedBookmarksExtensionPath))
                enabledExtensions.Add(sharedBookmarksExtensionPath);
            await WriteLogAsync(logPath, "INFO", $"Enabled extensions attached: {enabledExtensions.Count}");
            var contextFingerprint = await GenerateCamoufoxContextFingerprintAsync(profile, browserProxy, logPath);

            var config = new
            {
                os = profile.FingerprintConfig.Os,
                screen = new
                {
                    maxWidth = profile.FingerprintConfig.Screen.MaxWidth,
                    maxHeight = profile.FingerprintConfig.Screen.MaxHeight
                },
                user_data_dir = userDataDir,
                proxy = BuildCamoufoxProxy(browserProxy),
                geoip = contextFingerprint.UseCamoufoxGeoIp ? contextFingerprint.GeoIp : null,
                camoufox_config = contextFingerprint.CamoufoxConfig,
                addons = enabledExtensions,
                bookmarks = sharedBookmarks.Select(b => new
                {
                    title = b.Title,
                    url = b.Url,
                    folder = string.IsNullOrWhiteSpace(b.Folder) ? null : b.Folder
                }).ToList()
            };

            var configJson = JsonSerializer.Serialize(config);
            var tempConfigPath = Path.GetTempFileName();
            await File.WriteAllTextAsync(tempConfigPath, configJson);
            await WriteLogAsync(logPath, "INFO", $"Generated temporary launch config: {tempConfigPath}");

            var pythonDir = ResolvePythonScriptsPath();

            var launcher = ResolvePythonLauncher(pythonDir);
            var serverScript = Path.Combine(pythonDir, "camoufox-server.py");
            if (!File.Exists(serverScript))
                throw new FileNotFoundException($"Camoufox server script not found: {serverScript}");

            var fileName = launcher;
            var arguments = $"\"{serverScript}\" \"{tempConfigPath}\"";
            var existingCamoufoxPids = GetCamoufoxProcessIds();

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

            string? cdpUrl = null;
            var timeout = TimeSpan.FromSeconds(75);
            var startTime = DateTime.Now;
            Task<string?>? stdoutReadTask = null;

            while (cdpUrl == null && (DateTime.Now - startTime) < timeout)
            {
                if (process.HasExited)
                {
                    var errorOutput = await process.StandardError.ReadToEndAsync();
                    throw new InvalidOperationException($"Python process exited unexpectedly. Error: {errorOutput}");
                }

                stdoutReadTask ??= process.StandardOutput.ReadLineAsync();
                var remaining = timeout - (DateTime.Now - startTime);
                if (remaining <= TimeSpan.Zero)
                    break;

                var wait = remaining < TimeSpan.FromMilliseconds(250)
                    ? remaining
                    : TimeSpan.FromMilliseconds(250);
                var completed = await Task.WhenAny(stdoutReadTask, Task.Delay(wait));
                if (completed != stdoutReadTask)
                    continue;

                var line = await stdoutReadTask;
                stdoutReadTask = null;
                if (line == null)
                {
                    await Task.Delay(100);
                    continue;
                }

                if (line.Contains("ws://") || line.Contains("http://"))
                {
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
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                    catch
                    {
                        // The process may exit between timeout detection and kill.
                    }
                }

                var errorOutput = await process.StandardError.ReadToEndAsync();

                var errorMessage = string.IsNullOrWhiteSpace(errorOutput)
                    ? "Failed to get CDP URL from Python process (no error details available)"
                    : $"Failed to get CDP URL from Python process. Error: {errorOutput.Trim()}";

                throw new InvalidOperationException(errorMessage);
            }
            await WriteLogAsync(logPath, "INFO", $"Received CDP endpoint: {cdpUrl}");

            var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Firefox.ConnectAsync(cdpUrl);
            await WriteLogAsync(logPath, "INFO", "Playwright connected to browser.");
            browser.Disconnected += (_, _) => _ = HandleBrowserDisconnectedAsync(profileId);
            var browserProcessIds = GetCamoufoxProcessIds()
                .Except(existingCamoufoxPids)
                .ToList();
            await WriteLogAsync(logPath, "INFO", $"Tracked Camoufox process count: {browserProcessIds.Count}.");

            var instance = new RunningInstance
            {
                Process = process,
                CdpUrl = cdpUrl,
                TempConfigPath = tempConfigPath,
                Playwright = playwright,
                Browser = browser,
                SnapshotCts = new CancellationTokenSource(),
                BrowserProcessIds = browserProcessIds,
                ProxyBridgeProcess = proxyBridgeProcess,
                ProxyBridgeConfigPath = proxyBridgeConfigPath,
                ContextOptions = contextFingerprint.ContextOptions,
                ContextInitScript = contextFingerprint.InitScript,
                ContextOptionsPath = contextFingerprint.OptionsPath,
                ContextInitScriptPath = contextFingerprint.InitScriptPath,
                IsPersistentServer = true
            };

            await EnsureVisibleWindowAsync(instance, logPath);

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _ = HandleProcessExitedAsync(profileId);

            lock (_runningInstancesLock)
            {
                _runningInstances[profileId] = instance;
            }

            _ = RunTabsSnapshotLoopAsync(profileId, instance, logPath);
            await ImportStoredCookiesAsync(profileId, instance, logPath);
            await AddStoredLocalStorageInitScriptAsync(profileId, instance, logPath);
            await RestoreTabsAsync(profileId, instance, logPath);

            await WriteLogAsync(logPath, "INFO", $"Profile '{profile.Name}' started successfully.");
            NotifyProfileRunningStateChanged(profileId, true);
            return true;
        }
        catch (Exception ex)
        {
            if (proxyBridgeProcess != null)
                await StopProxyBridgeAsync(proxyBridgeProcess, proxyBridgeConfigPath, logPath);
            await WriteLogAsync(logPath, "ERROR", $"Start failed: {ex.Message}");
            Debug.WriteLine($"Error starting profile: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> StopProfileAsync(string profileId)
    {
        if (!TryGetRunningInstance(profileId, out var instance) || instance == null)
            return false;

        var profile = _databaseService.GetProfile(profileId);
        var profileName = profile?.Name ?? profileId;
        var logPath = _databaseService.GetProfileLogFilePath(profileId, profileName);
        await WriteLogAsync(logPath, "INFO", $"Stop requested for profile '{profileName}' ({profileId}).");

        try
        {
            instance.IsStopping = true;
            instance.SnapshotCts?.Cancel();
            await PersistTabsSnapshotAsync(profileId, instance, logPath, writeInfoLog: true);
            await RequestGracefulBrowserShutdownAsync(instance, logPath);
            instance.Playwright?.Dispose();

            if (instance.Process != null)
            {
                var exitedGracefully = await WaitForProcessExitAsync(instance.Process, TimeSpan.FromSeconds(8));
                if (!exitedGracefully)
                {
                    await WriteLogAsync(logPath, "WARN", "Graceful shutdown timeout. Force killing process tree.");
                    await KillProcessTreeAsync(instance.Process.Id, logPath);
                }
            }
            await KillTrackedBrowserProcessesAsync(instance.BrowserProcessIds, logPath);
            await StopProxyBridgeAsync(instance.ProxyBridgeProcess, instance.ProxyBridgeConfigPath, logPath);

            if (File.Exists(instance.TempConfigPath))
                File.Delete(instance.TempConfigPath);

            lock (_runningInstancesLock)
            {
                _runningInstances.Remove(profileId);
            }

            await WriteLogAsync(logPath, "INFO", $"Profile '{profileName}' stopped.");
            NotifyProfileRunningStateChanged(profileId, false);
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
        List<string> profileIds;
        lock (_runningInstancesLock)
        {
            profileIds = new List<string>(_runningInstances.Keys);
        }

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
        if (!IsRunning(profileId))
        {
            var startSuccess = await StartProfileAsync(profileId);
            if (!startSuccess)
                return (false, "Failed to start profile for cookie export.");
            startedTemporarily = true;
        }

        try
        {
            if (!TryGetRunningInstance(profileId, out var instance) || instance?.Browser == null)
                return (false, "No running browser instance found.");

            var context = await GetOrCreateContextAsync(instance, logPath);
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
                await StopProfileAsync(profileId);
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
        if (!IsRunning(profileId))
        {
            var startSuccess = await StartProfileAsync(profileId);
            if (!startSuccess)
                return (false, "Failed to start profile for cookie import.");
            startedTemporarily = true;
        }

        try
        {
            if (!TryGetRunningInstance(profileId, out var instance) || instance?.Browser == null)
                return (false, "No running browser instance found.");

            var context = await GetOrCreateContextAsync(instance, logPath);
            if (context == null)
                return (false, "No browser context found.");

            var json = await File.ReadAllTextAsync(filePath);
            var cookies = ParseCookiesForImport(json);
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
                await StopProfileAsync(profileId);
        }
    }

    public async Task<(bool Success, string? Url, string? Title, string Message)> OpenUrlAsync(string profileId, string rawUrl)
    {
        var profile = _databaseService.GetProfile(profileId);
        if (profile == null)
            return (false, null, null, "Profile not found.");

        var logPath = _databaseService.GetProfileLogFilePath(profileId, profile.Name);
        if (!TryNormalizeUrl(rawUrl, out var url, out var error))
            return (false, null, null, error);

        if (!IsRunning(profileId))
        {
            var startSuccess = await StartProfileAsync(profileId);
            if (!startSuccess)
                return (false, null, null, "Failed to start profile.");
        }

        try
        {
            if (!TryGetRunningInstance(profileId, out var instance) || instance?.Browser == null)
                return (false, null, null, "No running browser instance found.");

            var context = await GetOrCreateContextAsync(instance, logPath);
            if (context == null)
                return (false, null, null, "No browser context found.");

            var page = context.Pages.FirstOrDefault(p =>
                string.Equals(p.Url, "about:blank", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(p.Url))
                ?? await context.NewPageAsync();
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30000
            });

            await page.BringToFrontAsync();
            var title = await page.TitleAsync();
            await WriteLogAsync(logPath, "INFO", $"Opened URL from agent CLI: {page.Url}");
            return (true, page.Url, title, "URL opened.");
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "ERROR", $"Open URL failed: {ex.Message}");
            return (false, url, null, $"Open URL failed: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<OpenPageSnapshot>> GetOpenPagesAsync(string profileId, bool includeText)
    {
        if (!TryGetRunningInstance(profileId, out var instance) || instance?.Browser == null)
            return Array.Empty<OpenPageSnapshot>();

        var profile = _databaseService.GetProfile(profileId);
        var profileName = profile?.Name ?? profileId;
        var logPath = _databaseService.GetProfileLogFilePath(profileId, profileName);
        var context = await GetOrCreateContextAsync(instance, logPath);
        if (context == null)
            return Array.Empty<OpenPageSnapshot>();

        var pages = new List<OpenPageSnapshot>();
        foreach (var page in context.Pages)
        {
            string? title = null;
            string? text = null;
            try
            {
                title = await page.TitleAsync();
                if (includeText)
                    text = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 3000 });
            }
            catch (Exception ex)
            {
                text = $"<page inspection failed: {ex.Message}>";
            }

            pages.Add(new OpenPageSnapshot
            {
                Url = page.Url,
                Title = title,
                Text = text
            });
        }

        return pages;
    }

    public async Task<(bool Success, string Message, string? Url, string? Title)> ClickTextAsync(string profileId, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (false, "Text is required.", null, null);

        if (!TryGetRunningInstance(profileId, out var instance) || instance?.Browser == null)
            return (false, "No running browser instance found.", null, null);

        var profile = _databaseService.GetProfile(profileId);
        var profileName = profile?.Name ?? profileId;
        var logPath = _databaseService.GetProfileLogFilePath(profileId, profileName);
        var context = await GetOrCreateContextAsync(instance, logPath);
        var page = context?.Pages.LastOrDefault();
        if (page == null)
            return (false, "No open page found.", null, null);

        try
        {
            var clicked = await page.EvaluateAsync<bool>(
                """
                (needle) => {
                    const lowered = String(needle).toLowerCase();
                    const candidates = Array.from(document.querySelectorAll('button,a,[role="button"],input[type="button"],input[type="submit"]'));
                    const element = candidates.find((node) => {
                        const text = (node.innerText || node.textContent || node.value || '').toLowerCase();
                        return text.includes(lowered);
                    });
                    if (!element) {
                        return false;
                    }
                    element.click();
                    return true;
                }
                """,
                text.Trim());

            if (!clicked)
                return (false, $"Clickable text not found: {text}", page.Url, await page.TitleAsync());

            await page.WaitForTimeoutAsync(15000);
            await WriteLogAsync(logPath, "INFO", $"Clicked text from agent CLI: {text}");
            return (true, "Clicked.", page.Url, await page.TitleAsync());
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "ERROR", $"Click text failed: {ex.Message}");
            return (false, $"Click text failed: {ex.Message}", page.Url, null);
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

    public async Task<string> CreateAgentStorageStateFileAsync(string profileId)
    {
        var profileDir = _databaseService.GetProfileDataDirectory(profileId);
        Directory.CreateDirectory(profileDir);

        var cookies = new List<object>();
        var cookiesPath = _databaseService.GetProfileImportedCookiesFilePath(profileId);
        if (File.Exists(cookiesPath))
        {
            foreach (var cookie in ParseCookiesForImport(await File.ReadAllTextAsync(cookiesPath)))
            {
                cookies.Add(new
                {
                    name = cookie.Name,
                    value = cookie.Value,
                    domain = cookie.Domain,
                    path = cookie.Path,
                    expires = cookie.Expires,
                    httpOnly = cookie.HttpOnly,
                    secure = cookie.Secure,
                    sameSite = cookie.SameSite?.ToString()
                });
            }
        }

        var origins = new List<object>();
        var localStoragePath = _databaseService.GetProfileImportedLocalStorageFilePath(profileId);
        if (File.Exists(localStoragePath))
        {
            foreach (var origin in ParseLocalStorageForImport(await File.ReadAllTextAsync(localStoragePath)))
            {
                origins.Add(new
                {
                    origin = origin.Key,
                    localStorage = origin.Value.Select(item => new
                    {
                        name = item.Key,
                        value = item.Value
                    }).ToList()
                });
            }
        }

        var state = new
        {
            cookies,
            origins
        };

        var path = Path.Combine(profileDir, "agent-storage-state.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        }));
        return path;
    }

    public (string? OptionsPath, string? InitScriptPath) GetAgentContextFingerprintFiles(string profileId)
    {
        if (!TryGetRunningInstance(profileId, out var instance) || instance == null)
            return (null, null);

        return (instance.ContextOptionsPath, instance.ContextInitScriptPath);
    }

    private sealed class RunningInstance
    {
        public Process? Process { get; set; }
        public string CdpUrl { get; set; } = string.Empty;
        public string TempConfigPath { get; set; } = string.Empty;
        public IPlaywright? Playwright { get; set; }
        public IBrowser? Browser { get; set; }
        public CancellationTokenSource? SnapshotCts { get; set; }
        public bool IsStopping { get; set; }
        public List<int> BrowserProcessIds { get; set; } = new();
        public Process? ProxyBridgeProcess { get; set; }
        public string? ProxyBridgeConfigPath { get; set; }
        public BrowserNewContextOptions? ContextOptions { get; set; }
        public string? ContextInitScript { get; set; }
        public string? ContextOptionsPath { get; set; }
        public string? ContextInitScriptPath { get; set; }
        public bool ContextInitScriptApplied { get; set; }
        public bool IsPersistentServer { get; set; }
    }

    private sealed class CamoufoxContextFingerprint
    {
        public BrowserNewContextOptions ContextOptions { get; set; } = new();
        public string InitScript { get; set; } = string.Empty;
        public string OptionsPath { get; set; } = string.Empty;
        public string InitScriptPath { get; set; } = string.Empty;
        public string? GeoIp { get; set; }
        public bool UseCamoufoxGeoIp { get; set; } = true;
        public Dictionary<string, object?>? CamoufoxConfig { get; set; }
    }

    private sealed class ProxyBridgeLaunch
    {
        public Process Process { get; set; } = null!;
        public string ConfigPath { get; set; } = string.Empty;
        public ModelProxy BrowserProxy { get; set; } = null!;
    }

    public sealed class OpenPageSnapshot
    {
        public string Url { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? Text { get; set; }
    }

    private sealed class TabsSnapshot
    {
        public DateTime UpdatedAtUtc { get; set; }
        public List<string> Urls { get; set; } = new();
    }

    public void SaveImportedCookies(string profileId, IReadOnlyCollection<Cookie> cookies)
    {
        var path = _databaseService.GetProfileImportedCookiesFilePath(profileId);
        var json = JsonSerializer.Serialize(cookies, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    internal static string PrepareSharedBookmarks(string profileDir, IReadOnlyCollection<BookmarkItem> bookmarks)
    {
        Directory.CreateDirectory(profileDir);

        var bookmarksFilePath = Path.Combine(profileDir, "bookmarks.html");
        var bookmarksHtml = BuildBookmarksHtml(bookmarks);
        var bookmarksVersionPath = Path.Combine(profileDir, ".yellowfox-bookmarks.version");
        var currentVersion = ComputeBookmarksVersion(bookmarksHtml);
        var previousVersion = File.Exists(bookmarksVersionPath)
            ? File.ReadAllText(bookmarksVersionPath).Trim()
            : string.Empty;

        if (!string.Equals(previousVersion, currentVersion, StringComparison.Ordinal))
            DeletePlacesStores(profileDir);

        File.WriteAllText(bookmarksFilePath, bookmarksHtml);
        File.WriteAllText(bookmarksVersionPath, currentVersion);

        var userJsPath = Path.Combine(profileDir, "user.js");
        var userJsLines = File.Exists(userJsPath)
            ? File.ReadAllLines(userJsPath).ToList()
            : new List<string>();

        EnsureUserPref(userJsLines, "browser.places.importBookmarksHTML", "true");
        EnsureUserPref(userJsLines, "browser.bookmarks.restore_default_bookmarks", "false");
        EnsureUserPref(userJsLines, "browser.toolbars.bookmarks.visibility", "\"always\"");
        EnsureUserPref(userJsLines, "browser.toolbars.bookmarks.showOtherBookmarks", "false");
        EnsureUserPref(userJsLines, "browser.toolbars.bookmarks.showInPrivateBrowsing", "true");
        EnsureUserPref(userJsLines, "browser.policies.runOncePerModification.displayBookmarksToolbar", "\"always\"");
        EnsureUserPref(userJsLines, "toolkit.legacyUserProfileCustomizations.stylesheets", "true");
        EnsureUserPref(userJsLines, "browser.bookmarks.addedImportButton", "true");
        EnsureUserPref(userJsLines, "browser.startup.page", "0");
        EnsureUserPref(userJsLines, "browser.sessionstore.resume_from_crash", "false");
        EnsureUserPref(userJsLines, "browser.sessionstore.max_tabs_undo", "0");
        EnsureUserPref(userJsLines, "browser.sessionstore.max_windows_undo", "0");
        EnsureUserPref(userJsLines, "browser.uiCustomization.state", JsonSerializer.Serialize(BuildToolbarCustomizationState()));

        File.WriteAllLines(userJsPath, userJsLines);
        WriteToolbarState(profileDir);
        WriteUserChrome(profileDir);
        DeleteSessionStores(profileDir);
        return WriteSharedBookmarksExtension(profileDir, bookmarks);
    }

    internal static bool IsExtensionPathUsable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var trimmed = path.Trim();
        return Directory.Exists(trimmed) && File.Exists(Path.Combine(trimmed, "manifest.json"));
    }

    private static string BuildBookmarksHtml(IReadOnlyCollection<BookmarkItem> bookmarks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
        sb.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
        sb.AppendLine("<TITLE>Bookmarks</TITLE>");
        sb.AppendLine("<H1>Bookmarks</H1>");
        sb.AppendLine("<DL><p>");
        sb.AppendLine("    <DT><H3 PERSONAL_TOOLBAR_FOLDER=\"true\">Bookmarks Toolbar</H3>");
        sb.AppendLine("    <DL><p>");
        sb.AppendLine("        <DT><H3>yellowfox shared</H3>");
        sb.AppendLine("        <DL><p>");

        foreach (var group in bookmarks.GroupBy(b => string.IsNullOrWhiteSpace(b.Folder) ? null : b.Folder))
        {
            if (!string.IsNullOrWhiteSpace(group.Key))
            {
                var folder = System.Net.WebUtility.HtmlEncode(group.Key);
                sb.AppendLine($"            <DT><H3>{folder}</H3>");
                sb.AppendLine("            <DL><p>");
                foreach (var bookmark in group)
                {
                    AppendBookmark(sb, bookmark, 16);
                }
                sb.AppendLine("            </DL><p>");
            }
            else
            {
                foreach (var bookmark in group)
                {
                    AppendBookmark(sb, bookmark, 12);
                }
            }
        }

        sb.AppendLine("        </DL><p>");
        sb.AppendLine("    </DL><p>");
        sb.AppendLine("</DL><p>");
        return sb.ToString();
    }

    private static string WriteSharedBookmarksExtension(string profileDir, IReadOnlyCollection<BookmarkItem> bookmarks)
    {
        var extensionDir = Path.Combine(profileDir, "yellowfox-shared-bookmarks-extension");
        Directory.CreateDirectory(extensionDir);

        var manifestJson = """
        {
          "manifest_version": 2,
          "name": "YellowFox Shared Bookmarks",
          "version": "1.0.0",
          "applications": {
            "gecko": {
              "id": "yellowfox-shared-bookmarks@yellowfox.local"
            }
          },
          "permissions": ["bookmarks"],
          "background": {
            "scripts": ["background.js"]
          }
        }
        """;
        File.WriteAllText(Path.Combine(extensionDir, "manifest.json"), manifestJson);

        var payload = new
        {
            rootTitle = "yellowfox shared",
            bookmarks = bookmarks.Select(b => new
            {
                title = b.Title,
                url = b.Url,
                folder = string.IsNullOrWhiteSpace(b.Folder) ? null : b.Folder
            }).ToList()
        };
        var payloadJson = JsonSerializer.Serialize(payload);
        var backgroundJs = $$"""
        (() => {
          const payload = {{payloadJson}};

          async function findToolbarRoot() {
            const tree = await browser.bookmarks.getTree();
            const stack = [...tree];
            while (stack.length > 0) {
              const node = stack.shift();
              if (node.id === "toolbar_____" || node.title === "Bookmarks Toolbar") {
                return node;
              }
              if (node.children) {
                stack.push(...node.children);
              }
            }
            return tree[0];
          }

          async function getChildren(parentId) {
            try {
              return await browser.bookmarks.getChildren(parentId);
            } catch {
              return [];
            }
          }

          async function removeExistingFolder(parentId, title) {
            const children = await getChildren(parentId);
            for (const child of children) {
              if (child.title === title && !child.url) {
                await browser.bookmarks.removeTree(child.id);
              }
            }
          }

          async function run() {
            const toolbar = await findToolbarRoot();
            await removeExistingFolder(toolbar.id, payload.rootTitle);
            const root = await browser.bookmarks.create({ parentId: toolbar.id, title: payload.rootTitle });
            const folders = new Map();

            for (const item of payload.bookmarks) {
              let parentId = root.id;
              if (item.folder) {
                if (!folders.has(item.folder)) {
                  const folder = await browser.bookmarks.create({ parentId: root.id, title: item.folder });
                  folders.set(item.folder, folder.id);
                }
                parentId = folders.get(item.folder);
              }
              await browser.bookmarks.create({ parentId, title: item.title, url: item.url });
            }
          }

          run().catch(console.error);
        })();
        """;
        File.WriteAllText(Path.Combine(extensionDir, "background.js"), backgroundJs);

        return extensionDir;
    }

    private static void AppendBookmark(StringBuilder sb, BookmarkItem bookmark, int indent)
    {
        var spaces = new string(' ', indent);
        var title = System.Net.WebUtility.HtmlEncode(bookmark.Title);
        var url = System.Net.WebUtility.HtmlEncode(bookmark.Url);
        sb.AppendLine($"{spaces}<DT><A HREF=\"{url}\">{title}</A>");
    }

    private static string ComputeBookmarksVersion(string bookmarksHtml)
    {
        var bytes = Encoding.UTF8.GetBytes(bookmarksHtml);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void DeletePlacesStores(string profileDir)
    {
        var files = new[]
        {
            "places.sqlite",
            "places.sqlite-wal",
            "places.sqlite-shm",
            "favicons.sqlite",
            "favicons.sqlite-wal",
            "favicons.sqlite-shm"
        };

        foreach (var file in files)
        {
            var path = Path.Combine(profileDir, file);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void DeleteSessionStores(string profileDir)
    {
        var files = new[]
        {
            "sessionstore.jsonlz4",
            "sessionstore.js",
            "sessionstore.bak",
            "sessionCheckpoints.json",
            "tabs-state.json"
        };

        foreach (var file in files)
        {
            var path = Path.Combine(profileDir, file);
            if (File.Exists(path))
                File.Delete(path);
        }

        var backupsDir = Path.Combine(profileDir, "sessionstore-backups");
        if (Directory.Exists(backupsDir))
            Directory.Delete(backupsDir, recursive: true);
    }

    private static void EnsureUserPref(List<string> lines, string key, string value)
    {
        var prefPrefix = $"user_pref(\"{key}\"";
        var newLine = $"user_pref(\"{key}\", {value});";
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Contains(prefPrefix, StringComparison.Ordinal))
                continue;

            lines[i] = newLine;
            return;
        }

        lines.Add(newLine);
    }

    private static void WriteToolbarState(string profileDir)
    {
        var xulStorePath = Path.Combine(profileDir, "xulstore.json");
        var state = new Dictionary<string, object>
        {
            ["chrome://browser/content/browser.xhtml"] = new Dictionary<string, object>
            {
                ["PersonalToolbar"] = new Dictionary<string, string>
                {
                    ["collapsed"] = "false"
                },
                ["toolbar-menubar"] = new Dictionary<string, string>
                {
                    ["autohide"] = "true"
                }
            }
        };

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = false });
        File.WriteAllText(xulStorePath, json);
    }

    private static string BuildToolbarCustomizationState()
    {
        var state = new
        {
            placements = new Dictionary<string, string[]>
            {
                ["widget-overflow-fixed-list"] = Array.Empty<string>(),
                ["unified-extensions-area"] = Array.Empty<string>(),
                ["nav-bar"] = new[]
                {
                    "back-button",
                    "forward-button",
                    "stop-reload-button",
                    "bookmarks-menu-button",
                    "managed-bookmarks",
                    "personal-bookmarks",
                    "urlbar-container",
                    "unified-extensions-button"
                },
                ["toolbar-menubar"] = new[] { "menubar-items" },
                ["TabsToolbar"] = new[] { "tabbrowser-tabs", "new-tab-button", "alltabs-button" },
                ["vertical-tabs"] = Array.Empty<string>(),
                ["PersonalToolbar"] = new[] { "managed-bookmarks", "personal-bookmarks" }
            },
            seen = new[] { "developer-button" },
            dirtyAreaCache = new[] { "nav-bar", "vertical-tabs", "toolbar-menubar", "TabsToolbar", "PersonalToolbar" },
            currentVersion = 22,
            newElementCount = 2
        };

        return JsonSerializer.Serialize(state);
    }

    private static void WriteUserChrome(string profileDir)
    {
        var chromeDir = Path.Combine(profileDir, "chrome");
        Directory.CreateDirectory(chromeDir);
        var css = """
        #PersonalToolbar,
        #PersonalToolbar[collapsed="true"],
        #PersonalToolbar[hidden="true"] {
          visibility: visible !important;
          display: flex !important;
          height: 30px !important;
          min-height: 28px !important;
          max-height: 32px !important;
          padding-block: 2px !important;
          opacity: 1 !important;
        }

        #PersonalToolbar > toolbaritem,
        #PlacesToolbarItems,
        #PlacesToolbarItems > .bookmark-item {
          display: flex !important;
          visibility: visible !important;
          align-items: center !important;
        }
        """;
        File.WriteAllText(Path.Combine(chromeDir, "userChrome.css"), css);
    }

    internal static List<Cookie> ParseCookiesForImport(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("cookies", out var cookiesElement))
                root = cookiesElement;
            else if (root.TryGetProperty("export", out var exportElement) && exportElement.TryGetProperty("cookies", out cookiesElement))
                root = cookiesElement;
        }

        if (root.ValueKind != JsonValueKind.Array)
            return new List<Cookie>();

        var cookies = new List<Cookie>();
        foreach (var item in root.EnumerateArray())
        {
            if (!TryBuildCookie(item, out var cookie) || cookie == null)
                continue;
            cookies.Add(cookie);
        }

        return cookies;
    }

    internal static Dictionary<string, Dictionary<string, string>> ParseLocalStorageForImport(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement))
            root = dataElement;

        if (root.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (!TryParseDolphinLocalStorageEntry(property, out var origin, out var key, out var value))
                continue;

            if (!result.TryGetValue(origin, out var entries))
            {
                entries = new Dictionary<string, string>(StringComparer.Ordinal);
                result[origin] = entries;
            }

            entries[key] = value;
        }

        return result;
    }

    private static bool TryBuildCookie(JsonElement item, out Cookie? cookie)
    {
        cookie = null;
        var name = GetString(item, "name");
        var value = GetString(item, "value");
        var domain = GetString(item, "domain");
        var path = GetString(item, "path") ?? "/";
        if (string.IsNullOrWhiteSpace(name) || value == null || string.IsNullOrWhiteSpace(domain))
            return false;

        cookie = new Cookie
        {
            Name = name,
            Value = value,
            Domain = domain,
            Path = path,
            HttpOnly = GetBool(item, "httpOnly"),
            Secure = GetBool(item, "secure"),
            Expires = GetFloat(item, "expires") ?? GetFloat(item, "expirationDate"),
            SameSite = ParseSameSite(GetString(item, "sameSite"))
        };

        return true;
    }

    private static string? GetString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static bool? GetBool(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static float? GetFloat(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetSingle(out var parsed) => parsed,
            JsonValueKind.String when float.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static SameSiteAttribute? ParseSameSite(string? sameSite)
    {
        if (string.IsNullOrWhiteSpace(sameSite))
            return null;

        return sameSite.Trim().ToLowerInvariant().Replace("_", "-") switch
        {
            "strict" => SameSiteAttribute.Strict,
            "lax" => SameSiteAttribute.Lax,
            "none" => SameSiteAttribute.None,
            "no-restriction" => SameSiteAttribute.None,
            _ => null
        };
    }

    private async Task ImportStoredCookiesAsync(string profileId, RunningInstance instance, string logPath)
    {
        var path = _databaseService.GetProfileImportedCookiesFilePath(profileId);
        if (!File.Exists(path))
            return;

        try
        {
            var browser = instance.Browser;
            if (browser == null)
                return;

            var context = await GetOrCreateContextAsync(instance, logPath);
            if (context == null)
                return;

            var cookies = ParseCookiesForImport(await File.ReadAllTextAsync(path));
            if (cookies.Count == 0)
                return;

            await context.AddCookiesAsync(cookies);
            await WriteLogAsync(logPath, "INFO", $"Imported stored cookies. Count={cookies.Count}.");
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "WARN", $"Stored cookie import warning: {ex.Message}");
        }
    }

    private async Task AddStoredLocalStorageInitScriptAsync(string profileId, RunningInstance instance, string logPath)
    {
        var path = _databaseService.GetProfileImportedLocalStorageFilePath(profileId);
        if (!File.Exists(path))
            return;

        try
        {
            var browser = instance.Browser;
            if (browser == null)
                return;

            var context = await GetOrCreateContextAsync(instance, logPath);
            if (context == null)
                return;

            var storageByOrigin = ParseLocalStorageForImport(await File.ReadAllTextAsync(path));
            if (storageByOrigin.Count == 0)
                return;

            var json = JsonSerializer.Serialize(storageByOrigin);
            var script = $$"""
                (() => {
                    const yellowFoxLocalStorage = {{json}};
                    const entries = yellowFoxLocalStorage[location.origin];
                    if (!entries) {
                        return;
                    }

                    for (const [key, value] of Object.entries(entries)) {
                        try {
                            localStorage.setItem(key, value);
                        } catch {
                        }
                    }
                })();
                """;

            await context.AddInitScriptAsync(script);
            var entryCount = storageByOrigin.Sum(item => item.Value.Count);
            await WriteLogAsync(logPath, "INFO", $"Registered stored localStorage init script. Origins={storageByOrigin.Count}, entries={entryCount}.");
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "WARN", $"Stored localStorage import warning: {ex.Message}");
        }
    }

    private static bool TryParseDolphinLocalStorageEntry(JsonProperty property, out string origin, out string key, out string value)
    {
        origin = string.Empty;
        key = string.Empty;
        value = string.Empty;

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(property.Name));
        }
        catch
        {
            return false;
        }

        if (!decoded.StartsWith("_http://", StringComparison.OrdinalIgnoreCase) &&
            !decoded.StartsWith("_https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var separatorIndex = decoded.IndexOf("\0\u0001", StringComparison.Ordinal);
        if (separatorIndex < 0 || separatorIndex + 2 >= decoded.Length)
            return false;

        var rawOrigin = decoded[1..separatorIndex];
        key = decoded[(separatorIndex + 2)..];
        if (string.IsNullOrEmpty(key))
            return false;

        var partitionMarker = rawOrigin.IndexOf("/^0", StringComparison.Ordinal);
        if (partitionMarker > 0)
            rawOrigin = rawOrigin[..partitionMarker];

        if (!Uri.TryCreate(rawOrigin, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        origin = uri.GetLeftPart(UriPartial.Authority);
        value = property.Value.ValueKind == JsonValueKind.String
            ? property.Value.GetString() ?? string.Empty
            : property.Value.ToString();
        return true;
    }

    private static async Task WriteLogAsync(string logPath, string level, string message)
    {
        try
        {
            var logDir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrWhiteSpace(logDir))
                Directory.CreateDirectory(logDir);

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

    private async Task<ProxyBridgeLaunch> StartSocks5AuthBridgeAsync(ModelProxy proxy, string logPath)
    {
        var pythonDir = ResolvePythonScriptsPath();
        var launcher = ResolvePythonLauncher(pythonDir);
        var bridgeScript = Path.Combine(pythonDir, "socks5-auth-bridge.py");
        if (!File.Exists(bridgeScript))
            throw new FileNotFoundException($"SOCKS5 bridge script not found: {bridgeScript}");

        var configPath = Path.GetTempFileName();
        var configJson = JsonSerializer.Serialize(new
        {
            host = proxy.Host,
            port = proxy.Port,
            username = proxy.Username,
            password = proxy.Password
        });
        await File.WriteAllTextAsync(configPath, configJson);

        var startInfo = new ProcessStartInfo
        {
            FileName = launcher,
            Arguments = $"\"{bridgeScript}\" \"{configPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo);
        if (process == null)
            throw new InvalidOperationException("Failed to start SOCKS5 auth bridge.");

        try
        {
            var lineTask = process.StandardOutput.ReadLineAsync();
            var completed = await Task.WhenAny(lineTask, Task.Delay(TimeSpan.FromSeconds(5)));
            if (completed != lineTask)
                throw new InvalidOperationException("SOCKS5 auth bridge did not report a local endpoint.");

            var endpoint = await lineTask;
            if (string.IsNullOrWhiteSpace(endpoint) ||
                !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, "socks5", StringComparison.OrdinalIgnoreCase) ||
                uri.Port <= 0)
            {
                throw new InvalidOperationException("SOCKS5 auth bridge returned an invalid local endpoint.");
            }

            var port = uri.Port;
            await WriteLogAsync(logPath, "INFO", $"Started SOCKS5 auth bridge on 127.0.0.1:{port}.");
            return new ProxyBridgeLaunch
            {
                Process = process,
                ConfigPath = configPath,
                BrowserProxy = new ModelProxy
                {
                    Name = $"{proxy.Name} local bridge",
                    Type = "socks5",
                    Host = "127.0.0.1",
                    Port = port,
                    IsEnabled = true
                }
            };
        }
        catch
        {
            await StopProxyBridgeAsync(process, configPath, logPath);
            throw;
        }
    }

    private static bool IsAuthenticatedSocks5(ModelProxy proxy)
    {
        return string.Equals(ModelProxy.NormalizeType(proxy.Type), "socks5", StringComparison.OrdinalIgnoreCase) &&
               (!string.IsNullOrWhiteSpace(proxy.Username) || !string.IsNullOrWhiteSpace(proxy.Password));
    }

    private async Task<CamoufoxContextFingerprint> GenerateCamoufoxContextFingerprintAsync(Profile profile, ModelProxy? proxy, string logPath)
    {
        var pythonDir = ResolvePythonScriptsPath();
        var launcher = ResolvePythonLauncher(pythonDir);
        var script = Path.Combine(pythonDir, "camoufox-context-options.py");
        if (!File.Exists(script))
            throw new FileNotFoundException($"Camoufox context options script not found: {script}");

        var profileDir = _databaseService.GetProfileDataDirectory(profile.Id);
        Directory.CreateDirectory(profileDir);
        var configPath = Path.GetTempFileName();
        try
        {
            var config = new
            {
                os = profile.FingerprintConfig.Os,
                proxy = BuildCamoufoxProxy(proxy)
            };
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config));

            var startInfo = new ProcessStartInfo
            {
                FileName = launcher,
                Arguments = $"\"{script}\" \"{configPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("Failed to start Camoufox context options generator.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var exited = await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(20));
            if (!exited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new InvalidOperationException("Camoufox context options generator timed out.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Camoufox context options generator failed: {stderr.Trim()}");

            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            var contextOptionsElement = root.GetProperty("contextOptions");
            var initScript = root.TryGetProperty("initScript", out var initScriptElement) && initScriptElement.ValueKind == JsonValueKind.String
                ? initScriptElement.GetString() ?? string.Empty
                : string.Empty;
            var geoIp = root.TryGetProperty("geoIp", out var geoIpElement) && geoIpElement.ValueKind == JsonValueKind.String
                ? geoIpElement.GetString()
                : null;
            var camoufoxConfig = root.TryGetProperty("camoufoxConfig", out var camoufoxConfigElement) && camoufoxConfigElement.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(camoufoxConfigElement.GetRawText())
                : null;

            var options = BuildBrowserNewContextOptions(contextOptionsElement);
            var optionsPath = Path.Combine(profileDir, "agent-context-options.json");
            var initScriptPath = Path.Combine(profileDir, "agent-context-init.js");
            await File.WriteAllTextAsync(optionsPath, JsonSerializer.Serialize(ToAgentContextOptionsJson(contextOptionsElement), new JsonSerializerOptions { WriteIndented = true }));
            await File.WriteAllTextAsync(initScriptPath, initScript);
            await WriteLogAsync(logPath, "INFO", $"Generated Camoufox context fingerprint. Timezone={options.TimezoneId ?? "default"}, UserAgent={(string.IsNullOrWhiteSpace(options.UserAgent) ? "default" : "set")}.");

            return new CamoufoxContextFingerprint
            {
                ContextOptions = options,
                InitScript = initScript,
                OptionsPath = optionsPath,
                InitScriptPath = initScriptPath,
                GeoIp = geoIp,
                UseCamoufoxGeoIp = camoufoxConfig == null || camoufoxConfig.Count == 0,
                CamoufoxConfig = camoufoxConfig
            };
        }
        finally
        {
            if (File.Exists(configPath))
                File.Delete(configPath);
        }
    }

    private static BrowserNewContextOptions BuildBrowserNewContextOptions(JsonElement contextOptions)
    {
        var options = new BrowserNewContextOptions();
        if (TryGetJsonString(contextOptions, "user_agent", out var userAgent))
            options.UserAgent = userAgent;

        if (TryGetJsonString(contextOptions, "timezone_id", out var timezoneId))
            options.TimezoneId = timezoneId;

        if (contextOptions.TryGetProperty("device_scale_factor", out var dpr) && dpr.ValueKind == JsonValueKind.Number && dpr.TryGetSingle(out var deviceScaleFactor))
            options.DeviceScaleFactor = deviceScaleFactor;

        if (contextOptions.TryGetProperty("viewport", out var viewport) &&
            viewport.ValueKind == JsonValueKind.Object &&
            viewport.TryGetProperty("width", out var widthElement) &&
            viewport.TryGetProperty("height", out var heightElement) &&
            widthElement.TryGetInt32(out var width) &&
            heightElement.TryGetInt32(out var height))
        {
            options.ViewportSize = new ViewportSize { Width = width, Height = height };
            options.ScreenSize = new ScreenSize { Width = width, Height = height };
        }

        return options;
    }

    private static Dictionary<string, object?> ToAgentContextOptionsJson(JsonElement contextOptions)
    {
        var result = new Dictionary<string, object?>();
        if (TryGetJsonString(contextOptions, "user_agent", out var userAgent))
            result["user_agent"] = userAgent;
        if (TryGetJsonString(contextOptions, "timezone_id", out var timezoneId))
            result["timezone_id"] = timezoneId;
        if (contextOptions.TryGetProperty("device_scale_factor", out var dpr) && dpr.ValueKind == JsonValueKind.Number && dpr.TryGetSingle(out var deviceScaleFactor))
            result["device_scale_factor"] = deviceScaleFactor;
        if (contextOptions.TryGetProperty("viewport", out var viewport) && viewport.ValueKind == JsonValueKind.Object)
            result["viewport"] = JsonSerializer.Deserialize<Dictionary<string, int>>(viewport.GetRawText());

        return result;
    }

    private static bool TryGetJsonString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static async Task StopProxyBridgeAsync(Process? process, string? configPath, string logPath)
    {
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    await WriteLogAsync(logPath, "INFO", "Stopped SOCKS5 auth bridge.");
                }
            }
            catch (Exception ex)
            {
                await WriteLogAsync(logPath, "WARN", $"SOCKS5 auth bridge stop warning: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
        {
            try
            {
                File.Delete(configPath);
            }
            catch (Exception ex)
            {
                await WriteLogAsync(logPath, "WARN", $"SOCKS5 auth bridge config cleanup warning: {ex.Message}");
            }
        }
    }

    private static async Task<IBrowserContext?> GetOrCreateContextAsync(RunningInstance instance, string logPath)
    {
        try
        {
            var browser = instance.Browser;
            if (browser == null)
                return null;

            var context = await WaitForExistingContextAsync(browser, TimeSpan.FromSeconds(15));
            if (context != null)
            {
                if (!instance.ContextInitScriptApplied && !string.IsNullOrWhiteSpace(instance.ContextInitScript))
                {
                    await context.AddInitScriptAsync(instance.ContextInitScript);
                    instance.ContextInitScriptApplied = true;
                    await WriteLogAsync(logPath, "INFO", "Applied Camoufox context init script to existing context.");
                }
                return context;
            }

            if (instance.IsPersistentServer)
            {
                await WriteLogAsync(logPath, "WARN", "Persistent browser did not expose its default context. Refusing to create a second window/context.");
                return null;
            }

            context = await browser.NewContextAsync(instance.ContextOptions);
            if (!string.IsNullOrWhiteSpace(instance.ContextInitScript))
            {
                await context.AddInitScriptAsync(instance.ContextInitScript);
                instance.ContextInitScriptApplied = true;
            }

            await WriteLogAsync(logPath, "INFO", "Created Camoufox fingerprinted browser context for connected server.");
            return context;
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "WARN", $"Could not create browser context: {ex.Message}");
            return null;
        }
    }

    private static async Task<IBrowserContext?> WaitForExistingContextAsync(IBrowser browser, TimeSpan timeout)
    {
        var startedAt = DateTime.UtcNow;
        while (DateTime.UtcNow - startedAt < timeout)
        {
            var context = browser.Contexts.FirstOrDefault();
            if (context != null)
                return context;

            await Task.Delay(250);
        }

        return browser.Contexts.FirstOrDefault();
    }

    private static async Task EnsureVisibleWindowAsync(RunningInstance instance, string logPath)
    {
        try
        {
            var context = await GetOrCreateContextAsync(instance, logPath);
            if (context == null)
            {
                await WriteLogAsync(logPath, "WARN", "No browser context is available. Window may stay hidden.");
                return;
            }

            var page = context.Pages.FirstOrDefault();
            if (page == null)
            {
                page = await context.NewPageAsync();
                await page.GotoAsync("about:blank");
                await WriteLogAsync(logPath, "INFO", "Created first page in persistent context.");
            }

            await page.BringToFrontAsync();
            await WriteLogAsync(logPath, "INFO", "Browser window brought to front.");
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "WARN", $"Could not force browser window visibility: {ex.Message}");
        }
    }

    private async Task HandleProcessExitedAsync(string profileId)
    {
        RunningInstance? instance;
        lock (_runningInstancesLock)
        {
            if (!_runningInstances.TryGetValue(profileId, out instance) || instance == null)
                return;

            _runningInstances.Remove(profileId);
        }

        if (instance.IsStopping)
            return;

        var profile = _databaseService.GetProfile(profileId);
        var profileName = profile?.Name ?? profileId;
        var logPath = _databaseService.GetProfileLogFilePath(profileId, profileName);

        try
        {
            instance.SnapshotCts?.Cancel();
            await PersistTabsSnapshotAsync(profileId, instance, logPath, writeInfoLog: true);
            instance.Playwright?.Dispose();
            if (instance.Process != null)
            {
                var exited = await WaitForProcessExitAsync(instance.Process, TimeSpan.FromSeconds(3));
                if (!exited)
                    await KillProcessTreeAsync(instance.Process.Id, logPath);
            }
            await KillTrackedBrowserProcessesAsync(instance.BrowserProcessIds, logPath);
            await StopProxyBridgeAsync(instance.ProxyBridgeProcess, instance.ProxyBridgeConfigPath, logPath);

            if (File.Exists(instance.TempConfigPath))
                File.Delete(instance.TempConfigPath);

            await WriteLogAsync(logPath, "INFO", $"Profile '{profileName}' process exited. Marked as stopped.");
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "WARN", $"Process-exit handling warning: {ex.Message}");
        }
        finally
        {
            NotifyProfileRunningStateChanged(profileId, false);
        }
    }

    private async Task HandleBrowserDisconnectedAsync(string profileId)
    {
        RunningInstance? instance;
        lock (_runningInstancesLock)
        {
            if (!_runningInstances.TryGetValue(profileId, out instance) || instance == null)
                return;

            _runningInstances.Remove(profileId);
        }

        if (instance.IsStopping)
            return;

        var profile = _databaseService.GetProfile(profileId);
        var profileName = profile?.Name ?? profileId;
        var logPath = _databaseService.GetProfileLogFilePath(profileId, profileName);

        try
        {
            instance.SnapshotCts?.Cancel();
            await PersistTabsSnapshotAsync(profileId, instance, logPath, writeInfoLog: true);
            instance.Playwright?.Dispose();
            if (instance.Process != null)
            {
                var exited = await WaitForProcessExitAsync(instance.Process, TimeSpan.FromSeconds(3));
                if (!exited)
                    await KillProcessTreeAsync(instance.Process.Id, logPath);
            }
            await KillTrackedBrowserProcessesAsync(instance.BrowserProcessIds, logPath);
            await StopProxyBridgeAsync(instance.ProxyBridgeProcess, instance.ProxyBridgeConfigPath, logPath);

            if (File.Exists(instance.TempConfigPath))
                File.Delete(instance.TempConfigPath);

            await WriteLogAsync(logPath, "INFO", $"Browser disconnected for profile '{profileName}'. Marked as stopped.");
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "WARN", $"Browser-disconnect handling warning: {ex.Message}");
        }
        finally
        {
            NotifyProfileRunningStateChanged(profileId, false);
        }
    }

    private async Task RunTabsSnapshotLoopAsync(string profileId, RunningInstance instance, string logPath)
    {
        var cts = instance.SnapshotCts;
        if (cts == null)
            return;

        try
        {
            while (!cts.IsCancellationRequested)
            {
                await PersistTabsSnapshotAsync(profileId, instance, logPath, writeInfoLog: false);
                await Task.Delay(TabsSnapshotInterval, cts.Token);
            }
        }
        catch (TaskCanceledException)
        {
            // Expected on stop.
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "WARN", $"Tabs snapshot loop warning: {ex.Message}");
        }
    }

    private async Task PersistTabsSnapshotAsync(string profileId, RunningInstance instance, string logPath, bool writeInfoLog)
    {
        var context = instance.Browser?.Contexts.FirstOrDefault();
        if (context == null)
            return;

        var urls = context.Pages
            .Select(p => p.Url?.Trim())
            .Where(IsRestorableUrl)
            .Select(url => url!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var snapshot = new TabsSnapshot
        {
            UpdatedAtUtc = DateTime.UtcNow,
            Urls = urls
        };

        var snapshotPath = _databaseService.GetProfileTabsStateFilePath(profileId);
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(snapshotPath, json);

        if (writeInfoLog)
            await WriteLogAsync(logPath, "INFO", $"Saved tabs snapshot. Count={urls.Count}.");
    }

    private async Task RestoreTabsAsync(string profileId, RunningInstance instance, string logPath)
    {
        try
        {
            var snapshotPath = _databaseService.GetProfileTabsStateFilePath(profileId);
            if (!File.Exists(snapshotPath))
                return;

            var json = await File.ReadAllTextAsync(snapshotPath);
            var snapshot = JsonSerializer.Deserialize<TabsSnapshot>(json);
            if (snapshot?.Urls == null || snapshot.Urls.Count == 0)
                return;

            var context = instance.Browser?.Contexts.FirstOrDefault();
            if (context == null)
                return;

            var currentUrls = context.Pages
                .Select(p => p.Url?.Trim())
                .Where(IsRestorableUrl)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var tabsToRestore = snapshot.Urls
                .Where(IsRestorableUrl)
                .Where(url => !currentUrls.Contains(url))
                .ToList();

            if (tabsToRestore.Count == 0)
                return;

            var restored = 0;
            foreach (var url in tabsToRestore)
            {
                try
                {
                    var page = await context.NewPageAsync();
                    await page.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 20000
                    });
                    restored++;
                }
                catch
                {
                    // Keep restoring next tabs even if one URL fails.
                }
            }

            await WriteLogAsync(logPath, "INFO", $"Restored tabs from history. Restored={restored}, requested={tabsToRestore.Count}.");
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "WARN", $"Tab restore warning: {ex.Message}");
        }
    }

    private static bool IsRestorableUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (url.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase))
            return false;

        return Uri.TryCreate(url, UriKind.Absolute, out _);
    }

    private static bool TryNormalizeUrl(string rawUrl, out string url, out string error)
    {
        url = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            error = "URL is required.";
            return false;
        }

        var candidate = rawUrl.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
            candidate = $"https://{candidate}";

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "URL must be an absolute http or https URL.";
            return false;
        }

        url = uri.ToString();
        return true;
    }

    private bool TryGetRunningInstance(string profileId, out RunningInstance? instance)
    {
        lock (_runningInstancesLock)
        {
            return _runningInstances.TryGetValue(profileId, out instance);
        }
    }

    private static async Task KillProcessTreeAsync(int rootPid, string logPath)
    {
        try
        {
            Process? rootProcess = null;
            try
            {
                rootProcess = Process.GetProcessById(rootPid);
            }
            catch
            {
                return; // Already exited.
            }

            if (rootProcess.HasExited)
                return;

            rootProcess.Kill(entireProcessTree: true);
            await rootProcess.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            // Process already exited between lookup and kill.
        }
        catch (ArgumentException)
        {
            // PID is no longer valid.
        }
        catch (PlatformNotSupportedException ex)
        {
            await WriteLogAsync(logPath, "WARN", $"Process tree kill is not supported on this platform/runtime: {ex.Message}");
            try
            {
                var fallback = Process.GetProcessById(rootPid);
                if (!fallback.HasExited)
                {
                    fallback.Kill();
                    await fallback.WaitForExitAsync();
                }
            }
            catch
            {
                // Ignore if fallback also fails.
            }
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "WARN", $"Process tree kill warning: {ex.Message}");
        }
    }

    private static HashSet<int> GetCamoufoxProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("camoufox"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                        ids.Add(process.Id);
                }
                catch
                {
                    // Process may exit while scanning.
                }
            }
        }

        return ids;
    }

    private static async Task KillTrackedBrowserProcessesAsync(IReadOnlyCollection<int> processIds, string logPath)
    {
        if (processIds.Count == 0)
            return;

        var killed = 0;
        foreach (var processId in processIds.Distinct())
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                    continue;

                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                killed++;
            }
            catch (ArgumentException)
            {
                // PID no longer exists.
            }
            catch (InvalidOperationException)
            {
                // Process exited while being inspected.
            }
            catch (Exception ex)
            {
                await WriteLogAsync(logPath, "WARN", $"Tracked Camoufox process kill warning for PID {processId}: {ex.Message}");
            }
        }

        if (killed > 0)
            await WriteLogAsync(logPath, "INFO", $"Killed tracked Camoufox processes. Count={killed}.");
    }

    private static async Task RequestGracefulBrowserShutdownAsync(RunningInstance instance, string logPath)
    {
        try
        {
            var browser = instance.Browser;
            if (browser == null)
                return;

            var contexts = browser.Contexts.ToList();
            foreach (var context in contexts)
            {
                try
                {
                    await context.CloseAsync();
                }
                catch
                {
                    // Continue trying to close other contexts.
                }
            }

            if (browser.IsConnected)
            {
                await browser.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "WARN", $"Graceful browser shutdown warning: {ex.Message}");
        }
    }

    private static async Task<bool> WaitForProcessExitAsync(Process process, TimeSpan timeout)
    {
        if (process.HasExited)
            return true;

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    private void NotifyProfileRunningStateChanged(string profileId, bool isRunning)
    {
        ProfileRunningStateChanged?.Invoke(this, new ProfileRunningStateChangedEventArgs(profileId, isRunning));
    }

    private static string NormalizeProxyScheme(string? proxyType)
    {
        if (string.IsNullOrWhiteSpace(proxyType))
            return "http";

        var type = proxyType.Trim().ToLowerInvariant();
        return type switch
        {
            "socks5" => "socks5",
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

    private static string ResolvePythonLauncher(string pythonDir)
    {
        if (OperatingSystem.IsWindows())
        {
            var candidates = new[]
            {
                Path.Combine(pythonDir, "venv", "Scripts", "python.exe"),
                Path.Combine(pythonDir, "venv", "Scripts", "python3.exe"),
                "python"
            };

            foreach (var candidate in candidates)
            {
                if (candidate.Equals("python", StringComparison.OrdinalIgnoreCase) || File.Exists(candidate))
                    return candidate;
            }
        }
        else
        {
            var candidates = new[]
            {
                Path.Combine(pythonDir, "venv", "bin", "python3"),
                Path.Combine(pythonDir, "venv", "bin", "python"),
                "python3",
                "python"
            };

            foreach (var candidate in candidates)
            {
                if (candidate.StartsWith("python", StringComparison.Ordinal) || File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException($"Python launcher not found for scripts directory: {pythonDir}");
    }
}

public sealed class ProfileRunningStateChangedEventArgs : EventArgs
{
    public string ProfileId { get; }
    public bool IsRunning { get; }

    public ProfileRunningStateChangedEventArgs(string profileId, bool isRunning)
    {
        ProfileId = profileId;
        IsRunning = isRunning;
    }
}
