using System.Net;
using System.Text;
using System.Text.Json;
using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace SongsterrToPdf;

internal static class SongsterrWebsitePdfExporter
{
    private const int ViewportWidth = 1600;
    private const int ViewportHeight = 1200;
    private const int DeviceScaleFactor = 2;

    public static void Export(
        string songUrl,
        string pdfPath,
        string jsonPath,
        SongInfo songInfo,
        TrackInfo? trackInfo,
        int? defaultTrack,
        bool headless)
    {
        ExportAsync(songUrl, pdfPath, jsonPath, songInfo, trackInfo, defaultTrack, headless)
            .GetAwaiter()
            .GetResult();
    }

    private static async Task ExportAsync(
        string songUrl,
        string pdfPath,
        string jsonPath,
        SongInfo songInfo,
        TrackInfo? trackInfo,
        int? defaultTrack,
        bool headless)
    {
        var expectedMeasureCount = ReadMeasureCount(jsonPath);
        var executablePath = ResolveChromeExecutablePath();

        var launchOptions = new LaunchOptions
        {
            Headless = headless,
            ExecutablePath = executablePath,
            Args =
            [
                "--disable-background-networking",
                "--disable-background-timer-throttling",
                "--disable-blink-features=AutomationControlled",
                "--disable-renderer-backgrounding",
                "--mute-audio",
                "--no-first-run",
                "--window-size=1600,1200"
            ]
        };

        await using var browser = await Puppeteer.LaunchAsync(launchOptions);
        await using var page = await browser.NewPageAsync();

        await page.EvaluateExpressionOnNewDocumentAsync(
            """
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            """);

        await page.SetViewportAsync(new ViewPortOptions
        {
            Width = ViewportWidth,
            Height = ViewportHeight,
            DeviceScaleFactor = DeviceScaleFactor
        });

        await page.GoToAsync(songUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.Networkidle0],
            Timeout = 60_000
        });

        await page.WaitForSelectorAsync("#tablature", new WaitForSelectorOptions
        {
            Timeout = 30_000
        });

        await Task.Delay(2_000);

        var extractionJson = await page.EvaluateFunctionAsync<string>(
            """
            async options => {
              const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
              const normalize = value => (value || '').replace(/\s+/g, ' ').trim();

              const ensureCaptureCss = () => {
                if (document.getElementById('codex-songsterr-svg-export-style')) {
                  return;
                }

                const style = document.createElement('style');
                style.id = 'codex-songsterr-svg-export-style';
                style.textContent = `
                  #controls,
                  #showroom,
                  #showroom_header_desktop,
                  #youtube-container,
                  #song-sfx,
                  #header > .ZQQn2G_wrap > .ZQQn2G_info,
                  #header > .ZQQn2G_wrap > .ZQQn2G_artistLine,
                  #app > .W5VyFW_notifications,
                  #app > ._8RhTFG_topbarWide,
                  #app > ._8RhTFG_topbarNarrow,
                  [role="dialog"],
                  [aria-modal="true"],
                  [id*="cookie"],
                  [class*="cookie"],
                  [id*="consent"],
                  [class*="consent"],
                  [id*="privacy"],
                  [class*="privacy"],
                  [id*="onetrust"],
                  [class*="onetrust"],
                  .W5VyFW_notifications,
                  ._8RhTFG_topbarWide,
                  ._8RhTFG_topbarNarrow,
                  .x-nCkq_main {
                    display: none !important;
                  }
                `;

                document.head.appendChild(style);
              };

              const removeKnownPageArtifacts = () => {
                const selectors = [
                  '#song-sfx',
                  '#showroom_header_desktop',
                  '#showroom',
                  '#controls',
                  '#youtube-container',
                  '#app > .W5VyFW_notifications',
                  '#app > ._8RhTFG_topbarWide',
                  '#app > ._8RhTFG_topbarNarrow',
                  '#header > .ZQQn2G_wrap > .ZQQn2G_info',
                  '#header > .ZQQn2G_wrap > .ZQQn2G_artistLine',
                  '#tuning-button-location',
                  '#tuning-button-location *'
                ];

                for (const selector of selectors) {
                  for (const node of document.querySelectorAll(selector)) {
                    node.remove();
                  }
                }

                const firstRowSvg = document.querySelector('#tablature > div:first-child svg');
                if (firstRowSvg) {
                  for (const node of firstRowSvg.querySelectorAll('#tuning-button-location, #tuning-button-location *')) {
                    node.remove();
                  }
                }
              };

              const selectTrackIfNeeded = async () => {
                const wantedTitle = normalize(options.trackTitle);
                const wantedInstrument = normalize(options.instrument);

                if (!wantedTitle && !wantedInstrument) {
                  return;
                }

                const currentInstrument = normalize(document.querySelector('#control-mixer .gYbqeG_instrument')?.textContent);
                const currentName = normalize(document.querySelector('#control-mixer .gYbqeG_name')?.textContent);
                const currentLabel = [currentName, currentInstrument].filter(Boolean).join(' - ');
                const wantedLabel = [wantedTitle, wantedInstrument].filter(Boolean).join(' - ');

                if (currentLabel && wantedLabel && currentLabel.includes(wantedLabel)) {
                  return;
                }

                document.getElementById('control-mixer')?.click();
                await sleep(600);

                const candidates = Array.from(document.querySelectorAll('button, a, div, li'));
                const match = candidates.find(node => {
                  const text = normalize(node.innerText);
                  if (!text) {
                    return false;
                  }

                  return (wantedLabel && (text === wantedLabel || text.includes(wantedLabel))) ||
                    (wantedTitle && text.includes(wantedTitle));
                });

                match?.click();
                await sleep(1_600);
              };

              const getRowSvgs = () => {
                return Array.from(document.querySelectorAll('#tablature svg')).filter(svg => {
                  const viewBox = svg.viewBox?.baseVal;
                  if (viewBox && viewBox.width > 900 && viewBox.height > 120) {
                    return true;
                  }

                  const rect = svg.getBoundingClientRect();
                  return rect.width > 1_000 && rect.height > 120;
                });
              };

              const inlineComputedStyles = (originalSvg, cloneSvg) => {
                const originalNodes = [originalSvg, ...originalSvg.querySelectorAll('*')];
                const cloneNodes = [cloneSvg, ...cloneSvg.querySelectorAll('*')];
                const styleProperties = [
                  'display',
                  'visibility',
                  'fill',
                  'fill-opacity',
                  'stroke',
                  'stroke-opacity',
                  'stroke-width',
                  'stroke-linecap',
                  'stroke-linejoin',
                  'stroke-dasharray',
                  'stroke-dashoffset',
                  'opacity',
                  'font-size',
                  'font-family',
                  'font-style',
                  'font-weight',
                  'letter-spacing',
                  'text-anchor',
                  'dominant-baseline',
                  'paint-order'
                ];

                for (let index = 0; index < originalNodes.length; index++) {
                  const originalNode = originalNodes[index];
                  const cloneNode = cloneNodes[index];
                  if (!originalNode || !cloneNode) {
                    continue;
                  }

                  const computed = window.getComputedStyle(originalNode);
                  for (const property of styleProperties) {
                    const value = computed.getPropertyValue(property);
                    if (value) {
                      cloneNode.setAttribute(property, value);
                    }
                  }
                }
              };

              const sanitizeSvg = svg => {
                const clone = svg.cloneNode(true);
                inlineComputedStyles(svg, clone);
                const originalNodes = [svg, ...svg.querySelectorAll('*')];
                const cloneNodes = [clone, ...clone.querySelectorAll('*')];
                const greenMarkers = ['#66d93a', '#69d63c', '#67d63f', '#61d62c', 'rgb(102, 217, 58)', 'rgb(103, 214, 63)', 'rgb(117, 218, 60)'];
                const blueMarkers = ['#0b7cff', '#1677ff', '#1a73e8', 'rgb(11, 124, 255)', 'rgb(22, 119, 255)', 'rgb(26, 115, 232)'];

                for (let index = cloneNodes.length - 1; index >= 1; index--) {
                  const originalNode = originalNodes[index];
                  const cloneNode = cloneNodes[index];
                  if (!originalNode || !cloneNode) {
                    continue;
                  }

                  const computed = window.getComputedStyle(originalNode);
                  const paint = [
                    computed.getPropertyValue('fill'),
                    computed.getPropertyValue('stroke'),
                    computed.getPropertyValue('color'),
                    originalNode.getAttribute('fill') || '',
                    originalNode.getAttribute('stroke') || '',
                    originalNode.getAttribute('style') || '',
                    originalNode.getAttribute('filter') || '',
                    originalNode.getAttribute('href') || '',
                    originalNode.getAttribute('xlink:href') || '',
                    originalNode.className?.baseVal || originalNode.className || '',
                    originalNode.id || ''
                  ].join(' ').toLowerCase();

                  if (paint.includes('cursor') || paint.includes('playback') || paint.includes('selection') || paint.includes('highlight')) {
                    cloneNode.remove();
                    continue;
                  }

                  if (greenMarkers.some(marker => paint.includes(marker)) || blueMarkers.some(marker => paint.includes(marker))) {
                    cloneNode.remove();
                  }
                }

                return clone;
              };

              const stripFirstRowOverlay = (originalSvg, cloneSvg) => {
                const originalNodes = [originalSvg, ...originalSvg.querySelectorAll('*')];
                const cloneNodes = [cloneSvg, ...cloneSvg.querySelectorAll('*')];
                const allowedLeftTexts = new Set(['e', 'b', 'g', 'd', 'a', '6', '4', '1']);

                for (let index = cloneNodes.length - 1; index >= 0; index--) {
                  const originalNode = originalNodes[index];
                  const cloneNode = cloneNodes[index];
                  if (!originalNode || !cloneNode || originalNode === originalSvg || cloneNode === cloneSvg) {
                    continue;
                  }

                  if (typeof originalNode.getBBox !== 'function') {
                    continue;
                  }

                  try {
                    const box = originalNode.getBBox();
                    const bottom = box.y + box.height;
                    const right = box.x + box.width;
                    const isText = originalNode.tagName?.toLowerCase() === 'text';
                    const text = normalize(originalNode.textContent).toLowerCase();

                    if (!isText && box.x < 35 && box.y < 35 && right < 55 && bottom < 55) {
                      cloneNode.remove();
                      continue;
                    }

                    if (!isText && right < 90) {
                      cloneNode.remove();
                      continue;
                    }

                    if (isText && right < 90 && !allowedLeftTexts.has(text)) {
                      cloneNode.remove();
                    }
                  } catch {
                  }
                }
              };

              const parseMeasures = svg => {
                const texts = Array.from(svg.querySelectorAll('text')).map(node => ({
                  text: normalize(node.textContent),
                  x: Number.parseFloat(node.getAttribute('x') || '0'),
                  y: Number.parseFloat(node.getAttribute('y') || '0')
                }));

                const measureLabels = texts
                  .filter(item => /^\\d+$/.test(item.text) && item.x > 100 && item.y > 0 && item.y < 40)
                  .map(item => Number.parseInt(item.text, 10))
                  .filter(Number.isFinite);

                const noteMeasures = Array.from(svg.querySelectorAll('[data-notes-measure]'))
                  .map(node => Number.parseInt(node.getAttribute('data-notes-measure') || '', 10) + 1)
                  .filter(Number.isFinite);

                return Array.from(new Set([...measureLabels, ...noteMeasures])).sort((a, b) => a - b);
              };

              const rows = new Map();
              const collectVisibleRows = () => {
                for (const svg of getRowSvgs()) {
                  const cleaned = sanitizeSvg(svg);
                  const measures = parseMeasures(cleaned);
                  const firstMeasure = measures.length > 0 ? measures[0] : null;
                  const lastMeasure = measures.length > 0 ? measures[measures.length - 1] : null;

                  if (firstMeasure === null || lastMeasure === null) {
                    continue;
                  }

                  if (firstMeasure === 1) {
                    stripFirstRowOverlay(svg, cleaned);
                  }

                  const key = `${firstMeasure}-${lastMeasure}`;
                  const payload = {
                    firstMeasure,
                    lastMeasure,
                    svg: cleaned.outerHTML,
                    svgLength: cleaned.outerHTML.length
                  };

                  const previous = rows.get(key);
                  if (!previous || payload.svgLength > previous.svgLength) {
                    rows.set(key, payload);
                  }
                }
              };

              ensureCaptureCss();
              removeKnownPageArtifacts();
              await selectTrackIfNeeded();

              const tab = document.getElementById('tablature');
              tab?.scrollIntoView({ block: 'start' });
              await sleep(800);

              const maxScroll = () => Math.max(
                document.documentElement.scrollHeight,
                document.body.scrollHeight
              ) - window.innerHeight;

              const scan = async (step, reverse) => {
                const positions = [];
                const limit = Math.max(0, maxScroll());

                for (let y = 0; y <= limit + step; y += step) {
                  positions.push(Math.min(y, limit));
                }

                if (reverse) {
                  positions.reverse();
                }

                for (const position of positions) {
                  window.scrollTo(0, position);
                  await sleep(350);
                  collectVisibleRows();
                }
              };

              collectVisibleRows();
              await scan(Math.max(260, Math.round(window.innerHeight * 0.75)), false);
              await scan(Math.max(180, Math.round(window.innerHeight * 0.45)), true);
              await scan(140, false);

              window.scrollTo(0, 0);
              await sleep(250);
              collectVisibleRows();

              const renderedRows = Array.from(rows.values())
                .sort((left, right) => left.firstMeasure - right.firstMeasure);

              return JSON.stringify({
                rows: renderedRows,
                maxLastMeasure: renderedRows.length > 0 ? renderedRows[renderedRows.length - 1].lastMeasure : null,
                expectedLastMeasure: options.expectedLastMeasure
              });
            }
            """,
            new
            {
                trackTitle = trackInfo?.Title ?? string.Empty,
                instrument = trackInfo?.Instrument ?? string.Empty,
                expectedLastMeasure = expectedMeasureCount
            });

        var extraction = JsonSerializer.Deserialize<RenderedExtractionResult>(
            extractionJson,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            })
            ?? throw new InvalidOperationException("Songsterr SVG extraction returned invalid data.");

        if (extraction.Rows is null || extraction.Rows.Count == 0)
        {
            throw new InvalidOperationException("No Songsterr tablature rows were extracted.");
        }

        if (expectedMeasureCount > 0 &&
            extraction.MaxLastMeasure is not null &&
            extraction.MaxLastMeasure.Value < expectedMeasureCount)
        {
            throw new InvalidOperationException(
                $"Incomplete tablature extraction. Expected measure {expectedMeasureCount}, got {extraction.MaxLastMeasure.Value}.");
        }

        await ExportRowsAsPdfAsync(browser, extraction.Rows, pdfPath, songUrl, songInfo, trackInfo);
    }

    private static int ReadMeasureCount(string jsonPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        if (!document.RootElement.TryGetProperty("measures", out var measures) ||
            measures.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return measures.GetArrayLength();
    }

    private static string ResolveChromeExecutablePath()
    {
        string[] candidates =
        [
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            "/Applications/Chromium.app/Contents/MacOS/Chromium"
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Chrome was not found. Install Google Chrome to enable Songsterr SVG export.");
    }

    private static async Task ExportRowsAsPdfAsync(
        IBrowser browser,
        IReadOnlyList<RenderedRow> rows,
        string pdfPath,
        string songUrl,
        SongInfo songInfo,
        TrackInfo? trackInfo)
    {
        var title = songInfo.Title;
        var artist = songInfo.Artist;
        var tabKind = trackInfo?.Title switch
        {
            { Length: > 0 } label => label,
            _ when !string.IsNullOrWhiteSpace(trackInfo?.Instrument) => trackInfo!.Instrument,
            _ => "Tab"
        };
        var markup = new StringBuilder();
        markup.Append(
            """
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <style>
                @page { size: A4 landscape; margin: 18px; }
                html, body { margin: 0; padding: 0; }
                body {
                  font-family: Helvetica, Arial, sans-serif;
                  color: #222;
                }
                .header {
                  margin-bottom: 12px;
                }
                .title {
                  font-size: 28px;
                  font-weight: 700;
                  line-height: 1.1;
                  margin: 0 0 4px 0;
                }
                .artist {
                  font-size: 16px;
                  color: #444;
                  margin: 0 0 3px 0;
                }
                .tabkind {
                  font-size: 14px;
                  color: #555;
                  margin: 0 0 3px 0;
                }
                .url {
                  font-size: 11px;
                  color: #777;
                  margin: 0;
                }
                .row {
                  margin-bottom: 8px;
                  break-inside: avoid;
                  page-break-inside: avoid;
                }
                .row svg {
                  display: block;
                  width: 100%;
                  height: auto;
                }
              </style>
            </head>
            <body>
            """);

        markup.Append("<div class=\"header\">");
        markup.Append("<div class=\"title\">").Append(WebUtility.HtmlEncode(title)).Append("</div>");
        markup.Append("<div class=\"artist\">").Append(WebUtility.HtmlEncode(artist)).Append("</div>");
        markup.Append("<div class=\"tabkind\">").Append(WebUtility.HtmlEncode(tabKind)).Append("</div>");
        markup.Append("<div class=\"url\">").Append(WebUtility.HtmlEncode(songUrl)).Append("</div>");
        markup.Append("</div>");

        foreach (var row in rows)
        {
            var svg = row.FirstMeasure == 1
                ? MaskFirstRowArtifacts(CropSvgLeft(row.Svg, 28))
                : row.Svg;

            markup.Append("<div class=\"row\">");
            markup.Append(svg);
            markup.Append("</div>");
        }

        markup.Append("</body></html>");

        await using var pdfPage = await browser.NewPageAsync();
        await pdfPage.SetViewportAsync(new ViewPortOptions
        {
            Width = 1600,
            Height = 1200,
            DeviceScaleFactor = 1
        });
        await pdfPage.SetContentAsync(markup.ToString());
        await pdfPage.EmulateMediaTypeAsync(MediaType.Screen);
        await Task.Delay(250);
        await pdfPage.PdfAsync(pdfPath, new PdfOptions
        {
            PrintBackground = true,
            Landscape = true,
            Format = PaperFormat.A4,
            MarginOptions = new MarginOptions
            {
                Top = "18px",
                Right = "18px",
                Bottom = "18px",
                Left = "18px"
            }
        });
    }

    private static string CropSvgLeft(string svg, float amount)
    {
        if (amount <= 0)
        {
            return svg;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            svg,
            "viewBox=['\\\"](?<x>-?\\d+(?:\\.\\d+)?)\\s+(?<y>-?\\d+(?:\\.\\d+)?)\\s+(?<w>\\d+(?:\\.\\d+)?)\\s+(?<h>\\d+(?:\\.\\d+)?)['\\\"]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return svg;
        }

        var x = float.Parse(match.Groups["x"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var y = float.Parse(match.Groups["y"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var width = float.Parse(match.Groups["w"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var height = float.Parse(match.Groups["h"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var croppedWidth = Math.Max(1, width - amount);
        var replacement = $"viewBox=\"{(x + amount).ToString(System.Globalization.CultureInfo.InvariantCulture)} {y.ToString(System.Globalization.CultureInfo.InvariantCulture)} {croppedWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)} {height.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"";

        return System.Text.RegularExpressions.Regex.Replace(
            svg,
            "viewBox=['\\\"](?<x>-?\\d+(?:\\.\\d+)?)\\s+(?<y>-?\\d+(?:\\.\\d+)?)\\s+(?<w>\\d+(?:\\.\\d+)?)\\s+(?<h>\\d+(?:\\.\\d+)?)['\\\"]",
            replacement,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
    }

    private static string MaskFirstRowArtifacts(string svg)
    {
        const string overlay = "<rect x=\"0\" y=\"0\" width=\"60\" height=\"42\" fill=\"white\" />";
        const string closingTag = "</svg>";
        var index = svg.LastIndexOf(closingTag, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return svg;
        }

        return svg.Insert(index, overlay);
    }

    private sealed record RenderedExtractionResult(
        IReadOnlyList<RenderedRow>? Rows,
        int? MaxLastMeasure,
        int? ExpectedLastMeasure);

    private sealed record RenderedRow(
        int FirstMeasure,
        int LastMeasure,
        string Svg,
        int SvgLength);
}
