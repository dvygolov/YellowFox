using System;
using System.IO;
using System.Text.Json;
using YellowFox.Desktop.Models;

namespace YellowFox.Desktop.Services;

public class SettingsService
{
    private readonly string _settingsPath;
    private AppSettings? _settings;
    
    public SettingsService()
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        _settingsPath = Path.Combine(appDir, "settings.json");
    }
    
    public AppSettings GetSettings()
    {
        if (_settings != null)
            return _settings;
        
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            else
            {
                // Create default settings file
                _settings = new AppSettings();
                SaveSettings(_settings);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading settings: {ex.Message}");
            _settings = new AppSettings();
        }
        
        return _settings;
    }
    
    public void SaveSettings(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            File.WriteAllText(_settingsPath, json);
            _settings = settings;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving settings: {ex.Message}");
        }
    }
    
    public string GetPythonScriptsPath()
    {
        var settings = GetSettings();
        var pythonPath = settings.PythonScriptsPath;
        
        // If path is relative, make it relative to app directory
        if (!Path.IsPathRooted(pythonPath))
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            pythonPath = Path.Combine(appDir, pythonPath);
        }
        
        return pythonPath;
    }
}
