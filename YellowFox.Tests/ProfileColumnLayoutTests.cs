using System.Collections.Generic;
using YellowFox.Desktop.ViewModels;
using Xunit;

namespace YellowFox.Tests;

public class ProfileColumnLayoutTests
{
    [Fact]
    public void Resize_AddsDeltaAndClampsToMinimum()
    {
        var layout = new ProfileColumnLayout();

        layout.Resize(ProfileColumn.Select, 60);
        Assert.Equal(ProfileColumnLayout.SelectDefault + 60, layout.SelectWidth.Value);

        layout.Resize(ProfileColumn.Notes, -10000);
        Assert.Equal(ProfileColumnLayout.NotesMin, layout.NotesWidth.Value);
    }

    [Fact]
    public void Load_AppliesValidWidthsAndFallsBackForInvalidOnes()
    {
        var layout = new ProfileColumnLayout();

        layout.Load(new Dictionary<string, double>
        {
            ["ProfileWidth"] = 400,
            ["TagsWidth"] = 1,
            ["ProxyWidth"] = double.NaN
        });

        Assert.Equal(400, layout.ProfileWidth.Value);
        Assert.Equal(ProfileColumnLayout.TagsDefault, layout.TagsWidth.Value);
        Assert.Equal(ProfileColumnLayout.ProxyDefault, layout.ProxyWidth.Value);
    }

    [Fact]
    public void TreeContentWidth_IncludesColumnsGapsAndIndent()
    {
        var layout = new ProfileColumnLayout();

        Assert.Equal(1266, layout.TreeContentWidth);
    }
}
