using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Playwright;
using YellowFox.Desktop.Models;
using ModelProxy = YellowFox.Desktop.Models.Proxy;

namespace YellowFox.Desktop.Services;

public class BrowserService
{
    private static readonly TimeSpan TabsSnapshotInterval = TimeSpan.FromSeconds(15);
    private static readonly HttpClient BrokerHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private readonly DatabaseService _databaseService;
    private readonly SettingsService _settingsService;
    private readonly ProxyValidatorService _proxyValidatorService;
    private readonly Dictionary<string, RunningInstance> _runningInstances = new();
    private readonly HashSet<string> _startingProfiles = new(StringComparer.OrdinalIgnoreCase);
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

    public string? GetBrokerEndpoint(string profileId)
    {
        lock (_runningInstancesLock)
        {
            return _runningInstances.TryGetValue(profileId, out var instance)
                ? instance.BrokerUrl
                : null;
        }
    }

    public async Task<string> GetCamoufoxVersionDisplayAsync()
    {
        try
        {
            var pythonDir = ResolvePythonScriptsPath();
            var launcher = ResolvePythonLauncher(pythonDir);
            var startInfo = new ProcessStartInfo
            {
                FileName = launcher,
                Arguments = "-c \"from yellowfox_camoufox_home import configure_camoufox_home; configure_camoufox_home(); from camoufox.pkgman import installed_verstr; print(installed_verstr())\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            ApplyLocalPythonEnvironment(startInfo, pythonDir);

            using var process = Process.Start(startInfo);

            if (process == null)
                return "Camoufox: unavailable";

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var waitTask = process.WaitForExitAsync();
            var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(8)));
            if (completed != waitTask)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort cleanup only.
                }

                return "Camoufox: version timeout";
            }

            var version = (await stdoutTask).Trim();
            _ = await stderrTask;
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(version)
                ? $"Camoufox: {version}"
                : "Camoufox: unavailable";
        }
        catch
        {
            return "Camoufox: unavailable";
        }
    }

    public async Task<bool> StartProfileAsync(string profileId)
    {
        lock (_runningInstancesLock)
        {
            if (_runningInstances.ContainsKey(profileId) || _startingProfiles.Contains(profileId))
                return false;

            _startingProfiles.Add(profileId);
        }

        Profile? profile;
        try
        {
            profile = _databaseService.GetProfile(profileId);
        }
        catch
        {
            lock (_runningInstancesLock)
            {
                _startingProfiles.Remove(profileId);
            }

            throw;
        }

        if (profile == null)
        {
            lock (_runningInstancesLock)
            {
                _startingProfiles.Remove(profileId);
            }

            throw new InvalidOperationException($"Profile {profileId} not found");
        }

        var logPath = _databaseService.GetProfileLogFilePath(profileId, profile.Name);
        await WriteLogAsync(logPath, "INFO", $"Start requested for profile '{profile.Name}' ({profile.Id}).");

        Process? proxyBridgeProcess = null;
        string? proxyBridgeConfigPath = null;
        Process? process = null;
        string? tempConfigPath = null;
        var existingCamoufoxPids = new HashSet<int>();

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
                var countryLog = !string.IsNullOrWhiteSpace(validation.CountryCode)
                    ? $", country={validation.CountryCode}"
                    : string.Empty;
                await WriteLogAsync(logPath, "INFO", $"Proxy validation passed. IP={validation.ExternalIp ?? "unknown"}{countryLog}, latency={validation.LatencyMs}ms.");
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
            _ = PrepareSharedBookmarks(userDataDir, sharedBookmarks);
            WriteProfileIdentityPrefs(userDataDir, profile.Name);
            await WriteLogAsync(logPath, "INFO", $"Prepared profile directory and shared bookmarks: {userDataDir}");

            var extensionSync = PrepareSharedExtensions(userDataDir, _databaseService.GetEnabledExtensions());
            await WriteLogAsync(logPath, "INFO", $"Synced shared extensions. Installed={extensionSync.InstalledCount}, removed={extensionSync.RemovedCount}, skipped={extensionSync.SkippedCount}.");
            var enabledExtensions = Array.Empty<string>();
            var contextFingerprint = await GenerateCamoufoxContextFingerprintAsync(profile, browserProxy, logPath);
            var initialUrls = ReadTabsSnapshotUrls(profileId);

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
                geoip = contextFingerprint.GeoIp,
                camoufox_config = contextFingerprint.CamoufoxConfig,
                profile_id = profile.Id,
                profile_name = profile.Name,
                profile_app_user_model_id = BuildProfileAppUserModelId(profile.Id),
                profile_icon_path = Path.Combine(userDataDir, "yellowfox-profile.ico"),
                initial_urls = Array.Empty<string>(),
                cookies = ToBrokerCookiePayload(ReadImportedCookies(profileId)),
                addons = enabledExtensions,
                bookmarks = sharedBookmarks.Select(b => new
                {
                    title = b.Title,
                    url = b.Url,
                    folder = string.IsNullOrWhiteSpace(b.Folder) ? null : b.Folder
                }).ToList()
            };

            var configJson = JsonSerializer.Serialize(config);
            tempConfigPath = Path.GetTempFileName();
            await File.WriteAllTextAsync(tempConfigPath, configJson);
            await WriteLogAsync(logPath, "INFO", $"Generated temporary launch config: {tempConfigPath}");

            var pythonDir = ResolvePythonScriptsPath();

            var launcher = ResolvePythonLauncher(pythonDir);
            var serverScript = Path.Combine(pythonDir, "camoufox-broker.py");
            if (!File.Exists(serverScript))
                throw new FileNotFoundException($"Camoufox broker script not found: {serverScript}");

            var fileName = launcher;
            var arguments = $"\"{serverScript}\" \"{tempConfigPath}\"";
            existingCamoufoxPids = GetCamoufoxProcessIds();

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            ApplyLocalPythonEnvironment(startInfo, pythonDir);

            process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("Failed to start Python broker process");
            await WriteLogAsync(logPath, "INFO", $"Python broker process started. PID={process.Id}");

            var endpoints = await WaitForBrokerEndpointsAsync(process, TimeSpan.FromSeconds(120));
            await WriteLogAsync(logPath, "INFO", $"Received Playwright endpoint: {endpoints.PlaywrightEndpoint}");
            await WriteLogAsync(logPath, "INFO", $"Received broker endpoint: {endpoints.BrokerUrl}");
            var browserProcessIds = GetCamoufoxProcessIds()
                .Except(existingCamoufoxPids)
                .ToList();
            await WriteLogAsync(logPath, "INFO", $"Tracked Camoufox process count: {browserProcessIds.Count}.");

            var instance = new RunningInstance
            {
                Process = process,
                CdpUrl = endpoints.PlaywrightEndpoint,
                BrokerUrl = endpoints.BrokerUrl,
                TempConfigPath = tempConfigPath,
                SnapshotCts = new CancellationTokenSource(),
                BrowserProcessIds = browserProcessIds,
                ProxyBridgeProcess = proxyBridgeProcess,
                ProxyBridgeConfigPath = proxyBridgeConfigPath,
                ContextOptions = contextFingerprint.ContextOptions,
                ContextInitScript = contextFingerprint.InitScript,
                ContextOptionsPath = contextFingerprint.OptionsPath,
                ContextInitScriptPath = contextFingerprint.InitScriptPath,
                IsPersistentServer = true,
                WindowMonitorStartupDelay = initialUrls.Count > 0
                    ? TimeSpan.FromSeconds(Math.Min(120, Math.Max(60, initialUrls.Count * 20)))
                    : TimeSpan.FromSeconds(10)
            };

            lock (_runningInstancesLock)
            {
                _runningInstances[profileId] = instance;
            }

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _ = HandleProcessExitedAsync(profileId);

            await RestoreTabsAsync(profileId, instance, logPath);
            _ = RunTabsSnapshotLoopAsync(profileId, instance, logPath);
            _ = RunBrowserWindowMonitorAsync(profileId, instance, logPath);

            await WriteLogAsync(logPath, "INFO", $"Profile '{profile.Name}' started successfully.");
            NotifyProfileRunningStateChanged(profileId, true);
            return true;
        }
        catch (Exception ex)
        {
            if (process != null && !process.HasExited)
                await KillProcessTreeAsync(process.Id, logPath);

            var spawnedCamoufoxPids = GetCamoufoxProcessIds()
                .Except(existingCamoufoxPids)
                .ToList();
            await KillTrackedBrowserProcessesAsync(spawnedCamoufoxPids, logPath);

            if (proxyBridgeProcess != null)
                await StopProxyBridgeAsync(proxyBridgeProcess, proxyBridgeConfigPath, logPath);
            if (!string.IsNullOrWhiteSpace(tempConfigPath) && File.Exists(tempConfigPath))
                File.Delete(tempConfigPath);
            await WriteLogAsync(logPath, "ERROR", $"Start failed: {ex.Message}");
            Debug.WriteLine($"Error starting profile: {ex.Message}");
            return false;
        }
        finally
        {
            lock (_runningInstancesLock)
            {
                _startingProfiles.Remove(profileId);
            }
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
            await TryPersistTabsSnapshotAsync(profileId, instance, logPath, writeInfoLog: true);
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

        var content = await File.ReadAllTextAsync(filePath);
        return await ImportCookiesFromTextAsync(profileId, content, null, $"file '{filePath}'");
    }

    public async Task<(bool Success, string Message)> ImportCookiesFromTextAsync(string profileId, string content, string? domain, string sourceDescription = "manual input")
    {
        var profile = _databaseService.GetProfile(profileId);
        var profileName = profile?.Name ?? profileId;
        var logPath = _databaseService.GetProfileLogFilePath(profileId, profileName);
        await WriteLogAsync(logPath, "INFO", $"Cookie import requested from {sourceDescription}.");

        if (!TryParseCookiesForImport(content, domain, out var cookies, out var parseError))
            return (false, parseError);

        if (cookies.Count == 0)
            return (false, "No cookies found.");

        SaveImportedCookies(profileId, cookies);
        await WriteLogAsync(logPath, "INFO", $"Saved imported cookies. Count={cookies.Count}.");

        if (!IsRunning(profileId))
        {
            await WriteLogAsync(logPath, "INFO", "Profile is not running. Cookies will be applied on next start.");
            return (true, $"Saved {cookies.Count} cookies. They will be applied when the profile starts.");
        }

        try
        {
            if (!TryGetRunningInstance(profileId, out var instance) || instance == null)
                return (false, "No running browser instance found.");

            if (!string.IsNullOrWhiteSpace(instance.BrokerUrl))
            {
                var imported = await BrokerPostAsync<BrokerCookieResponse>(instance.BrokerUrl, "cookies", new
                {
                    cookies = ToBrokerCookiePayload(cookies)
                });
                var importedCount = imported.Count ?? cookies.Count;
                await WriteLogAsync(logPath, "INFO", $"Cookie import completed through broker. Count={importedCount}");
                return (true, $"Imported {importedCount} cookies.");
            }

            if (instance.Browser == null)
                return (false, "No running browser instance found.");

            var context = await GetOrCreateContextAsync(instance, logPath);
            if (context == null)
                return (false, "No browser context found.");

            await context.AddCookiesAsync(cookies);
            await WriteLogAsync(logPath, "INFO", $"Cookie import completed. Count={cookies.Count}");
            return (true, $"Imported {cookies.Count} cookies.");
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "ERROR", $"Cookie import failed: {ex.Message}");
            return (false, $"Import failed: {ex.Message}");
        }
    }

    internal static bool TryParseCookiesForImport(string content, string? domain, out List<Cookie> cookies, out string error)
    {
        cookies = new List<Cookie>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(content))
        {
            error = "Cookie input is empty.";
            return false;
        }

        var trimmed = content.Trim();
        if (LooksLikeJson(trimmed))
        {
            try
            {
                cookies = ParseCookiesForImport(trimmed);
            }
            catch (JsonException ex)
            {
                error = $"Invalid JSON cookie format: {ex.Message}";
                return false;
            }

            if (cookies.Count == 0)
            {
                error = "No cookies found in JSON.";
                return false;
            }

            return true;
        }

        if (TryParseNameValueCookies(trimmed, domain, out cookies, out error))
        {
            if (cookies.Count > 0)
                return true;

            if (!string.IsNullOrWhiteSpace(error))
                return false;
        }

        if (LooksLikeNameValueCookieString(trimmed))
        {
            error = string.IsNullOrWhiteSpace(error)
                ? "No cookies found."
                : error;
            return false;
        }

        error = "Cookie format not found. Supported formats: JSON or name=value; name2=value2.";
        return false;
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
            if (!TryGetRunningInstance(profileId, out var instance) || instance == null)
                return (false, null, null, "No running browser instance found.");

            if (!string.IsNullOrWhiteSpace(instance.BrokerUrl))
            {
                var opened = await BrokerPostAsync<BrokerOpenResponse>(instance.BrokerUrl, "open", new { url });
                await WriteLogAsync(logPath, "INFO", $"Opened URL from broker CLI: {opened.Url}");
                return (true, opened.Url, opened.Title, "URL opened.");
            }

            if (instance.Browser == null)
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
        if (!TryGetRunningInstance(profileId, out var instance) || instance == null)
            return Array.Empty<OpenPageSnapshot>();

        var profile = _databaseService.GetProfile(profileId);
        var profileName = profile?.Name ?? profileId;
        var logPath = _databaseService.GetProfileLogFilePath(profileId, profileName);
        if (!string.IsNullOrWhiteSpace(instance.BrokerUrl))
        {
            var response = await BrokerGetAsync<BrokerPagesResponse>(instance.BrokerUrl, $"pages?text={includeText.ToString().ToLowerInvariant()}");
            return response.Pages.Select(page => new OpenPageSnapshot
            {
                Url = page.Url ?? string.Empty,
                Title = page.Title,
                Text = page.Text
            }).ToList();
        }

        if (instance.Browser == null)
            return Array.Empty<OpenPageSnapshot>();

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

        if (!TryGetRunningInstance(profileId, out var instance) || instance == null)
            return (false, "No running browser instance found.", null, null);

        var profile = _databaseService.GetProfile(profileId);
        var profileName = profile?.Name ?? profileId;
        var logPath = _databaseService.GetProfileLogFilePath(profileId, profileName);
        if (!string.IsNullOrWhiteSpace(instance.BrokerUrl))
        {
            var clicked = await BrokerPostAsync<BrokerClickResponse>(instance.BrokerUrl, "click", new { text = text.Trim() });
            if (clicked.Success)
                await WriteLogAsync(logPath, "INFO", $"Clicked text from broker CLI: {text}");
            return (clicked.Success, clicked.Message ?? (clicked.Success ? "Clicked." : "Click failed."), clicked.Url, clicked.Title);
        }

        if (instance.Browser == null)
            return (false, "No running browser instance found.", null, null);

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
        public string? BrokerUrl { get; set; }
        public string TempConfigPath { get; set; } = string.Empty;
        public IPlaywright? Playwright { get; set; }
        public IBrowser? Browser { get; set; }
        public IBrowserContext? PersistentContext { get; set; }
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
        public TimeSpan WindowMonitorStartupDelay { get; set; } = TimeSpan.FromSeconds(10);
    }

    internal sealed record ExtensionSyncResult(int InstalledCount, int RemovedCount, int SkippedCount);

    private sealed class ManagedExtensionState
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public string SourceHash { get; set; } = string.Empty;
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

    private List<Cookie> ReadImportedCookies(string profileId)
    {
        var path = _databaseService.GetProfileImportedCookiesFilePath(profileId);
        if (!File.Exists(path))
            return new List<Cookie>();

        try
        {
            return ParseCookiesForImport(File.ReadAllText(path));
        }
        catch
        {
            return new List<Cookie>();
        }
    }

    private static List<object> ToBrokerCookiePayload(IReadOnlyCollection<Cookie> cookies)
    {
        return cookies.Select(cookie => new
        {
            name = cookie.Name,
            value = cookie.Value,
            url = cookie.Url,
            domain = cookie.Domain,
            path = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
            expires = cookie.Expires,
            httpOnly = cookie.HttpOnly,
            secure = cookie.Secure,
            sameSite = cookie.SameSite?.ToString()
        }).Cast<object>().ToList();
    }

    internal static string PrepareSharedBookmarks(string profileDir, IReadOnlyCollection<BookmarkItem> bookmarks)
    {
        Directory.CreateDirectory(profileDir);

        var bookmarksFilePath = Path.Combine(profileDir, "bookmarks.html");
        var bookmarksHtml = BuildBookmarksHtml(bookmarks);
        var bookmarksVersionPath = Path.Combine(profileDir, ".yellowfox-bookmarks.version");
        var managedBookmarksStatePath = Path.Combine(profileDir, ".yellowfox-managed-bookmarks.json");
        var currentVersion = ComputeBookmarksVersion(bookmarksHtml);
        var previousManagedBookmarks = ReadManagedBookmarksState(profileDir, managedBookmarksStatePath);
        var placesPath = Path.Combine(profileDir, "places.sqlite");
        var shouldImportBookmarksHtml = !File.Exists(placesPath);

        File.WriteAllText(bookmarksFilePath, bookmarksHtml);
        File.WriteAllText(bookmarksVersionPath, currentVersion);
        WriteManagedBookmarksState(managedBookmarksStatePath, bookmarks);
        if (!shouldImportBookmarksHtml)
            SyncManagedBookmarksInPlaces(placesPath, bookmarks, previousManagedBookmarks);

        var userJsPath = Path.Combine(profileDir, "user.js");
        var userJsLines = File.Exists(userJsPath)
            ? File.ReadAllLines(userJsPath).ToList()
            : new List<string>();

        EnsureUserPref(userJsLines, "browser.places.importBookmarksHTML", shouldImportBookmarksHtml ? "true" : "false");
        EnsureUserPref(userJsLines, "browser.bookmarks.restore_default_bookmarks", "false");
        EnsureUserPref(userJsLines, "browser.toolbars.bookmarks.visibility", "\"always\"");
        EnsureUserPref(userJsLines, "browser.toolbars.bookmarks.showOtherBookmarks", "false");
        EnsureUserPref(userJsLines, "browser.toolbars.bookmarks.showInPrivateBrowsing", "true");
        EnsureUserPref(userJsLines, "browser.policies.runOncePerModification.displayBookmarksToolbar", "\"always\"");
        EnsureUserPref(userJsLines, "browser.bookmarks.addedImportButton", "true");
        EnsureUserPref(userJsLines, "browser.startup.page", "3");
        EnsureUserPref(userJsLines, "browser.aboutwelcome.enabled", "false");
        EnsureUserPref(userJsLines, "browser.preonboarding.enabled", "false");
        EnsureUserPref(userJsLines, "browser.tabs.drawInTitlebar", "false");
        EnsureUserPref(userJsLines, "browser.tabs.inTitlebar", "0");
        EnsureUserPref(userJsLines, "taskbar.grouping.useprofile", "true");
        EnsureUserPref(userJsLines, "browser.startup.blankWindow", "false");
        EnsureUserPref(userJsLines, "extensions.autoDisableScopes", "0");
        EnsureUserPref(userJsLines, "extensions.enabledScopes", "5");
        EnsureUserPref(userJsLines, "xpinstall.signatures.required", "false");
        EnsureUserPref(userJsLines, "datareporting.policy.dataSubmissionEnabled", "false");
        EnsureUserPref(userJsLines, "datareporting.policy.dataSubmissionPolicyAcceptedVersion", "999");
        EnsureUserPref(userJsLines, "datareporting.policy.dataSubmissionPolicyNotifiedTime", "\"0\"");
        ApplyNavigationPrefs(userJsLines);
        EnsureUserPref(userJsLines, "browser.sessionstore.resume_from_crash", "true");
        EnsureUserPref(userJsLines, "browser.sessionstore.max_tabs_undo", "25");
        EnsureUserPref(userJsLines, "browser.sessionstore.max_windows_undo", "3");
        RemoveUserPref(userJsLines, "datareporting.healthreport.uploadEnabled");
        RemoveUserPref(userJsLines, "toolkit.telemetry.enabled");
        RemoveUserPref(userJsLines, "toolkit.telemetry.unified");
        RemoveUserPref(userJsLines, "toolkit.telemetry.archive.enabled");
        RemoveUserPref(userJsLines, "toolkit.telemetry.newProfilePing.enabled");
        RemoveUserPref(userJsLines, "toolkit.telemetry.reportingpolicy.firstRun");
        RemoveUserPref(userJsLines, "toolkit.telemetry.shutdownPingSender.enabled");
        RemoveUserPref(userJsLines, "app.shield.optoutstudies.enabled");
        RemoveUserPref(userJsLines, "toolkit.legacyUserProfileCustomizations.stylesheets");
        RemoveUserPref(userJsLines, "browser.uiCustomization.state");

        File.WriteAllLines(userJsPath, userJsLines);
        WriteToolbarPrefs(Path.Combine(profileDir, "prefs.js"), shouldImportBookmarksHtml);
        DeleteToolbarState(profileDir);
        DeleteSearchEngineCache(profileDir);
        DeleteUserChrome(profileDir);
        return WriteSharedBookmarksExtension(profileDir, bookmarks, previousManagedBookmarks);
    }

    internal static bool IsExtensionPathUsable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var trimmed = path.Trim();
        return Directory.Exists(trimmed) && File.Exists(Path.Combine(trimmed, "manifest.json"));
    }

    internal static ExtensionSyncResult PrepareSharedExtensions(string profileDir, IReadOnlyCollection<ExtensionItem> extensions)
    {
        Directory.CreateDirectory(profileDir);
        var profileExtensionsDir = Path.Combine(profileDir, "extensions");
        Directory.CreateDirectory(profileExtensionsDir);

        var statePath = Path.Combine(profileDir, ".yellowfox-managed-extensions.json");
        var previous = ReadManagedExtensionsState(statePath);
        var previousById = previous
            .Where(e => !string.IsNullOrWhiteSpace(e.Id))
            .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<string, ManagedExtensionState>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;

        foreach (var extension in extensions.Where(e => e.IsEnabled))
        {
            if (!TryBuildManagedExtensionState(extension, out var state) || state == null)
            {
                skipped++;
                continue;
            }

            current[state.Id] = state;
        }

        var removed = 0;
        foreach (var previousItem in previous)
        {
            if (current.ContainsKey(previousItem.Id))
                continue;

            if (DeleteManagedProfileExtension(profileExtensionsDir, previousItem.Id))
                removed++;
        }

        var installed = 0;
        foreach (var item in current.Values)
        {
            previousById.TryGetValue(item.Id, out var previousItem);
            if (!CopyManagedProfileExtension(profileExtensionsDir, item, previousItem))
                continue;

            installed++;
        }

        WriteManagedExtensionsState(statePath, current.Values);
        if (installed > 0 || removed > 0 || ManagedExtensionStartupCacheNeedsReset(profileDir, current.Values))
            DeleteExtensionStartupCaches(profileDir);
        WriteExtensionToolbarPrefs(profileDir, current.Values);

        return new ExtensionSyncResult(installed, removed, skipped);
    }

    private static bool ManagedExtensionStartupCacheNeedsReset(string profileDir, IEnumerable<ManagedExtensionState> managedExtensions)
    {
        var managedIds = managedExtensions
            .Select(e => e.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (managedIds.Count == 0)
            return false;

        var extensionsJsonPath = Path.Combine(profileDir, "extensions.json");
        if (!File.Exists(extensionsJsonPath))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(extensionsJsonPath));
            if (!document.RootElement.TryGetProperty("addons", out var addons) || addons.ValueKind != JsonValueKind.Array)
                return true;

            foreach (var addon in addons.EnumerateArray())
            {
                if (!addon.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
                    continue;

                var id = idElement.GetString();
                if (string.IsNullOrWhiteSpace(id) || !managedIds.Contains(id))
                    continue;

                if (addon.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.False)
                    return true;
                if (addon.TryGetProperty("userDisabled", out var userDisabled) && userDisabled.ValueKind == JsonValueKind.True)
                    return true;
                if (addon.TryGetProperty("appDisabled", out var appDisabled) && appDisabled.ValueKind == JsonValueKind.True)
                    return true;

                managedIds.Remove(id);
            }

            return managedIds.Count > 0;
        }
        catch
        {
            return true;
        }
    }

    private static List<ManagedExtensionState> ReadManagedExtensionsState(string statePath)
    {
        try
        {
            if (!File.Exists(statePath))
                return new List<ManagedExtensionState>();

            return JsonSerializer.Deserialize<List<ManagedExtensionState>>(File.ReadAllText(statePath), BookmarkStateJsonOptions)
                ?? new List<ManagedExtensionState>();
        }
        catch
        {
            return new List<ManagedExtensionState>();
        }
    }

    private static void WriteManagedExtensionsState(string statePath, IEnumerable<ManagedExtensionState> extensions)
    {
        var items = extensions
            .OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        File.WriteAllText(statePath, JsonSerializer.Serialize(items, BookmarkStateJsonOptions));
    }

    private static bool TryBuildManagedExtensionState(ExtensionItem extension, out ManagedExtensionState? state)
    {
        state = null;
        if (!IsExtensionPathUsable(extension.Path))
            return false;

        var sourcePath = Path.GetFullPath(extension.Path.Trim());
        try
        {
            _ = ExtensionCompatService.NormalizeManifestForFirefox(sourcePath, extension.Name, out var isCompatible);
            if (!isCompatible)
                return false;
        }
        catch
        {
            return false;
        }
        var manifestPath = Path.Combine(sourcePath, "manifest.json");
        string? addonId;
        try
        {
            addonId = ReadExtensionId(manifestPath);
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(addonId))
            return false;

        state = new ManagedExtensionState
        {
            Id = addonId.Trim(),
            Name = extension.Name.Trim(),
            SourcePath = sourcePath,
            SourceHash = ComputeDirectoryHash(sourcePath)
        };
        return true;
    }

    private static string? ReadExtensionId(string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        if (TryGetGeckoExtensionId(root, "browser_specific_settings", out var id))
            return id;
        if (TryGetGeckoExtensionId(root, "applications", out id))
            return id;
        return null;
    }

    private static bool TryGetGeckoExtensionId(JsonElement root, string settingsProperty, out string? id)
    {
        id = null;
        if (!root.TryGetProperty(settingsProperty, out var settings) || settings.ValueKind != JsonValueKind.Object)
            return false;
        if (!settings.TryGetProperty("gecko", out var gecko) || gecko.ValueKind != JsonValueKind.Object)
            return false;
        if (!gecko.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            return false;

        id = idElement.GetString();
        return !string.IsNullOrWhiteSpace(id);
    }

    private static bool CopyManagedProfileExtension(string profileExtensionsDir, ManagedExtensionState extension, ManagedExtensionState? previous)
    {
        var targetPath = Path.Combine(profileExtensionsDir, $"{extension.Id}.xpi");
        var legacyDirectoryPath = Path.Combine(profileExtensionsDir, extension.Id);
        var sourcePath = extension.SourcePath;
        if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
            return false;
        if (previous != null &&
            string.Equals(previous.SourceHash, extension.SourceHash, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(targetPath))
            return false;

        if (Directory.Exists(targetPath))
            Directory.Delete(targetPath, recursive: true);
        if (File.Exists(targetPath))
            File.Delete(targetPath);
        if (Directory.Exists(legacyDirectoryPath))
            Directory.Delete(legacyDirectoryPath, recursive: true);

        ZipFile.CreateFromDirectory(sourcePath, targetPath, CompressionLevel.Fastest, includeBaseDirectory: false);
        return true;
    }

    private static string ComputeDirectoryHash(string directory)
    {
        using var sha = SHA256.Create();
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories).OrderBy(p => Path.GetRelativePath(directory, p), StringComparer.OrdinalIgnoreCase))
        {
            var relativePathBytes = Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, file).Replace('\\', '/'));
            sha.TransformBlock(relativePathBytes, 0, relativePathBytes.Length, null, 0);
            var content = File.ReadAllBytes(file);
            sha.TransformBlock(content, 0, content.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
    }

    private static bool DeleteManagedProfileExtension(string profileExtensionsDir, string addonId)
    {
        var removed = false;
        var directoryPath = Path.Combine(profileExtensionsDir, addonId);
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
            removed = true;
        }

        var xpiPath = Path.Combine(profileExtensionsDir, $"{addonId}.xpi");
        if (File.Exists(xpiPath))
        {
            File.Delete(xpiPath);
            removed = true;
        }

        return removed;
    }

    private static void DeleteExtensionStartupCaches(string profileDir)
    {
        foreach (var fileName in new[]
        {
            "extensions.json",
            "extension-settings.json",
            "addonStartup.json.lz4",
            "addonStartup.json.lz4.tmp"
        })
        {
            var path = Path.Combine(profileDir, fileName);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static string BuildBookmarksHtml(IReadOnlyCollection<BookmarkItem> bookmarks)
    {
        var items = NormalizeBookmarkTreeItems(bookmarks);
        var byParent = items
            .GroupBy(item => item.ParentId ?? string.Empty)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.SortOrder).ThenByDescending(item => item.IsFolder).ThenBy(item => item.Title).ToList());

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
        sb.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
        sb.AppendLine("<TITLE>Bookmarks</TITLE>");
        sb.AppendLine("<H1>Bookmarks</H1>");
        sb.AppendLine("<DL><p>");
        sb.AppendLine("    <DT><H3 PERSONAL_TOOLBAR_FOLDER=\"true\">Bookmarks Toolbar</H3>");
        sb.AppendLine("    <DL><p>");

        AppendBookmarkChildren(sb, byParent, string.Empty, 8);

        sb.AppendLine("    </DL><p>");
        sb.AppendLine("</DL><p>");
        return sb.ToString();
    }

    private static List<BookmarkItem> NormalizeBookmarkTreeItems(IReadOnlyCollection<BookmarkItem> bookmarks)
    {
        var items = bookmarks.Select(item => new BookmarkItem
        {
            Id = item.Id,
            Title = item.Title,
            Url = item.Url,
            Folder = string.IsNullOrWhiteSpace(item.Folder) ? null : item.Folder.Trim(),
            ParentId = string.IsNullOrWhiteSpace(item.ParentId) ? null : item.ParentId,
            IsFolder = item.IsFolder,
            SortOrder = item.SortOrder
        }).ToList();

        AddLegacyFolderNodes(items);

        var byId = items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var item in items)
        {
            item.Folder = item.IsFolder
                ? BookmarkPath(item, byId, includeSelf: true)
                : BookmarkPath(item, byId, includeSelf: false) ?? item.Folder;
        }

        return items;
    }

    private static void AddLegacyFolderNodes(List<BookmarkItem> items)
    {
        var byPath = items
            .Where(item => item.IsFolder)
            .Select(item => new
            {
                Item = item,
                Path = string.IsNullOrWhiteSpace(item.Folder) ? item.Title.Trim() : item.Folder.Trim()
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Item, StringComparer.OrdinalIgnoreCase);

        foreach (var bookmark in items.Where(item => !item.IsFolder
                                                     && string.IsNullOrWhiteSpace(item.ParentId)
                                                     && !string.IsNullOrWhiteSpace(item.Folder)).ToList())
        {
            string? parentId = null;
            var currentPath = string.Empty;
            foreach (var part in SplitBookmarkPath(bookmark.Folder!))
            {
                currentPath = string.IsNullOrWhiteSpace(currentPath) ? part : $"{currentPath}/{part}";
                if (!byPath.TryGetValue(currentPath, out var folder))
                {
                    folder = new BookmarkItem
                    {
                        Id = LegacyFolderId(currentPath),
                        Title = part,
                        Url = string.Empty,
                        ParentId = parentId,
                        IsFolder = true,
                        Folder = currentPath,
                        SortOrder = 0
                    };
                    byPath[currentPath] = folder;
                    items.Add(folder);
                }

                parentId = folder.Id;
            }

            bookmark.ParentId = parentId;
        }
    }

    private static string LegacyFolderId(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.Trim().ToUpperInvariant()));
        return $"legacy-folder-{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }

    private static string? BookmarkPath(BookmarkItem item, IReadOnlyDictionary<string, BookmarkItem> byId, bool includeSelf)
    {
        var parts = new Stack<string>();
        var current = includeSelf ? item : null;
        var currentId = includeSelf ? item.Id : item.ParentId;

        while (current != null || !string.IsNullOrWhiteSpace(currentId))
        {
            if (current == null)
            {
                if (currentId == null || !byId.TryGetValue(currentId, out current))
                    break;
            }

            if (current.IsFolder && !string.IsNullOrWhiteSpace(current.Title))
                parts.Push(current.Title.Trim());

            currentId = current.ParentId;
            current = null;
        }

        return parts.Count == 0 ? null : string.Join("/", parts);
    }

    private static string WriteSharedBookmarksExtension(
        string profileDir,
        IReadOnlyCollection<BookmarkItem> bookmarks,
        IReadOnlyCollection<BookmarkItem> previousBookmarks)
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
          "permissions": ["bookmarks", "storage"],
          "background": {
            "scripts": ["background.js"]
          }
        }
        """;
        File.WriteAllText(Path.Combine(extensionDir, "manifest.json"), manifestJson);

        var normalizedBookmarks = NormalizeBookmarkTreeItems(bookmarks);
        var normalizedPreviousBookmarks = NormalizeBookmarkTreeItems(previousBookmarks);
        var payload = new
        {
            legacyRootTitle = "yellowfox shared",
            previousFolders = normalizedPreviousBookmarks.Where(b => b.IsFolder).Select(b => b.Folder).Where(f => !string.IsNullOrWhiteSpace(f)).ToList(),
            folders = normalizedBookmarks.Where(b => b.IsFolder).Select(b => b.Folder).Where(f => !string.IsNullOrWhiteSpace(f)).ToList(),
            previousBookmarks = normalizedPreviousBookmarks.Where(b => !b.IsFolder).Select(b => new
            {
                title = b.Title,
                url = b.Url,
                folder = string.IsNullOrWhiteSpace(b.Folder) ? null : b.Folder
            }).ToList(),
            bookmarks = normalizedBookmarks.Where(b => !b.IsFolder).Select(b => new
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

          function normalizeItem(item) {
            return {
              title: String(item?.title || ""),
              url: String(item?.url || ""),
              folder: item?.folder ? String(item.folder) : null,
            };
          }

          function itemKey(item) {
            const normalized = normalizeItem(item);
            return `${normalized.folder || ""}\n${normalized.title}\n${normalized.url}`;
          }

          async function findChildFolder(parentId, title) {
            const children = await getChildren(parentId);
            return children.find((child) => child.title === title && !child.url);
          }

          async function ensureFolder(parentId, title) {
            const existing = await findChildFolder(parentId, title);
            if (existing) {
              return existing;
            }
            return await browser.bookmarks.create({ parentId, title });
          }

          async function ensureFolderPath(toolbar, path) {
            let parentId = toolbar.id;
            const parts = String(path || "").split("/").map((part) => part.trim()).filter(Boolean);
            for (const part of parts) {
              const folder = await ensureFolder(parentId, part);
              parentId = folder.id;
            }
            return parentId;
          }

          async function findFolderPath(toolbar, path) {
            let parentId = toolbar.id;
            let folder = null;
            const parts = String(path || "").split("/").map((part) => part.trim()).filter(Boolean);
            for (const part of parts) {
              folder = await findChildFolder(parentId, part);
              if (!folder) {
                return null;
              }
              parentId = folder.id;
            }
            return folder;
          }

          async function ensureBookmark(parentId, item) {
            const children = await getChildren(parentId);
            const existing = children.find((child) => child.url === item.url && child.title === item.title);
            if (existing) {
              return existing;
            }
            return await browser.bookmarks.create({ parentId, title: item.title, url: item.url });
          }

          async function removeBookmark(parentId, item) {
            const normalized = normalizeItem(item);
            const children = await getChildren(parentId);
            for (const child of children) {
              if (child.url === normalized.url && child.title === normalized.title) {
                await browser.bookmarks.remove(child.id);
              }
            }
          }

          async function removeManagedItem(toolbar, item) {
            const normalized = normalizeItem(item);
            if (!normalized.title || !normalized.url) {
              return;
            }

            if (normalized.folder) {
              const folder = await findFolderPath(toolbar, normalized.folder);
              if (!folder) {
                return;
              }
              await removeBookmark(folder.id, normalized);
              return;
            }

            await removeBookmark(toolbar.id, normalized);
          }

          async function removeEmptyManagedFolders(toolbar, previousItems, currentItems) {
            const currentFolders = new Set([
              ...(payload.folders || []),
              ...currentItems.map((item) => normalizeItem(item).folder).filter(Boolean),
            ]);
            const previousFolders = new Set([
              ...(payload.previousFolders || []),
              ...previousItems.map((item) => normalizeItem(item).folder).filter(Boolean),
            ]);
            const folderPaths = Array.from(previousFolders).sort((a, b) => b.length - a.length);
            for (const path of folderPaths) {
              if (currentFolders.has(path)) {
                continue;
              }
              const folder = await findFolderPath(toolbar, path);
              if (!folder) {
                continue;
              }
              const children = await getChildren(folder.id);
              if (children.length === 0) {
                await browser.bookmarks.removeTree(folder.id);
              }
            }
          }

          async function run() {
            const toolbar = await findToolbarRoot();
            await removeExistingFolder(toolbar.id, payload.legacyRootTitle);
            const stored = await browser.storage.local.get("managedBookmarks").catch(() => ({}));
            const previousItems = (stored.managedBookmarks || payload.previousBookmarks || []).map(normalizeItem);
            const currentItems = (payload.bookmarks || []).map(normalizeItem);
            const currentKeys = new Set(currentItems.map(itemKey));
            for (const item of previousItems) {
              if (!currentKeys.has(itemKey(item))) {
                await removeManagedItem(toolbar, item);
              }
            }
            await removeEmptyManagedFolders(toolbar, previousItems, currentItems);

            const folders = new Map();
            for (const path of payload.folders || []) {
              if (path) {
                folders.set(path, await ensureFolderPath(toolbar, path));
              }
            }

            for (const item of currentItems) {
              let parentId = toolbar.id;
              if (item.folder) {
                if (!folders.has(item.folder)) {
                  folders.set(item.folder, await ensureFolderPath(toolbar, item.folder));
                }
                parentId = folders.get(item.folder);
              }
              await ensureBookmark(parentId, item);
            }
            await browser.storage.local.set({ managedBookmarks: currentItems }).catch(() => {});
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

    private static void AppendBookmarkChildren(
        StringBuilder sb,
        IReadOnlyDictionary<string, List<BookmarkItem>> byParent,
        string parentId,
        int indent)
    {
        if (!byParent.TryGetValue(parentId, out var children))
            return;

        foreach (var item in children)
        {
            if (item.IsFolder)
            {
                var spaces = new string(' ', indent);
                var title = System.Net.WebUtility.HtmlEncode(item.Title);
                sb.AppendLine($"{spaces}<DT><H3>{title}</H3>");
                sb.AppendLine($"{spaces}<DL><p>");
                AppendBookmarkChildren(sb, byParent, item.Id, indent + 4);
                sb.AppendLine($"{spaces}</DL><p>");
            }
            else
            {
                AppendBookmark(sb, item, indent);
            }
        }
    }

    private static string ComputeBookmarksVersion(string bookmarksHtml)
    {
        var bytes = Encoding.UTF8.GetBytes(bookmarksHtml);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static IReadOnlyCollection<BookmarkItem> ReadManagedBookmarksState(string profileDir, string statePath)
    {
        try
        {
            if (File.Exists(statePath))
            {
                var items = JsonSerializer.Deserialize<List<BookmarkItem>>(File.ReadAllText(statePath), BookmarkStateJsonOptions);
                if (items != null)
                    return items;
            }

            var backgroundPath = Path.Combine(profileDir, "yellowfox-shared-bookmarks-extension", "background.js");
            if (File.Exists(backgroundPath))
            {
                var backgroundJs = File.ReadAllText(backgroundPath);
                var match = Regex.Match(backgroundJs, @"const\s+payload\s*=\s*(\{.*?\});", RegexOptions.Singleline);
                if (match.Success)
                {
                    using var document = JsonDocument.Parse(match.Groups[1].Value);
                    if (document.RootElement.TryGetProperty("bookmarks", out var bookmarksElement))
                    {
                        var items = JsonSerializer.Deserialize<List<BookmarkItem>>(bookmarksElement.GetRawText(), BookmarkStateJsonOptions);
                        if (items != null)
                            return items;
                    }
                }
            }
        }
        catch
        {
            // Best-effort migration from older bookmark sync state.
        }

        return Array.Empty<BookmarkItem>();
    }

    private static void WriteManagedBookmarksState(string statePath, IReadOnlyCollection<BookmarkItem> bookmarks)
    {
        var items = NormalizeBookmarkTreeItems(bookmarks).Select(b => new BookmarkItem
        {
            Id = b.Id,
            Title = b.Title,
            Url = b.Url,
            Folder = string.IsNullOrWhiteSpace(b.Folder) ? null : b.Folder,
            ParentId = b.ParentId,
            IsFolder = b.IsFolder,
            SortOrder = b.SortOrder
        }).ToList();
        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(statePath, json);
    }

    private static readonly JsonSerializerOptions BookmarkStateJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static void SyncManagedBookmarksInPlaces(
        string placesPath,
        IReadOnlyCollection<BookmarkItem> bookmarks,
        IReadOnlyCollection<BookmarkItem> previousBookmarks)
    {
        bookmarks = NormalizeBookmarkTreeItems(bookmarks);
        previousBookmarks = NormalizeBookmarkTreeItems(previousBookmarks);

        if (!IsSqliteDatabase(placesPath))
            return;

        try
        {
            using var connection = new SqliteConnection($"Data Source={placesPath};Pooling=False");
            connection.Open();

            var toolbarId = GetToolbarBookmarkId(connection);
            if (toolbarId <= 0)
                return;

            var currentKeys = bookmarks.Select(ManagedBookmarkKey).ToHashSet(StringComparer.Ordinal);
            foreach (var previous in previousBookmarks)
            {
                if (!currentKeys.Contains(ManagedBookmarkKey(previous)))
                    RemoveManagedBookmark(connection, toolbarId, previous);
            }

            RemoveEmptyManagedFolders(connection, toolbarId, previousBookmarks, bookmarks);

            foreach (var folder in bookmarks.Where(b => b.IsFolder && !string.IsNullOrWhiteSpace(b.Folder)))
                EnsurePlacesFolderPath(connection, toolbarId, folder.Folder!);

            foreach (var bookmark in bookmarks.Where(b => !b.IsFolder))
            {
                var parentId = toolbarId;
                if (!string.IsNullOrWhiteSpace(bookmark.Folder))
                    parentId = EnsurePlacesFolderPath(connection, toolbarId, bookmark.Folder.Trim());

                EnsurePlacesBookmark(connection, parentId, bookmark.Title.Trim(), bookmark.Url.Trim());
            }
        }
        catch (SqliteException)
        {
            // Older tests and some corrupted profiles may have a non-SQLite placeholder.
            // Leave the file untouched instead of deleting user bookmarks.
        }
    }

    private static bool IsSqliteDatabase(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[16];
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Read(header) == header.Length
                && Encoding.ASCII.GetString(header) == "SQLite format 3\0";
        }
        catch
        {
            return false;
        }
    }

    private static string ManagedBookmarkKey(BookmarkItem bookmark)
    {
        return string.Join("\n", bookmark.Folder?.Trim() ?? string.Empty, bookmark.Title.Trim(), bookmark.Url.Trim());
    }

    private static long EnsurePlacesFolderPath(SqliteConnection connection, long toolbarId, string path)
    {
        var parentId = toolbarId;
        foreach (var part in SplitBookmarkPath(path))
            parentId = EnsurePlacesFolder(connection, parentId, part);
        return parentId;
    }

    private static long FindPlacesFolderPath(SqliteConnection connection, long toolbarId, string path)
    {
        var parentId = toolbarId;
        long folderId = 0;
        foreach (var part in SplitBookmarkPath(path))
        {
            folderId = FindPlacesFolder(connection, parentId, part);
            if (folderId <= 0)
                return 0;
            parentId = folderId;
        }

        return folderId;
    }

    private static string[] SplitBookmarkPath(string path)
    {
        return path
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
    }

    private static long GetToolbarBookmarkId(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM moz_bookmarks WHERE guid = 'toolbar_____' LIMIT 1";
        var result = command.ExecuteScalar();
        return result is long id ? id : 0;
    }

    private static long EnsurePlacesFolder(SqliteConnection connection, long toolbarId, string title)
    {
        using (var find = connection.CreateCommand())
        {
            find.CommandText = @"
                SELECT id
                FROM moz_bookmarks
                WHERE parent = $parent AND type = 2 AND title = $title
                ORDER BY id
                LIMIT 1";
            find.Parameters.AddWithValue("$parent", toolbarId);
            find.Parameters.AddWithValue("$title", title);
            var existing = find.ExecuteScalar();
            if (existing is long id)
                return id;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        using var insert = connection.CreateCommand();
        insert.CommandText = @"
            INSERT INTO moz_bookmarks(type, fk, parent, position, title, dateAdded, lastModified, guid)
            VALUES(2, NULL, $parent, $position, $title, $now, $now, $guid);
            SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$parent", toolbarId);
        insert.Parameters.AddWithValue("$position", NextBookmarkPosition(connection, toolbarId));
        insert.Parameters.AddWithValue("$title", title);
        insert.Parameters.AddWithValue("$now", now);
        insert.Parameters.AddWithValue("$guid", CreatePlacesGuid());
        return (long)insert.ExecuteScalar()!;
    }

    private static void EnsurePlacesBookmark(SqliteConnection connection, long parentId, string title, string url)
    {
        using (var find = connection.CreateCommand())
        {
            find.CommandText = @"
                SELECT b.id
                FROM moz_bookmarks b
                JOIN moz_places p ON p.id = b.fk
                WHERE b.parent = $parent AND b.type = 1 AND b.title = $title AND p.url = $url
                LIMIT 1";
            find.Parameters.AddWithValue("$parent", parentId);
            find.Parameters.AddWithValue("$title", title);
            find.Parameters.AddWithValue("$url", url);
            if (find.ExecuteScalar() is long)
                return;
        }

        var placeId = EnsurePlace(connection, url, title);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        using var insert = connection.CreateCommand();
        insert.CommandText = @"
            INSERT INTO moz_bookmarks(type, fk, parent, position, title, dateAdded, lastModified, guid)
            VALUES(1, $place, $parent, $position, $title, $now, $now, $guid)";
        insert.Parameters.AddWithValue("$place", placeId);
        insert.Parameters.AddWithValue("$parent", parentId);
        insert.Parameters.AddWithValue("$position", NextBookmarkPosition(connection, parentId));
        insert.Parameters.AddWithValue("$title", title);
        insert.Parameters.AddWithValue("$now", now);
        insert.Parameters.AddWithValue("$guid", CreatePlacesGuid());
        insert.ExecuteNonQuery();
    }

    private static long EnsurePlace(SqliteConnection connection, string url, string title)
    {
        using (var find = connection.CreateCommand())
        {
            find.CommandText = "SELECT id FROM moz_places WHERE url = $url LIMIT 1";
            find.Parameters.AddWithValue("$url", url);
            if (find.ExecuteScalar() is long id)
                return id;
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = @"
            INSERT INTO moz_places(url, title, rev_host, visit_count, hidden, typed, frecency, guid, foreign_count, url_hash)
            VALUES($url, $title, $revHost, 0, 0, 0, -1, $guid, 1, 0);
            SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$url", url);
        insert.Parameters.AddWithValue("$title", title);
        insert.Parameters.AddWithValue("$revHost", BuildReverseHost(url));
        insert.Parameters.AddWithValue("$guid", CreatePlacesGuid());
        return (long)insert.ExecuteScalar()!;
    }

    private static void RemoveManagedBookmark(SqliteConnection connection, long toolbarId, BookmarkItem bookmark)
    {
        var parentId = toolbarId;
        if (!string.IsNullOrWhiteSpace(bookmark.Folder))
        {
            parentId = FindPlacesFolderPath(connection, toolbarId, bookmark.Folder.Trim());
            if (parentId <= 0)
                return;
        }

        using var delete = connection.CreateCommand();
        delete.CommandText = @"
            DELETE FROM moz_bookmarks
            WHERE id IN (
                SELECT b.id
                FROM moz_bookmarks b
                JOIN moz_places p ON p.id = b.fk
                WHERE b.parent = $parent AND b.type = 1 AND b.title = $title AND p.url = $url
            )";
        delete.Parameters.AddWithValue("$parent", parentId);
        delete.Parameters.AddWithValue("$title", bookmark.Title.Trim());
        delete.Parameters.AddWithValue("$url", bookmark.Url.Trim());
        delete.ExecuteNonQuery();
    }

    private static void RemoveEmptyManagedFolders(
        SqliteConnection connection,
        long toolbarId,
        IReadOnlyCollection<BookmarkItem> previousBookmarks,
        IReadOnlyCollection<BookmarkItem> bookmarks)
    {
        var currentFolders = bookmarks
            .Select(b => b.Folder?.Trim())
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .ToHashSet(StringComparer.Ordinal);
        var previousFolders = previousBookmarks
            .Select(b => b.Folder?.Trim())
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(folder => folder?.Length ?? 0);

        foreach (var folder in previousFolders)
        {
            if (folder == null || currentFolders.Contains(folder))
                continue;

            var folderId = FindPlacesFolderPath(connection, toolbarId, folder);
            if (folderId <= 0 || CountBookmarkChildren(connection, folderId) > 0)
                continue;

            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM moz_bookmarks WHERE id = $id";
            delete.Parameters.AddWithValue("$id", folderId);
            delete.ExecuteNonQuery();
        }
    }

    private static long FindPlacesFolder(SqliteConnection connection, long toolbarId, string title)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id
            FROM moz_bookmarks
            WHERE parent = $parent AND type = 2 AND title = $title
            ORDER BY id
            LIMIT 1";
        command.Parameters.AddWithValue("$parent", toolbarId);
        command.Parameters.AddWithValue("$title", title);
        var result = command.ExecuteScalar();
        return result is long id ? id : 0;
    }

    private static long CountBookmarkChildren(SqliteConnection connection, long parentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM moz_bookmarks WHERE parent = $parent";
        command.Parameters.AddWithValue("$parent", parentId);
        return (long)command.ExecuteScalar()!;
    }

    private static long NextBookmarkPosition(SqliteConnection connection, long parentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(position) + 1, 0) FROM moz_bookmarks WHERE parent = $parent";
        command.Parameters.AddWithValue("$parent", parentId);
        return (long)command.ExecuteScalar()!;
    }

    private static string BuildReverseHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? new string(uri.Host.Reverse().ToArray()) + "."
            : string.Empty;
    }

    private static string CreatePlacesGuid()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[12];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
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

    private static void RemoveUserPref(List<string> lines, string key)
    {
        var prefPrefix = $"user_pref(\"{key}\"";
        lines.RemoveAll(line => line.Contains(prefPrefix, StringComparison.Ordinal));
    }

    private static void WriteToolbarPrefs(string prefsPath, bool shouldImportBookmarksHtml)
    {
        var lines = File.Exists(prefsPath)
            ? File.ReadAllLines(prefsPath).ToList()
            : new List<string>();

        EnsureUserPref(lines, "browser.toolbars.bookmarks.visibility", "\"always\"");
        EnsureUserPref(lines, "browser.places.importBookmarksHTML", shouldImportBookmarksHtml ? "true" : "false");
        EnsureUserPref(lines, "browser.toolbars.bookmarks.showOtherBookmarks", "false");
        EnsureUserPref(lines, "browser.toolbars.bookmarks.showInPrivateBrowsing", "true");
        EnsureUserPref(lines, "browser.aboutwelcome.enabled", "false");
        EnsureUserPref(lines, "browser.preonboarding.enabled", "false");
        EnsureUserPref(lines, "browser.tabs.drawInTitlebar", "false");
        EnsureUserPref(lines, "browser.tabs.inTitlebar", "0");
        EnsureUserPref(lines, "taskbar.grouping.useprofile", "true");
        EnsureUserPref(lines, "browser.startup.blankWindow", "false");
        EnsureUserPref(lines, "extensions.autoDisableScopes", "0");
        EnsureUserPref(lines, "extensions.enabledScopes", "5");
        EnsureUserPref(lines, "xpinstall.signatures.required", "false");
        EnsureUserPref(lines, "datareporting.policy.dataSubmissionEnabled", "false");
        EnsureUserPref(lines, "datareporting.policy.dataSubmissionPolicyAcceptedVersion", "999");
        EnsureUserPref(lines, "datareporting.policy.dataSubmissionPolicyNotifiedTime", "\"0\"");
        ApplyNavigationPrefs(lines);
        EnsureUserPref(lines, "browser.startup.page", "3");
        EnsureUserPref(lines, "browser.sessionstore.resume_from_crash", "true");
        EnsureUserPref(lines, "browser.sessionstore.max_tabs_undo", "25");
        EnsureUserPref(lines, "browser.sessionstore.max_windows_undo", "3");
        RemoveUserPref(lines, "datareporting.healthreport.uploadEnabled");
        RemoveUserPref(lines, "toolkit.telemetry.enabled");
        RemoveUserPref(lines, "toolkit.telemetry.unified");
        RemoveUserPref(lines, "toolkit.telemetry.archive.enabled");
        RemoveUserPref(lines, "toolkit.telemetry.newProfilePing.enabled");
        RemoveUserPref(lines, "toolkit.telemetry.reportingpolicy.firstRun");
        RemoveUserPref(lines, "toolkit.telemetry.shutdownPingSender.enabled");
        RemoveUserPref(lines, "app.shield.optoutstudies.enabled");
        RemoveUserPref(lines, "toolkit.legacyUserProfileCustomizations.stylesheets");
        RemoveUserPref(lines, "browser.uiCustomization.state");

        File.WriteAllLines(prefsPath, lines);
    }

    private static void ApplyNavigationPrefs(List<string> lines)
    {
        EnsureUserPref(lines, "keyword.enabled", "true");
        EnsureUserPref(lines, "browser.link.open_newwindow", "3");
        EnsureUserPref(lines, "browser.link.open_newwindow.restriction", "0");
        EnsureUserPref(lines, "dom.event.contextmenu.enabled", "false");
        EnsureUserPref(lines, "browser.fixup.fallback-to-https", "true");
        EnsureUserPref(lines, "browser.fixup.upgrade_to_https", "true");
        EnsureUserPref(lines, "dom.security.https_first", "true");
        EnsureUserPref(lines, "dom.security.https_first_pbm", "true");
        EnsureUserPref(lines, "dom.security.https_only_mode", "true");
        EnsureUserPref(lines, "dom.security.https_only_mode_pbm", "true");
        EnsureUserPref(lines, "dom.security.https_only_mode.upgrade_local", "true");
        EnsureUserPref(lines, "dom.security.https_only_mode_ever_enabled", "true");
        EnsureUserPref(lines, "browser.search.defaultenginename", "\"Google\"");
        EnsureUserPref(lines, "browser.search.defaultenginename.US", "\"Google\"");
        EnsureUserPref(lines, "browser.search.selectedEngine", "\"Google\"");
        EnsureUserPref(lines, "browser.search.defaultPrivateEngine", "\"Google\"");
        EnsureUserPref(lines, "browser.search.separatePrivateDefault", "false");
        EnsureUserPref(lines, "browser.search.separatePrivateDefault.ui.enabled", "false");
        EnsureUserPref(lines, "browser.search.order.1", "\"Google\"");
        EnsureUserPref(lines, "browser.search.update", "false");
        EnsureUserPref(lines, "browser.urlbar.searchSuggestionsChoice", "true");
        EnsureUserPref(lines, "browser.urlbar.placeholderName", "\"Google\"");
        EnsureUserPref(lines, "browser.urlbar.placeholderName.private", "\"Google\"");
    }

    private static void WriteProfileIdentityPrefs(string profileDir, string profileName)
    {
        var safeName = string.IsNullOrWhiteSpace(profileName)
            ? "YellowFox"
            : profileName.Trim();
        var escapedName = JsonSerializer.Serialize(safeName);

        foreach (var fileName in new[] { "user.js", "prefs.js" })
        {
            var path = Path.Combine(profileDir, fileName);
            var lines = File.Exists(path)
                ? File.ReadAllLines(path).ToList()
                : new List<string>();
            EnsureUserPref(lines, "yellowfox.profile.name", escapedName);
            EnsureUserPref(lines, "browser.tabs.drawInTitlebar", "false");
            EnsureUserPref(lines, "browser.tabs.inTitlebar", "0");
            RemoveUserPref(lines, "toolkit.legacyUserProfileCustomizations.stylesheets");
            File.WriteAllLines(path, lines);
        }

        DeleteUserChrome(profileDir);
    }

    private static string BuildProfileAppUserModelId(string profileId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(profileId));
        return $"YellowFox.Camoufox.{Convert.ToHexString(hash)[..16]}";
    }

    private static void DeleteToolbarState(string profileDir)
    {
        var xulStorePath = Path.Combine(profileDir, "xulstore.json");
        if (File.Exists(xulStorePath))
            File.Delete(xulStorePath);
    }

    private static void DeleteSearchEngineCache(string profileDir)
    {
        var searchCachePath = Path.Combine(profileDir, "search.json.mozlz4");
        if (File.Exists(searchCachePath))
            File.Delete(searchCachePath);
    }

    private static void WriteExtensionToolbarPrefs(string profileDir, IEnumerable<ManagedExtensionState> extensions)
    {
        var extensionStates = extensions.ToList();
        var toolbarPrefValue = extensionStates.Count > 0
            ? JsonSerializer.Serialize(JsonSerializer.Serialize(BuildToolbarCustomizationState(extensionStates)))
            : null;
        foreach (var fileName in new[] { "user.js", "prefs.js" })
        {
            var path = Path.Combine(profileDir, fileName);
            var lines = File.Exists(path)
                ? File.ReadAllLines(path).ToList()
                : new List<string>();
            if (toolbarPrefValue == null)
                RemoveUserPref(lines, "browser.uiCustomization.state");
            else
                EnsureUserPref(lines, "browser.uiCustomization.state", toolbarPrefValue);
            File.WriteAllLines(path, lines);
        }
    }

    private static object BuildToolbarCustomizationState(IEnumerable<ManagedExtensionState> extensions)
    {
        var extensionWidgets = extensions
            .Select(e => ExtensionActionWidgetId(e.Id))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var navBarItems = new List<string>
        {
            "back-button",
            "forward-button",
            "stop-reload-button",
            "bookmarks-menu-button",
            "urlbar-container"
        };
        navBarItems.AddRange(extensionWidgets);
        navBarItems.Add("unified-extensions-button");

        var state = new
        {
            placements = new Dictionary<string, string[]>
            {
                ["widget-overflow-fixed-list"] = Array.Empty<string>(),
                ["unified-extensions-area"] = Array.Empty<string>(),
                ["nav-bar"] = navBarItems.ToArray(),
                ["toolbar-menubar"] = new[] { "menubar-items" },
                ["TabsToolbar"] = new[] { "tabbrowser-tabs", "new-tab-button", "alltabs-button", "titlebar-buttonbox-container" },
                ["vertical-tabs"] = Array.Empty<string>(),
                ["PersonalToolbar"] = new[] { "personal-bookmarks" }
            },
            seen = extensionWidgets.Concat(new[] { "developer-button", "screenshot-button", "unified-extensions-button" }).ToArray(),
            dirtyAreaCache = new[] { "nav-bar", "vertical-tabs", "toolbar-menubar", "TabsToolbar", "PersonalToolbar", "unified-extensions-area" },
            currentVersion = 22,
            newElementCount = 2
        };

        return state;
    }

    private static string ExtensionActionWidgetId(string addonId)
    {
        var builder = new StringBuilder();
        foreach (var ch in addonId.Trim().ToLowerInvariant())
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        return $"{builder}-browser-action";
    }

    private static void DeleteUserChrome(string profileDir)
    {
        var userChromePath = Path.Combine(profileDir, "chrome", "userChrome.css");
        if (File.Exists(userChromePath))
            File.Delete(userChromePath);
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

    private static bool LooksLikeJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var first = content.TrimStart()[0];
        return first is '[' or '{';
    }

    private static bool LooksLikeNameValueCookieString(string content)
    {
        return content.Contains('=') && !content.Contains('\n') && !content.Contains('\r');
    }

    private static bool TryParseNameValueCookies(string content, string? rawDomain, out List<Cookie> cookies, out string error)
    {
        cookies = new List<Cookie>();
        error = string.Empty;

        if (!LooksLikeNameValueCookieString(content))
            return false;

        if (!TryNormalizeCookieDomain(rawDomain, out var domain, out error))
            return true;

        var parts = content.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var separatorIndex = part.IndexOf('=');
            if (separatorIndex <= 0)
            {
                error = "Cookie format not found. Expected name=value pairs separated by semicolons.";
                cookies.Clear();
                return true;
            }

            var name = part[..separatorIndex].Trim();
            var value = part[(separatorIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(name) || IsCookieAttributeName(name))
            {
                error = "Cookie format not found. Expected browser cookies, not Set-Cookie attributes.";
                cookies.Clear();
                return true;
            }

            cookies.Add(new Cookie
            {
                Name = name,
                Value = value,
                Domain = domain,
                Path = "/",
                Secure = true
            });
        }

        if (cookies.Count == 0)
        {
            error = "No cookies found.";
            return true;
        }

        return true;
    }

    private static bool TryNormalizeCookieDomain(string? rawDomain, out string domain, out string error)
    {
        domain = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rawDomain))
        {
            error = "Domain is required for name=value cookie format.";
            return false;
        }

        var value = rawDomain.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            value = uri.Host;
        else
            value = value.Split('/')[0].Split(':')[0];

        value = value.Trim().TrimStart('.');
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains(' ') ||
            !value.Contains('.') ||
            !Regex.IsMatch(value, "^[A-Za-z0-9.-]+$"))
        {
            error = "Enter a valid domain for name=value cookie format, for example facebook.com.";
            return false;
        }

        domain = $".{value.ToLowerInvariant()}";
        return true;
    }

    private static bool IsCookieAttributeName(string name)
    {
        return name.Trim().ToLowerInvariant() switch
        {
            "path" or "domain" or "expires" or "max-age" or "samesite" or "secure" or "httponly" => true,
            _ => false
        };
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
        var expires = NormalizeImportedCookieExpires(GetFloat(item, "expires") ?? GetFloat(item, "expirationDate"));
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
            Expires = expires,
            SameSite = ParseSameSite(GetString(item, "sameSite"))
        };

        return true;
    }

    private static float? NormalizeImportedCookieExpires(float? expires)
    {
        if (expires == null)
            return null;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (expires.Value > now)
            return expires;

        return DateTimeOffset.UtcNow.AddMonths(6).ToUnixTimeSeconds();
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
            "0" => SameSiteAttribute.Strict,
            "1" => SameSiteAttribute.Lax,
            "2" => SameSiteAttribute.None,
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
            ApplyLocalPythonEnvironment(startInfo, pythonDir);

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

    private static async Task<BrowserTypeLaunchPersistentContextOptions> GenerateCamoufoxLaunchOptionsAsync(string launcher, string script, string configPath, string logPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = launcher,
            Arguments = $"\"{script}\" --print-options \"{configPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["PYTHONUTF8"] = "1";

        using var process = Process.Start(startInfo);
        if (process == null)
            throw new InvalidOperationException("Failed to start Camoufox launch options generator.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exited = await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(30));
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new InvalidOperationException("Camoufox launch options generator timed out.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Camoufox launch options generator failed: {stderr.Trim()}");

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        var options = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = false
        };

        if (TryGetJsonString(root, "executablePath", out var executablePath))
            options.ExecutablePath = executablePath;

        if (root.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array)
        {
            options.Args = argsElement
                .EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)
                .ToArray();
        }

        if (root.TryGetProperty("timeout", out var timeoutElement) && timeoutElement.TryGetDouble(out var timeout))
            options.Timeout = (float)timeout;

        if (root.TryGetProperty("env", out var envElement) && envElement.ValueKind == JsonValueKind.Object)
        {
            options.Env = envElement.EnumerateObject()
                .Where(property => property.Value.ValueKind != JsonValueKind.Null && property.Value.ValueKind != JsonValueKind.Undefined)
                .ToDictionary(property => property.Name, property => JsonValueToString(property.Value));
        }

        if (root.TryGetProperty("firefoxUserPrefs", out var prefsElement) && prefsElement.ValueKind == JsonValueKind.Object)
        {
            options.FirefoxUserPrefs = prefsElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => JsonValueToObject(property.Value));
        }

        if (root.TryGetProperty("proxy", out var proxyElement) && proxyElement.ValueKind == JsonValueKind.Object &&
            TryGetJsonString(proxyElement, "server", out var proxyServer))
        {
            var proxy = new Microsoft.Playwright.Proxy { Server = proxyServer };
            if (TryGetJsonString(proxyElement, "username", out var username))
                proxy.Username = username;
            if (TryGetJsonString(proxyElement, "password", out var password))
                proxy.Password = password;
            options.Proxy = proxy;
        }

        await WriteLogAsync(logPath, "INFO", $"Generated direct Camoufox launch options. Executable={(string.IsNullOrWhiteSpace(options.ExecutablePath) ? "default" : options.ExecutablePath)}.");
        return options;
    }

    private static string JsonValueToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => value.GetRawText()
        };
    }

    private static object JsonValueToObject(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var integer) => integer,
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            _ => value.GetRawText()
        };
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
            await TryPersistTabsSnapshotAsync(profileId, instance, logPath, writeInfoLog: true);
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
            await TryPersistTabsSnapshotAsync(profileId, instance, logPath, writeInfoLog: true);
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

    private async Task HandleBrowserWindowClosedAsync(string profileId)
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
            instance.IsStopping = true;
            instance.SnapshotCts?.Cancel();
            await TryPersistTabsSnapshotAsync(profileId, instance, logPath, writeInfoLog: true);
            await RequestGracefulBrowserShutdownAsync(instance, logPath);
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

            await WriteLogAsync(logPath, "INFO", $"Browser window closed for profile '{profileName}'. Marked as stopped.");
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "WARN", $"Browser-window-close handling warning: {ex.Message}");
        }
        finally
        {
            NotifyProfileRunningStateChanged(profileId, false);
        }
    }

    private async Task RunBrowserWindowMonitorAsync(string profileId, RunningInstance instance, string logPath)
    {
        var cts = instance.SnapshotCts;
        if (cts == null)
            return;

        try
        {
            await Task.Delay(instance.WindowMonitorStartupDelay, cts.Token);
            var missingVisibleWindowCount = 0;
            while (!cts.IsCancellationRequested)
            {
                if (HasVisibleTrackedBrowserWindow(instance))
                {
                    missingVisibleWindowCount = 0;
                }
                else if (++missingVisibleWindowCount >= 3)
                {
                    await WriteLogAsync(logPath, "INFO", "No visible Camoufox window detected. Treating profile as stopped.");
                    await HandleBrowserWindowClosedAsync(profileId);
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
            }
        }
        catch (TaskCanceledException)
        {
            // Expected on stop.
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "WARN", $"Browser window monitor warning: {ex.Message}");
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

    private async Task TryPersistTabsSnapshotAsync(string profileId, RunningInstance instance, string logPath, bool writeInfoLog)
    {
        try
        {
            await PersistTabsSnapshotAsync(profileId, instance, logPath, writeInfoLog);
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "WARN", $"Tabs snapshot skipped: {ex.Message}");
        }
    }

    private async Task PersistTabsSnapshotAsync(string profileId, RunningInstance instance, string logPath, bool writeInfoLog)
    {
        List<string> urls;
        if (!string.IsNullOrWhiteSpace(instance.BrokerUrl))
        {
            var response = await BrokerGetAsync<BrokerPagesResponse>(instance.BrokerUrl, "pages?text=false");
            urls = response.Pages
                .Select(p => p.Url?.Trim())
                .Where(IsRestorableUrl)
                .Select(url => url!)
                .ToList();
        }
        else
        {
            var context = instance.Browser?.Contexts.FirstOrDefault();
            if (context == null)
                return;

            urls = context.Pages
                .Select(p => p.Url?.Trim())
                .Where(IsRestorableUrl)
                .Select(url => url!)
                .ToList();
        }

        var snapshot = new TabsSnapshot
        {
            UpdatedAtUtc = DateTime.UtcNow,
            Urls = urls
        };

        var snapshotPath = _databaseService.GetProfileTabsStateFilePath(profileId);
        if (urls.Count == 0 && File.Exists(snapshotPath))
        {
            try
            {
                var previous = JsonSerializer.Deserialize<TabsSnapshot>(await File.ReadAllTextAsync(snapshotPath));
                if (previous?.Urls?.Any(IsRestorableUrl) == true)
                {
                    if (writeInfoLog)
                        await WriteLogAsync(logPath, "WARN", "Skipped empty tabs snapshot to keep previous restorable tabs.");
                    return;
                }
            }
            catch
            {
                // If the existing snapshot is unreadable, replace it below.
            }
        }

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

            if (!string.IsNullOrWhiteSpace(instance.BrokerUrl))
            {
                var current = await BrokerGetAsync<BrokerPagesResponse>(instance.BrokerUrl, "pages?text=false");
                var brokerCurrentUrls = current.Pages
                    .Select(p => p.Url?.Trim())
                    .Where(IsRestorableUrl)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (brokerCurrentUrls.Count > 0)
                {
                    await WriteLogAsync(logPath, "INFO", "Skipped YellowFox tab fallback because Firefox restored native session tabs.");
                    return;
                }

                var brokerTabsToRestore = snapshot.Urls
                    .Where(IsRestorableUrl)
                    .Where(url => !brokerCurrentUrls.Contains(url))
                    .ToList();
                foreach (var url in brokerTabsToRestore)
                    await BrokerPostAsync<BrokerOpenResponse>(instance.BrokerUrl, "open", new { url });
                if (brokerTabsToRestore.Count > 0)
                    await WriteLogAsync(logPath, "INFO", $"Restored tabs from broker history. Restored={brokerTabsToRestore.Count}, requested={brokerTabsToRestore.Count}.");
                return;
            }

            var context = instance.Browser?.Contexts.FirstOrDefault();
            if (context == null)
                return;

            var currentUrls = context.Pages
                .Select(p => p.Url?.Trim())
                .Where(IsRestorableUrl)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (currentUrls.Count > 0)
            {
                await WriteLogAsync(logPath, "INFO", "Skipped YellowFox tab fallback because Firefox restored native session tabs.");
                return;
            }

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
                    var page = context.Pages.FirstOrDefault(p => !IsRestorableUrl(p.Url))
                        ?? await context.NewPageAsync();
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

            if (context.Pages.Any(p => IsRestorableUrl(p.Url)))
            {
                foreach (var blankPage in context.Pages.Where(p => !IsRestorableUrl(p.Url)).ToList())
                {
                    try
                    {
                        await blankPage.CloseAsync();
                    }
                    catch
                    {
                        // Blank pages may already be gone while restore is settling.
                    }
                }
            }

            await WriteLogAsync(logPath, "INFO", $"Restored tabs from history. Restored={restored}, requested={tabsToRestore.Count}.");
        }
        catch (Exception ex)
        {
            await WriteLogAsync(logPath, "WARN", $"Tab restore warning: {ex.Message}");
        }
    }

    private IReadOnlyList<string> ReadTabsSnapshotUrls(string profileId)
    {
        try
        {
            var snapshotPath = _databaseService.GetProfileTabsStateFilePath(profileId);
            if (!File.Exists(snapshotPath))
                return Array.Empty<string>();

            var snapshot = JsonSerializer.Deserialize<TabsSnapshot>(File.ReadAllText(snapshotPath));
            var urls = snapshot?.Urls?
                .Where(IsRestorableUrl)
                .Select(url => url.Trim())
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .ToList();
            return urls ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsRestorableUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (url.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        if (System.Net.IPAddress.TryParse(uri.Host, out var address))
        {
            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                bytes.Length == 4 &&
                bytes[0] == 0)
            {
                return false;
            }
        }

        return true;
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
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.ProcessName, "camoufox", StringComparison.OrdinalIgnoreCase) && !process.HasExited)
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

    private static bool HasVisibleTrackedBrowserWindow(RunningInstance instance)
    {
        var trackedProcessIds = instance.BrowserProcessIds
            .Distinct()
            .ToHashSet();

        foreach (var processId in trackedProcessIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Refresh();
                if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero)
                {
                    if (!OperatingSystem.IsWindows() || IsWindowVisible(process.MainWindowHandle))
                        return true;
                }
            }
            catch
            {
                // PID may already be gone.
            }
        }

        if (OperatingSystem.IsWindows() && HasVisibleTopLevelWindowForProcessIds(trackedProcessIds))
            return true;

        return false;
    }

    private static bool HasVisibleTopLevelWindowForProcessIds(IReadOnlySet<int> processIds)
    {
        if (processIds.Count == 0)
            return false;

        var found = false;
        var shellWindow = GetShellWindow();
        EnumWindows((hWnd, _) =>
        {
            if (hWnd == IntPtr.Zero || hWnd == shellWindow || !IsWindowVisible(hWnd))
                return true;

            GetWindowThreadProcessId(hWnd, out var windowProcessId);
            if (!processIds.Contains((int)windowProcessId))
                return true;

            found = true;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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
            if (!string.IsNullOrWhiteSpace(instance.BrokerUrl))
            {
                try
                {
                    await BrokerPostAsync<BrokerOkResponse>(instance.BrokerUrl, "stop", new { });
                }
                catch (Exception ex)
                {
                    await WriteLogAsync(logPath, "WARN", $"Broker shutdown warning: {ex.Message}");
                }
                return;
            }

            var browser = instance.Browser;
            if (browser == null)
                return;

            if (instance.PersistentContext != null)
            {
                await instance.PersistentContext.CloseAsync();
                return;
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

    private static async Task<BrokerEndpoints> WaitForBrokerEndpointsAsync(Process process, TimeSpan timeout)
    {
        var startedAt = DateTime.UtcNow;
        Task<string?>? stdoutReadTask = null;
        string? brokerUrl = null;
        string? playwrightEndpoint = null;
        while (DateTime.UtcNow - startedAt < timeout)
        {
            if (process.HasExited)
            {
                var error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? "Camoufox broker exited before it reported an endpoint."
                    : $"Camoufox broker exited before it reported an endpoint: {error.Trim()}");
            }

            stdoutReadTask ??= process.StandardOutput.ReadLineAsync();
            var completed = await Task.WhenAny(stdoutReadTask, Task.Delay(250));
            if (completed != stdoutReadTask)
                continue;

            var line = await stdoutReadTask;
            stdoutReadTask = null;
            if (line == null)
                continue;

            var playwrightMatch = Regex.Match(line, @"YELLOWFOX_PLAYWRIGHT\s+(wss?://[^\s]+)");
            if (playwrightMatch.Success)
                playwrightEndpoint = playwrightMatch.Groups[1].Value.TrimEnd('/');

            var brokerMatch = Regex.Match(line, @"YELLOWFOX_BROKER\s+(https?://[^\s]+)");
            if (brokerMatch.Success)
                brokerUrl = brokerMatch.Groups[1].Value.TrimEnd('/');

            if (!string.IsNullOrWhiteSpace(brokerUrl) && !string.IsNullOrWhiteSpace(playwrightEndpoint))
                return new BrokerEndpoints(brokerUrl, playwrightEndpoint);
        }

        throw new TimeoutException("Timed out waiting for Camoufox broker and Playwright endpoints.");
    }

    private sealed record BrokerEndpoints(string BrokerUrl, string PlaywrightEndpoint);

    private static async Task<T> BrokerGetAsync<T>(string brokerUrl, string path)
    {
        using var response = await BrokerHttpClient.GetAsync($"{brokerUrl.TrimEnd('/')}/{path.TrimStart('/')}");
        var json = await response.Content.ReadAsStringAsync();
        EnsureBrokerHttpSuccess(response, json);
        var payload = JsonSerializer.Deserialize<T>(json, BrokerJsonOptions)
            ?? throw new InvalidOperationException("Broker returned an empty response.");
        ThrowIfBrokerError(payload, json);
        return payload;
    }

    private static async Task<T> BrokerPostAsync<T>(string brokerUrl, string path, object body)
    {
        var jsonBody = JsonSerializer.Serialize(body);
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var response = await BrokerHttpClient.PostAsync($"{brokerUrl.TrimEnd('/')}/{path.TrimStart('/')}", content);
        var json = await response.Content.ReadAsStringAsync();
        EnsureBrokerHttpSuccess(response, json);
        var payload = JsonSerializer.Deserialize<T>(json, BrokerJsonOptions)
            ?? throw new InvalidOperationException("Broker returned an empty response.");
        ThrowIfBrokerError(payload, json);
        return payload;
    }

    private static readonly JsonSerializerOptions BrokerJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static void ThrowIfBrokerError<T>(T payload, string json)
    {
        if (payload is BrokerOkResponse ok && !ok.Ok)
            throw new InvalidOperationException(ok.Error ?? json);
    }

    private static void EnsureBrokerHttpSuccess(HttpResponseMessage response, string json)
    {
        if (response.IsSuccessStatusCode)
            return;

        try
        {
            var error = JsonSerializer.Deserialize<BrokerOkResponse>(json, BrokerJsonOptions);
            if (!string.IsNullOrWhiteSpace(error?.Error))
                throw new InvalidOperationException(error.Error);
        }
        catch (JsonException)
        {
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(json)
                ? $"Broker request failed: {(int)response.StatusCode} {response.ReasonPhrase}"
                : $"Broker request failed: {(int)response.StatusCode} {response.ReasonPhrase}: {json}");
    }

    private class BrokerOkResponse
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
    }

    private class BrokerOpenResponse : BrokerOkResponse
    {
        public string? Url { get; set; }
        public string? Title { get; set; }
    }

    private sealed class BrokerClickResponse : BrokerOpenResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
    }

    private sealed class BrokerCookieResponse : BrokerOkResponse
    {
        public int? Count { get; set; }
    }

    private sealed class BrokerPagesResponse : BrokerOkResponse
    {
        public List<BrokerPage> Pages { get; set; } = new();
    }

    private sealed class BrokerPage
    {
        public string? Url { get; set; }
        public string? Title { get; set; }
        public string? Text { get; set; }
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
                if ((candidate.Equals("python", StringComparison.OrdinalIgnoreCase) || File.Exists(candidate)) &&
                    IsPythonLauncherUsable(candidate))
                {
                    return candidate;
                }
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
                if ((candidate.StartsWith("python", StringComparison.Ordinal) || File.Exists(candidate)) &&
                    IsPythonLauncherUsable(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException($"Python launcher not found for scripts directory: {pythonDir}");
    }

    private static void ApplyLocalPythonEnvironment(ProcessStartInfo startInfo, string pythonDir)
    {
        var localAppData = Path.Combine(pythonDir, ".localappdata");
        var xdgCache = Path.Combine(pythonDir, ".cache");
        Directory.CreateDirectory(localAppData);
        Directory.CreateDirectory(xdgCache);

        if (OperatingSystem.IsWindows())
            startInfo.Environment["LOCALAPPDATA"] = localAppData;
        else
            startInfo.Environment["XDG_CACHE_HOME"] = xdgCache;

        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment.TryGetValue("PYTHONPATH", out var pythonPath);
        startInfo.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(pythonPath)
            ? pythonDir
            : $"{pythonDir}{Path.PathSeparator}{pythonPath}";
    }

    private static bool IsPythonLauncherUsable(string candidate)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = candidate,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (process == null)
                return false;

            return process.WaitForExit(3000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
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
