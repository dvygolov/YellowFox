using System;
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
    private bool _isLastCheckSuccessful = true;

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
    public bool CanChangeIp => SelectedProxy?.HasIpChangeUrl == true;
    public string ConnectionStatusText => IsLastCheckSuccessful ? "Active" : "Error";
    public string ConnectionStatusForeground => IsLastCheckSuccessful ? "#6EDB76" : "#FF6E6E";
    public string ConnectionStatusBackground => IsLastCheckSuccessful ? "#153A1B" : "#3A1515";
    public string ConnectionStatusBorder => IsLastCheckSuccessful ? "#2E7D32" : "#A33A3A";

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
        OnPropertyChanged(nameof(CanChangeIp));
    }

    partial void OnTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsHttpType));
        OnPropertyChanged(nameof(IsNotHttpType));
    }

    partial void OnIsLastCheckSuccessfulChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(ConnectionStatusForeground));
        OnPropertyChanged(nameof(ConnectionStatusBackground));
        OnPropertyChanged(nameof(ConnectionStatusBorder));
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
        if (SelectedProxy == null)
            return;

        var editor = new ProxyEditorViewModel(SelectedProxy.Proxy);
        if (!await ShowProxyEditorAsync(editor))
            return;

        try
        {
            var proxy = editor.BuildProxy(SelectedProxy.Proxy);
            _databaseService.UpdateProxy(proxy);
            StatusMessage = $"Updated proxy: {proxy.Name}";
            await RefreshAndCheckAsync();
            SelectedProxy = Proxies.FirstOrDefault(item => item.Proxy.Id == proxy.Id);
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
    private async Task DeleteProxy()
    {
        if (SelectedProxy == null)
            return;

        var confirmed = await ConfirmDelete(SelectedProxy.Proxy.Name);
        if (!confirmed)
            return;

        _databaseService.DeleteProxy(SelectedProxy.Proxy.Id);
        StatusMessage = $"Deleted proxy: {SelectedProxy.Proxy.Name}";

        await RefreshAndCheckAsync();
        ResetForm();
        SelectedProxy = null;
    }

    [RelayCommand]
    private async Task TestProxy()
    {
        if (SelectedProxy == null)
            return;

        StatusMessage = "Testing proxy...";
        var selectedItem = SelectedProxy;
        var proxy = selectedItem.Proxy;
        var result = await _proxyValidatorService.ValidateAsync(proxy);

        if (result.IsSuccess)
        {
            IsLastCheckSuccessful = true;
            var countryFlagPath = await GetCountryFlagPathAsync(result.CountryCode);
            selectedItem.SetSuccess(result.ExternalIp, result.LatencyMs, result.CountryCode, result.CountryName, countryFlagPath);
            StatusMessage = FormatValidationMessage(result.ExternalIp, result.LatencyMs);
        }
        else
        {
            IsLastCheckSuccessful = false;
            selectedItem.SetFailed(result.Error);
            StatusMessage = $"FAILED | {result.Error}";
        }
    }

    [RelayCommand]
    private async Task ChangeIp()
    {
        if (SelectedProxy == null || !SelectedProxy.HasIpChangeUrl)
            return;

        var selectedItem = SelectedProxy;
        StatusMessage = $"Changing IP for {selectedItem.Proxy.Name}...";
        try
        {
            var rotation = await _proxyIpRotationService.ChangeIpAsync(selectedItem.Proxy);
            if (!rotation.Success)
            {
                selectedItem.SetFailed(rotation.Message);
                IsLastCheckSuccessful = false;
                StatusMessage = $"IP change failed: {rotation.Message}";
                return;
            }

            StatusMessage = "IP change requested. Rechecking proxy...";
            await CheckProxyItemAsync(selectedItem);
            StatusMessage = selectedItem.ValidationState == ProxyValidationState.Success
                ? $"IP changed: {selectedItem.ValidationMessage}"
                : $"IP change requested, recheck failed: {selectedItem.ValidationMessage}";
        }
        catch (Exception ex)
        {
            selectedItem.SetFailed(ex.Message);
            IsLastCheckSuccessful = false;
            StatusMessage = $"IP change failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Refresh()
    {
        await RefreshAndCheckAsync();
    }

    public async Task RefreshAndCheckAsync()
    {
        LoadProxies();
        if (Proxies.Count == 0)
        {
            StatusMessage = "No proxies";
            return;
        }

        StatusMessage = $"Checking {Proxies.Count} proxies...";
        var tasks = Proxies.Select(CheckProxyItemAsync).ToArray();
        await Task.WhenAll(tasks);

        var ok = Proxies.Count(proxy => proxy.ValidationState == ProxyValidationState.Success);
        var failed = Proxies.Count(proxy => proxy.ValidationState == ProxyValidationState.Failed);
        StatusMessage = $"Checked proxies: {ok} OK, {failed} failed";
    }

    private void LoadProxies()
    {
        Proxies.Clear();
        var proxies = _databaseService.GetAllProxies();
        foreach (var proxy in proxies)
        {
            Proxies.Add(new ProxyItemViewModel(proxy));
        }
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

    private static string FormatValidationMessage(string? externalIp, long latencyMs)
    {
        return $"OK | IP: {externalIp ?? "unknown"} | {latencyMs}ms";
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
        IsLastCheckSuccessful = true;
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

    private async Task<bool> ConfirmDelete(string proxyName)
    {
        var mainWindow = GetMainWindow();
        var box = MessageBoxManager.GetMessageBoxCustom(
            new MessageBoxCustomParams
            {
                ContentTitle = "Delete Proxy",
                ContentMessage = $"Delete proxy '{proxyName}'?",
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

    public string TypeDisplay => Proxy.Type.ToUpperInvariant();
    public string Endpoint => $"{Proxy.Host}:{Proxy.Port}";
    public string AuthDisplay => string.IsNullOrWhiteSpace(Proxy.Username) ? "No auth" : Proxy.Username!;
    public string EnabledDisplay => Proxy.IsEnabled ? "Enabled" : "Disabled";
    public bool HasIpChangeUrl => !string.IsNullOrWhiteSpace(Proxy.IpChangeUrl);
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

    public ProxyItemViewModel(Proxy proxy)
    {
        Proxy = proxy;
    }

    partial void OnValidationStateChanged(ProxyValidationState value)
    {
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(StatusBorderColor));
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
