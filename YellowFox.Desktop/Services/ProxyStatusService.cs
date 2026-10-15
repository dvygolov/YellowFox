using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using YellowFox.Desktop.Models;
using YellowFox.Desktop.ViewModels;

namespace YellowFox.Desktop.Services;

/// <summary>
/// Tracks alive/dead status of all enabled proxies. Checks run asynchronously
/// in the background; UI subscribes to <see cref="StatusChanged"/>.
/// </summary>
public sealed class ProxyStatusService
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    private readonly DatabaseService _databaseService;
    private readonly ProxyValidatorService _proxyValidatorService;
    private readonly ConcurrentDictionary<string, ProxyValidationState> _statuses = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    /// <summary>Raised on the UI thread. Args: proxy id, new state.</summary>
    public event Action<string, ProxyValidationState>? StatusChanged;

    public ProxyStatusService(DatabaseService databaseService, ProxyValidatorService proxyValidatorService)
    {
        _databaseService = databaseService;
        _proxyValidatorService = proxyValidatorService;
    }

    public ProxyValidationState GetStatus(string proxyId)
    {
        return _statuses.TryGetValue(proxyId, out var state) ? state : ProxyValidationState.Unknown;
    }

    /// <summary>
    /// Starts background checks for every enabled proxy that has not been checked yet.
    /// Must be called on the UI thread (reads the database synchronously); the network
    /// checks themselves run off the UI thread.
    /// </summary>
    public void EnsureAllChecked()
    {
        Proxy[] proxies;
        try
        {
            proxies = _databaseService.GetAllProxies().Where(p => p.IsEnabled).ToArray();
        }
        catch
        {
            return;
        }

        foreach (var proxy in proxies)
            EnsureChecked(proxy);
    }

    /// <summary>Starts a background check for a single proxy if its status is unknown.</summary>
    public void EnsureChecked(Proxy proxy)
    {
        if (GetStatus(proxy.Id) != ProxyValidationState.Unknown)
            return;

        _ = CheckProxyAsync(proxy);
    }

    private async Task CheckProxyAsync(Proxy proxy)
    {
        if (!_inFlight.TryAdd(proxy.Id, 0))
            return;

        try
        {
            SetStatus(proxy.Id, ProxyValidationState.Checking);

            var validationTask = _proxyValidatorService.ValidateAsync(proxy);
            var completedTask = await Task.WhenAny(validationTask, Task.Delay(CheckTimeout));

            if (completedTask != validationTask)
            {
                SetStatus(proxy.Id, ProxyValidationState.Failed);
                return;
            }

            var result = await validationTask;
            SetStatus(proxy.Id, result.IsSuccess ? ProxyValidationState.Success : ProxyValidationState.Failed);
        }
        catch
        {
            SetStatus(proxy.Id, ProxyValidationState.Failed);
        }
        finally
        {
            _inFlight.TryRemove(proxy.Id, out _);
        }
    }

    private void SetStatus(string proxyId, ProxyValidationState state)
    {
        _statuses[proxyId] = state;
        Dispatcher.UIThread.Post(() => StatusChanged?.Invoke(proxyId, state));
    }
}
