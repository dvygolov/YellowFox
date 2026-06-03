using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YellowFox.Cli;

public static class AgentCli
{
    public const string PipeName = "yellowfox-agent";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        try
        {
            if (TryHandleDesktopCommand(args, out var desktopJson))
            {
                await output.WriteLineAsync(desktopJson);
                return IsOk(desktopJson) ? 0 : 1;
            }

            var request = BuildRequest(args);
            var responseJson = await SendWithDesktopAutostartAsync(request);
            await output.WriteLineAsync(responseJson);
            return IsOk(responseJson) ? 0 : 1;
        }
        catch (AgentCliException ex)
        {
            await output.WriteLineAsync(CreateFailureJson(ex.Code, ex.Message));
            return ex.ExitCode;
        }
        catch (TimeoutException)
        {
            await output.WriteLineAsync(CreateFailureJson("desktop_unavailable", "YellowFox Desktop is not running or did not accept the agent connection."));
            return 2;
        }
        catch (OperationCanceledException)
        {
            await output.WriteLineAsync(CreateFailureJson("desktop_unavailable", "YellowFox Desktop is not running or did not accept the agent connection."));
            return 2;
        }
        catch (UnauthorizedAccessException ex)
        {
            await output.WriteLineAsync(CreateFailureJson("pipe_access_denied", ex.Message));
            return 2;
        }
        catch (IOException ex)
        {
            await output.WriteLineAsync(CreateFailureJson("pipe_error", ex.Message));
            return 2;
        }
    }

    public static AgentCliRequest BuildRequest(string[] rawArgs)
    {
        var args = rawArgs
            .Where(arg => !string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (args.Length < 2)
            throw new AgentCliException("bad_request", "Usage: yellowfox <desktop|profile|proxy|extension|bookmark|dolphin> <command> [--key value]", 1);

        var scope = args[0].ToLowerInvariant();
        var action = args[1].ToLowerInvariant();
        var parsedArgs = ParseOptions(args.Skip(2).ToArray());

        var command = (scope, action) switch
        {
            ("profile", "list") => "profile.list",
            ("profile", "create") => "profile.create",
            ("profile", "update") => "profile.update",
            ("profile", "delete") => "profile.delete",
            ("profile", "clone") => "profile.clone",
            ("profile", "start") => "profile.start",
            ("profile", "stop") => "profile.stop",
            ("profile", "endpoint") => "profile.endpoint",
            ("profile", "open") => "profile.open",
            ("profile", "attach") => "profile.attach",
            ("profile", "pages") => "profile.pages",
            ("profile", "click") => "profile.click",
            ("profile", "import-cookies") => "profile.importCookies",
            ("profile", "export-cookies") => "profile.exportCookies",
            ("profile", "log") => "profile.log",
            ("proxy", "list") => "proxy.list",
            ("proxy", "add") => "proxy.add",
            ("proxy", "update") => "proxy.update",
            ("proxy", "delete") => "proxy.delete",
            ("proxy", "test") => "proxy.test",
            ("proxy", "change-ip") => "proxy.changeIp",
            ("extension", "list") => "extension.list",
            ("extension", "add") => "extension.add",
            ("extension", "import-url") => "extension.importUrl",
            ("extension", "import-archive") => "extension.importArchive",
            ("extension", "update") => "extension.update",
            ("extension", "toggle") => "extension.toggle",
            ("extension", "delete") => "extension.delete",
            ("bookmark", "list") => "bookmark.list",
            ("bookmark", "add") => "bookmark.add",
            ("bookmark", "add-folder") => "bookmark.addFolder",
            ("bookmark", "update") => "bookmark.update",
            ("bookmark", "delete") => "bookmark.delete",
            ("dolphin", "import") => "dolphin.import",
            _ => throw new AgentCliException("unknown_command", $"Unknown command: {scope} {action}", 1)
        };

        return new AgentCliRequest(command, parsedArgs);
    }

    public static string CreateFailureJson(string code, string message)
    {
        return JsonSerializer.Serialize(new
        {
            ok = false,
            error = new
            {
                code,
                message
            }
        }, JsonOptions);
    }

    private static async Task<string> SendAsync(AgentCliRequest request)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await pipe.ConnectAsync(cts.Token);

        using var reader = new StreamReader(pipe);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };

        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
        var response = await reader.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(response))
            throw new IOException("Desktop returned an empty agent response.");

        return response;
    }

    private static async Task<string> SendWithDesktopAutostartAsync(AgentCliRequest request)
    {
        try
        {
            return await SendAsync(request);
        }
        catch (Exception ex) when (IsDesktopUnavailableException(ex))
        {
            var start = StartDesktop();
            if (!start.Success)
                throw new AgentCliException("desktop_start_failed", start.Message, 2);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            Exception? lastError = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    await Task.Delay(500);
                    return await SendAsync(request);
                }
                catch (Exception retryEx) when (IsDesktopUnavailableException(retryEx))
                {
                    lastError = retryEx;
                }
            }

            throw lastError ?? ex;
        }
    }

    private static bool IsDesktopUnavailableException(Exception ex)
    {
        return ex is TimeoutException or OperationCanceledException or IOException;
    }

    private static bool TryHandleDesktopCommand(string[] rawArgs, out string json)
    {
        json = string.Empty;
        var args = rawArgs
            .Where(arg => !string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (args.Length < 2 || !string.Equals(args[0], "desktop", StringComparison.OrdinalIgnoreCase))
            return false;

        var action = args[1].ToLowerInvariant();
        json = action switch
        {
            "status" => CreateSuccessJson(DesktopStatus()),
            "start" => DesktopStartJson(),
            _ => throw new AgentCliException("unknown_command", $"Unknown command: desktop {action}", 1)
        };
        return true;
    }

    private static string DesktopStartJson()
    {
        var start = StartDesktop();
        return start.Success
            ? CreateSuccessJson(new { running = true, started = start.Started, path = start.Path })
            : CreateFailureJson("desktop_start_failed", start.Message);
    }

    private static object DesktopStatus()
    {
        var processes = Process.GetProcessesByName("YellowFox.Desktop");
        return new
        {
            running = processes.Length > 0,
            count = processes.Length,
            processIds = processes.Select(process => process.Id).ToArray()
        };
    }

    private static DesktopStartResult StartDesktop()
    {
        var running = Process.GetProcessesByName("YellowFox.Desktop");
        if (running.Length > 0)
            return new DesktopStartResult(true, false, null, "YellowFox Desktop is already running.");

        var path = ResolveDesktopExecutablePath();
        if (path == null)
            return new DesktopStartResult(false, false, null, "YellowFox.Desktop.exe was not found. Build the desktop project first.");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
                UseShellExecute = true
            });
            return new DesktopStartResult(true, true, path, "YellowFox Desktop started.");
        }
        catch (Exception ex)
        {
            return new DesktopStartResult(false, false, path, ex.Message);
        }
    }

    private static string? ResolveDesktopExecutablePath()
    {
        var candidates = new List<string>();
        var envPath = Environment.GetEnvironmentVariable("YELLOWFOX_DESKTOP_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
            candidates.Add(envPath);

        candidates.Add(Path.Combine(Environment.CurrentDirectory, "YellowFox.Desktop", "bin", "Debug", "net8.0", "YellowFox.Desktop.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "YellowFox.Desktop.exe"));
        candidates.Add(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "YellowFox.Desktop", "bin", "Debug", "net8.0", "YellowFox.Desktop.exe")));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string CreateSuccessJson(object data)
    {
        return JsonSerializer.Serialize(new
        {
            ok = true,
            data
        }, JsonOptions);
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                throw new AgentCliException("bad_request", $"Unexpected argument: {arg}", 1);

            var key = arg[2..];
            if (string.IsNullOrWhiteSpace(key))
                throw new AgentCliException("bad_request", "Empty option name.", 1);

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new AgentCliException("bad_request", $"Missing value for --{key}", 1);

            result[key] = args[index + 1];
            index++;
        }

        return result;
    }

    private static bool IsOk(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record DesktopStartResult(bool Success, bool Started, string? Path, string Message);

public sealed record AgentCliRequest(string Command, Dictionary<string, string?> Args);

public sealed class AgentCliException : Exception
{
    public string Code { get; }
    public int ExitCode { get; }

    public AgentCliException(string code, string message, int exitCode)
        : base(message)
    {
        Code = code;
        ExitCode = exitCode;
    }
}
