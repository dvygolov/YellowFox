using Avalonia.Media;
using YellowFox.Desktop.Models;

namespace YellowFox.Desktop.ViewModels;

public sealed class TagOption
{
    public TagOption(Tag? tag)
    {
        Tag = tag;
        if (tag != null)
            Brush = new SolidColorBrush(Color.Parse(tag.Color));
    }

    public Tag? Tag { get; }
    public string? Id => Tag?.Id;
    public string DisplayName => Tag?.Name ?? "No tag";
    public string Glyph => Tag == null ? "\uE738" : TagIconCatalog.GlyphFor(Tag.Icon);
    public IBrush? Brush { get; }
}
