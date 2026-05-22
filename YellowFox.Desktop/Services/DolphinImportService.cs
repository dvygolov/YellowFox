using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using YellowFox.Desktop.Models;

namespace YellowFox.Desktop.Services;

public sealed class DolphinImportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly DatabaseService _databaseService;
    private readonly BrowserService _browserService;
    private readonly HttpClient _httpClient = new();

    public DolphinImportService(DatabaseService databaseService, BrowserService browserService)
    {
        _databaseService = databaseService;
        _browserService = browserService;
    }

    public async Task<DolphinImportResult> ImportAsync(DolphinImportOptions options)
    {
        var token = ReadToken(options.TokenFilePath);
        var localSessionToken = ReadOptionalToken(options.LocalSessionTokenFilePath);

        var proxies = await GetPagedDataAsync(options.CloudBase, "/proxy", token);
        var proxyMap = ImportProxies(proxies);

        var profiles = await GetPagedDataAsync(options.CloudBase, "/browser_profiles", token);
        var result = ImportProfiles(profiles, proxyMap);

        if (options.ImportCookies)
        {
            await EnsureLocalAuthAsync(options.LocalBase, token);
            foreach (var importedProfile in result.Profiles)
            {
                var cookieResult = await ExportAndStoreCookiesAsync(options.LocalBase, localSessionToken, importedProfile);
                if (cookieResult)
                    result.CookiesImported++;
                else
                    result.CookieImportFailures++;

                var localStorageResult = await ExportAndStoreLocalStorageAsync(options.LocalBase, localSessionToken, importedProfile);
                if (localStorageResult)
                    result.LocalStorageExported++;
                else
                    result.LocalStorageExportFailures++;
            }
        }

        result.ProxiesImported = proxyMap.Count;
        return result;
    }

    private Dictionary<string, Proxy> ImportProxies(IReadOnlyCollection<JsonElement> proxies)
    {
        var map = new Dictionary<string, Proxy>(StringComparer.Ordinal);
        foreach (var item in proxies)
        {
            var dolphinId = GetRequiredString(item, "id");
            var type = Proxy.NormalizeType(GetString(item, "type") ?? Proxy.HttpType);
            var host = GetRequiredString(item, "host");
            var port = GetInt(item, "port") ?? throw new InvalidOperationException($"Dolphin proxy {dolphinId} has no port.");
            var name = GetString(item, "name") ?? $"Dolphin Proxy {dolphinId}";

            var proxy = _databaseService.GetProxyByDolphinProxyId(dolphinId)
                ?? _databaseService.GetAllProxies().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? new Proxy();

            proxy.Name = MakeUniqueProxyName(name, proxy.Id);
            proxy.Type = type;
            proxy.Host = host;
            proxy.Port = port;
            proxy.Username = GetString(item, "login");
            proxy.Password = GetString(item, "password");
            proxy.DolphinProxyId = dolphinId;
            proxy.IsEnabled = true;

            if (_databaseService.GetProxy(proxy.Id) == null)
                _databaseService.CreateProxy(proxy);
            else
                _databaseService.UpdateProxy(proxy);

            map[dolphinId] = proxy;
        }

        return map;
    }

    private DolphinImportResult ImportProfiles(IReadOnlyCollection<JsonElement> profiles, IReadOnlyDictionary<string, Proxy> proxyMap)
    {
        var result = new DolphinImportResult();
        foreach (var item in profiles)
        {
            var dolphinId = GetRequiredString(item, "id");
            var name = GetString(item, "name") ?? $"Dolphin Profile {dolphinId}";
            var profile = _databaseService.GetProfileByDolphinProfileId(dolphinId)
                ?? _databaseService.GetAllProfiles().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? new Profile();

            profile.Name = MakeUniqueProfileName(name, profile.Id);
            profile.Notes = ExtractNotes(item) ?? $"Imported from Dolphin profile {dolphinId}";
            profile.DolphinProfileId = dolphinId;
            profile.ProxyId = ResolveImportedProxyId(item, proxyMap);
            profile.FingerprintConfig = new FingerprintConfig
            {
                Os = NormalizeOs(GetString(item, "platform")),
                Screen = ExtractScreen(item)
            };

            if (_databaseService.GetProfile(profile.Id) == null)
                _databaseService.CreateProfile(profile);
            else
                _databaseService.UpdateProfile(profile);

            result.Profiles.Add(new DolphinImportedProfile
            {
                YellowFoxProfileId = profile.Id,
                DolphinProfileId = dolphinId,
                Name = profile.Name
            });
        }

        return result;
    }

    private async Task<bool> ExportAndStoreCookiesAsync(string localBase, string? localSessionToken, DolphinImportedProfile profile)
    {
        var payload = new
        {
            browserProfiles = new[]
            {
                new
                {
                    id = int.Parse(profile.DolphinProfileId),
                    name = profile.Name,
                    transfer = 0
                }
            },
            plan = "base",
            doNotSave = true
        };

        using var request = CreateJsonRequest(HttpMethod.Post, JoinEndpoint(localBase, "/export-cookies"), payload);
        if (!string.IsNullOrWhiteSpace(localSessionToken))
            request.Headers.Add("X-Anty-Session-Token", localSessionToken);

        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return false;

        var json = await response.Content.ReadAsStringAsync();
        var cookies = BrowserService.ParseCookiesForImport(json);
        if (cookies.Count == 0)
            return false;

        _browserService.SaveImportedCookies(profile.YellowFoxProfileId, cookies);
        return true;
    }

    private async Task<bool> ExportAndStoreLocalStorageAsync(string localBase, string? localSessionToken, DolphinImportedProfile profile)
    {
        var payload = new
        {
            transfer = 0,
            plan = "base"
        };

        using var request = CreateJsonRequest(HttpMethod.Post, JoinEndpoint(localBase, $"/local-storage/export/{profile.DolphinProfileId}"), payload);
        if (!string.IsNullOrWhiteSpace(localSessionToken))
            request.Headers.Add("X-Anty-Session-Token", localSessionToken);

        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return false;

        var json = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json))
            return false;

        await File.WriteAllTextAsync(_databaseService.GetProfileImportedLocalStorageFilePath(profile.YellowFoxProfileId), json);
        return true;
    }

    private async Task EnsureLocalAuthAsync(string localBase, string token)
    {
        using var request = CreateJsonRequest(HttpMethod.Post, JoinEndpoint(localBase, "/auth/login-with-token"), new { token });
        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task<List<JsonElement>> GetPagedDataAsync(string cloudBase, string path, string token)
    {
        var result = new List<JsonElement>();
        var page = 1;
        var lastPage = 1;

        do
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{JoinEndpoint(cloudBase, path)}?page={page}&limit=50");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            lastPage = GetInt(root, "last_page") ?? 1;

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                    result.Add(item.Clone());
            }

            page++;
        }
        while (page <= lastPage);

        return result;
    }

    private static HttpRequestMessage CreateJsonRequest(HttpMethod method, string url, object payload)
    {
        var request = new HttpRequestMessage(method, url);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        return request;
    }

    private static string ReadToken(string? tokenFilePath)
    {
        var path = ResolveTokenFilePath(tokenFilePath);
        var raw = File.ReadAllText(path).Trim();
        if (raw.StartsWith("{", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(raw);
            foreach (var key in new[] { "token", "api_token", "apiKey", "key" })
            {
                if (document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? throw new InvalidOperationException("Token file has empty token.");
            }
        }

        foreach (var line in raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("#", StringComparison.Ordinal))
                return trimmed.Trim('\'', '"');
        }

        throw new InvalidOperationException("Dolphin token file is empty.");
    }

    private static string? ReadOptionalToken(string? tokenFilePath)
    {
        var path = ResolveLocalSessionTokenFilePath(tokenFilePath);
        return File.Exists(path) ? ReadToken(path) : null;
    }

    private static string ResolveTokenFilePath(string? tokenFilePath)
    {
        if (!string.IsNullOrWhiteSpace(tokenFilePath))
            return tokenFilePath;

        var envToken = Environment.GetEnvironmentVariable("DOLPHIN_ANTY_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
            return WriteEphemeralToken(envToken);

        var envTokenPath = Environment.GetEnvironmentVariable("DOLPHIN_ANTY_TOKEN_FILE");
        if (!string.IsNullOrWhiteSpace(envTokenPath))
            return envTokenPath;

        throw new InvalidOperationException("Dolphin token is required. Pass --token-file, set DOLPHIN_ANTY_TOKEN, or set DOLPHIN_ANTY_TOKEN_FILE.");
    }

    private static string ResolveLocalSessionTokenFilePath(string? tokenFilePath)
    {
        if (!string.IsNullOrWhiteSpace(tokenFilePath))
            return tokenFilePath;

        return Environment.GetEnvironmentVariable("DOLPHIN_ANTY_LOCAL_SESSION_TOKEN_FILE")
            ?? string.Empty;
    }

    private static string WriteEphemeralToken(string token)
    {
        var path = Path.Combine(Path.GetTempPath(), $"yellowfox-dolphin-token-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, token);
        return path;
    }

    private static string? ResolveImportedProxyId(JsonElement profile, IReadOnlyDictionary<string, Proxy> proxyMap)
    {
        var proxyId = GetString(profile, "proxyId");
        if (proxyId == null)
            return null;

        return proxyMap.TryGetValue(proxyId, out var proxy) ? proxy.Id : null;
    }

    private static string NormalizeOs(string? platform)
    {
        return platform?.Trim().ToLowerInvariant() switch
        {
            "macos" or "mac" => "macos",
            "linux" => "linux",
            _ => "windows"
        };
    }

    private static ScreenConfig ExtractScreen(JsonElement profile)
    {
        if (profile.TryGetProperty("screen", out var screen) &&
            screen.ValueKind == JsonValueKind.Object &&
            screen.TryGetProperty("resolution", out var resolution) &&
            resolution.ValueKind == JsonValueKind.String)
        {
            var parts = resolution.GetString()?.Split(new[] { 'x', 'X', '×' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts?.Length == 2 &&
                int.TryParse(parts[0], out var width) &&
                int.TryParse(parts[1], out var height))
            {
                return new ScreenConfig { MaxWidth = width, MaxHeight = height };
            }
        }

        return new ScreenConfig { MaxWidth = 1920, MaxHeight = 1080 };
    }

    private static string? ExtractNotes(JsonElement profile)
    {
        if (profile.TryGetProperty("notes", out var notes))
        {
            if (notes.ValueKind == JsonValueKind.String)
                return notes.GetString();
            if (notes.ValueKind == JsonValueKind.Object && notes.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return content.GetString();
        }

        return null;
    }

    private string MakeUniqueProfileName(string name, string currentId)
    {
        return MakeUniqueName(name, currentId, _databaseService.GetAllProfiles().Select(p => (p.Id, p.Name)));
    }

    private string MakeUniqueProxyName(string name, string currentId)
    {
        return MakeUniqueName(name, currentId, _databaseService.GetAllProxies().Select(p => (p.Id, p.Name)));
    }

    private static string MakeUniqueName(string name, string currentId, IEnumerable<(string Id, string Name)> existing)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "Imported" : name.Trim();
        var used = existing
            .Where(e => !string.Equals(e.Id, currentId, StringComparison.Ordinal))
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!used.Contains(baseName))
            return baseName;

        for (var index = 2; index < 10000; index++)
        {
            var candidate = $"{baseName} ({index})";
            if (!used.Contains(candidate))
                return candidate;
        }

        return $"{baseName} ({Guid.NewGuid():N})";
    }

    private static string JoinEndpoint(string baseUrl, string path)
    {
        return $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
    }

    private static string GetRequiredString(JsonElement element, string name)
    {
        return GetString(element, name) ?? throw new InvalidOperationException($"Missing required Dolphin field: {name}");
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }
}

public sealed class DolphinImportOptions
{
    public string CloudBase { get; set; } = "https://anty-api.com";
    public string LocalBase { get; set; } = "http://127.0.0.1:3001/v1.0";
    public string? TokenFilePath { get; set; }
    public string? LocalSessionTokenFilePath { get; set; }
    public bool ImportCookies { get; set; } = true;
}

public sealed class DolphinImportResult
{
    public int ProxiesImported { get; set; }
    public int CookiesImported { get; set; }
    public int CookieImportFailures { get; set; }
    public int LocalStorageExported { get; set; }
    public int LocalStorageExportFailures { get; set; }
    public List<DolphinImportedProfile> Profiles { get; } = new();
    public int ProfilesImported => Profiles.Count;
}

public sealed class DolphinImportedProfile
{
    public string YellowFoxProfileId { get; set; } = string.Empty;
    public string DolphinProfileId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
