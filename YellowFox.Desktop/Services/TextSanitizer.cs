using System;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace YellowFox.Desktop.Services;

public static class TextSanitizer
{
    private static readonly Regex TagsRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public static string HtmlToPlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var withBreaks = Regex.Replace(value, @"</(p|div|h[1-6]|li|br|tr)>", "\n", RegexOptions.IgnoreCase);
        var withoutTags = TagsRegex.Replace(withBreaks, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        var normalizedLines = decoded
            .Replace('\u00A0', ' ')
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .Select(line => WhitespaceRegex.Replace(line, " ").Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));

        return string.Join(Environment.NewLine, normalizedLines);
    }
}
