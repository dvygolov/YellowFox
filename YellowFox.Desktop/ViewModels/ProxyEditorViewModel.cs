using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using YellowFox.Desktop.Models;

namespace YellowFox.Desktop.ViewModels;

public partial class ProxyEditorViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _type = Proxy.HttpType;

    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private int _port;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    public ObservableCollection<string> TypeOptions { get; } = new()
    {
        Proxy.HttpType,
        Proxy.Socks5Type
    };

    public string Title { get; }

    public ProxyEditorViewModel(Proxy? proxy = null)
    {
        Title = proxy == null ? "New Proxy" : "Edit Proxy";

        if (proxy == null)
            return;

        Name = proxy.Name;
        Type = Proxy.NormalizeType(proxy.Type);
        Host = proxy.Host;
        Port = proxy.Port;
        Username = proxy.Username ?? string.Empty;
        Password = proxy.Password ?? string.Empty;
    }

    public bool TryValidate(out string validationError)
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

    public Proxy BuildProxy(Proxy proxy)
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
}
