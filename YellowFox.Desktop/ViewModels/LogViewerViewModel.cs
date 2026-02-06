using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace YellowFox.Desktop.ViewModels;

public partial class LogViewerViewModel : ViewModelBase
{
    private readonly string _logFilePath;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _logFilePathDisplay;

    public ObservableCollection<LogLineViewModel> Lines { get; } = new();

    public LogViewerViewModel(string logFilePath, string profileName)
    {
        _logFilePath = logFilePath;
        Title = $"Log Viewer - {profileName}";
        LogFilePathDisplay = logFilePath;
        LoadLines();
    }

    [RelayCommand]
    private void Refresh()
    {
        LoadLines();
    }

    [RelayCommand]
    private void Clear()
    {
        try
        {
            var directory = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(_logFilePath, string.Empty);
            LoadLines();
        }
        catch (Exception ex)
        {
            Lines.Clear();
            Lines.Add(new LogLineViewModel($"Failed to clear log: {ex.Message}", "ERROR"));
        }
    }

    private void LoadLines()
    {
        Lines.Clear();

        if (!File.Exists(_logFilePath))
        {
            Lines.Add(new LogLineViewModel("Log file not found.", "WARN"));
            return;
        }

        foreach (var line in File.ReadAllLines(_logFilePath))
        {
            var level = ParseLevel(line);
            Lines.Add(new LogLineViewModel(line, level));
        }
    }

    private static string ParseLevel(string line)
    {
        if (line.Contains("| ERROR ", StringComparison.OrdinalIgnoreCase))
            return "ERROR";
        if (line.Contains("| WARN ", StringComparison.OrdinalIgnoreCase))
            return "WARN";
        return "INFO";
    }
}

public class LogLineViewModel : ViewModelBase
{
    public string Text { get; }
    public string Level { get; }
    public string Foreground { get; }
    public string Background { get; }

    public LogLineViewModel(string text, string level)
    {
        Text = text;
        Level = level;

        if (level == "ERROR")
        {
            Foreground = "#FFC7C7";
            Background = "#3A1414";
            return;
        }

        if (level == "WARN")
        {
            Foreground = "#FFE8B3";
            Background = "#3A2A12";
            return;
        }

        Foreground = "#D4D4D4";
        Background = "#202326";
    }
}
