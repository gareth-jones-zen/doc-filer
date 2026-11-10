using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace DocFiler;

/// <summary>
/// Renders a markdown file to a styled PDF beside it: pandoc makes self-contained HTML, then
/// headless Edge (or Chrome) prints it. No LaTeX needed.
///
///   run.ps1 pdf notes.md [--css my.css] [--image-width 1200] [--output other.pdf]
///
/// It came from the EA repo's make-summary-pdf.sh (Aug to Nov 2026), and keeps the fixes for
/// the traps that script hit, every one of which failed silently:
///   * YAML frontmatter is stripped first. Left in, pandoc reads it as metadata and prints the
///     title twice.
///   * Relative image links resolve against the source file's folder (--resource-path), not
///     the temp folder the stripped body is written to. Without that, every image was dropped
///     and the PDF still "passed": 89KB against 832KB with its thirteen screenshots.
///     pandoc's "Could not fetch resource" warning is now an error, so that can't recur.
///   * The PDF path is absolute, because Edge resolves --print-to-pdf against its own working
///     directory and otherwise writes nowhere useful.
///   * The old PDF is deleted before printing. A PDF open in a viewer can't be overwritten, and
///     last run's file then passed every check (24 Sep 2026); now a lock fails the run.
///   * Edge's exit code is ignored (it is non-zero on corporate-policy noise); the file is
///     checked instead: it must exist, start "%PDF-" and be over 10KB.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class Pdf
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(5);

    public static int Run(string[] args)
    {
        string? source = null, css = null, output = null;
        int? imageWidth = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--css" when i + 1 < args.Length:
                    css = args[++i];
                    break;
                case "--output" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--image-width" when i + 1 < args.Length && int.TryParse(args[i + 1], out var width) && width > 0:
                    imageWidth = width;
                    i++;
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                default:
                    if (source is null && !args[i].StartsWith('-'))
                    {
                        source = args[i];
                        break;
                    }
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    PrintUsage();
                    return 2;
            }
        }

        if (source is null || !File.Exists(source))
        {
            Console.Error.WriteLine(source is null ? "Name the markdown file to render." : $"Not found: {source}");
            PrintUsage();
            return 2;
        }

        source = Path.GetFullPath(source);
        output = Path.GetFullPath(output ?? Path.ChangeExtension(source, ".pdf"));
        css = Path.GetFullPath(css ?? Path.Combine(AppContext.BaseDirectory, "styles", "document.css"));
        if (!File.Exists(css))
        {
            Console.Error.WriteLine($"Stylesheet not found: {css}");
            return 2;
        }

        var browser = FindBrowser();
        if (browser is null)
        {
            Console.Error.WriteLine("No Edge or Chrome found to print the PDF.");
            return 2;
        }

        var temp = Directory.CreateTempSubdirectory("doc-filer-pdf-");
        try
        {
            return Render(source, output, css, imageWidth, browser, temp.FullName);
        }
        finally
        {
            // Edge can hold a file in the temp folder for a few seconds after it exits.
            try { temp.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static int Render(string source, string output, string css, int? imageWidth, string browser, string temp)
    {
        var body = StripFrontmatter(File.ReadAllText(source));
        if (string.IsNullOrWhiteSpace(body))
        {
            Console.Error.WriteLine("Nothing left to render after the frontmatter.");
            return 1;
        }
        var bodyPath = Path.Combine(temp, "body.md");
        File.WriteAllText(bodyPath, body);

        var title = TitleLine().Match(body) is { Success: true } match ? match.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(source);
        var sourceFolder = Path.GetDirectoryName(source)!;
        var resourcePath = sourceFolder;
        if (imageWidth is int width)
        {
            var reduced = Path.Combine(temp, "img");
            var count = ShrinkImages(body, sourceFolder, reduced, width);
            Console.WriteLine($"{count} images reduced to at most {width}px wide for the PDF");
            resourcePath = $"{reduced};{sourceFolder}";
        }

        var html = Path.Combine(temp, "document.html");
        var (code, errors) = RunProcess(Pandoc(), [
            bodyPath, "-f", "gfm", "-t", "html5", "--standalone", "--embed-resources",
            "--resource-path", resourcePath, "--css", css, "--metadata", $"pagetitle={title}", "-o", html,
        ]);
        if (code != 0)
        {
            Console.Error.WriteLine($"pandoc failed: {errors.Trim()}");
            return 1;
        }
        var missing = errors.Split('\n').Where(line => line.Contains("Could not fetch resource")).Select(line => line.Trim()).ToList();
        if (missing.Count > 0)
        {
            Console.Error.WriteLine("Images or other resources couldn't be found, so the PDF would be missing them:");
            missing.ForEach(line => Console.Error.WriteLine($"  {line}"));
            return 1;
        }

        try
        {
            File.Delete(output);
        }
        catch (IOException)
        {
            Console.Error.WriteLine($"Can't replace {output}: it's open somewhere (a PDF viewer, or OneDrive). Close it and run again.");
            return 1;
        }

        var (_, browserLog) = RunProcess(browser, [
            "--headless", "--disable-gpu", "--no-pdf-header-footer", $"--print-to-pdf={output}", new Uri(html).AbsoluteUri,
        ]);

        // Edge's exit code is unreliable, so check what it made.
        var file = new FileInfo(output);
        if (!file.Exists)
        {
            Console.Error.WriteLine("The browser didn't produce a PDF. Its output:");
            Console.Error.WriteLine(string.Join(Environment.NewLine, browserLog.Trim().Split('\n').TakeLast(20)));
            return 1;
        }
        var header = new byte[5];
        using (var stream = file.OpenRead())
        {
            stream.ReadExactly(header);
        }
        if (file.Length < 10_000 || System.Text.Encoding.ASCII.GetString(header) != "%PDF-")
        {
            Console.Error.WriteLine($"The PDF looks wrong: {file.Length} bytes.");
            return 1;
        }

        Console.WriteLine($"{output}  ({file.Length:N0} bytes)");
        return 0;
    }

    /// <summary>Drops a leading YAML block (--- ... ---). A file without one is left as it is.</summary>
    public static string StripFrontmatter(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd() != "---")
        {
            return text;
        }
        var end = Array.FindIndex(lines, 1, line => line.TrimEnd() is "---" or "...");
        return end < 0 ? text : string.Join("\n", lines.Skip(end + 1));
    }

    private static int ShrinkImages(string body, string sourceFolder, string target, int width)
    {
        var paths = ImageLink().Matches(body).Select(m => m.Groups["angled"].Success ? m.Groups["angled"].Value : m.Groups["plain"].Value)
            .Where(path => !path.Contains("://"))
            .Distinct();
        var count = 0;
        foreach (var path in paths)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var from = Path.Combine(sourceFolder, Uri.UnescapeDataString(path));
            if (extension is not (".jpg" or ".jpeg" or ".png") || !File.Exists(from))
            {
                continue;
            }
            var to = Path.Combine(target, Uri.UnescapeDataString(path));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            using var image = Image.FromFile(from);
            var newWidth = Math.Min(width, image.Width);
            var newHeight = (int)Math.Round(image.Height * (double)newWidth / image.Width);
            using var resized = new Bitmap(newWidth, newHeight);
            using (var graphics = Graphics.FromImage(resized))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image, 0, 0, newWidth, newHeight);
            }
            if (extension == ".png")
            {
                resized.Save(to, ImageFormat.Png);
            }
            else
            {
                var jpeg = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(Encoder.Quality, 72L);
                resized.Save(to, jpeg, parameters);
            }
            count++;
        }
        return count;
    }

    private static string Pandoc()
    {
        var bundled = Environment.GetEnvironmentVariable("DOC_FILER_PANDOC");
        return !string.IsNullOrWhiteSpace(bundled) && File.Exists(bundled) ? bundled : "pandoc";
    }

    private static string? FindBrowser()
    {
        string[] candidates =
        [
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static (int Code, string Errors) RunProcess(string file, string[] arguments)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Couldn't start {file}");
        // Read both streams as they come, so neither fills its pipe and blocks the process.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ProcessTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{Path.GetFileName(file)} didn't finish within {ProcessTimeout.TotalMinutes} minutes");
        }
        return (process.ExitCode, stderr.Result + stdout.Result);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            doc-filer pdf: render a markdown file to a styled PDF beside it.

              run.ps1 pdf <file.md> [options]

            Options:
                  --css <p>          Stylesheet (default: styles/document.css, shipped with the tool).
                  --image-width <n>  Shrink images wider than n pixels, in the PDF only. Worth it on
                                     image-heavy documents; the originals aren't touched.
                  --output <p>       Where to write the PDF (default: beside the source, as .pdf).

            Fails, rather than writing an incomplete PDF, if an image can't be found.
            """);
    }

    [GeneratedRegex(@"^# (.+)$", RegexOptions.Multiline)]
    private static partial Regex TitleLine();

    [GeneratedRegex(@"!\[[^\]]*\]\((?:<(?<angled>[^>]+)>|(?<plain>[^)<>\s]+))")]
    private static partial Regex ImageLink();
}
