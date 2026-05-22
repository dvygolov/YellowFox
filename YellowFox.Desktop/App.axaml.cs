using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using YellowFox.Desktop.ViewModels;
using YellowFox.Desktop.Views;
using YellowFox.Desktop.Services;

namespace YellowFox.Desktop;

public partial class App : Application
{
    private BrowserService? _browserService;
    private AgentPipeServer? _agentPipeServer;
    
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            
            // Initialize services
            var settingsService = new SettingsService();
            var databaseService = new DatabaseService();
            var proxyValidatorService = new ProxyValidatorService();
            var extensionStorageService = new ExtensionStorageService(databaseService);
            _browserService = new BrowserService(databaseService, settingsService, proxyValidatorService);
            var dolphinImportService = new DolphinImportService(databaseService, _browserService);
            _agentPipeServer = new AgentPipeServer(databaseService, _browserService, proxyValidatorService, dolphinImportService);
            _agentPipeServer.Start();
            
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(databaseService, _browserService, proxyValidatorService, extensionStorageService),
            };
            
            // Handle application exit
            desktop.ShutdownRequested += OnShutdownRequested;
        }

        base.OnFrameworkInitializationCompleted();
    }
    
    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        // Stop all running browsers
        if (_browserService != null)
        {
            await _browserService.StopAllAsync();
        }

        if (_agentPipeServer != null)
        {
            await _agentPipeServer.DisposeAsync();
        }
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
