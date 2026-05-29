using System;
using CommunityToolkit.Mvvm.ComponentModel;
using YellowFox.Desktop.Models;

namespace YellowFox.Desktop.ViewModels;

public partial class BookmarkEditorViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _titleText = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _folder = string.Empty;

    public string Title { get; }
    public bool IsFolder { get; }
    public bool IsBookmark => !IsFolder;
    public string ParentId { get; }
    public string ParentDisplay { get; }

    public BookmarkEditorViewModel(BookmarkItem? bookmark = null, bool isFolder = false, string? parentId = null, string? parentDisplay = null)
    {
        IsFolder = bookmark?.IsFolder ?? isFolder;
        ParentId = bookmark?.ParentId ?? parentId ?? string.Empty;
        ParentDisplay = string.IsNullOrWhiteSpace(parentDisplay) ? "Bookmarks Toolbar" : parentDisplay;
        Title = bookmark == null
            ? (IsFolder ? "New Folder" : "New Bookmark")
            : (IsFolder ? "Edit Folder" : "Edit Bookmark");

        if (bookmark == null)
            return;

        TitleText = bookmark.Title;
        Url = bookmark.Url;
        Folder = bookmark.Folder ?? string.Empty;
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

        if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out _))
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
        return bookmark;
    }
}
