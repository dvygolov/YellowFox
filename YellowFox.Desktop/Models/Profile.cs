using System;
using System.Text.Json;

namespace YellowFox.Desktop.Models;

public class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string? ProxyId { get; set; }
    public string? DolphinProfileId { get; set; }
    public FingerprintConfig FingerprintConfig { get; set; } = new();
}

public class FingerprintConfig
{
    public string Os { get; set; } = GetDefaultOs();
    public ScreenConfig Screen { get; set; } = new();
    
    private static string GetDefaultOs()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "macos";
        if (OperatingSystem.IsLinux()) return "linux";
        return "windows";
    }
    
    public string ToJson()
    {
        return JsonSerializer.Serialize(new
        {
            os = Os,
            screen = new
            {
                maxWidth = Screen.MaxWidth,
                maxHeight = Screen.MaxHeight
            }
        });
    }
}

public class ScreenConfig
{
    public int MaxWidth { get; set; } = 1920;
    public int MaxHeight { get; set; } = 1080;
}

public class ScreenPreset
{
    public string Name { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    
    public static readonly ScreenPreset[] Presets = new[]
    {
        new ScreenPreset { Name = "1920x1080 (Full HD)", Width = 1920, Height = 1080 },
        new ScreenPreset { Name = "1366x768 (Laptop)", Width = 1366, Height = 768 },
        new ScreenPreset { Name = "2560x1440 (2K)", Width = 2560, Height = 1440 },
        new ScreenPreset { Name = "3840x2160 (4K)", Width = 3840, Height = 2160 },
        new ScreenPreset { Name = "1536x864 (HD+)", Width = 1536, Height = 864 }
    };
    
    public override string ToString() => Name;
}
