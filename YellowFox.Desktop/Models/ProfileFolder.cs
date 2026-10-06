using System;

namespace YellowFox.Desktop.Models;

public class ProfileFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public int SortOrder { get; set; }
}
