using System;

namespace YellowFox.Desktop.Models;

public class Proxy
{
    public const string HttpType = "http";
    public const string Socks5Type = "socks5";

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = HttpType;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? IpChangeUrl { get; set; }
    public string? DolphinProxyId { get; set; }
    public bool IsEnabled { get; set; } = true;

    public static string NormalizeType(string? type)
    {
        var normalized = string.IsNullOrWhiteSpace(type)
            ? HttpType
            : type.Trim().ToLowerInvariant();

        return normalized switch
        {
            HttpType => HttpType,
            Socks5Type => Socks5Type,
            _ => throw new ArgumentException($"Unsupported proxy type: {type}", nameof(type))
        };
    }

    public string ToProxyUrl()
    {
        var scheme = NormalizeType(Type);
        var credentials = string.Empty;

        if (!string.IsNullOrWhiteSpace(Username))
        {
            var user = Uri.EscapeDataString(Username);
            var pass = Uri.EscapeDataString(Password ?? string.Empty);
            credentials = $"{user}:{pass}@";
        }

        return $"{scheme}://{credentials}{Host}:{Port}";
    }
}
