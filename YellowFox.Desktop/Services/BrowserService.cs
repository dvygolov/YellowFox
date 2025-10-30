using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using YellowFox.Desktop.Models;

namespace YellowFox.Desktop.Services;

public class BrowserService
{
    private readonly DatabaseService _databaseService;
    private readonly SettingsService _settingsService;
    private readonly Dictionary<string, RunningInstance> _runningInstances = new();
    
    public BrowserService(DatabaseService databaseService, SettingsService settingsService)
    {
        _databaseService = databaseService;
        _settingsService = settingsService;
    }
    
    public bool IsRunning(string profileId) => _runningInstances.ContainsKey(profileId);
    
    public async Task<bool> StartProfileAsync(string profileId)
    {
        if (IsRunning(profileId))
            return false; // Already running
        
        var profile = _databaseService.GetProfile(profileId);
        if (profile == null)
            throw new InvalidOperationException($"Profile {profileId} not found");
        
        try
        {
            // Get profile data directory
            var userDataDir = _databaseService.GetProfileDataDirectory(profileId);
            
            // Create config JSON with user_data_dir
            var config = new
            {
                os = profile.FingerprintConfig.Os,
                screen = new
                {
                    maxWidth = profile.FingerprintConfig.Screen.MaxWidth,
                    maxHeight = profile.FingerprintConfig.Screen.MaxHeight
                },
                user_data_dir = userDataDir
            };
            
            var configJson = JsonSerializer.Serialize(config);
            
            // Create temp config file
            var tempConfigPath = Path.GetTempFileName();
            await File.WriteAllTextAsync(tempConfigPath, configJson);
            
            // Get paths from settings
            var pythonDir = _settingsService.GetPythonScriptsPath();
            
            // On Windows, use batch file that activates venv
            // On Linux/macOS, use shell script that activates venv
            string fileName;
            string arguments;
            
            if (OperatingSystem.IsWindows())
            {
                // Use batch file that handles venv activation
                var batchFile = Path.Combine(pythonDir, "start-server.bat");
                fileName = batchFile;
                arguments = $"\"{tempConfigPath}\"";
            }
            else
            {
                // Linux/macOS: use shell script that handles venv activation
                var shellScript = Path.Combine(pythonDir, "start-server.sh");
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
            
            var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Firefox.ConnectAsync(cdpUrl);
            
            // Store running instance
            _runningInstances[profileId] = new RunningInstance
            {
                Process = process,
                CdpUrl = cdpUrl,
                TempConfigPath = tempConfigPath,
                Playwright = playwright,
                Browser = browser
            };
            var page = await browser.NewPageAsync();
            await page.GotoAsync("https://yellowweb.top");
            
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error starting profile: {ex.Message}");
            return false;
        }
    }
    
    public async Task<bool> StopProfileAsync(string profileId)
    {
        if (!_runningInstances.TryGetValue(profileId, out var instance))
            return false; // Not running
        
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
            return true;
        }
        catch (Exception ex)
        {
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
    
    private class RunningInstance
    {
        public Process? Process { get; set; }
        public string CdpUrl { get; set; } = string.Empty;
        public string TempConfigPath { get; set; } = string.Empty;
        public IPlaywright? Playwright { get; set; }
        public IBrowser? Browser { get; set; }
    }
}
