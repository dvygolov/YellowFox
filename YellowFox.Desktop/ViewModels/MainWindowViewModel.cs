using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YellowFox.Desktop.Services;

namespace YellowFox.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public ProfilesViewModel ProfilesViewModel { get; }
    public ProxiesViewModel ProxiesViewModel { get; }
    public ExtensionsViewModel ExtensionsViewModel { get; }
    public BookmarksViewModel BookmarksViewModel { get; }

    [ObservableProperty]
    private string _currentSection = "profiles";

    public bool IsProfilesSection => CurrentSection == "profiles";
    public bool IsProxiesSection => CurrentSection == "proxies";
    public bool IsExtensionsSection => CurrentSection == "extensions";
    public bool IsBookmarksSection => CurrentSection == "bookmarks";
    public bool IsNotProfilesSection => !IsProfilesSection;
    public bool IsNotProxiesSection => !IsProxiesSection;
    public bool IsNotExtensionsSection => !IsExtensionsSection;
    public bool IsNotBookmarksSection => !IsBookmarksSection;

    public MainWindowViewModel(
        DatabaseService databaseService,
        BrowserService browserService,
        ProxyValidatorService proxyValidatorService,
        ExtensionStorageService extensionStorageService)
    {
        ProfilesViewModel = new ProfilesViewModel(databaseService, browserService);
        ProxiesViewModel = new ProxiesViewModel(databaseService, proxyValidatorService);
        ExtensionsViewModel = new ExtensionsViewModel(databaseService, extensionStorageService);
        BookmarksViewModel = new BookmarksViewModel(databaseService);
    }

    partial void OnCurrentSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsProfilesSection));
        OnPropertyChanged(nameof(IsProxiesSection));
        OnPropertyChanged(nameof(IsExtensionsSection));
        OnPropertyChanged(nameof(IsBookmarksSection));
        OnPropertyChanged(nameof(IsNotProfilesSection));
        OnPropertyChanged(nameof(IsNotProxiesSection));
        OnPropertyChanged(nameof(IsNotExtensionsSection));
        OnPropertyChanged(nameof(IsNotBookmarksSection));
    }

    [RelayCommand]
    private void ShowProfiles()
    {
        CurrentSection = "profiles";
    }

    [RelayCommand]
    private void ShowProxies()
    {
        CurrentSection = "proxies";
    }

    [RelayCommand]
    private void ShowExtensions()
    {
        CurrentSection = "extensions";
    }

    [RelayCommand]
    private void ShowBookmarks()
    {
        CurrentSection = "bookmarks";
    }
}
