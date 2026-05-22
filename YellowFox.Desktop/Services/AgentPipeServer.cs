using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using YellowFox.Desktop.Models;

namespace YellowFox.Desktop.Services;

public sealed class AgentPipeServer : IAsyncDisposable
{
    public const string PipeName = "yellowfox-agent";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly DatabaseService _databaseService;
    private readonly BrowserService _browserService;
    private readonly ProxyValidatorService _proxyValidatorService;
    private readonly DolphinImportService _dolphinImportService;
    private readonly CancellationTokenSource _cts = new();
    private Task? _serverTask;

    public AgentPipeServer(DatabaseService databaseService, BrowserService browserService, ProxyValidatorService proxyValidatorService, DolphinImportService dolphinImportService)
    {
        _databaseService = databaseService;
        _browserService = browserService;
        _proxyValidatorService = proxyValidatorService;
        _dolphinImportService = dolphinImportService;
    }

    public void Start()
    {
        _serverTask ??= Task.Run(() => RunAsync(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        if (_serverTask != null)
        {
            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await pipe.WaitForConnectionAsync(cancellationToken);
            _ = Task.Run(() => HandleClientAsync(pipe, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
        try
        {
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };

            var line = await reader.ReadLineAsync(cancellationToken);
            var response = string.IsNullOrWhiteSpace(line)
                ? AgentResponse.Fail("bad_request", "Request body is empty.")
                : await ExecuteRawAsync(line);

            await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
        }
        catch
        {
            // The CLI receives a pipe-level failure if we cannot write a structured error.
        }
        }
    }

    private async Task<AgentResponse> ExecuteRawAsync(string rawJson)
    {
        AgentRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<AgentRequest>(rawJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            return AgentResponse.Fail("bad_json", ex.Message);
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Command))
            return AgentResponse.Fail("bad_request", "Command is required.");

        try
        {
            return request.Command.Trim().ToLowerInvariant() switch
            {
                "profile.list" => AgentResponse.Success(ListProfiles()),
                "profile.start" => await StartProfileAsync(GetRequired(request, "id")),
                "profile.stop" => await StopProfileAsync(GetRequired(request, "id")),
                "profile.endpoint" => GetProfileEndpoint(GetRequired(request, "id")),
                "profile.open" => await OpenProfileUrlAsync(GetRequired(request, "id"), GetRequired(request, "url")),
                "profile.attach" => await AttachProfileAsync(GetRequired(request, "id")),
                "profile.pages" => await GetProfilePagesAsync(GetRequired(request, "id"), !IsFalse(GetOptional(request, "text"))),
                "profile.click" => await ClickProfileTextAsync(GetRequired(request, "id"), GetRequired(request, "text")),
                "profile.update" => UpdateProfile(request),
                "proxy.list" => AgentResponse.Success(ListProxies()),
                "proxy.add" => AddProxy(request),
                "proxy.update" => UpdateProxy(request),
                "proxy.delete" => DeleteProxy(GetRequired(request, "id")),
                "proxy.test" => await TestProxyAsync(GetRequired(request, "id")),
                "dolphin.import" => await ImportDolphinAsync(request),
                _ => AgentResponse.Fail("unknown_command", $"Unknown command: {request.Command}")
            };
        }
        catch (ArgumentException ex)
        {
            return AgentResponse.Fail("bad_request", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return AgentResponse.Fail("not_found", ex.Message);
        }
        catch (Exception ex)
        {
            return AgentResponse.Fail("internal_error", ex.Message);
        }
    }

    private object ListProfiles()
    {
        return _databaseService.GetAllProfiles().Select(profile =>
        {
            var proxy = string.IsNullOrWhiteSpace(profile.ProxyId) ? null : _databaseService.GetProxy(profile.ProxyId);
            return new
            {
                id = profile.Id,
                name = profile.Name,
                notes = profile.Notes,
                proxyId = profile.ProxyId,
                proxyName = proxy?.Name,
                os = profile.FingerprintConfig.Os,
                screen = new
                {
                    width = profile.FingerprintConfig.Screen.MaxWidth,
                    height = profile.FingerprintConfig.Screen.MaxHeight
                },
                running = _browserService.IsRunning(profile.Id),
                endpoint = _browserService.GetEndpoint(profile.Id)
            };
        }).ToList();
    }

    private async Task<AgentResponse> StartProfileAsync(string idOrName)
    {
        var profile = ResolveProfile(idOrName);
        if (!_browserService.IsRunning(profile.Id))
        {
            var started = await _browserService.StartProfileAsync(profile.Id);
            if (!started)
                return AgentResponse.Fail("start_failed", $"Failed to start profile: {profile.Name}");
        }

        return AgentResponse.Success(ProfileRuntimeData(profile));
    }

    private async Task<AgentResponse> StopProfileAsync(string idOrName)
    {
        var profile = ResolveProfile(idOrName);
        if (_browserService.IsRunning(profile.Id))
            await _browserService.StopProfileAsync(profile.Id);

        return AgentResponse.Success(ProfileRuntimeData(profile));
    }

    private AgentResponse GetProfileEndpoint(string idOrName)
    {
        var profile = ResolveProfile(idOrName);
        return AgentResponse.Success(ProfileRuntimeData(profile));
    }

    private async Task<AgentResponse> OpenProfileUrlAsync(string idOrName, string url)
    {
        var profile = ResolveProfile(idOrName);
        var result = await _browserService.OpenUrlAsync(profile.Id, url);
        if (!result.Success)
            return AgentResponse.Fail("open_failed", result.Message);

        return AgentResponse.Success(new
        {
            id = profile.Id,
            name = profile.Name,
            running = _browserService.IsRunning(profile.Id),
            endpoint = _browserService.GetEndpoint(profile.Id),
            url = result.Url,
            title = result.Title
        });
    }

    private async Task<AgentResponse> AttachProfileAsync(string idOrName)
    {
        var profile = ResolveProfile(idOrName);
        if (!_browserService.IsRunning(profile.Id))
        {
            var started = await _browserService.StartProfileAsync(profile.Id);
            if (!started)
                return AgentResponse.Fail("start_failed", $"Failed to start profile: {profile.Name}");
        }

        var storageStatePath = await _browserService.CreateAgentStorageStateFileAsync(profile.Id);
        var contextFingerprint = _browserService.GetAgentContextFingerprintFiles(profile.Id);
        return AgentResponse.Success(new
        {
            id = profile.Id,
            name = profile.Name,
            running = _browserService.IsRunning(profile.Id),
            endpoint = _browserService.GetEndpoint(profile.Id),
            storageStatePath,
            contextOptionsPath = contextFingerprint.OptionsPath,
            initScriptPath = contextFingerprint.InitScriptPath
        });
    }

    private async Task<AgentResponse> GetProfilePagesAsync(string idOrName, bool includeText)
    {
        var profile = ResolveProfile(idOrName);
        var pages = await _browserService.GetOpenPagesAsync(profile.Id, includeText);
        return AgentResponse.Success(new
        {
            id = profile.Id,
            name = profile.Name,
            running = _browserService.IsRunning(profile.Id),
            endpoint = _browserService.GetEndpoint(profile.Id),
            pages
        });
    }

    private async Task<AgentResponse> ClickProfileTextAsync(string idOrName, string text)
    {
        var profile = ResolveProfile(idOrName);
        var result = await _browserService.ClickTextAsync(profile.Id, text);
        if (!result.Success)
            return AgentResponse.Fail("click_failed", result.Message);

        return AgentResponse.Success(new
        {
            id = profile.Id,
            name = profile.Name,
            running = _browserService.IsRunning(profile.Id),
            endpoint = _browserService.GetEndpoint(profile.Id),
            url = result.Url,
            title = result.Title
        });
    }

    private object ProfileRuntimeData(Profile profile)
    {
        return new
        {
            id = profile.Id,
            name = profile.Name,
            running = _browserService.IsRunning(profile.Id),
            endpoint = _browserService.GetEndpoint(profile.Id)
        };
    }

    private AgentResponse UpdateProfile(AgentRequest request)
    {
        var profile = ResolveProfile(GetRequired(request, "id"));
        if (TryGet(request, "proxy-id", out var proxyId) || TryGet(request, "proxyId", out proxyId))
        {
            profile.ProxyId = ResolveProxyIdOrNone(proxyId);
        }

        _databaseService.UpdateProfile(profile);
        return AgentResponse.Success(ProfileRuntimeData(profile));
    }

    private object ListProxies()
    {
        return _databaseService.GetAllProxies().Select(ProxyData).ToList();
    }

    private AgentResponse AddProxy(AgentRequest request)
    {
        var proxy = new Proxy
        {
            Name = GetRequired(request, "name").Trim(),
            Type = Proxy.NormalizeType(GetRequired(request, "type")),
            Host = GetRequired(request, "host").Trim(),
            Port = GetRequiredInt(request, "port"),
            Username = GetOptional(request, "username"),
            Password = GetOptional(request, "password"),
            IsEnabled = true
        };

        _databaseService.CreateProxy(proxy);
        return AgentResponse.Success(ProxyData(proxy));
    }

    private AgentResponse UpdateProxy(AgentRequest request)
    {
        var proxy = ResolveProxy(GetRequired(request, "id"));

        if (TryGet(request, "name", out var name))
            proxy.Name = name.Trim();
        if (TryGet(request, "type", out var type))
            proxy.Type = Proxy.NormalizeType(type);
        if (TryGet(request, "host", out var host))
            proxy.Host = host.Trim();
        if (TryGet(request, "port", out var port))
            proxy.Port = ParsePort(port);
        if (TryGet(request, "username", out var username))
            proxy.Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        if (TryGet(request, "password", out var password))
            proxy.Password = string.IsNullOrWhiteSpace(password) ? null : password;

        _databaseService.UpdateProxy(proxy);
        return AgentResponse.Success(ProxyData(proxy));
    }

    private AgentResponse DeleteProxy(string idOrName)
    {
        var proxy = ResolveProxy(idOrName);
        _databaseService.DeleteProxy(proxy.Id);
        return AgentResponse.Success(new { id = proxy.Id, name = proxy.Name, deleted = true });
    }

    private async Task<AgentResponse> TestProxyAsync(string idOrName)
    {
        var proxy = ResolveProxy(idOrName);
        var result = await _proxyValidatorService.ValidateAsync(proxy);
        return AgentResponse.Success(new
        {
            id = proxy.Id,
            name = proxy.Name,
            type = proxy.Type,
            success = result.IsSuccess,
            externalIp = result.ExternalIp,
            latencyMs = result.LatencyMs,
            error = result.Error
        });
    }

    private async Task<AgentResponse> ImportDolphinAsync(AgentRequest request)
    {
        var options = new DolphinImportOptions
        {
            CloudBase = GetOptional(request, "cloud-base") ?? GetOptional(request, "cloudBase") ?? "https://anty-api.com",
            LocalBase = GetOptional(request, "local-base") ?? GetOptional(request, "localBase") ?? "http://127.0.0.1:3001/v1.0",
            TokenFilePath = GetOptional(request, "token-file") ?? GetOptional(request, "tokenFile"),
            LocalSessionTokenFilePath = GetOptional(request, "local-session-token-file") ?? GetOptional(request, "localSessionTokenFile"),
            ImportCookies = !IsFalse(GetOptional(request, "cookies"))
        };

        var result = await _dolphinImportService.ImportAsync(options);
        return AgentResponse.Success(result);
    }

    private static bool IsFalse(string? value)
    {
        return string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "no", StringComparison.OrdinalIgnoreCase);
    }

    private object ProxyData(Proxy proxy)
    {
        return new
        {
            id = proxy.Id,
            name = proxy.Name,
            type = Proxy.NormalizeType(proxy.Type),
            host = proxy.Host,
            port = proxy.Port,
            username = proxy.Username,
            hasPassword = !string.IsNullOrEmpty(proxy.Password),
            enabled = proxy.IsEnabled
        };
    }

    private Profile ResolveProfile(string idOrName)
    {
        var direct = _databaseService.GetProfile(idOrName);
        if (direct != null)
            return direct;

        var matches = _databaseService.GetAllProfiles()
            .Where(p => string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Profile not found: {idOrName}"),
            _ => throw new ArgumentException($"Profile name is ambiguous: {idOrName}")
        };
    }

    private Proxy ResolveProxy(string idOrName)
    {
        var direct = _databaseService.GetProxy(idOrName);
        if (direct != null)
            return direct;

        var matches = _databaseService.GetAllProxies()
            .Where(p => string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Proxy not found: {idOrName}"),
            _ => throw new ArgumentException($"Proxy name is ambiguous: {idOrName}")
        };
    }

    private string? ResolveProxyIdOrNone(string value)
    {
        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
            return null;

        return ResolveProxy(value).Id;
    }

    private static string GetRequired(AgentRequest request, string key)
    {
        if (!TryGet(request, key, out var value))
            throw new ArgumentException($"Missing required argument: {key}");

        return value;
    }

    private static string? GetOptional(AgentRequest request, string key)
    {
        return TryGet(request, key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static int GetRequiredInt(AgentRequest request, string key)
    {
        return ParsePort(GetRequired(request, key));
    }

    private static int ParsePort(string value)
    {
        if (!int.TryParse(value, out var port) || port <= 0 || port > 65535)
            throw new ArgumentException("Port must be between 1 and 65535.");

        return port;
    }

    private static bool TryGet(AgentRequest request, string key, out string value)
    {
        value = string.Empty;
        if (request.Args == null)
            return false;

        if (!request.Args.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return false;

        value = raw;
        return true;
    }
}

public sealed class AgentRequest
{
    public string Command { get; set; } = string.Empty;
    public Dictionary<string, string?> Args { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AgentResponse
{
    public bool Ok { get; init; }
    public object? Data { get; init; }
    public AgentError? Error { get; init; }

    public static AgentResponse Success(object? data)
    {
        return new AgentResponse { Ok = true, Data = data };
    }

    public static AgentResponse Fail(string code, string message)
    {
        return new AgentResponse
        {
            Ok = false,
            Error = new AgentError { Code = code, Message = message }
        };
    }
}

public sealed class AgentError
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
