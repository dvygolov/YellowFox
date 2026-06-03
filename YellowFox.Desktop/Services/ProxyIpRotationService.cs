using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using YellowFox.Desktop.Models;

namespace YellowFox.Desktop.Services;

public sealed class ProxyIpRotationService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<ProxyIpRotationResult> ChangeIpAsync(Proxy proxy, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(proxy.IpChangeUrl))
            return new ProxyIpRotationResult(false, "Proxy has no IP change URL.");

        if (!Uri.TryCreate(proxy.IpChangeUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new ProxyIpRotationResult(false, "IP change URL must be a valid http/https URL.");
        }

        using var response = await HttpClient.GetAsync(uri, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = string.IsNullOrWhiteSpace(body)
            ? $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim()
            : body.Trim();

        return response.IsSuccessStatusCode
            ? new ProxyIpRotationResult(true, message)
            : new ProxyIpRotationResult(false, message);
    }
}

public sealed record ProxyIpRotationResult(bool Success, string Message);
