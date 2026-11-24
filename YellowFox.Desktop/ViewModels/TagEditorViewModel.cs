using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YellowFox.Desktop.Models;

namespace YellowFox.Desktop.ViewModels;

public sealed partial class TagColorOption : ObservableObject
{
    public TagColorOption(string color, bool isPreset = true)
    {
        Color = color;
        IsPreset = isPreset;
        Brush = new SolidColorBrush(Avalonia.Media.Color.Parse(color));
    }

    public string Color { get; }
    public bool IsPreset { get; }
    public IBrush Brush { get; }

    [ObservableProperty]
    private bool _isSelected;
}

public partial class TagEditorViewModel : ViewModelBase
{
    private TagColorOption? _customColorOption;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private TagIconOption? _selectedIconOption;

    [ObservableProperty]
    private Color _selectedColor;

    public ObservableCollection<TagIconOption> Icons { get; } = new(TagIconCatalog.Options);
    public ObservableCollection<TagColorOption> Colors { get; } =
        new(TagIconCatalog.Palette.Select(color => new TagColorOption(color)));
    public string Title { get; }
    public string PreviewGlyph => SelectedIconOption?.Glyph ?? TagIconCatalog.GlyphFor(null);
    public string SelectedColorHex => $"#{SelectedColor.R:X2}{SelectedColor.G:X2}{SelectedColor.B:X2}";
    public IBrush PreviewBrush => new SolidColorBrush(SelectedColor);

    public TagEditorViewModel(Tag? tag = null)
    {
        Title = tag == null ? "New Tag" : "Edit Tag";
        SelectedColor = ParseColor(tag?.Color);

        if (tag == null)
        {
            SelectedIconOption = Icons[0];
            return;
        }

        Name = tag.Name;
        var normalized = TagIconCatalog.FromKey(tag.Icon).Key;
        SelectedIconOption = Icons.FirstOrDefault(option => option.Key == normalized) ?? Icons[0];
    }

    partial void OnSelectedIconOptionChanged(TagIconOption? value)
    {
        OnPropertyChanged(nameof(PreviewGlyph));
    }

    partial void OnSelectedColorChanged(Color value)
    {
        var hex = SelectedColorHex;
        SyncCustomColorOption(hex);

        foreach (var option in Colors)
            option.IsSelected = string.Equals(option.Color, hex, StringComparison.OrdinalIgnoreCase);

        OnPropertyChanged(nameof(SelectedColorHex));
        OnPropertyChanged(nameof(PreviewBrush));
    }

    private void SyncCustomColorOption(string hex)
    {
        if (Colors.Any(option => option.IsPreset && string.Equals(option.Color, hex, StringComparison.OrdinalIgnoreCase)))
        {
            if (_customColorOption != null)
            {
                Colors.Remove(_customColorOption);
                _customColorOption = null;
            }

            return;
        }

        if (_customColorOption != null && !string.Equals(_customColorOption.Color, hex, StringComparison.OrdinalIgnoreCase))
        {
            Colors.Remove(_customColorOption);
            _customColorOption = null;
        }

        if (_customColorOption == null)
        {
            _customColorOption = new TagColorOption(hex, isPreset: false);
            Colors.Add(_customColorOption);
        }
    }

    [RelayCommand]
    private void SelectPresetColor(TagColorOption? option)
    {
        if (option != null)
            SelectedColor = Avalonia.Media.Color.Parse(option.Color);
    }

    private static Color ParseColor(string? color)
    {
        if (!string.IsNullOrWhiteSpace(color) && Avalonia.Media.Color.TryParse(color.Trim(), out var parsed))
            return parsed;

        return Avalonia.Media.Color.Parse(TagIconCatalog.DefaultColor);
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
        tag.Color = SelectedColorHex;
        return tag;
    }
}
