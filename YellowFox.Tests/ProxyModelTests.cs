using YellowFox.Desktop.Models;

namespace YellowFox.Tests;

public class ProxyModelTests
{
    [Fact]
    public void ToProxyUrl_ShouldBuildHttpUrlWithoutCredentials()
    {
        var proxy = new Proxy
        {
            Type = "http",
            Host = "127.0.0.1",
            Port = 8080
        };

        var url = proxy.ToProxyUrl();

        Assert.Equal("http://127.0.0.1:8080", url);
    }

    [Fact]
    public void ToProxyUrl_ShouldPreserveSocks5WithCredentials()
    {
        var proxy = new Proxy
        {
            Type = "socks5",
            Host = "proxy.local",
            Port = 1080,
            Username = "user",
            Password = "pass"
        };

        var url = proxy.ToProxyUrl();

        Assert.Equal("socks5://user:pass@proxy.local:1080", url);
    }
}
