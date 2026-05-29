using System;

namespace YellowFox.Desktop.Models;

public class BookmarkItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Folder { get; set; }
    public string? ParentId { get; set; }
    public bool IsFolder { get; set; }
    public int SortOrder { get; set; }
}
