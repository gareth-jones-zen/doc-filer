using System.Text.RegularExpressions;

namespace DocFiler;

/// <summary>
/// Cleans up the markdown pandoc produces from a Teams transcript .docx — stray escape
/// characters, media placeholders, speaker names split across lines, non-breaking spaces.
///
/// These methods are carried over unchanged from the original implementation, in the same
/// order, deliberately: transcripts already in the repo were produced by them, so any change
/// here would show up as a diff on every file the next time it is reprocessed.
/// </summary>
public static class TranscriptPrettifier
{
    public static string Prettify(string input)
    {
        input = StripMediaRefsFromMarkdown(input);
        input = PutBoldStatementsOnASingleLineInMarkdown(input);
        input = RemoveTrailingLinebreaks(input);
        input = RemoveLinebreakFromTrailingDots(input);
        input = FixSlashApostrophes(input);
        input = FixSlashBars(input);
        input = FixSlashTildes(input);
        input = FixSlashQuotes(input);
        input = FixNbsps(input);
        input = FixBulletPointsWithLeadingSpaces(input);
        input = StripDataUriImages(input);
        return input;
    }

    /// <summary>
    /// Drops images embedded as data: URIs.
    ///
    /// Added after the methods above rather than among them: those are carried over unchanged
    /// so that reprocessing an existing transcript produces no diff, and this one cannot break
    /// that guarantee because no file already in the repo contains a data: URI. They arrive
    /// only from HTML sources — a Chrome-saved form can carry a company logo inline as
    /// ~15KB of base64 on the first line, which is pure noise in a markdown record.
    /// </summary>
    /// Two forms, because the output format decides which one pandoc emits: markdown writes
    /// ![](data:...) and gfm writes an &lt;img src="data:..."&gt; tag, which can wrap across lines.
    public static string StripDataUriImages(string input)
    {
        input = Regex.Replace(input, @"!\[[^\]]*\]\(data:[^)]*\)(\{[^}]*\})?\r?\n?", string.Empty);
        return Regex.Replace(input, @"<img\b[^>]*?src=""data:[^""]*""[^>]*?/?>\r?\n?", string.Empty,
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
    }

    public static string StripMediaRefsFromMarkdown(string input)
    {
        var lastMedia = 0;
        var media = input.IndexOf("![](media", lastMedia);
        while (media >= 0)
        {
            var mediaClose = input.IndexOf(")", media);
            if (mediaClose >= 0)
            {
                var parametersOpen = input.IndexOf("{", mediaClose);
                if (parametersOpen == mediaClose + 1)
                {
                    var parametersClose = input.IndexOf("}", parametersOpen);
                    if (parametersClose >= 0)
                    {
                        input = input.Substring(0, media) + input.Substring(parametersClose + 1);
                        lastMedia = media;
                    }
                    else
                    {
                        lastMedia = media + 1;
                    }
                }
                else
                {
                    lastMedia = media + 1;
                }
            }
            else
            {
                lastMedia = media + 1;
            }
            media = input.IndexOf("![](media", lastMedia);
        }
        return input;
    }

    public static string PutBoldStatementsOnASingleLineInMarkdown(string input)
    {
        input = PutBoldStatementsOnASingleLineInMarkdown(input, "\r\n");
        input = PutBoldStatementsOnASingleLineInMarkdown(input, "\n");
        return input;
    }

    public static string PutBoldStatementsOnASingleLineInMarkdown(string input, string linebreak)
    {
        var lastBold = 0;
        var bold = input.IndexOf("**\\" + linebreak, lastBold);
        while (bold >= 0)
        {
            var boldClose = input.IndexOf("**", bold);
            var nextLinebreak = input.IndexOf(linebreak, bold);
            // bold must be first bit of next line
            if (boldClose < nextLinebreak)
            {
                input = input.Substring(0, bold + 2) + input.Substring(bold + 3 + linebreak.Length);
                lastBold = boldClose + 2;
                bold = input.IndexOf("**\\" + linebreak, lastBold);
            }
            else
            {
                bold = -1;
            }
        }
        return input;
    }

    public static string RemoveLinebreakFromTrailingDots(string input)
    {
        input = input.Replace("\\...\r\n", "...\r\n");
        input = input.Replace("\\...\n", "...\n");
        input = input.Replace("\\...\\\r\n", "...\r\n");
        input = input.Replace("\\...\\\n", "...\n");
        return input;
    }

    public static string RemoveTrailingLinebreaks(string input)
    {
        input = input.Replace("\\\r\n", "\r\n");
        input = input.Replace("\\\n", "\n");
        return input;
    }

    public static string FixSlashApostrophes(string input) => input.Replace("\\'", "'");

    public static string FixSlashBars(string input) => input.Replace("\\|", "|");

    public static string FixSlashTildes(string input) => input.Replace("\\~", "~");

    public static string FixSlashQuotes(string input) => input.Replace("\\\"", "\"");

    public static string FixNbsps(string input)
    {
        input = Regex.Replace(input, @"\p{Zs}", " ");
        input = Regex.Replace(input, @"[\u00B7\u2022\u2219\u22C5]", "-");
        return input;
    }

    public static string FixBulletPointsWithLeadingSpaces(string input) =>
        Regex.Replace(input, @"^-\s+", "- ", RegexOptions.Multiline);
}
