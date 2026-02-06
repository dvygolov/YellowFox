using YellowFox.Desktop.Models;
using YellowFox.Desktop.Services;

namespace YellowFox.Tests;

public class ProxyValidatorServiceTests
{
    [Fact]
    public async Task ValidateAsync_WithInvalidPort_ShouldFailFast()
    {
        var service = new ProxyValidatorService();
        var proxy = new Proxy
        {
            Name = "Bad Proxy",
            Type = "http",
            Host = "127.0.0.1",
            Port = 70000
        };

        var result = await service.ValidateAsync(proxy);

        Assert.False(result.IsSuccess);
        Assert.Contains("Invalid host or port", result.Error);
    }
}
