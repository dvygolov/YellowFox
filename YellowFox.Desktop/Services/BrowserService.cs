using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using YellowFox.Desktop.Models;

namespace YellowFox.Desktop.Services;

public class BrowserService
{
    private readonly DatabaseService _databaseService;
    private readonly Dictionary<string, RunningInstance> _runningInstances = new();
    
    public BrowserService(DatabaseService databaseService)
    {
        _databaseService = databaseService;
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
            
            // Get paths
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var pythonScript = Path.Combine(appDir, "python", "camoufox-server.py");
            
            // Determine Python command
            var pythonCommand = OperatingSystem.IsWindows() ? "python" : "python3";
            
            // Start Python process
            var processStartInfo = new ProcessStartInfo
            {
                FileName = pythonCommand,
                Arguments = $"\"{pythonScript}\" \"{tempConfigPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            var process = Process.Start(processStartInfo);
            if (process == null)
                throw new InvalidOperationException("Failed to start Python process");
            
            // Read CDP URL from stdout
            var cdpUrl = await process.StandardOutput.ReadLineAsync();
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
            
            // Connect Playwright to CDP endpoint
            var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectOverCDPAsync(cdpUrl);
            
            // Store running instance
            _runningInstances[profileId] = new RunningInstance
            {
                Process = process,
                CdpUrl = cdpUrl,
                TempConfigPath = tempConfigPath,
                Playwright = playwright,
                Browser = browser
            };
            
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
