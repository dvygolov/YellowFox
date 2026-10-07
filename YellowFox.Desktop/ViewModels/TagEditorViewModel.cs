using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using YellowFox.Desktop.Models;

namespace YellowFox.Desktop.ViewModels;

public sealed class TagColorOption
{
    public TagColorOption(string color)
    {
        Color = color;
        Brush = new SolidColorBrush(Avalonia.Media.Color.Parse(color));
    }

    public string Color { get; }
    public IBrush Brush { get; }
}

public partial class TagEditorViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private TagIconOption? _selectedIconOption;

    [ObservableProperty]
    private TagColorOption? _selectedColorOption;

    public ObservableCollection<TagIconOption> Icons { get; } = new(TagIconCatalog.Options);
    public ObservableCollection<TagColorOption> Colors { get; } =
        new(TagIconCatalog.Palette.Select(color => new TagColorOption(color)));
    public string Title { get; }
    public string PreviewGlyph => SelectedIconOption?.Glyph ?? TagIconCatalog.GlyphFor(null);
    public string PreviewColor => SelectedColorOption?.Color ?? TagIconCatalog.DefaultColor;
    public IBrush PreviewBrush => SelectedColorOption?.Brush ?? Colors[0].Brush;

    public TagEditorViewModel(Tag? tag = null)
    {
        Title = tag == null ? "New Tag" : "Edit Tag";

        if (tag == null)
        {
            SelectedIconOption = Icons[0];
            SelectedColorOption = FindColor(TagIconCatalog.DefaultColor);
            return;
        }

        Name = tag.Name;
        var normalized = TagIconCatalog.FromKey(tag.Icon).Key;
        SelectedIconOption = Icons.FirstOrDefault(option => option.Key == normalized) ?? Icons[0];
        SelectedColorOption = FindColor(tag.Color);
    }

    partial void OnSelectedIconOptionChanged(TagIconOption? value)
    {
        OnPropertyChanged(nameof(PreviewGlyph));
    }

    partial void OnSelectedColorOptionChanged(TagColorOption? value)
    {
        OnPropertyChanged(nameof(PreviewColor));
        OnPropertyChanged(nameof(PreviewBrush));
    }

    private TagColorOption FindColor(string? color)
    {
        if (!string.IsNullOrWhiteSpace(color))
        {
            var match = Colors.FirstOrDefault(option =>
                string.Equals(option.Color, color.Trim(), System.StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        return Colors[0];
    }

    public bool TryValidate(out string validationError)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            validationError = "Tag name is required.";
            return false;
        }

        if (Name.Trim().Length > 32)
        {
            validationError = "Tag name must be 32 characters or fewer.";
            return false;
        }

        validationError = string.Empty;
        return true;
    }

    public Tag BuildTag(Tag tag)
    {
        tag.Name = Name.Trim();
        tag.Icon = SelectedIconOption?.Key ?? TagIconCatalog.DefaultKey;
        tag.Color = SelectedColorOption?.Color ?? TagIconCatalog.DefaultColor;
        return tag;
    }
}
