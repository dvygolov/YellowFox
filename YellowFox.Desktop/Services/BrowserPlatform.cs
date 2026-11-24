using System;

namespace YellowFox.Desktop.Services;

/// <summary>
/// Names and layout of the Camoufox runtime across platforms. Camoufox ships
/// <c>camoufox.exe</c> on Windows, <c>camoufox</c> inside <c>Camoufox.app</c> on macOS
/// and <c>camoufox-bin</c> on Linux.
/// </summary>
internal static class BrowserPlatform
{
    public const string DefaultProcessName = "camoufox";
    public const string LinuxProcessName = "camoufox-bin";

    /// <summary>Name reported by the platform process list for the current OS.</summary>
    public static string ProcessName => OperatingSystem.IsLinux() ? LinuxProcessName : DefaultProcessName;

    /// <summary>True for every Camoufox binary name, regardless of the host OS.</summary>
    public static bool IsKnownProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var normalized = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;

        return normalized.Equals(DefaultProcessName, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(LinuxProcessName, StringComparison.OrdinalIgnoreCase);
    }
}
