using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using MsBox.Avalonia.Models;
using YellowFox.Desktop.Models;
using YellowFox.Desktop.Services;

namespace YellowFox.Desktop.ViewModels;

public partial class ProxiesViewModel : ViewModelBase
{
    private readonly DatabaseService _databaseService;
    private readonly ProxyValidatorService _proxyValidatorService;

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
    public string ConnectionStatusText => IsLastCheckSuccessful ? "Active" : "Error";
    public string ConnectionStatusForeground => IsLastCheckSuccessful ? "#6EDB76" : "#FF6E6E";
    public string ConnectionStatusBackground => IsLastCheckSuccessful ? "#153A1B" : "#3A1515";
    public string ConnectionStatusBorder => IsLastCheckSuccessful ? "#2E7D32" : "#A33A3A";

    public ProxiesViewModel(DatabaseService databaseService, ProxyValidatorService proxyValidatorService)
    {
        _databaseService = databaseService;
        _proxyValidatorService = proxyValidatorService;
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
        }

        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(FormTitle));
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
    private void NewProxy()
    {
        SelectedProxy = null;
        ResetForm();
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
        if (!ValidateForm(out var validationError))
        {
            await ShowMessage("Validation", validationError);
            return;
        }

        StatusMessage = "Testing proxy...";
        var proxy = BuildProxy(SelectedProxy?.Proxy ?? new Proxy());
        var result = await _proxyValidatorService.ValidateAsync(proxy);

        if (result.IsSuccess)
        {
            IsLastCheckSuccessful = true;
            StatusMessage = $"OK | IP: {result.ExternalIp ?? "unknown"} | {result.LatencyMs}ms";
        }
        else
        {
            IsLastCheckSuccessful = false;
            StatusMessage = $"FAILED | {result.Error}";
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
            item.SetSuccess(result.ExternalIp, result.LatencyMs);
        else
            item.SetFailed(result.Error);
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
        proxy.IsEnabled = true;
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

    public string TypeDisplay => Proxy.Type.ToUpperInvariant();
    public string Endpoint => $"{Proxy.Host}:{Proxy.Port}";
    public string AuthDisplay => string.IsNullOrWhiteSpace(Proxy.Username) ? "No auth" : Proxy.Username!;
    public string EnabledDisplay => Proxy.IsEnabled ? "Enabled" : "Disabled";
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
    }

    public void SetSuccess(string? externalIp, long latencyMs)
    {
        ValidationState = ProxyValidationState.Success;
        ValidationMessage = string.IsNullOrWhiteSpace(externalIp)
            ? $"OK | {latencyMs}ms"
            : $"OK | {externalIp} | {latencyMs}ms";
    }

    public void SetFailed(string? error)
    {
        ValidationState = ProxyValidationState.Failed;
        ValidationMessage = string.IsNullOrWhiteSpace(error) ? "Failed" : error;
    }
}

public enum ProxyValidationState
{
    Unknown,
    Checking,
    Success,
    Failed
}
