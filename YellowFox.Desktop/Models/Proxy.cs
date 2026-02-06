using System;

namespace YellowFox.Desktop.Models;

public class Proxy
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "http"; // http only
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool IsEnabled { get; set; } = true;

    public string ToProxyUrl()
    {
        const string scheme = "http";
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
