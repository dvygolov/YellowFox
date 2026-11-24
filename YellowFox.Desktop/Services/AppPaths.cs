using System;
using System.IO;

namespace YellowFox.Desktop.Services;

/// <summary>
/// Runtime locations that depend on the platform. Windows keeps the portable layout next
/// to the executable. macOS (.app bundle) and Linux (AppImage, /opt, package managers)
/// install into read-only directories, so their user data lives in the user profile.
/// </summary>
public static class AppPaths
{
    /// <summary>Overrides the data directory, used by tests and portable setups.</summary>
    public const string DataDirectoryVariable = "YELLOWFOX_DATA_DIR";

    private static string? _dataDirectory;

    public static string DataDirectory => _dataDirectory ??= ResolveDataDirectory();

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    private static string ResolveDataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        if (OperatingSystem.IsWindows())
            return Path.Combine(AppContext.BaseDirectory, "data");

        var userRoot = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrWhiteSpace(userRoot))
            userRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        return Path.Combine(userRoot, "YellowFox");
    }
}
