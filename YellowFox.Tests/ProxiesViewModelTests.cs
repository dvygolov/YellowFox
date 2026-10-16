using YellowFox.Desktop.Models;
using YellowFox.Desktop.Services;
using YellowFox.Desktop.ViewModels;

namespace YellowFox.Tests;

public class ProxiesViewModelTests : IDisposable
{
    private readonly string _testDataDir;

    public ProxiesViewModelTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "yellowfox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDataDir);
    }

    [Fact]
    public void LoadProxies_ShouldShowHowManyProfilesUseEachProxy()
    {
        var database = new DatabaseService(_testDataDir, disablePooling: true);
        var sharedProxy = new Proxy { Name = "Shared", Host = "127.0.0.1", Port = 8080 };
        var unusedProxy = new Proxy { Name = "Unused", Type = Proxy.Socks5Type, Host = "127.0.0.1", Port = 1080 };
        database.CreateProxy(sharedProxy);
        database.CreateProxy(unusedProxy);
        database.CreateProfile(new Profile { Name = "Profile A", ProxyId = sharedProxy.Id });
        database.CreateProfile(new Profile { Name = "Profile B", ProxyId = sharedProxy.Id });

        var viewModel = new ProxiesViewModel(database, new ProxyValidatorService(), new ProxyIpRotationService());

        Assert.Equal(2, viewModel.Proxies.Single(item => item.Proxy.Id == sharedProxy.Id).ProfileCount);
        Assert.Equal(0, viewModel.Proxies.Single(item => item.Proxy.Id == unusedProxy.Id).ProfileCount);
    }

    [Fact]
    public void StatusSortRank_ShouldPlaceFailuresFirstAndSuccessfulProxiesLast()
    {
        var item = new ProxyItemViewModel(new Proxy { Name = "Proxy", Host = "127.0.0.1", Port = 8080 }, profileCount: 0);
        var unknownRank = item.StatusSortRank;

        item.SetFailed("Connection refused");
        var failedRank = item.StatusSortRank;
        item.SetChecking();
        var checkingRank = item.StatusSortRank;
        item.SetSuccess("203.0.113.10", 25, "US", "United States", countryFlagPath: null);
        var successRank = item.StatusSortRank;

        Assert.True(failedRank < unknownRank);
        Assert.True(unknownRank < checkingRank);
        Assert.True(checkingRank < successRank);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
            Directory.Delete(_testDataDir, true);
    }
}
