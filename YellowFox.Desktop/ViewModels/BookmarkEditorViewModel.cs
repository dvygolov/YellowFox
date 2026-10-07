using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using YellowFox.Desktop.Models;
using YellowFox.Desktop.Services;

namespace YellowFox.Desktop.ViewModels;

public partial class BookmarkEditorViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _titleText = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _folder = string.Empty;

    [ObservableProperty]
    private TagOption? _selectedTagOption;

    public ObservableCollection<TagOption> TagOptions { get; } = new();
    public string Title { get; }
    public bool IsFolder { get; }
    public bool IsBookmark => !IsFolder;
    public string ParentId { get; }
    public string ParentDisplay { get; }

    public BookmarkEditorViewModel(DatabaseService databaseService, BookmarkItem? bookmark = null, bool isFolder = false, string? parentId = null, string? parentDisplay = null)
    {
        IsFolder = bookmark?.IsFolder ?? isFolder;
        ParentId = bookmark?.ParentId ?? parentId ?? string.Empty;
        ParentDisplay = string.IsNullOrWhiteSpace(parentDisplay) ? "Bookmarks Toolbar" : parentDisplay;
        Title = bookmark == null
            ? (IsFolder ? "New Folder" : "New Bookmark")
            : (IsFolder ? "Edit Folder" : "Edit Bookmark");

        LoadTags(databaseService, bookmark?.TagId);

        if (bookmark == null)
            return;

        TitleText = bookmark.Title;
        Url = bookmark.Url;
        Folder = bookmark.Folder ?? string.Empty;
    }

    private void LoadTags(DatabaseService databaseService, string? selectedTagId)
    {
        TagOptions.Clear();
        TagOptions.Add(new TagOption(null));
        foreach (var tag in databaseService.GetAllTags())
            TagOptions.Add(new TagOption(tag));

        SelectedTagOption = TagOptions.FirstOrDefault(option => option.Id == selectedTagId) ?? TagOptions[0];
    }

    public bool TryValidate(out string validationError)
    {
        if (string.IsNullOrWhiteSpace(TitleText))
        {
            validationError = IsFolder ? "Folder name is required." : "Title is required.";
            return false;
        }

        if (IsFolder)
        {
            validationError = string.Empty;
            return true;
        }

        if (string.IsNullOrWhiteSpace(Url))
        {
            validationError = "URL is required.";
            return false;
        }

        var trimmedUrl = Url.Trim();
        if (trimmedUrl.StartsWith("javascript:", System.StringComparison.OrdinalIgnoreCase))
        {
            validationError = string.Empty;
            return true;
        }

        if (!System.Uri.TryCreate(trimmedUrl, System.UriKind.Absolute, out _))
        {
            validationError = "URL is invalid.";
            return false;
        }

        validationError = string.Empty;
        return true;
    }

    public BookmarkItem BuildBookmark(BookmarkItem bookmark)
    {
        bookmark.Title = TitleText.Trim();
        bookmark.Url = IsFolder ? string.Empty : Url.Trim();
        bookmark.Folder = string.IsNullOrWhiteSpace(Folder) ? null : Folder.Trim();
        bookmark.ParentId = string.IsNullOrWhiteSpace(ParentId) ? null : ParentId;
        bookmark.IsFolder = IsFolder;
        bookmark.TagId = SelectedTagOption?.Id;
        return bookmark;
    }
}
