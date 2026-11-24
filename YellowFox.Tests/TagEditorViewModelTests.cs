using Avalonia.Media;
using YellowFox.Desktop.Models;
using YellowFox.Desktop.ViewModels;

namespace YellowFox.Tests;

public class TagEditorViewModelTests
{
    [Fact]
    public void Constructor_ShouldSelectColorOutsidePalette()
    {
        var tag = new Tag { Name = "error", Icon = "tag", Color = "#dc2626" };

        var editor = new TagEditorViewModel(tag);

        Assert.Equal("#DC2626", editor.SelectedColorHex);
        Assert.Equal(Color.Parse("#dc2626"), ((SolidColorBrush)editor.PreviewBrush).Color);

        var selected = Assert.Single(editor.Colors, option => option.IsSelected);
        Assert.Equal("#DC2626", selected.Color);
        Assert.False(selected.IsPreset);
    }

    [Fact]
    public void Constructor_ShouldSelectMatchingPaletteColor()
    {
        var tag = new Tag { Name = "active", Icon = "check", Color = "#6EDB76" };

        var editor = new TagEditorViewModel(tag);

        var selected = Assert.Single(editor.Colors, option => option.IsSelected);
        Assert.Equal("#6EDB76", selected.Color);
        Assert.True(selected.IsPreset);
        Assert.All(editor.Colors, option => Assert.True(option.IsPreset));
    }

    [Fact]
    public void SelectPresetColor_ShouldReplaceCustomColorWithPreset()
    {
        var editor = new TagEditorViewModel(new Tag { Name = "error", Color = "#dc2626" });
        var preset = editor.Colors.First(option => option.Color == "#FFB74D");

        editor.SelectPresetColorCommand.Execute(preset);

        Assert.Equal("#FFB74D", editor.SelectedColorHex);
        Assert.All(editor.Colors, option => Assert.True(option.IsPreset));
        Assert.True(preset.IsSelected);
    }

    [Fact]
    public void SelectedColor_ShouldAddCustomSwatchOutsidePalette()
    {
        var editor = new TagEditorViewModel(new Tag { Name = "error", Color = "#dc2626" });

        editor.SelectedColor = Color.Parse("#123456");

        var selected = Assert.Single(editor.Colors, option => option.IsSelected);
        Assert.Equal("#123456", selected.Color);
        Assert.False(selected.IsPreset);
    }

    [Fact]
    public void BuildTag_ShouldPersistCustomColor()
    {
        var tag = new Tag { Name = "error", Color = "#dc2626" };
        var editor = new TagEditorViewModel(tag);
        editor.SelectedColor = Color.Parse("#123456");

        var built = editor.BuildTag(tag);

        Assert.Equal("#123456", built.Color);
        Assert.Equal("error", built.Name);
    }

    [Fact]
    public void Constructor_ShouldFallBackToDefaultColorForInvalidValue()
    {
        var editor = new TagEditorViewModel(new Tag { Name = "error", Color = "not-a-color" });

        Assert.Equal(TagIconCatalog.DefaultColor, editor.SelectedColorHex);
    }
}
