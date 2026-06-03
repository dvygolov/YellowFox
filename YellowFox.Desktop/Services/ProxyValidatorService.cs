using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YellowFox.Desktop.Models;

namespace YellowFox.Desktop.Services;

public class ProxyValidatorService
{
    private static readonly Uri[] HttpProbeUrls =
    {
        new("http://ip-api.com/json/?fields=status,message,query,country,countryCode"),
        new("https://api.ip.sb/geoip"),
        new("https://ipapi.co/json/"),
        new("https://api64.ipify.org?format=json"),
        new("https://ifconfig.me/ip")
    };

    private static readonly Uri[] Socks5ProbeUrls =
    {
        new("http://ip-api.com/json/?fields=status,message,query,country,countryCode"),
        new("http://api64.ipify.org?format=json"),
        new("http://ifconfig.me/ip")
    };

    private static readonly string[] DirectCountryLookupUrlFormats =
    {
        "http://ip-api.com/json/{0}?fields=status,message,query,country,countryCode",
        "https://ipapi.co/{0}/json/"
    };

    public async Task<ProxyValidationResult> ValidateAsync(Proxy proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy.Host) || proxy.Port <= 0 || proxy.Port > 65535)
        {
            return ProxyValidationResult.Failed("Invalid host or port.");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            ProxyValidationResult result;
            if (proxy.Type.Equals("socks5", StringComparison.OrdinalIgnoreCase))
            {
                result = await ValidateSocks5Async(proxy, stopwatch);
            }
            else
            {
                result = await ValidateHttpAsync(proxy, stopwatch);
            }

            return await AddCountryIfMissingAsync(result);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProxyValidationResult.Failed(ex.Message, stopwatch.ElapsedMilliseconds);
        }
    }

    private static async Task<ProxyValidationResult> ValidateHttpAsync(Proxy proxy, Stopwatch stopwatch)
    {
        var webProxy = new WebProxy(proxy.ToProxyUrl());
        if (!string.IsNullOrWhiteSpace(proxy.Username))
        {
            webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password ?? string.Empty);
        }

        var handler = new SocketsHttpHandler
        {
            Proxy = webProxy,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };

        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        ProxyProbeResult? probeResult = null;
        Exception? lastError = null;
        foreach (var probeUrl in HttpProbeUrls)
        {
            try
            {
                var response = await httpClient.GetAsync(probeUrl);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync();
                probeResult = TryExtractProbeResult(body);
                if (!string.IsNullOrWhiteSpace(probeResult.Ip))
                    break;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        stopwatch.Stop();
        if (!string.IsNullOrWhiteSpace(probeResult?.Ip))
            return ProxyValidationResult.Success(probeResult, stopwatch.ElapsedMilliseconds);

        if (lastError != null)
            return ProxyValidationResult.Failed(lastError.Message, stopwatch.ElapsedMilliseconds);

        return ProxyValidationResult.Success("unknown", stopwatch.ElapsedMilliseconds);
    }

    private static async Task<ProxyValidationResult> AddCountryIfMissingAsync(ProxyValidationResult result)
    {
        if (!result.IsSuccess ||
            !string.IsNullOrWhiteSpace(result.CountryCode) ||
            string.IsNullOrWhiteSpace(result.ExternalIp) ||
            string.Equals(result.ExternalIp, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            foreach (var format in DirectCountryLookupUrlFormats)
            {
                var url = string.Format(CultureInfo.InvariantCulture, format, Uri.EscapeDataString(result.ExternalIp));
                try
                {
                    var body = await httpClient.GetStringAsync(url, cancellation.Token);
                    var probeResult = TryExtractProbeResult(body);
                    if (!string.IsNullOrWhiteSpace(probeResult.CountryCode) || !string.IsNullOrWhiteSpace(probeResult.CountryName))
                        return result.WithCountry(probeResult.CountryCode, probeResult.CountryName);
                }
                catch
                {
                    // Try the next lookup provider.
                }
            }
        }
        catch
        {
            // Country is optional; keep the proxy check result if enrichment fails.
        }

        return result;
    }

    private static async Task<ProxyValidationResult> ValidateSocks5Async(Proxy proxy, Stopwatch stopwatch)
    {
        Exception? lastError = null;
        foreach (var probeUrl in Socks5ProbeUrls)
        {
            try
            {
                var body = await FetchViaSocks5Async(proxy, probeUrl);
                var probeResult = TryExtractProbeResult(body);
                if (!string.IsNullOrWhiteSpace(probeResult.Ip))
                {
                    stopwatch.Stop();
                    return ProxyValidationResult.Success(probeResult, stopwatch.ElapsedMilliseconds);
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        stopwatch.Stop();
        if (lastError != null)
            return ProxyValidationResult.Failed(lastError.Message, stopwatch.ElapsedMilliseconds);

        return ProxyValidationResult.Success("unknown", stopwatch.ElapsedMilliseconds);
    }

    private static async Task<string> FetchViaSocks5Async(Proxy proxy, Uri probeUrl)
    {
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(proxy.Host, proxy.Port);
        using var stream = tcpClient.GetStream();

        var requiresAuth = !string.IsNullOrWhiteSpace(proxy.Username);

        // greeting
        var methods = requiresAuth ? new byte[] { 0x00, 0x02 } : new byte[] { 0x00 };
        await stream.WriteAsync(new[] { (byte)0x05, (byte)methods.Length }.Concat(methods).ToArray());

        var greetingReply = await ReadExactAsync(stream, 2);
        if (greetingReply[0] != 0x05 || greetingReply[1] == 0xFF)
        {
            throw new IOException("SOCKS5 auth method rejected.");
        }

        if (greetingReply[1] == 0x02)
        {
            var username = Encoding.UTF8.GetBytes(proxy.Username ?? string.Empty);
            var password = Encoding.UTF8.GetBytes(proxy.Password ?? string.Empty);
            if (username.Length > 255 || password.Length > 255)
            {
                throw new IOException("SOCKS5 credentials are too long.");
            }

            var authRequest = new byte[3 + username.Length + password.Length];
            authRequest[0] = 0x01;
            authRequest[1] = (byte)username.Length;
            Buffer.BlockCopy(username, 0, authRequest, 2, username.Length);
            authRequest[2 + username.Length] = (byte)password.Length;
            Buffer.BlockCopy(password, 0, authRequest, 3 + username.Length, password.Length);
            await stream.WriteAsync(authRequest);

            var authReply = await ReadExactAsync(stream, 2);
            if (authReply[1] != 0x00)
            {
                throw new IOException("SOCKS5 username/password rejected.");
            }
        }

        var hostBytes = Encoding.ASCII.GetBytes(probeUrl.Host);
        var connectRequest = new byte[7 + hostBytes.Length];
        connectRequest[0] = 0x05; // version
        connectRequest[1] = 0x01; // CONNECT
        connectRequest[2] = 0x00; // reserved
        connectRequest[3] = 0x03; // DOMAIN
        connectRequest[4] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, connectRequest, 5, hostBytes.Length);
        var port = probeUrl.IsDefaultPort ? 80 : probeUrl.Port;
        connectRequest[5 + hostBytes.Length] = (byte)(port >> 8);
        connectRequest[6 + hostBytes.Length] = (byte)(port & 0xFF);

        await stream.WriteAsync(connectRequest);

        var connectHeader = await ReadExactAsync(stream, 4);
        if (connectHeader[1] != 0x00)
        {
            throw new IOException($"SOCKS5 connect failed (code: {connectHeader[1]}).");
        }

        var atyp = connectHeader[3];
        var addrLength = atyp switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => (await ReadExactAsync(stream, 1))[0],
            _ => 0
        };
        if (addrLength > 0)
        {
            _ = await ReadExactAsync(stream, addrLength);
        }
        _ = await ReadExactAsync(stream, 2); // bound port

        var path = string.IsNullOrWhiteSpace(probeUrl.PathAndQuery) ? "/" : probeUrl.PathAndQuery;
        var httpRequest = $"GET {path} HTTP/1.1\r\nHost: {probeUrl.Host}\r\nConnection: close\r\n\r\n";
        var requestBytes = Encoding.ASCII.GetBytes(httpRequest);
        await stream.WriteAsync(requestBytes);
        await stream.FlushAsync();

        var rawResponse = await ReadToEndAsync(stream);
        var bodyIndex = rawResponse.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var body = bodyIndex >= 0 ? rawResponse[(bodyIndex + 4)..] : rawResponse;
        return TryDecodeChunkedBody(body);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;

        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer, offset, count - offset);
            if (read <= 0)
                throw new IOException("Unexpected end of stream.");
            offset += read;
        }

        return buffer;
    }

    private static async Task<string> ReadToEndAsync(Stream stream)
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder();
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
        return sb.ToString();
    }

    private static string TryDecodeChunkedBody(string body)
    {
        // If body is regular JSON, return as-is.
        if (body.TrimStart().StartsWith("{", StringComparison.Ordinal))
            return body;

        // Best-effort dechunk: each chunk starts with hex size, then payload.
        try
        {
            var index = 0;
            var output = new StringBuilder();
            while (index < body.Length)
            {
                var lineEnd = body.IndexOf("\r\n", index, StringComparison.Ordinal);
                if (lineEnd < 0)
                    break;

                var sizeHex = body[index..lineEnd].Trim();
                if (!int.TryParse(sizeHex, System.Globalization.NumberStyles.HexNumber, null, out var size))
                    break;

                index = lineEnd + 2;
                if (size == 0)
                    break;

                if (index + size > body.Length)
                    break;

                output.Append(body.Substring(index, size));
                index += size + 2; // payload + CRLF
            }

            return output.Length > 0 ? output.ToString() : body;
        }
        catch
        {
            return body;
        }
    }

    private static ProxyProbeResult TryExtractProbeResult(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new ProxyProbeResult(null, null, null);

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var ip = GetString(document.RootElement, "ip")
                    ?? GetString(document.RootElement, "query")
                    ?? GetString(document.RootElement, "ip_addr");
                var countryCode = GetString(document.RootElement, "country_code")
                    ?? GetString(document.RootElement, "countryCode");
                var countryName = GetString(document.RootElement, "country_name");

                var country = GetString(document.RootElement, "country");
                if (string.IsNullOrWhiteSpace(countryCode) && IsCountryCode(country))
                    countryCode = country;
                else if (string.IsNullOrWhiteSpace(countryName))
                    countryName = country;

                countryCode = NormalizeCountryCode(countryCode);
                countryName = NormalizeCountryName(countryName, countryCode);

                if (!string.IsNullOrWhiteSpace(ip))
                    return new ProxyProbeResult(ip, countryCode, countryName);
            }
        }
        catch
        {
            // Ignore and continue with fallback extraction.
        }

        var jsonMatch = Regex.Match(content, "\"ip\"\\s*:\\s*\"(?<ip>[^\"]+)\"", RegexOptions.IgnoreCase);
        if (jsonMatch.Success)
            return new ProxyProbeResult(jsonMatch.Groups["ip"].Value, null, null);

        var queryMatch = Regex.Match(content, "\"query\"\\s*:\\s*\"(?<ip>[^\"]+)\"", RegexOptions.IgnoreCase);
        if (queryMatch.Success)
            return new ProxyProbeResult(queryMatch.Groups["ip"].Value, null, null);

        var ipv4Match = Regex.Match(content, @"\b(\d{1,3}\.){3}\d{1,3}\b");
        if (ipv4Match.Success)
            return new ProxyProbeResult(ipv4Match.Value, null, null);

        var ipv6Match = Regex.Match(content, @"\b([0-9a-f]{1,4}:){2,7}[0-9a-f]{1,4}\b", RegexOptions.IgnoreCase);
        return ipv6Match.Success
            ? new ProxyProbeResult(ipv6Match.Value, null, null)
            : new ProxyProbeResult(null, null, null);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool IsCountryCode(string? value)
    {
        var trimmed = value?.Trim();
        return !string.IsNullOrWhiteSpace(trimmed) &&
               trimmed.Length == 2 &&
               trimmed.All(char.IsLetter);
    }

    private static string? NormalizeCountryCode(string? countryCode)
    {
        return IsCountryCode(countryCode) ? countryCode!.Trim().ToUpperInvariant() : null;
    }

    private static string? NormalizeCountryName(string? countryName, string? countryCode)
    {
        if (!string.IsNullOrWhiteSpace(countryName))
            return countryName.Trim();

        if (string.IsNullOrWhiteSpace(countryCode))
            return null;

        try
        {
            return new RegionInfo(countryCode).EnglishName;
        }
        catch
        {
            return countryCode;
        }
    }
}

public sealed record ProxyProbeResult(string? Ip, string? CountryCode, string? CountryName);

public sealed class ProxyValidationResult
{
    public bool IsSuccess { get; init; }
    public string? Error { get; init; }
    public string? ExternalIp { get; init; }
    public string? CountryCode { get; init; }
    public string? CountryName { get; init; }
    public long LatencyMs { get; init; }

    public static ProxyValidationResult Success(string? ip, long latencyMs)
    {
        return new ProxyValidationResult
        {
            IsSuccess = true,
            ExternalIp = ip,
            LatencyMs = latencyMs
        };
    }

    public static ProxyValidationResult Success(ProxyProbeResult probeResult, long latencyMs)
    {
        return new ProxyValidationResult
        {
            IsSuccess = true,
            ExternalIp = probeResult.Ip,
            CountryCode = probeResult.CountryCode,
            CountryName = probeResult.CountryName,
            LatencyMs = latencyMs
        };
    }

    public ProxyValidationResult WithCountry(string? countryCode, string? countryName)
    {
        return new ProxyValidationResult
        {
            IsSuccess = IsSuccess,
            Error = Error,
            ExternalIp = ExternalIp,
            CountryCode = countryCode,
            CountryName = countryName,
            LatencyMs = LatencyMs
        };
    }

    public static ProxyValidationResult Failed(string error, long latencyMs = 0)
    {
        return new ProxyValidationResult
        {
            IsSuccess = false,
            Error = error,
            LatencyMs = latencyMs
        };
    }
}
