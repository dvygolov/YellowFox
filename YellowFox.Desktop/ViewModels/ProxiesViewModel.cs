using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using MsBox.Avalonia.Models;
using YellowFox.Desktop.Models;
using YellowFox.Desktop.Services;
using YellowFox.Desktop.Views;

namespace YellowFox.Desktop.ViewModels;

public partial class ProxiesViewModel : ViewModelBase
{
    private readonly DatabaseService _databaseService;
    private readonly ProxyValidatorService _proxyValidatorService;
    private readonly ProxyIpRotationService _proxyIpRotationService;

    [ObservableProperty]
    private ProxyItemViewModel? _selectedProxy;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _type = "http";

    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private int _port;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _ipChangeLink = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private ProxyStatusFilter _statusFilter = ProxyStatusFilter.All;

    private readonly List<ProxyItemViewModel> _allProxies = new();
    private readonly List<ProxyItemViewModel> _selectedProxies = new();

    public ObservableCollection<ProxyItemViewModel> Proxies { get; } = new();

    public ObservableCollection<string> TypeOptions { get; } = new()
    {
        Proxy.HttpType,
        Proxy.Socks5Type
    };

    public bool IsEditMode => SelectedProxy != null;
    public string FormTitle => IsEditMode ? "Edit Proxy" : "New Proxy";
    public bool IsHttpType => string.Equals(Type, "http", StringComparison.OrdinalIgnoreCase);
    public bool IsNotHttpType => !IsHttpType;

    public bool IsSingleSelection => _selectedProxies.Count == 1;
    public bool CanChangeIpForSelection => _selectedProxies.Count > 0 && _selectedProxies.All(p => p.HasIpChangeUrl);
    public string CheckMenuHeader => _selectedProxies.Count > 1 ? $"✅ Check ({_selectedProxies.Count})" : "✅ Check";
    public string ChangeIpMenuHeader => _selectedProxies.Count > 1 ? $"🔁 Change IP ({_selectedProxies.Count})" : "🔁 Change IP";
    public string DeleteMenuHeader => _selectedProxies.Count > 1 ? $"🗑️ Delete ({_selectedProxies.Count})" : "🗑️ Delete";

    public string StatusFilterColor => StatusFilter == ProxyStatusFilter.All ? "#858585" : "#4FA8FF";
    public string StatusFilterToolTip => StatusFilter switch
    {
        ProxyStatusFilter.Valid => "Filter: checked only (click to change)",
        ProxyStatusFilter.Invalid => "Filter: failed only (click to change)",
        _ => "Filter: all (click to change)"
    };

    /// <summary>Called from the view when the grid selection changes.</summary>
    public void SyncSelection(IEnumerable<ProxyItemViewModel> selected)
    {
        _selectedProxies.Clear();
        _selectedProxies.AddRange(selected);
        OnPropertyChanged(nameof(IsSingleSelection));
        OnPropertyChanged(nameof(CanChangeIpForSelection));
        OnPropertyChanged(nameof(CheckMenuHeader));
        OnPropertyChanged(nameof(ChangeIpMenuHeader));
        OnPropertyChanged(nameof(DeleteMenuHeader));
    }

    partial void OnStatusFilterChanged(ProxyStatusFilter value)
    {
        OnPropertyChanged(nameof(StatusFilterColor));
        OnPropertyChanged(nameof(StatusFilterToolTip));
        ApplyFilter();
    }

    public ProxiesViewModel(DatabaseService databaseService, ProxyValidatorService proxyValidatorService, ProxyIpRotationService proxyIpRotationService)
    {
        _databaseService = databaseService;
        _proxyValidatorService = proxyValidatorService;
        _proxyIpRotationService = proxyIpRotationService;
        LoadProxies();
    }

    partial void OnSelectedProxyChanged(ProxyItemViewModel? value)
    {
        if (value == null)
        {
            ResetForm();
        }
        else
        {
            Name = value.Proxy.Name;
            Type = Proxy.NormalizeType(value.Proxy.Type);
            Host = value.Proxy.Host;
            Port = value.Proxy.Port;
            Username = value.Proxy.Username ?? string.Empty;
            Password = value.Proxy.Password ?? string.Empty;
            IpChangeLink = value.Proxy.IpChangeUrl ?? string.Empty;
        }

        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(FormTitle));
    }

    partial void OnTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsHttpType));
        OnPropertyChanged(nameof(IsNotHttpType));
    }

    [RelayCommand]
    private async Task NewProxy()
    {
        var editor = new ProxyEditorViewModel();
        if (!await ShowProxyEditorAsync(editor))
            return;

        try
        {
            var proxy = editor.BuildProxy(new Proxy());
            _databaseService.CreateProxy(proxy);
            StatusMessage = $"Created proxy: {proxy.Name}";
            await RefreshAndCheckAsync();
            SelectedProxy = Proxies.FirstOrDefault(item => item.Proxy.Id == proxy.Id);
        }
        catch (Exception ex)
        {
            await ShowMessage("Error", ex.Message);
        }
    }

    [RelayCommand]
    private async Task EditProxy()
    {
        var target = _selectedProxies.Count == 1 ? _selectedProxies[0] : SelectedProxy;
        if (target == null)
            return;

        var editor = new ProxyEditorViewModel(target.Proxy);
        if (!await ShowProxyEditorAsync(editor))
            return;

        try
        {
            var proxy = editor.BuildProxy(target.Proxy);
            _databaseService.UpdateProxy(proxy);
            StatusMessage = $"Updated proxy: {proxy.Name}";
            await RefreshAndCheckAsync();
            SelectedProxy = Proxies.FirstOrDefault(p => p.Proxy.Id == proxy.Id);
        }
        catch (Exception ex)
        {
            await ShowMessage("Error", ex.Message);
        }
    }

    [RelayCommand]
    private void SetHttpType()
    {
        Type = Proxy.HttpType;
    }

    [RelayCommand]
    private void SetSocks5Type()
    {
        Type = Proxy.Socks5Type;
    }

    [RelayCommand]
    private async Task SaveProxy()
    {
        if (!ValidateForm(out var validationError))
        {
            await ShowMessage("Validation", validationError);
            return;
        }

        try
        {
            if (SelectedProxy == null)
            {
                var proxy = BuildProxy(new Proxy());
                _databaseService.CreateProxy(proxy);
                StatusMessage = $"Created proxy: {proxy.Name}";
            }
            else
            {
                var proxy = BuildProxy(SelectedProxy.Proxy);
                _databaseService.UpdateProxy(proxy);
                StatusMessage = $"Updated proxy: {proxy.Name}";
            }

            await RefreshAndCheckAsync();
            ResetForm();
            SelectedProxy = null;
        }
        catch (Exception ex)
        {
            await ShowMessage("Error", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteProxies()
    {
        var targets = _selectedProxies.Count > 0
            ? _selectedProxies.ToList()
            : (SelectedProxy != null ? new List<ProxyItemViewModel> { SelectedProxy } : new List<ProxyItemViewModel>());
        if (targets.Count == 0)
            return;

        var message = targets.Count == 1
            ? $"Delete proxy '{targets[0].Proxy.Name}'?"
            : $"Delete {targets.Count} proxies?";
        var confirmed = await ConfirmDelete(message);
        if (!confirmed)
            return;

        foreach (var target in targets)
            _databaseService.DeleteProxy(target.Proxy.Id);

        StatusMessage = targets.Count == 1
            ? $"Deleted proxy: {targets[0].Proxy.Name}"
            : $"Deleted {targets.Count} proxies";

        await RefreshAndCheckAsync();
        ResetForm();
        SelectedProxy = null;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task TestProxies()
    {
        var targets = _selectedProxies.Count > 0
            ? _selectedProxies.ToList()
            : (SelectedProxy != null ? new List<ProxyItemViewModel> { SelectedProxy } : new List<ProxyItemViewModel>());
        if (targets.Count == 0)
            return;

        StatusMessage = targets.Count == 1
            ? $"Testing proxy: {targets[0].Proxy.Name}..."
            : $"Checking {targets.Count} proxies...";
        await Task.WhenAll(targets.Select(CheckProxyItemAsync));

        var ok = targets.Count(p => p.ValidationState == ProxyValidationState.Success);
        StatusMessage = targets.Count == 1
            ? (targets[0].ValidationState == ProxyValidationState.Success
                ? $"OK | {targets[0].Proxy.Name} | {targets[0].ValidationMessage}"
                : $"FAILED | {targets[0].Proxy.Name} | {targets[0].ValidationMessage}")
            : $"Checked {targets.Count} proxies: {ok} OK, {targets.Count - ok} failed";
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ChangeIps()
    {
        var targets = (_selectedProxies.Count > 0
                ? _selectedProxies.ToList()
                : (SelectedProxy != null ? new List<ProxyItemViewModel> { SelectedProxy } : new List<ProxyItemViewModel>()))
            .Where(p => p.HasIpChangeUrl)
            .ToList();
        if (targets.Count == 0)
            return;

        var succeeded = 0;
        foreach (var target in targets)
        {
            StatusMessage = $"Changing IP for {target.Proxy.Name}...";
            try
            {
                var rotation = await _proxyIpRotationService.ChangeIpAsync(target.Proxy);
                if (!rotation.Success)
                {
                    target.SetFailed(rotation.Message);
                    StatusMessage = $"IP change failed for {target.Proxy.Name}: {rotation.Message}";
                    continue;
                }

                await CheckProxyItemAsync(target);
                if (target.ValidationState == ProxyValidationState.Success)
                    succeeded++;
            }
            catch (Exception ex)
            {
                target.SetFailed(ex.Message);
                StatusMessage = $"IP change failed for {target.Proxy.Name}: {ex.Message}";
            }
        }

        StatusMessage = targets.Count == 1
            ? (succeeded == 1
                ? $"IP changed: {targets[0].ValidationMessage}"
                : $"IP change requested, recheck failed: {targets[0].ValidationMessage}")
            : $"IP change done: {succeeded}/{targets.Count} succeeded";
    }

    [RelayCommand]
    private void SetStatusFilter(string? filter)
    {
        if (Enum.TryParse<ProxyStatusFilter>(filter, ignoreCase: true, out var parsed))
            StatusFilter = parsed;
    }

    [RelayCommand]
    private async Task Refresh()
    {
        await RefreshAndCheckAsync();
    }

    public async Task RefreshAndCheckAsync()
    {
        LoadProxies();
        if (_allProxies.Count == 0)
        {
            StatusMessage = "No proxies";
            return;
        }

        StatusMessage = $"Checking {_allProxies.Count} proxies...";
        var tasks = _allProxies.Select(CheckProxyItemAsync).ToArray();
        await Task.WhenAll(tasks);

        var ok = _allProxies.Count(proxy => proxy.ValidationState == ProxyValidationState.Success);
        var failed = _allProxies.Count(proxy => proxy.ValidationState == ProxyValidationState.Failed);
        StatusMessage = $"Checked proxies: {ok} OK, {failed} failed";
    }

    private void LoadProxies()
    {
        _allProxies.Clear();
        var proxies = _databaseService.GetAllProxies();
        var profileCounts = _databaseService.GetAllProfiles()
            .Where(profile => !string.IsNullOrWhiteSpace(profile.ProxyId))
            .GroupBy(profile => profile.ProxyId!)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        foreach (var proxy in proxies)
        {
            profileCounts.TryGetValue(proxy.Id, out var profileCount);
            _allProxies.Add(new ProxyItemViewModel(proxy, profileCount));
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Proxies.Clear();
        foreach (var item in _allProxies.Where(MatchesFilter))
            Proxies.Add(item);
    }

    private bool MatchesFilter(ProxyItemViewModel item)
    {
        return StatusFilter switch
        {
            ProxyStatusFilter.Valid => item.ValidationState == ProxyValidationState.Success,
            ProxyStatusFilter.Invalid => item.ValidationState == ProxyValidationState.Failed,
            _ => true
        };
    }

    private async Task CheckProxyItemAsync(ProxyItemViewModel item)
    {
        item.SetChecking();
        var validationTask = _proxyValidatorService.ValidateAsync(item.Proxy);
        var completedTask = await Task.WhenAny(validationTask, Task.Delay(TimeSpan.FromSeconds(15)));

        if (completedTask != validationTask)
        {
            item.SetFailed("Timeout");
            return;
        }

        var result = await validationTask;
        if (result.IsSuccess)
        {
            var countryFlagPath = await GetCountryFlagPathAsync(result.CountryCode);
            item.SetSuccess(result.ExternalIp, result.LatencyMs, result.CountryCode, result.CountryName, countryFlagPath);
        }
        else
        {
            item.SetFailed(result.Error);
        }
    }

    private static async Task<string?> GetCountryFlagPathAsync(string? countryCode)
    {
        try
        {
            return await CountryFlagCache.GetFlagPathAsync(countryCode);
        }
        catch
        {
            return null;
        }
    }

    private void ResetForm()
    {
        Name = string.Empty;
        Type = Proxy.HttpType;
        Host = string.Empty;
        Port = 0;
        Username = string.Empty;
        Password = string.Empty;
        IpChangeLink = string.Empty;
    }

    private bool ValidateForm(out string validationError)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            validationError = "Name is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            validationError = "Host is required.";
            return false;
        }

        if (Port <= 0 || Port > 65535)
        {
            validationError = "Port must be between 1 and 65535.";
            return false;
        }

        if (!TypeOptions.Contains(Type, StringComparer.OrdinalIgnoreCase))
        {
            validationError = "Proxy type must be http or socks5.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(IpChangeLink) &&
            (!Uri.TryCreate(IpChangeLink.Trim(), UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            validationError = "IP change URL must be a valid http/https URL.";
            return false;
        }

        validationError = string.Empty;
        return true;
    }

    private Proxy BuildProxy(Proxy proxy)
    {
        proxy.Name = Name.Trim();
        proxy.Type = Proxy.NormalizeType(Type);
        proxy.Host = Host.Trim();
        proxy.Port = Port;
        proxy.Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();
        proxy.Password = string.IsNullOrWhiteSpace(Password) ? null : Password;
        proxy.IpChangeUrl = string.IsNullOrWhiteSpace(IpChangeLink) ? null : IpChangeLink.Trim();
        return proxy;
    }

    private async Task<bool> ConfirmDelete(string message)
    {
        var mainWindow = GetMainWindow();
        var box = MessageBoxManager.GetMessageBoxCustom(
            new MessageBoxCustomParams
            {
                ContentTitle = "Delete Proxy",
                ContentMessage = message,
                ButtonDefinitions = new[]
                {
                    new ButtonDefinition { Name = "Yes", IsDefault = true },
                    new ButtonDefinition { Name = "No", IsCancel = true }
                },
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                MinWidth = 360,
                MaxWidth = 560,
                SizeToContent = SizeToContent.WidthAndHeight
            });

        var result = await box.ShowWindowDialogAsync(mainWindow!);
        return result == "Yes";
    }

    private async Task ShowMessage(string title, string message)
    {
        var mainWindow = GetMainWindow();
        var box = MessageBoxManager.GetMessageBoxCustom(
            new MessageBoxCustomParams
            {
                ContentTitle = title,
                ContentMessage = message,
                ButtonDefinitions = new[] { new ButtonDefinition { Name = "OK", IsDefault = true } },
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                MinWidth = 360,
                MaxWidth = 560,
                SizeToContent = SizeToContent.WidthAndHeight
            });

        await box.ShowWindowDialogAsync(mainWindow!);
    }

    private async Task<bool> ShowProxyEditorAsync(ProxyEditorViewModel editor)
    {
        var window = new ProxyEditorWindow
        {
            DataContext = editor
        };

        return await window.ShowDialog<bool>(GetMainWindow());
    }

    private Window GetMainWindow()
    {
        return Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow!
            : throw new InvalidOperationException("Main window not found");
    }
}

public partial class ProxyItemViewModel : ViewModelBase
{
    public Proxy Proxy { get; }

    [ObservableProperty]
    private ProxyValidationState _validationState = ProxyValidationState.Unknown;

    [ObservableProperty]
    private string _validationMessage = "Not checked";

    [ObservableProperty]
    private Bitmap? _countryFlagImage;

    [ObservableProperty]
    private bool _hasCountryFlagImage;

    [ObservableProperty]
    private string _countryCodeFallback = string.Empty;

    [ObservableProperty]
    private bool _hasCountryCodeFallback;

    [ObservableProperty]
    private string _countryToolTip = string.Empty;

    public int ProfileCount { get; }
    public string TypeLabel => string.Equals(Proxy.NormalizeType(Proxy.Type), Proxy.Socks5Type, StringComparison.Ordinal) ? "S5" : "HTTP";
    public string TypeToolTip => string.Equals(Proxy.NormalizeType(Proxy.Type), Proxy.Socks5Type, StringComparison.Ordinal) ? "SOCKS5 proxy" : "HTTP proxy";
    public string TypeBadgeForeground => string.Equals(Proxy.NormalizeType(Proxy.Type), Proxy.Socks5Type, StringComparison.Ordinal) ? "#C9A7FF" : "#7CC4FF";
    public string TypeBadgeBackground => string.Equals(Proxy.NormalizeType(Proxy.Type), Proxy.Socks5Type, StringComparison.Ordinal) ? "#2E2145" : "#16324A";
    public string Endpoint => $"{Proxy.Host}:{Proxy.Port}";
    public string EnabledDisplay => Proxy.IsEnabled ? "Enabled" : "Disabled";
    public bool HasIpChangeUrl => !string.IsNullOrWhiteSpace(Proxy.IpChangeUrl);
    public int StatusSortRank => ValidationState switch
    {
        ProxyValidationState.Failed => 0,
        ProxyValidationState.Unknown => 1,
        ProxyValidationState.Checking => 2,
        ProxyValidationState.Success => 3,
        _ => 1
    };
    public string StatusColor => ValidationState switch
    {
        ProxyValidationState.Success => "#6EDB76",
        ProxyValidationState.Failed => "#FF5F5F",
        ProxyValidationState.Checking => "#4FA8FF",
        _ => "#777D84"
    };

    public string StatusBorderColor => ValidationState switch
    {
        ProxyValidationState.Success => "#2E7D32",
        ProxyValidationState.Failed => "#A33A3A",
        ProxyValidationState.Checking => "#1F5C92",
        _ => "#55595E"
    };

    public string StatusGlyph => ValidationState switch
    {
        ProxyValidationState.Success => "\uE73E",
        ProxyValidationState.Failed => "\uE711",
        ProxyValidationState.Checking => "\uE895",
        _ => "\uE738"
    };

    public string StatusForeground => ValidationState switch
    {
        ProxyValidationState.Success => "#6EDB76",
        ProxyValidationState.Failed => "#FF5F5F",
        ProxyValidationState.Checking => "#4FA8FF",
        _ => "#777D84"
    };

    public string StatusChipBackground => ValidationState switch
    {
        ProxyValidationState.Success => "#1B3A22",
        ProxyValidationState.Failed => "#3A1B1B",
        ProxyValidationState.Checking => "#152C42",
        _ => "#2A2E33"
    };

    public bool IsChecking => ValidationState == ProxyValidationState.Checking;
    public bool IsNotChecking => !IsChecking;

    public ProxyItemViewModel(Proxy proxy, int profileCount)
    {
        Proxy = proxy;
        ProfileCount = profileCount;
    }

    partial void OnValidationStateChanged(ProxyValidationState value)
    {
        OnPropertyChanged(nameof(StatusSortRank));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(StatusBorderColor));
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(StatusForeground));
        OnPropertyChanged(nameof(StatusChipBackground));
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(IsNotChecking));
    }

    public void SetChecking()
    {
        ValidationState = ProxyValidationState.Checking;
        ValidationMessage = "Checking...";
        CountryFlagImage = null;
        HasCountryFlagImage = false;
        CountryCodeFallback = string.Empty;
        HasCountryCodeFallback = false;
        CountryToolTip = string.Empty;
    }

    public void SetSuccess(string? externalIp, long latencyMs, string? countryCode, string? countryName, string? countryFlagPath)
    {
        ValidationState = ProxyValidationState.Success;
        CountryFlagImage = LoadFlagImage(countryFlagPath);
        HasCountryFlagImage = CountryFlagImage != null;
        CountryCodeFallback = HasCountryFlagImage ? string.Empty : FormatCountryCodeFallback(countryCode);
        HasCountryCodeFallback = !string.IsNullOrWhiteSpace(CountryCodeFallback);
        CountryToolTip = FormatCountryToolTip(countryCode, countryName);
        ValidationMessage = string.IsNullOrWhiteSpace(externalIp)
            ? $"OK | {latencyMs}ms"
            : FormatSuccessMessage(externalIp, latencyMs);
    }

    public void SetFailed(string? error)
    {
        ValidationState = ProxyValidationState.Failed;
        ValidationMessage = string.IsNullOrWhiteSpace(error) ? "Failed" : error;
        CountryFlagImage = null;
        HasCountryFlagImage = false;
        CountryCodeFallback = string.Empty;
        HasCountryCodeFallback = false;
        CountryToolTip = string.Empty;
    }

    private static string FormatSuccessMessage(string externalIp, long latencyMs)
    {
        return $"OK | {externalIp} | {latencyMs}ms";
    }

    private static string FormatCountryToolTip(string? countryCode, string? countryName)
    {
        if (!string.IsNullOrWhiteSpace(countryName) && !string.IsNullOrWhiteSpace(countryCode))
            return $"{countryName} ({countryCode})";

        return countryName ?? countryCode ?? string.Empty;
    }

    private static Bitmap? LoadFlagImage(string? countryFlagPath)
    {
        if (string.IsNullOrWhiteSpace(countryFlagPath))
            return null;

        try
        {
            return new Bitmap(countryFlagPath);
        }
        catch
        {
            TryDeleteBrokenFlag(countryFlagPath);
            return null;
        }
    }

    private static void TryDeleteBrokenFlag(string countryFlagPath)
    {
        try
        {
            File.Delete(countryFlagPath);
        }
        catch
        {
            // The next refresh can keep using the fallback country code.
        }
    }

    private static string FormatCountryCodeFallback(string? countryCode)
    {
        var normalized = countryCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length != 2)
            return string.Empty;

        if (!normalized.All(ch => ch >= 'A' && ch <= 'Z'))
            return string.Empty;

        return normalized;
    }
}

public enum ProxyValidationState
{
    Unknown,
    Checking,
    Success,
    Failed
}

public enum ProxyStatusFilter
{
    All,
    Valid,
    Invalid
}
