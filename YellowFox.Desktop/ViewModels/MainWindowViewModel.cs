using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;
using System;
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
    public TagsViewModel TagsViewModel { get; }

    [ObservableProperty]
    private string _currentSection = "profiles";

    [ObservableProperty]
    private string _camoufoxVersionStatus = "Camoufox: checking...";

    [ObservableProperty]
    private string _browserUpdateStatus = "";

    public Func<Task>? BrowserUpdateRequested { get; set; }

    [RelayCommand]
    private async Task CheckBrowserUpdate()
    {
        if (BrowserUpdateRequested != null)
            await BrowserUpdateRequested();
    }

    public string YellowFoxVersionStatus => $"YellowFox: {YellowFoxBuildInfo.Version}";

    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    public bool IsProfilesSection => CurrentSection == "profiles";
    public bool IsProxiesSection => CurrentSection == "proxies";
    public bool IsExtensionsSection => CurrentSection == "extensions";
    public bool IsBookmarksSection => CurrentSection == "bookmarks";
    public bool IsTagsSection => CurrentSection == "tags";
    public bool IsNotProfilesSection => !IsProfilesSection;
    public bool IsNotProxiesSection => !IsProxiesSection;
    public bool IsNotExtensionsSection => !IsExtensionsSection;
    public bool IsNotBookmarksSection => !IsBookmarksSection;
    public bool IsNotTagsSection => !IsTagsSection;
    public double SidebarWidth => IsSidebarExpanded ? 200 : 74;
    public string SidebarToggleIcon => IsSidebarExpanded ? "\uE72B" : "\uE72A";
    public string SidebarToggleTip => IsSidebarExpanded ? "Collapse navigation" : "Expand navigation";

    public MainWindowViewModel(
        DatabaseService databaseService,
        BrowserService browserService,
        ProxyValidatorService proxyValidatorService,
        ExtensionStorageService extensionStorageService,
        ProxyIpRotationService proxyIpRotationService,
        SettingsService settingsService)
    {
        _browserService = browserService;
        ProfilesViewModel = new ProfilesViewModel(databaseService, browserService, settingsService);
        ProxiesViewModel = new ProxiesViewModel(databaseService, proxyValidatorService, proxyIpRotationService);
        ExtensionsViewModel = new ExtensionsViewModel(databaseService, extensionStorageService);
        BookmarksViewModel = new BookmarksViewModel(databaseService);
        TagsViewModel = new TagsViewModel(databaseService);
        _ = LoadCamoufoxVersionAsync();
    }

    partial void OnCurrentSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsProfilesSection));
        OnPropertyChanged(nameof(IsProxiesSection));
        OnPropertyChanged(nameof(IsExtensionsSection));
        OnPropertyChanged(nameof(IsBookmarksSection));
        OnPropertyChanged(nameof(IsTagsSection));
        OnPropertyChanged(nameof(IsNotProfilesSection));
        OnPropertyChanged(nameof(IsNotProxiesSection));
        OnPropertyChanged(nameof(IsNotExtensionsSection));
        OnPropertyChanged(nameof(IsNotBookmarksSection));
        OnPropertyChanged(nameof(IsNotTagsSection));
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
        ProfilesViewModel.ReloadTagChips();
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

    [RelayCommand]
    private void ShowTags()
    {
        CurrentSection = "tags";
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
