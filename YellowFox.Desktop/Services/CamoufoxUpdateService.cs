using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace YellowFox.Desktop.Services;

public sealed class CamoufoxUpdateService
{
    private readonly SettingsService _settingsService;

    public CamoufoxUpdateService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task<CamoufoxPrerequisiteStatus> CheckPrerequisitesAsync(CancellationToken cancellationToken = default)
    {
        var pythonDir = ResolvePythonScriptsPath();
        var installerScript = Path.Combine(pythonDir, "install-camoufox-browser.py");
        if (!File.Exists(installerScript))
        {
            return CamoufoxPrerequisiteStatus.Failure(
                CamoufoxPrerequisiteState.InstallerMissing,
                $"Camoufox installer script not found: {installerScript}");
        }

        if (!TryResolvePythonLauncher(pythonDir, out var launcher))
        {
            return CamoufoxPrerequisiteStatus.Failure(
                CamoufoxPrerequisiteState.PythonMissing,
                "Python launcher was not found. Install Python or create the YellowFox python/venv environment.");
        }

        if (!await ArePythonDependenciesInstalledAsync(launcher, cancellationToken))
        {
            return CamoufoxPrerequisiteStatus.Failure(
                CamoufoxPrerequisiteState.PythonDependenciesMissing,
                "Python is available, but required Camoufox packages are missing.");
        }

        var installState = await CheckInstalledAsync(cancellationToken);
        if (installState == null || !installState.Installed)
        {
            return CamoufoxPrerequisiteStatus.Failure(
                CamoufoxPrerequisiteState.CamoufoxMissing,
                "Camoufox browser is not installed.");
        }

        return CamoufoxPrerequisiteStatus.Ready();
    }

    public async Task<CamoufoxUpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunInstallerScriptAsync("--check-update", TimeSpan.FromSeconds(60), cancellationToken);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
                return null;

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var payload = JsonSerializer.Deserialize<CamoufoxUpdatePayload>(result.StandardOutput.Trim(), options);
            if (payload?.Latest == null || payload.UpdateAvailable != true)
                return null;

            return new CamoufoxUpdateInfo(
                payload.Current?.Folder ?? "unknown",
                payload.Latest.Folder ?? "unknown",
                payload.Latest.Version ?? "unknown",
                payload.Latest.Build ?? "unknown",
                payload.Latest.AssetUpdatedAt);
        }
        catch
        {
            return null;
        }
    }

    public async Task<CamoufoxUpdateResult> InstallLatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunInstallerScriptAsync("--install-latest", TimeSpan.FromMinutes(30), cancellationToken);
            var message = string.IsNullOrWhiteSpace(result.StandardOutput)
                ? result.StandardError
                : result.StandardOutput;

            return result.ExitCode == 0
                ? CamoufoxUpdateResult.Success(message.Trim())
                : CamoufoxUpdateResult.Failure(message.Trim());
        }
        catch (Exception ex)
        {
            return CamoufoxUpdateResult.Failure(ex.Message);
        }
    }

    public async Task<CamoufoxUpdateResult> InstallPythonDependenciesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var pythonDir = ResolvePythonScriptsPath();
            if (!TryResolvePythonLauncher(pythonDir, out var launcher))
                return CamoufoxUpdateResult.Failure("Python launcher was not found.");

            var requirementsPath = Path.Combine(pythonDir, "requirements.txt");
            if (!File.Exists(requirementsPath))
                return CamoufoxUpdateResult.Failure($"requirements.txt not found: {requirementsPath}");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = launcher,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-m");
            process.StartInfo.ArgumentList.Add("pip");
            process.StartInfo.ArgumentList.Add("install");
            process.StartInfo.ArgumentList.Add("-r");
            process.StartInfo.ArgumentList.Add(requirementsPath);

            if (!process.Start())
                return CamoufoxUpdateResult.Failure("Failed to start pip.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var message = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return process.ExitCode == 0
                ? CamoufoxUpdateResult.Success(message.Trim())
                : CamoufoxUpdateResult.Failure(message.Trim());
        }
        catch (Exception ex)
        {
            return CamoufoxUpdateResult.Failure(ex.Message);
        }
    }

    private async Task<CamoufoxInstallState?> CheckInstalledAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunInstallerScriptAsync("--check-installed", TimeSpan.FromSeconds(20), cancellationToken);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
                return null;

            return JsonSerializer.Deserialize<CamoufoxInstallState>(
                result.StandardOutput.Trim(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private async Task<ScriptResult> RunInstallerScriptAsync(string argument, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pythonDir = ResolvePythonScriptsPath();
        var launcher = ResolvePythonLauncher(pythonDir);
        var script = Path.Combine(pythonDir, "install-camoufox-browser.py");
        if (!File.Exists(script))
            throw new FileNotFoundException($"Camoufox installer script not found: {script}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = launcher,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(script);
        process.StartInfo.ArgumentList.Add(argument);

        if (!process.Start())
            throw new InvalidOperationException("Failed to start Camoufox installer.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("Camoufox update check timed out.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ScriptResult(process.ExitCode, stdout, stderr);
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
        return TryResolvePythonLauncher(pythonDir, out var launcher)
            ? launcher
            : throw new FileNotFoundException($"Python launcher not found for scripts directory: {pythonDir}");
    }

    private static bool TryResolvePythonLauncher(string pythonDir, out string launcher)
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[]
            {
                Path.Combine(pythonDir, "venv", "Scripts", "python.exe"),
                Path.Combine(pythonDir, "venv", "Scripts", "python3.exe"),
                "python"
            }
            : new[]
            {
                Path.Combine(pythonDir, "venv", "bin", "python3"),
                Path.Combine(pythonDir, "venv", "bin", "python"),
                "python3",
                "python"
            };

        foreach (var candidate in candidates)
        {
            if ((candidate.StartsWith("python", StringComparison.OrdinalIgnoreCase) || File.Exists(candidate)) &&
                IsPythonLauncherUsable(candidate))
            {
                launcher = candidate;
                return true;
            }
        }

        launcher = string.Empty;
        return false;
    }

    private static async Task<bool> ArePythonDependenciesInstalledAsync(string launcher, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = launcher,
                Arguments = "-c \"import camoufox, requests, websocket\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (process == null)
                return false;

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
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

            return process != null && process.WaitForExit(3000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private sealed record ScriptResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class CamoufoxUpdatePayload
    {
        [JsonPropertyName("current")]
        public CamoufoxUpdatePayloadVersion? Current { get; set; }

        [JsonPropertyName("latest")]
        public CamoufoxUpdatePayloadVersion? Latest { get; set; }

        [JsonPropertyName("update_available")]
        public bool? UpdateAvailable { get; set; }
    }

    private sealed class CamoufoxUpdatePayloadVersion
    {
        [JsonPropertyName("folder")]
        public string? Folder { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("build")]
        public string? Build { get; set; }

        [JsonPropertyName("asset_updated_at")]
        public string? AssetUpdatedAt { get; set; }
    }

    private sealed class CamoufoxInstallState
    {
        [JsonPropertyName("installed")]
        public bool Installed { get; set; }
    }
}

public enum CamoufoxPrerequisiteState
{
    Ready,
    PythonMissing,
    PythonDependenciesMissing,
    InstallerMissing,
    CamoufoxMissing
}

public sealed record CamoufoxPrerequisiteStatus(
    CamoufoxPrerequisiteState State,
    string Message)
{
    public bool IsReady => State == CamoufoxPrerequisiteState.Ready;

    public static CamoufoxPrerequisiteStatus Ready() => new(CamoufoxPrerequisiteState.Ready, string.Empty);
    public static CamoufoxPrerequisiteStatus Failure(CamoufoxPrerequisiteState state, string message) => new(state, message);
}

public sealed record CamoufoxUpdateInfo(
    string CurrentFolder,
    string LatestFolder,
    string LatestVersion,
    string LatestBuild,
    string? AssetUpdatedAt);

public sealed record CamoufoxUpdateResult(bool IsSuccess, string Message)
{
    public static CamoufoxUpdateResult Success(string message) => new(true, message);
    public static CamoufoxUpdateResult Failure(string message) => new(false, message);
}
