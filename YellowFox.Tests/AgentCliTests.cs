using System.Text.Json;
using YellowFox.Cli;

namespace YellowFox.Tests;

public class AgentCliTests
{
    [Fact]
    public void BuildRequest_ShouldMapProxyAddCommand()
    {
        var request = AgentCli.BuildRequest(new[]
        {
            "proxy", "add", "--json",
            "--name", "Proxy A",
            "--type", "socks5",
            "--host", "127.0.0.1",
            "--port", "1080"
        });

        Assert.Equal("proxy.add", request.Command);
        Assert.Equal("Proxy A", request.Args["name"]);
        Assert.Equal("socks5", request.Args["type"]);
        Assert.Equal("127.0.0.1", request.Args["host"]);
        Assert.Equal("1080", request.Args["port"]);
    }

    [Fact]
    public void BuildRequest_ShouldMapProfileOpenCommand()
    {
        var request = AgentCli.BuildRequest(new[]
        {
            "profile", "open",
            "--id", "NRD GGL3",
            "--url", "example.com",
            "--json"
        });

        Assert.Equal("profile.open", request.Command);
        Assert.Equal("NRD GGL3", request.Args["id"]);
        Assert.Equal("example.com", request.Args["url"]);
    }

    [Fact]
    public void BuildRequest_ShouldMapProfilePagesCommand()
    {
        var request = AgentCli.BuildRequest(new[]
        {
            "profile", "pages",
            "--id", "NRD GGL3",
            "--text", "true",
            "--json"
        });

        Assert.Equal("profile.pages", request.Command);
        Assert.Equal("NRD GGL3", request.Args["id"]);
        Assert.Equal("true", request.Args["text"]);
    }

    [Fact]
    public void BuildRequest_ShouldMapProfileAttachCommand()
    {
        var request = AgentCli.BuildRequest(new[]
        {
            "profile", "attach",
            "--id", "NRD GGL3",
            "--json"
        });

        Assert.Equal("profile.attach", request.Command);
        Assert.Equal("NRD GGL3", request.Args["id"]);
    }

    [Fact]
    public void BuildRequest_ShouldMapProfileEndpointCommand()
    {
        var request = AgentCli.BuildRequest(new[]
        {
            "profile", "endpoint",
            "--id", "NRD GGL3",
            "--json"
        });

        Assert.Equal("profile.endpoint", request.Command);
        Assert.Equal("NRD GGL3", request.Args["id"]);
    }

    [Fact]
    public void BuildRequest_ShouldMapProfileClickCommand()
    {
        var request = AgentCli.BuildRequest(new[]
        {
            "profile", "click",
            "--id", "NRD GGL3",
            "--text", "Scan My Browser",
            "--json"
        });

        Assert.Equal("profile.click", request.Command);
        Assert.Equal("NRD GGL3", request.Args["id"]);
        Assert.Equal("Scan My Browser", request.Args["text"]);
    }

    [Fact]
    public void BuildRequest_ShouldMapProxyChangeIpCommand()
    {
        var request = AgentCli.BuildRequest(new[]
        {
            "proxy", "change-ip",
            "--id", "Mobile Proxy",
            "--json"
        });

        Assert.Equal("proxy.changeIp", request.Command);
        Assert.Equal("Mobile Proxy", request.Args["id"]);
    }

    [Fact]
    public void BuildRequest_ShouldMapProxyIpChangeUrlOption()
    {
        var request = AgentCli.BuildRequest(new[]
        {
            "proxy", "add",
            "--name", "Mobile Proxy",
            "--type", "http",
            "--host", "127.0.0.1",
            "--port", "8080",
            "--ip-change-url", "https://proxy.example/rotate",
            "--json"
        });

        Assert.Equal("proxy.add", request.Command);
        Assert.Equal("https://proxy.example/rotate", request.Args["ip-change-url"]);
    }

    [Fact]
    public void CreateFailureJson_ShouldUseCliResponseShape()
    {
        var json = AgentCli.CreateFailureJson("desktop_unavailable", "Desktop is not running.");
        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("desktop_unavailable", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Desktop is not running.", document.RootElement.GetProperty("error").GetProperty("message").GetString());
    }
}
