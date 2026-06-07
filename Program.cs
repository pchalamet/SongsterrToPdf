using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using QuestPDF.Infrastructure;

namespace SongsterrToPdf;

internal static partial class Program
{
    private static async Task<int> Main(string[] args)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var options = CliOptions.Parse(args);
        if (options.ShowHelp)
        {
            CliOptions.PrintHelp();
            return 0;
        }

        if (options.ErrorMessage is not null)
        {
            Console.Error.WriteLine(options.ErrorMessage);
            CliOptions.PrintHelp();
            return 1;
        }

        if (!SongsterrDownloader.IsValidSongsterrUrl(options.Url!))
        {
            Console.Error.WriteLine("Invalid URL. Expected: https://www.songsterr.com/a/wsa/<artist>-<song>-tab-s<id>");
            return 1;
        }

        var downloader = new SongsterrDownloader(options);
        var result = await downloader.DownloadAsync(options.Url!, options.OutputDirectory);

        if (result.Success)
        {
            return 0;
        }

        Console.Error.WriteLine("Download failed");
        if (options.Verbose)
        {
            foreach (var error in result.Errors)
            {
                Console.Error.WriteLine($"  {error}");
            }
        }

        return 1;
    }
}

internal sealed record CliOptions(
    string? Url,
    string? OutputDirectory,
    bool Headless,
    bool Verbose,
    bool GeneratePdf,
    PdfOrientation PdfOrientation,
    bool ShowHelp,
    string? ErrorMessage)
{
    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new CliOptions(null, null, true, false, false, PdfOrientation.Landscape, true, null);
        }

        string? url = null;
        string? outputDirectory = null;
        var headless = true;
        var verbose = false;
        var generatePdf = false;
        var pdfOrientation = PdfOrientation.Landscape;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    break;
                case "-o":
                case "--output":
                    if (i + 1 >= args.Length)
                    {
                        return new CliOptions(null, null, true, false, false, PdfOrientation.Landscape, false, "Missing value for --output.");
                    }

                    outputDirectory = args[++i];
                    break;
                case "--no-headless":
                    headless = false;
                    break;
                case "--pdf":
                    generatePdf = true;
                    break;
                case "--portrait":
                    pdfOrientation = PdfOrientation.Portrait;
                    break;
                case "--landscape":
                    pdfOrientation = PdfOrientation.Landscape;
                    break;
                case "-v":
                case "--verbose":
                    verbose = true;
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        return new CliOptions(null, null, true, false, false, PdfOrientation.Landscape, false, $"Unknown option: {arg}");
                    }

                    if (url is not null)
                    {
                        return new CliOptions(null, null, true, false, false, PdfOrientation.Landscape, false, "Only one Songsterr URL may be provided.");
                    }

                    url = arg;
                    break;
            }
        }

        if (showHelp)
        {
            return new CliOptions(url, outputDirectory, headless, verbose, generatePdf, pdfOrientation, true, null);
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return new CliOptions(null, outputDirectory, headless, verbose, generatePdf, pdfOrientation, false, "A Songsterr URL is required.");
        }

        return new CliOptions(url, outputDirectory, headless, verbose, generatePdf, pdfOrientation, false, null);
    }

    public static void PrintHelp()
    {
        Console.WriteLine(
            """
            SongsterrToPdf

            Usage:
              dotnet run -- "<songsterr-url>" [options]

            Options:
              -o, --output <dir>   Output directory (default: ./<artist>_<song>/)
              --pdf                Generate PDF tablature files
              --portrait           Generate portrait PDFs
              --landscape          Generate landscape PDFs (default)
              --no-headless        Show the Chrome browser window
              -v, --verbose        Enable verbose debug output
              -h, --help           Show this help message

            Examples:
              dotnet run -- "https://www.songsterr.com/a/wsa/metallica-enter-sandman-tab-s27"
              dotnet run -- "https://www.songsterr.com/a/wsa/metallica-enter-sandman-tab-s27" -o ./downloads
              dotnet run -- "https://www.songsterr.com/a/wsa/metallica-enter-sandman-tab-s27" --pdf --portrait
              dotnet run -- "https://www.songsterr.com/a/wsa/metallica-enter-sandman-tab-s27" --pdf --no-headless
            """);
    }
}

internal enum PdfOrientation
{
    Portrait,
    Landscape
}

internal sealed partial class SongsterrDownloader
{
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly CliOptions _options;

    public SongsterrDownloader(CliOptions options)
    {
        _options = options;
    }

    public static bool IsValidSongsterrUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Host.Contains("songsterr.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Contains("/a/wsa/", StringComparison.Ordinal);
    }

    public async Task<DownloadResult> DownloadAsync(string url, string? outputDirectory)
    {
        var result = new DownloadResult();
        ChromeDriver? driver = null;

        try
        {
            driver = CreateDriver();

            Console.WriteLine($"Loading {url}");
            driver.Navigate().GoToUrl(url);

            WaitForPageBody(driver, TimeSpan.FromSeconds(15));
            await Task.Delay(TimeSpan.FromSeconds(5));

            var pageSource = driver.PageSource;
            var songInfo = TryExtractSongInfo(pageSource);
            result.SongInfo = songInfo;

            if (songInfo is null)
            {
                result.Errors.Add("Could not extract song metadata.");
                return result;
            }

            Console.WriteLine($"{songInfo.Artist} - {songInfo.Title} ({songInfo.Tracks.Count} tracks)");

            var trackUrls = CaptureTrackUrls(driver);
            if (trackUrls.Count == 0)
            {
                trackUrls = ExtractTrackUrlsFromHtml(pageSource);
            }

            Log($"Found {trackUrls.Count} JSON URLs");

            if (trackUrls.Count == 0)
            {
                result.Errors.Add("No track URLs found in browser logs or page HTML.");
                return result;
            }

            outputDirectory ??= BuildDefaultOutputDirectory(songInfo);
            Directory.CreateDirectory(outputDirectory);

            var trackNames = BuildTrackNames(songInfo.Tracks);
            for (var i = 0; i < trackUrls.Count; i++)
            {
                var trackUrl = trackUrls[i];
                RenderProgress(i + 1, trackUrls.Count);

                try
                {
                    var trackIndex = TryGetTrackIndex(trackUrl);
                    var trackName = trackIndex is not null && trackNames.TryGetValue(trackIndex.Value, out var mappedTrackName)
                        ? mappedTrackName
                        : $"unknown_{result.Files.Count:D2}";

                    var json = await DownloadTrackJsonAsync(trackUrl);
                    var fileName = $"{trackName}.json";
                    var filePath = Path.Combine(outputDirectory, fileName);

                    await File.WriteAllTextAsync(filePath, json, Encoding.UTF8);
                    var fileInfo = new FileInfo(filePath);

                    result.Files.Add(new DownloadedFile(
                        fileName,
                        filePath,
                        fileInfo.Length,
                        trackIndex,
                        trackUrl));
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Failed to download {trackUrl}: {ex.Message}");
                }
            }

            if (trackUrls.Count > 0)
            {
                Console.WriteLine();
            }

            var metadataPath = Path.Combine(outputDirectory, "metadata.json");
            var metadataJson = JsonSerializer.Serialize(
                new
                {
                    url,
                    song_info = songInfo,
                    downloaded_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    files = result.Files.Select(file => file.Name).ToArray()
                },
                JsonOptions.Indented);
            await File.WriteAllTextAsync(metadataPath, metadataJson, Encoding.UTF8);

            var metadataInfo = new FileInfo(metadataPath);
            result.Files.Add(new DownloadedFile("metadata.json", metadataPath, metadataInfo.Length, null, null));

            result.Success = result.Files.Count > 1;

            if (_options.GeneratePdf && result.Success)
            {
                Console.Write("Generating PDFs... ");

                var pdfCount = 0;
                foreach (var file in result.Files.Where(file => file.TrackIndex is not null))
                {
                    var track = file.TrackIndex < songInfo.Tracks.Count ? songInfo.Tracks[file.TrackIndex.Value] : null;
                    var pdfPath = Path.ChangeExtension(file.Path, ".pdf")!;

                    try
                    {
                        SongsterrWebsitePdfExporter.Export(url, pdfPath, file.Path, songInfo, track, songInfo.DefaultTrack, _options.Headless, _options.PdfOrientation);
                        pdfCount++;
                    }
                    catch (Exception ex)
                    {
                        Log($"Website PDF export failed for {file.Name}: {ex.Message}");

                        try
                        {
                            TrackPdfExporter.Export(file.Path, pdfPath, songInfo, track);
                            pdfCount++;
                        }
                        catch (Exception fallbackEx)
                        {
                            Log($"Fallback PDF generation failed for {file.Name}: {fallbackEx.Message}");
                        }
                    }
                }

                Console.WriteLine($"{pdfCount} PDFs");
            }

            if (result.Success)
            {
                var totalSize = result.Files.Sum(file => file.Size);
                Console.WriteLine($"Done: {result.Files.Count - 1} tracks, {totalSize:N0} bytes -> {outputDirectory}");
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Download failed: {ex.Message}");
            Console.Error.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            driver?.Quit();
            driver?.Dispose();
        }

        return result;
    }

    private ChromeDriver CreateDriver()
    {
        var options = new ChromeOptions();
        if (_options.Headless)
        {
            options.AddArgument("--headless=new");
        }

        options.AddArgument("--disable-blink-features=AutomationControlled");
        options.AddArgument("--disable-infobars");
        options.AddArgument("--window-size=1920,1080");
        options.AddExcludedArgument("enable-automation");
        options.AddAdditionalOption("useAutomationExtension", false);
        options.SetLoggingPreference(LogType.Performance, LogLevel.All);

        Log("Starting Chrome browser...");
        var driver = new ChromeDriver(options);
        driver.ExecuteScript("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");
        return driver;
    }

    private void Log(string message)
    {
        if (_options.Verbose)
        {
            Console.WriteLine($"[DEBUG] {message}");
        }
    }

    private static void WaitForPageBody(IWebDriver driver, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (driver.FindElements(By.TagName("body")).Count > 0)
                {
                    return;
                }
            }
            catch (WebDriverException)
            {
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException("Timed out waiting for page body.");
    }

    private SongInfo? TryExtractSongInfo(string pageSource)
    {
        var match = StateScriptRegex().Match(pageSource);
        if (!match.Success)
        {
            Log("No state script found in page.");
            return null;
        }

        var decodedState = WebUtility.UrlDecode(match.Groups[1].Value);

        try
        {
            using var document = JsonDocument.Parse(decodedState);
            if (!document.RootElement.TryGetProperty("meta", out var meta) ||
                !meta.TryGetProperty("current", out var current))
            {
                return null;
            }

            var tracks = new List<TrackInfo>();
            if (current.TryGetProperty("tracks", out var tracksElement) &&
                tracksElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var track in tracksElement.EnumerateArray())
                {
                    tracks.Add(new TrackInfo(
                        GetString(track, "title") ?? GetString(track, "name") ?? GetString(track, "instrument") ?? "Unknown",
                        GetString(track, "instrument") ?? string.Empty,
                        ReadIntArray(track, "tuning")));
                }
            }

            return new SongInfo(
                GetString(current, "title") ?? "Unknown",
                GetString(current, "artist") ?? "Unknown",
                GetInt(current, "songId"),
                GetInt(current, "revisionId"),
                GetInt(current, "defaultTrack"),
                tracks);
        }
        catch (JsonException ex)
        {
            Log($"Failed to parse state JSON: {ex.Message}");
            return null;
        }
    }

    private List<string> CaptureTrackUrls(ChromeDriver driver)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var attempt = 0; attempt < 10 && urls.Count == 0; attempt++)
        {
            Thread.Sleep(1000);
            foreach (var entry in driver.Manage().Logs.GetLog(LogType.Performance))
            {
                try
                {
                    using var document = JsonDocument.Parse(entry.Message);
                    var message = document.RootElement.GetProperty("message");
                    var method = message.GetProperty("method").GetString();
                    if (method is not ("Network.requestWillBeSent" or "Network.responseReceived"))
                    {
                        continue;
                    }

                    var parameters = message.GetProperty("params");
                    var payload = method == "Network.requestWillBeSent"
                        ? parameters.GetProperty("request")
                        : parameters.GetProperty("response");
                    var candidate = payload.GetProperty("url").GetString();

                    if (candidate is not null &&
                        candidate.Contains("cloudfront.net", StringComparison.OrdinalIgnoreCase) &&
                        candidate.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        urls.Add(candidate);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        return urls.OrderBy(static url => url, StringComparer.Ordinal).ToList();
    }

    private static List<string> ExtractTrackUrlsFromHtml(string pageSource)
    {
        return CloudFrontJsonRegex()
            .Matches(pageSource)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static url => url, StringComparer.Ordinal)
            .ToList();
    }

    private static string BuildDefaultOutputDirectory(SongInfo songInfo)
    {
        var artist = SanitizeFileName(songInfo.Artist);
        var title = SanitizeFileName(songInfo.Title);
        return Path.Combine(Directory.GetCurrentDirectory(), $"{artist}_{title}");
    }

    private static Dictionary<int, string> BuildTrackNames(IReadOnlyList<TrackInfo> tracks)
    {
        var result = new Dictionary<int, string>();
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var name = track.Title;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = track.Instrument;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"track_{i}";
            }

            result[i] = $"{i:D2}_{SanitizeFileName(name)}";
        }

        return result;
    }

    private static async Task<string> DownloadTrackJsonAsync(string url)
    {
        var rawJson = await HttpClient.GetStringAsync(url);
        using var document = JsonDocument.Parse(rawJson);
        return JsonSerializer.Serialize(document.RootElement, JsonOptions.Indented);
    }

    private static int? TryGetTrackIndex(string url)
    {
        var match = TrackIndexRegex().Match(url);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static string SanitizeFileName(string value)
    {
        var safe = InvalidFileCharsRegex().Replace(value, string.Empty);
        safe = WhitespaceRegex().Replace(safe, "_");
        return safe.Trim('_').ToLowerInvariant();
    }

    private static void RenderProgress(int current, int total)
    {
        var progress = (int)Math.Round(current / (double)total * 30, MidpointRounding.AwayFromZero);
        progress = Math.Clamp(progress, 0, 30);
        var bar = new string('#', progress) + new string('-', 30 - progress);
        Console.Write($"\r[{bar}] {current}/{total}");
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static IReadOnlyList<int> ReadIntArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<int>();
        }

        var items = new List<int>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number))
            {
                items.Add(number);
            }
        }

        return items;
    }

    [GeneratedRegex(@"<script id=""state""[^>]*>([^<]+)</script>", RegexOptions.Singleline)]
    private static partial Regex StateScriptRegex();

    [GeneratedRegex(@"https://[^\s""'<>]+cloudfront\.net/[^\s""'<>]+\.json", RegexOptions.IgnoreCase)]
    private static partial Regex CloudFrontJsonRegex();

    [GeneratedRegex(@"/(\d+)\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex TrackIndexRegex();

    [GeneratedRegex(@"[^\w\s-]")]
    private static partial Regex InvalidFileCharsRegex();

    [GeneratedRegex(@"[-\s]+")]
    private static partial Regex WhitespaceRegex();
}

internal sealed class DownloadResult
{
    public bool Success { get; set; }

    public SongInfo? SongInfo { get; set; }

    public List<DownloadedFile> Files { get; } = new();

    public List<string> Errors { get; } = new();
}

internal sealed record DownloadedFile(
    string Name,
    string Path,
    long Size,
    int? TrackIndex,
    string? Url);

internal sealed record SongInfo(
    string Title,
    string Artist,
    int? SongId,
    int? RevisionId,
    int? DefaultTrack,
    IReadOnlyList<TrackInfo> Tracks);

internal sealed record TrackInfo(
    string Title,
    string Instrument,
    IReadOnlyList<int> Tuning);

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true
    };
}
