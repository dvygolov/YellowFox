using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using System;
using System.Threading.Tasks;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using MsBox.Avalonia.Models;
using YellowFox.Desktop.ViewModels;
using YellowFox.Desktop.Views;
using YellowFox.Desktop.Services;

namespace YellowFox.Desktop;

public partial class App : Application
{
    private BrowserService? _browserService;
    private AgentPipeServer? _agentPipeServer;
    private static bool _checkingBrowserUpdate;
    
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
            var proxyIpRotationService = new ProxyIpRotationService();
            var extensionStorageService = new ExtensionStorageService(databaseService);
            var camoufoxUpdateService = new CamoufoxUpdateService(settingsService);
            _browserService = new BrowserService(databaseService, settingsService, proxyValidatorService);
            var dolphinImportService = new DolphinImportService(databaseService, _browserService);
            _agentPipeServer = new AgentPipeServer(databaseService, _browserService, proxyValidatorService, dolphinImportService, extensionStorageService, proxyIpRotationService);
            _agentPipeServer.Start();

            var mainWindowViewModel = new MainWindowViewModel(databaseService, _browserService, proxyValidatorService, extensionStorageService, proxyIpRotationService);
            var mainWindow = new MainWindow
            {
                DataContext = mainWindowViewModel,
            };
            desktop.MainWindow = mainWindow;
            mainWindowViewModel.BrowserUpdateRequested = () => CheckCamoufoxUpdateAsync(mainWindow, mainWindowViewModel, camoufoxUpdateService);
            mainWindow.Opened += async (_, _) => await CheckCamoufoxEnvironmentAsync(mainWindow, mainWindowViewModel, camoufoxUpdateService);

            // Handle application exit
            desktop.ShutdownRequested += OnShutdownRequested;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task CheckCamoufoxEnvironmentAsync(
        Window owner,
        MainWindowViewModel mainWindowViewModel,
        CamoufoxUpdateService camoufoxUpdateService)
    {
        var prerequisites = await camoufoxUpdateService.CheckPrerequisitesAsync();
        switch (prerequisites.State)
        {
            case CamoufoxPrerequisiteState.Ready:
                await CheckCamoufoxUpdateAsync(owner, mainWindowViewModel, camoufoxUpdateService);
                return;

            case CamoufoxPrerequisiteState.PythonMissing:
                await ShowMessageAsync(
                    owner,
                    "Python не найден",
                    "YellowFox не нашёл Python. Для запуска Camoufox нужен Python с установленными зависимостями YellowFox.\n\n" +
                    "Установите Python или создайте окружение python/venv, затем перезапустите YellowFox.");
                return;

            case CamoufoxPrerequisiteState.InstallerMissing:
                await ShowMessageAsync(
                    owner,
                    "Установщик Camoufox не найден",
                    prerequisites.Message);
                return;

            case CamoufoxPrerequisiteState.PythonDependenciesMissing:
                await HandleMissingPythonDependenciesAsync(owner, camoufoxUpdateService);
                return;

            case CamoufoxPrerequisiteState.CamoufoxMissing:
                await HandleMissingCamoufoxAsync(owner, mainWindowViewModel, camoufoxUpdateService);
                return;
        }
    }

    private static async Task HandleMissingPythonDependenciesAsync(
        Window owner,
        CamoufoxUpdateService camoufoxUpdateService)
    {
        var shouldInstall = await ShowYesNoAsync(
            owner,
            "Не установлены зависимости Camoufox",
            "Python найден, но не установлены Python-пакеты, необходимые для Camoufox.\n\n" +
            "Установить зависимости из python/requirements.txt сейчас?");

        if (!shouldInstall)
            return;

        var result = await camoufoxUpdateService.InstallPythonDependenciesAsync();
        await ShowMessageAsync(
            owner,
            result.IsSuccess ? "Зависимости установлены" : "Ошибка установки зависимостей",
            result.IsSuccess
                ? "Python-зависимости Camoufox установлены. Перезапустите YellowFox, чтобы повторить проверку окружения."
                : $"Не удалось установить Python-зависимости.\n\n{result.Message}");
    }

    private static async Task HandleMissingCamoufoxAsync(
        Window owner,
        MainWindowViewModel mainWindowViewModel,
        CamoufoxUpdateService camoufoxUpdateService)
    {
        var shouldInstall = await ShowYesNoAsync(
            owner,
            "Camoufox не установлен",
            "На компьютере не найден установленный браузер Camoufox, поэтому профили YellowFox не смогут запускаться.\n\n" +
            "Установить Camoufox сейчас?");

        if (!shouldInstall)
            return;

        var result = await InstallBrowserWithProgressAsync(owner, camoufoxUpdateService);
        await ShowMessageAsync(
            owner,
            result.IsSuccess ? "Camoufox установлен" : "Ошибка установки Camoufox",
            result.IsSuccess
                ? "Camoufox установлен. YellowFox готов к запуску профилей."
                : $"Не удалось установить Camoufox.\n\n{result.Message}");

        if (result.IsSuccess)
            await mainWindowViewModel.RefreshCamoufoxVersionAsync();
    }

    private static async Task CheckCamoufoxUpdateAsync(
        Window owner,
        MainWindowViewModel mainWindowViewModel,
        CamoufoxUpdateService camoufoxUpdateService)
    {
        if (_checkingBrowserUpdate)
            return;
        _checkingBrowserUpdate = true;
        mainWindowViewModel.BrowserUpdateStatus = "Проверка обновлений ядра…";
        try
        {
            await CheckCamoufoxUpdateCoreAsync(owner, mainWindowViewModel, camoufoxUpdateService);
        }
        finally
        {
            _checkingBrowserUpdate = false;
        }
    }

    private static async Task CheckCamoufoxUpdateCoreAsync(Window owner,
        MainWindowViewModel mainWindowViewModel, CamoufoxUpdateService camoufoxUpdateService)
    {
        var update = await camoufoxUpdateService.CheckForUpdateAsync();
        if (update == null)
        {
            mainWindowViewModel.BrowserUpdateStatus = camoufoxUpdateService.LastCheckError == null
                ? "Ядро обновлено до последней версии"
                : "Проверка не удалась: " + camoufoxUpdateService.LastCheckError;
            return;
        }

        mainWindowViewModel.BrowserUpdateStatus = $"Доступно ядро {update.LatestFolder}";

        var shouldUpdate = await ShowYesNoAsync(
            owner,
            "Обновление Camoufox",
            $"Доступна новая версия Camoufox: {update.LatestFolder}.\n" +
            $"Текущая версия: {update.CurrentFolder}.\n\n" +
            "Будут загружены все файлы официального ядра. Старая сборка сохранится.\n" +
            "Перед установкой закройте профили браузера.\n\nОбновить сейчас?");
        if (!shouldUpdate)
            return;

        var result = await InstallBrowserWithProgressAsync(owner, camoufoxUpdateService, update.LatestFolder);
        await ShowMessageAsync(
            owner,
            result.IsSuccess ? "Camoufox обновлён" : "Ошибка обновления Camoufox",
            result.IsSuccess
                ? $"Camoufox обновлён до {update.LatestFolder}."
                : $"Не удалось обновить Camoufox.\n\n{result.Message}");

        if (result.IsSuccess)
        {
            mainWindowViewModel.BrowserUpdateStatus = "Ядро обновлено";
            await mainWindowViewModel.RefreshCamoufoxVersionAsync();
        }
        else
            mainWindowViewModel.BrowserUpdateStatus = "Обновление не выполнено. Можно повторить проверку.";
    }

    private static async Task<CamoufoxUpdateResult> InstallBrowserWithProgressAsync(
        Window owner, CamoufoxUpdateService service, string? version = null)
    {
        if (service.HasRunningBrowser())
            return CamoufoxUpdateResult.Failure("Закройте профили Camoufox и повторите проверку обновлений кнопкой внизу окна.");

        var status = new TextBlock { Text = "Подготовка загрузки…", TextWrapping = TextWrapping.Wrap };
        var dialog = new Window
        {
            Title = "Обновление ядра Camoufox",
            Width = 500, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(24), Spacing = 16,
                Children =
                {
                    new TextBlock { Text = version == null ? "Установка Camoufox" : $"Установка {version}", FontSize = 18 },
                    status,
                    new ProgressBar { IsIndeterminate = true, Height = 6 },
                    new TextBlock { Text = "Дождитесь окончания загрузки и распаковки.", TextWrapping = TextWrapping.Wrap }
                }
            }
        };
        var finished = false;
        dialog.Closing += (_, e) => e.Cancel = !finished;
        var shown = dialog.ShowDialog(owner);
        try
        {
            return await service.InstallLatestAsync(version: version, progress: new Progress<string>(line => status.Text = line));
        }
        finally
        {
            finished = true;
            dialog.Close();
            await shown;
        }
    }

    private static async Task<bool> ShowYesNoAsync(Window owner, string title, string message)
    {
        var box = MessageBoxManager.GetMessageBoxCustom(
            new MessageBoxCustomParams
            {
                ContentTitle = title,
                ContentMessage = message,
                ButtonDefinitions = new[]
                {
                    new ButtonDefinition { Name = "Да", IsDefault = true },
                    new ButtonDefinition { Name = "Нет", IsCancel = true }
                },
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                MinWidth = 420,
                MaxWidth = 680,
                SizeToContent = SizeToContent.WidthAndHeight
            });

        var answer = await box.ShowWindowDialogAsync(owner);
        return answer == "Да";
    }

    private static async Task ShowMessageAsync(Window owner, string title, string message)
    {
        var box = MessageBoxManager.GetMessageBoxCustom(
            new MessageBoxCustomParams
            {
                ContentTitle = title,
                ContentMessage = message,
                ButtonDefinitions = new[] { new ButtonDefinition { Name = "OK", IsDefault = true } },
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                MinWidth = 420,
                MaxWidth = 680,
                SizeToContent = SizeToContent.WidthAndHeight
            });

        await box.ShowWindowDialogAsync(owner);
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
