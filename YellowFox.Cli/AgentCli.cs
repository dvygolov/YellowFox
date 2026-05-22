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
            var request = BuildRequest(args);
            var responseJson = await SendAsync(request);
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
            throw new AgentCliException("bad_request", "Usage: yellowfox <profile|proxy|dolphin> <command> [--key value]", 1);

        var scope = args[0].ToLowerInvariant();
        var action = args[1].ToLowerInvariant();
        var parsedArgs = ParseOptions(args.Skip(2).ToArray());

        var command = (scope, action) switch
        {
            ("profile", "list") => "profile.list",
            ("profile", "start") => "profile.start",
            ("profile", "stop") => "profile.stop",
            ("profile", "endpoint") => "profile.endpoint",
            ("profile", "open") => "profile.open",
            ("profile", "attach") => "profile.attach",
            ("profile", "pages") => "profile.pages",
            ("profile", "click") => "profile.click",
            ("profile", "update") => "profile.update",
            ("proxy", "list") => "proxy.list",
            ("proxy", "add") => "proxy.add",
            ("proxy", "update") => "proxy.update",
            ("proxy", "delete") => "proxy.delete",
            ("proxy", "test") => "proxy.test",
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
