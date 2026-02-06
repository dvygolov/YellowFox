using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using YellowFox.Desktop.Models;

namespace YellowFox.Desktop.Services;

public class ProxyValidatorService
{
    private static readonly string[] ProbeUrls =
    {
        "http://api.ipify.org?format=json",
        "http://ipv4.icanhazip.com",
        "http://ifconfig.me/ip"
    };
    private const string ProbeHost = "api.ipify.org";

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

            return result;
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

        string? ip = null;
        Exception? lastError = null;
        foreach (var probeUrl in ProbeUrls)
        {
            try
            {
                var response = await httpClient.GetAsync(probeUrl);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync();
                ip = TryExtractIp(body);
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        stopwatch.Stop();
        if (!string.IsNullOrWhiteSpace(ip))
            return ProxyValidationResult.Success(ip, stopwatch.ElapsedMilliseconds);

        if (lastError != null)
            return ProxyValidationResult.Failed(lastError.Message, stopwatch.ElapsedMilliseconds);

        return ProxyValidationResult.Success("unknown", stopwatch.ElapsedMilliseconds);
    }

    private static async Task<ProxyValidationResult> ValidateSocks5Async(Proxy proxy, Stopwatch stopwatch)
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
            stopwatch.Stop();
            return ProxyValidationResult.Failed("SOCKS5 auth method rejected.", stopwatch.ElapsedMilliseconds);
        }

        if (greetingReply[1] == 0x02)
        {
            var username = Encoding.UTF8.GetBytes(proxy.Username ?? string.Empty);
            var password = Encoding.UTF8.GetBytes(proxy.Password ?? string.Empty);
            if (username.Length > 255 || password.Length > 255)
            {
                stopwatch.Stop();
                return ProxyValidationResult.Failed("SOCKS5 credentials are too long.", stopwatch.ElapsedMilliseconds);
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
                stopwatch.Stop();
                return ProxyValidationResult.Failed("SOCKS5 username/password rejected.", stopwatch.ElapsedMilliseconds);
            }
        }

        var hostBytes = Encoding.ASCII.GetBytes(ProbeHost);
        var connectRequest = new byte[7 + hostBytes.Length];
        connectRequest[0] = 0x05; // version
        connectRequest[1] = 0x01; // CONNECT
        connectRequest[2] = 0x00; // reserved
        connectRequest[3] = 0x03; // DOMAIN
        connectRequest[4] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, connectRequest, 5, hostBytes.Length);
        connectRequest[5 + hostBytes.Length] = 0x00; // port 80
        connectRequest[6 + hostBytes.Length] = 0x50;

        await stream.WriteAsync(connectRequest);

        var connectHeader = await ReadExactAsync(stream, 4);
        if (connectHeader[1] != 0x00)
        {
            stopwatch.Stop();
            return ProxyValidationResult.Failed($"SOCKS5 connect failed (code: {connectHeader[1]}).", stopwatch.ElapsedMilliseconds);
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

        var httpRequest = $"GET /?format=json HTTP/1.1\r\nHost: {ProbeHost}\r\nConnection: close\r\n\r\n";
        var requestBytes = Encoding.ASCII.GetBytes(httpRequest);
        await stream.WriteAsync(requestBytes);
        await stream.FlushAsync();

        var rawResponse = await ReadToEndAsync(stream);
        var bodyIndex = rawResponse.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var body = bodyIndex >= 0 ? rawResponse[(bodyIndex + 4)..] : rawResponse;
        var decodedBody = TryDecodeChunkedBody(body);
        var ip = TryExtractIp(decodedBody) ?? "unknown";
        stopwatch.Stop();
        return ProxyValidationResult.Success(ip, stopwatch.ElapsedMilliseconds);
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

    private static string? TryExtractIp(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        try
        {
            var data = JsonSerializer.Deserialize<IpResponse>(content);
            if (!string.IsNullOrWhiteSpace(data?.Ip))
                return data.Ip;
        }
        catch
        {
            // Ignore and continue with fallback extraction.
        }

        var jsonMatch = Regex.Match(content, "\"ip\"\\s*:\\s*\"(?<ip>[^\"]+)\"", RegexOptions.IgnoreCase);
        if (jsonMatch.Success)
            return jsonMatch.Groups["ip"].Value;

        var ipv4Match = Regex.Match(content, @"\b(\d{1,3}\.){3}\d{1,3}\b");
        if (ipv4Match.Success)
            return ipv4Match.Value;

        var ipv6Match = Regex.Match(content, @"\b([0-9a-f]{1,4}:){2,7}[0-9a-f]{1,4}\b", RegexOptions.IgnoreCase);
        return ipv6Match.Success ? ipv6Match.Value : null;
    }

    private sealed class IpResponse
    {
        public string? Ip { get; set; }
    }
}

public sealed class ProxyValidationResult
{
    public bool IsSuccess { get; init; }
    public string? Error { get; init; }
    public string? ExternalIp { get; init; }
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
