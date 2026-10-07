using System;
using System.Collections.Generic;
using System.Linq;

namespace YellowFox.Desktop.Models;

public class Tag
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = TagIconCatalog.DefaultKey;
    public string Color { get; set; } = TagIconCatalog.DefaultColor;
    public int SortOrder { get; set; }
}

public sealed class TagIconOption
{
    public TagIconOption(string key, string displayName, string glyph, string defaultColor)
    {
        Key = key;
        DisplayName = displayName;
        Glyph = glyph;
        DefaultColor = defaultColor;
    }

    public string Key { get; }
    public string DisplayName { get; }
    public string Glyph { get; }
    public string DefaultColor { get; }
}

public static class TagIconCatalog
{
    public const string DefaultKey = "tag";
    public const string DefaultColor = "#4FA8FF";

    public static readonly IReadOnlyList<string> Palette = new[]
    {
        "#4FA8FF",
        "#6EDB76",
        "#FFB74D",
        "#FF6E6E",
        "#B388FF",
        "#4DD0E1",
        "#F06292",
        "#AED581",
        "#FFD54F",
        "#90A4AE",
        "#FF8A65",
        "#64B5F6"
    };

    public static readonly IReadOnlyList<TagIconOption> Options = new[]
    {
        new TagIconOption("tag", "Tag", "\uE8EC", "#4FA8FF"),
        new TagIconOption("star", "Star", "\uE735", "#FFD54F"),
        new TagIconOption("flag", "Flag", "\uE7C1", "#FF6E6E"),
        new TagIconOption("folder", "Folder", "\uE8B7", "#FFB74D"),
        new TagIconOption("people", "People", "\uE716", "#4DD0E1"),
        new TagIconOption("globe", "Globe", "\uE774", "#64B5F6"),
        new TagIconOption("shop", "Shop", "\uE719", "#F06292"),
        new TagIconOption("heart", "Heart", "\uEB51", "#FF6E6E"),
        new TagIconOption("lock", "Lock", "\uE72E", "#90A4AE"),
        new TagIconOption("warning", "Warning", "\uE7BA", "#FFB74D"),
        new TagIconOption("check", "Check", "\uE73E", "#6EDB76"),
        new TagIconOption("library", "Library", "\uE8F1", "#B388FF"),
        new TagIconOption("newfolder", "New folder", "\uE8F4", "#FFB74D"),
        new TagIconOption("game", "Game", "\uE7FC", "#AED581"),
        new TagIconOption("contact", "Contact", "\uE77B", "#4DD0E1"),
        new TagIconOption("pin", "Pin", "\uE718", "#FF8A65"),
        new TagIconOption("eye", "Eye", "\uE7B3", "#90A4AE"),
        new TagIconOption("mail", "Mail", "\uE715", "#64B5F6"),
        new TagIconOption("phone", "Phone", "\uE717", "#6EDB76"),
        new TagIconOption("camera", "Camera", "\uE722", "#F06292")
    };

    public static TagIconOption FromKey(string? key)
    {
        var normalized = key?.Trim().ToLowerInvariant();
        return Options.FirstOrDefault(option => option.Key == normalized) ?? Options[0];
    }

    public static string GlyphFor(string? key) => FromKey(key).Glyph;
}
