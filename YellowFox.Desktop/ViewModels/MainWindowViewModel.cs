using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;
using YellowFox.Desktop;
using YellowFox.Desktop.Services;

namespace YellowFox.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly BrowserService _browserService;

    public ProfilesViewModel ProfilesViewModel { get; }
    public ProxiesViewModel ProxiesViewModel { get; }
    public ExtensionsViewModel ExtensionsViewModel { get; }
    public BookmarksViewModel BookmarksViewModel { get; }

    [ObservableProperty]
    private string _currentSection = "profiles";

    [ObservableProperty]
    private string _camoufoxVersionStatus = "Camoufox: checking...";

    public string YellowFoxVersionStatus => $"YellowFox: {YellowFoxBuildInfo.Version}";

    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    public bool IsProfilesSection => CurrentSection == "profiles";
    public bool IsProxiesSection => CurrentSection == "proxies";
    public bool IsExtensionsSection => CurrentSection == "extensions";
    public bool IsBookmarksSection => CurrentSection == "bookmarks";
    public bool IsNotProfilesSection => !IsProfilesSection;
    public bool IsNotProxiesSection => !IsProxiesSection;
    public bool IsNotExtensionsSection => !IsExtensionsSection;
    public bool IsNotBookmarksSection => !IsBookmarksSection;
    public double SidebarWidth => IsSidebarExpanded ? 200 : 74;
    public string SidebarToggleIcon => IsSidebarExpanded ? "\uE72B" : "\uE72A";
    public string SidebarToggleTip => IsSidebarExpanded ? "Collapse navigation" : "Expand navigation";

    public MainWindowViewModel(
        DatabaseService databaseService,
        BrowserService browserService,
        ProxyValidatorService proxyValidatorService,
        ExtensionStorageService extensionStorageService)
    {
        _browserService = browserService;
        ProfilesViewModel = new ProfilesViewModel(databaseService, browserService);
        ProxiesViewModel = new ProxiesViewModel(databaseService, proxyValidatorService);
        ExtensionsViewModel = new ExtensionsViewModel(databaseService, extensionStorageService);
        BookmarksViewModel = new BookmarksViewModel(databaseService);
        _ = LoadCamoufoxVersionAsync();
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

    partial void OnIsSidebarExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(SidebarWidth));
        OnPropertyChanged(nameof(SidebarToggleIcon));
        OnPropertyChanged(nameof(SidebarToggleTip));
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarExpanded = !IsSidebarExpanded;
    }

    [RelayCommand]
    private void ShowProfiles()
    {
        CurrentSection = "profiles";
    }

    [RelayCommand]
    private async Task ShowProxies()
    {
        CurrentSection = "proxies";
        await ProxiesViewModel.RefreshAndCheckAsync();
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

    private async Task LoadCamoufoxVersionAsync()
    {
        CamoufoxVersionStatus = await _browserService.GetCamoufoxVersionDisplayAsync();
    }

    public Task RefreshCamoufoxVersionAsync()
    {
        return LoadCamoufoxVersionAsync();
    }
}
