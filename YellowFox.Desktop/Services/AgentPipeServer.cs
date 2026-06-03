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
    private readonly ExtensionStorageService _extensionStorageService;
    private readonly ProxyIpRotationService _proxyIpRotationService;
    private readonly CancellationTokenSource _cts = new();
    private Task? _serverTask;

    public AgentPipeServer(DatabaseService databaseService, BrowserService browserService, ProxyValidatorService proxyValidatorService, DolphinImportService dolphinImportService, ExtensionStorageService extensionStorageService, ProxyIpRotationService proxyIpRotationService)
    {
        _databaseService = databaseService;
        _browserService = browserService;
        _proxyValidatorService = proxyValidatorService;
        _dolphinImportService = dolphinImportService;
        _extensionStorageService = extensionStorageService;
        _proxyIpRotationService = proxyIpRotationService;
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
                "profile.create" => CreateProfile(request),
                "profile.start" => await StartProfileAsync(GetRequired(request, "id")),
                "profile.stop" => await StopProfileAsync(GetRequired(request, "id")),
                "profile.endpoint" => GetProfileEndpoint(GetRequired(request, "id")),
                "profile.open" => await OpenProfileUrlAsync(GetRequired(request, "id"), GetRequired(request, "url")),
                "profile.attach" => await AttachProfileAsync(GetRequired(request, "id")),
                "profile.pages" => await GetProfilePagesAsync(GetRequired(request, "id"), !IsFalse(GetOptional(request, "text"))),
                "profile.click" => await ClickProfileTextAsync(GetRequired(request, "id"), GetRequired(request, "text")),
                "profile.update" => UpdateProfile(request),
                "profile.delete" => await DeleteProfileAsync(GetRequired(request, "id")),
                "profile.clone" => CloneProfile(request),
                "profile.importcookies" => await ImportProfileCookiesAsync(request),
                "profile.exportcookies" => await ExportProfileCookiesAsync(request),
                "profile.log" => GetProfileLog(GetRequired(request, "id")),
                "proxy.list" => AgentResponse.Success(ListProxies()),
                "proxy.add" => AddProxy(request),
                "proxy.update" => UpdateProxy(request),
                "proxy.delete" => DeleteProxy(GetRequired(request, "id")),
                "proxy.test" => await TestProxyAsync(GetRequired(request, "id")),
                "proxy.changeip" => await ChangeProxyIpAsync(GetRequired(request, "id")),
                "extension.list" => AgentResponse.Success(ListExtensions()),
                "extension.add" => AddExtension(request),
                "extension.importurl" => await ImportExtensionUrlAsync(request),
                "extension.importarchive" => ImportExtensionArchive(request),
                "extension.update" => UpdateExtension(request),
                "extension.toggle" => ToggleExtension(request),
                "extension.delete" => DeleteExtension(GetRequired(request, "id")),
                "bookmark.list" => AgentResponse.Success(ListBookmarks()),
                "bookmark.add" => AddBookmark(request, isFolder: false),
                "bookmark.addfolder" => AddBookmark(request, isFolder: true),
                "bookmark.update" => UpdateBookmark(request),
                "bookmark.delete" => DeleteBookmark(GetRequired(request, "id")),
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

    private AgentResponse CreateProfile(AgentRequest request)
    {
        var profile = new Profile
        {
            Name = GetRequired(request, "name").Trim(),
            Notes = GetOptional(request, "notes"),
            ProxyId = TryGet(request, "proxy-id", out var proxyId) || TryGet(request, "proxyId", out proxyId)
                ? ResolveProxyIdOrNone(proxyId)
                : null,
            FingerprintConfig = BuildFingerprintConfig(request)
        };

        _databaseService.CreateProfile(profile);
        return AgentResponse.Success(ProfileData(profile));
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

    private object ProfileData(Profile profile)
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
    }

    private AgentResponse UpdateProfile(AgentRequest request)
    {
        var profile = ResolveProfile(GetRequired(request, "id"));
        if (TryGet(request, "name", out var name))
            profile.Name = name.Trim();
        if (TryGet(request, "notes", out var notes))
            profile.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes;
        if (TryGet(request, "proxy-id", out var proxyId) || TryGet(request, "proxyId", out proxyId))
        {
            profile.ProxyId = ResolveProxyIdOrNone(proxyId);
        }
        if (TryGet(request, "os", out var os))
            profile.FingerprintConfig.Os = NormalizeOs(os);
        if (TryGet(request, "width", out var width))
            profile.FingerprintConfig.Screen.MaxWidth = ParsePositiveInt(width, "width");
        if (TryGet(request, "height", out var height))
            profile.FingerprintConfig.Screen.MaxHeight = ParsePositiveInt(height, "height");

        _databaseService.UpdateProfile(profile);
        return AgentResponse.Success(ProfileData(profile));
    }

    private async Task<AgentResponse> DeleteProfileAsync(string idOrName)
    {
        var profile = ResolveProfile(idOrName);
        if (_browserService.IsRunning(profile.Id))
            await _browserService.StopProfileAsync(profile.Id);

        _databaseService.DeleteProfile(profile.Id);
        return AgentResponse.Success(new { id = profile.Id, name = profile.Name, deleted = true });
    }

    private AgentResponse CloneProfile(AgentRequest request)
    {
        var source = ResolveProfile(GetRequired(request, "id"));
        var clone = _databaseService.CloneProfile(source.Id, GetRequired(request, "name").Trim());
        return AgentResponse.Success(ProfileData(clone));
    }

    private async Task<AgentResponse> ImportProfileCookiesAsync(AgentRequest request)
    {
        var profile = ResolveProfile(GetRequired(request, "id"));
        var domain = GetOptional(request, "domain");
        var result = TryGet(request, "file", out var file)
            ? await _browserService.ImportCookiesAsync(profile.Id, file)
            : await _browserService.ImportCookiesFromTextAsync(profile.Id, GetRequired(request, "text"), domain, "CLI");

        return result.Success
            ? AgentResponse.Success(new { id = profile.Id, name = profile.Name, message = result.Message })
            : AgentResponse.Fail("cookie_import_failed", result.Message);
    }

    private async Task<AgentResponse> ExportProfileCookiesAsync(AgentRequest request)
    {
        var profile = ResolveProfile(GetRequired(request, "id"));
        var result = await _browserService.ExportCookiesAsync(profile.Id, GetRequired(request, "file"));
        return result.Success
            ? AgentResponse.Success(new { id = profile.Id, name = profile.Name, message = result.Message })
            : AgentResponse.Fail("cookie_export_failed", result.Message);
    }

    private AgentResponse GetProfileLog(string idOrName)
    {
        var profile = ResolveProfile(idOrName);
        var path = _browserService.GetOrCreateProfileLogPath(profile.Id);
        return AgentResponse.Success(new
        {
            id = profile.Id,
            name = profile.Name,
            logPath = path
        });
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
            IpChangeUrl = GetOptional(request, "ip-change-url") ?? GetOptional(request, "ipChangeUrl"),
            IsEnabled = !IsFalse(GetOptional(request, "enabled"))
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
        if (TryGet(request, "ip-change-url", out var ipChangeUrl) || TryGet(request, "ipChangeUrl", out ipChangeUrl))
            proxy.IpChangeUrl = string.IsNullOrWhiteSpace(ipChangeUrl) ? null : ipChangeUrl.Trim();
        if (TryGet(request, "enabled", out var enabled))
            proxy.IsEnabled = !IsFalse(enabled);

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
            countryCode = result.CountryCode,
            countryName = result.CountryName,
            latencyMs = result.LatencyMs,
            error = result.Error
        });
    }

    private async Task<AgentResponse> ChangeProxyIpAsync(string idOrName)
    {
        var proxy = ResolveProxy(idOrName);
        var rotation = await _proxyIpRotationService.ChangeIpAsync(proxy);
        if (!rotation.Success)
            return AgentResponse.Fail("ip_change_failed", rotation.Message);

        var validation = await _proxyValidatorService.ValidateAsync(proxy);
        return AgentResponse.Success(new
        {
            id = proxy.Id,
            name = proxy.Name,
            message = rotation.Message,
            validation = new
            {
                success = validation.IsSuccess,
                externalIp = validation.ExternalIp,
                countryCode = validation.CountryCode,
                countryName = validation.CountryName,
                latencyMs = validation.LatencyMs,
                error = validation.Error
            }
        });
    }

    private object ListExtensions()
    {
        return _databaseService.GetAllExtensions().Select(ExtensionData).ToList();
    }

    private AgentResponse AddExtension(AgentRequest request)
    {
        var extension = new ExtensionItem
        {
            Name = GetRequired(request, "name").Trim(),
            Path = GetRequired(request, "path").Trim(),
            IsEnabled = !IsFalse(GetOptional(request, "enabled"))
        };

        if (_extensionStorageService.IsArchivePath(extension.Path))
            extension.Path = _extensionStorageService.StoreArchive(extension.Path, extension.Id);
        else if (!BrowserService.IsExtensionPathUsable(extension.Path))
            return AgentResponse.Fail("bad_request", "Path must point to an unpacked extension folder with manifest.json, or to a .zip/.xpi archive.");

        _databaseService.CreateExtension(extension);
        return AgentResponse.Success(ExtensionData(extension));
    }

    private async Task<AgentResponse> ImportExtensionUrlAsync(AgentRequest request)
    {
        var extension = await _extensionStorageService.ImportFromUrlAsync(GetRequired(request, "url"));
        return AgentResponse.Success(ExtensionData(extension));
    }

    private AgentResponse ImportExtensionArchive(AgentRequest request)
    {
        var path = GetRequired(request, "path");
        var name = GetOptional(request, "name") ?? Path.GetFileNameWithoutExtension(path);
        var extension = _extensionStorageService.ImportArchive(path, name);
        return AgentResponse.Success(ExtensionData(extension));
    }

    private AgentResponse UpdateExtension(AgentRequest request)
    {
        var extension = ResolveExtension(GetRequired(request, "id"));
        if (TryGet(request, "name", out var name))
            extension.Name = name.Trim();
        if (TryGet(request, "path", out var path))
        {
            path = path.Trim();
            extension.Path = _extensionStorageService.IsArchivePath(path)
                ? _extensionStorageService.StoreArchive(path, extension.Id)
                : path;
        }
        if (TryGet(request, "enabled", out var enabled))
            extension.IsEnabled = !IsFalse(enabled);

        _databaseService.UpdateExtension(extension);
        return AgentResponse.Success(ExtensionData(extension));
    }

    private AgentResponse ToggleExtension(AgentRequest request)
    {
        var extension = ResolveExtension(GetRequired(request, "id"));
        extension.IsEnabled = TryGet(request, "enabled", out var enabled)
            ? !IsFalse(enabled)
            : !extension.IsEnabled;
        _databaseService.UpdateExtension(extension);
        return AgentResponse.Success(ExtensionData(extension));
    }

    private AgentResponse DeleteExtension(string idOrName)
    {
        var extension = ResolveExtension(idOrName);
        _extensionStorageService.DeleteExtensionWithFiles(extension);
        return AgentResponse.Success(new { id = extension.Id, name = extension.Name, deleted = true });
    }

    private object ListBookmarks()
    {
        return _databaseService.GetAllBookmarks().Select(BookmarkData).ToList();
    }

    private AgentResponse AddBookmark(AgentRequest request, bool isFolder)
    {
        var bookmark = new BookmarkItem
        {
            Title = GetRequired(request, "title").Trim(),
            Url = isFolder ? string.Empty : GetRequired(request, "url").Trim(),
            ParentId = GetOptional(request, "parent-id") ?? GetOptional(request, "parentId"),
            IsFolder = isFolder
        };

        if (TryGet(request, "folder", out var folder))
            bookmark.Folder = folder;
        if (TryGet(request, "sort-order", out var sortOrder) || TryGet(request, "sortOrder", out sortOrder))
            bookmark.SortOrder = ParsePositiveOrZeroInt(sortOrder, "sort-order");

        _databaseService.CreateBookmark(bookmark);
        return AgentResponse.Success(BookmarkData(bookmark));
    }

    private AgentResponse UpdateBookmark(AgentRequest request)
    {
        var bookmark = ResolveBookmark(GetRequired(request, "id"));
        if (TryGet(request, "title", out var title))
            bookmark.Title = title.Trim();
        if (TryGet(request, "url", out var url))
            bookmark.Url = url.Trim();
        if (TryGet(request, "folder", out var folder))
            bookmark.Folder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
        if (TryGet(request, "parent-id", out var parentId) || TryGet(request, "parentId", out parentId))
            bookmark.ParentId = string.IsNullOrWhiteSpace(parentId) || string.Equals(parentId, "none", StringComparison.OrdinalIgnoreCase) ? null : parentId;
        if (TryGet(request, "sort-order", out var sortOrder) || TryGet(request, "sortOrder", out sortOrder))
            bookmark.SortOrder = ParsePositiveOrZeroInt(sortOrder, "sort-order");

        _databaseService.UpdateBookmark(bookmark);
        return AgentResponse.Success(BookmarkData(bookmark));
    }

    private AgentResponse DeleteBookmark(string id)
    {
        var bookmark = ResolveBookmark(id);
        _databaseService.DeleteBookmark(bookmark.Id);
        return AgentResponse.Success(new { id = bookmark.Id, title = bookmark.Title, deleted = true });
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
            hasIpChangeUrl = !string.IsNullOrWhiteSpace(proxy.IpChangeUrl),
            enabled = proxy.IsEnabled
        };
    }

    private object ExtensionData(ExtensionItem extension)
    {
        return new
        {
            id = extension.Id,
            name = extension.Name,
            path = extension.Path,
            enabled = extension.IsEnabled
        };
    }

    private static object BookmarkData(BookmarkItem bookmark)
    {
        return new
        {
            id = bookmark.Id,
            title = bookmark.Title,
            url = bookmark.Url,
            folder = bookmark.Folder,
            parentId = bookmark.ParentId,
            isFolder = bookmark.IsFolder,
            sortOrder = bookmark.SortOrder
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

    private ExtensionItem ResolveExtension(string idOrName)
    {
        var matches = _databaseService.GetAllExtensions()
            .Where(extension => string.Equals(extension.Id, idOrName, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(extension.Name, idOrName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Extension not found: {idOrName}"),
            _ => throw new ArgumentException($"Extension name is ambiguous: {idOrName}")
        };
    }

    private BookmarkItem ResolveBookmark(string id)
    {
        return _databaseService.GetAllBookmarks().FirstOrDefault(bookmark => string.Equals(bookmark.Id, id, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Bookmark not found: {id}");
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

    private static int ParsePositiveInt(string value, string name)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
            throw new ArgumentException($"{name} must be a positive integer.");

        return parsed;
    }

    private static int ParsePositiveOrZeroInt(string value, string name)
    {
        if (!int.TryParse(value, out var parsed) || parsed < 0)
            throw new ArgumentException($"{name} must be zero or a positive integer.");

        return parsed;
    }

    private static string NormalizeOs(string os)
    {
        var normalized = os.Trim().ToLowerInvariant();
        return normalized is "windows" or "macos" or "linux"
            ? normalized
            : throw new ArgumentException($"Unsupported OS: {os}");
    }

    private static FingerprintConfig BuildFingerprintConfig(AgentRequest request)
    {
        return new FingerprintConfig
        {
            Os = TryGet(request, "os", out var os) ? NormalizeOs(os) : new FingerprintConfig().Os,
            Screen = new ScreenConfig
            {
                MaxWidth = TryGet(request, "width", out var width) ? ParsePositiveInt(width, "width") : 1280,
                MaxHeight = TryGet(request, "height", out var height) ? ParsePositiveInt(height, "height") : 720
            }
        };
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
