# SongsterrToPdf

`SongsterrToPdf` is a .NET 10 console tool that downloads Songsterr track JSON and exports PDF tablature.

For the PDF path, it uses Songsterr's own browser renderer, captures the hydrated SVG rows, and writes them into a PDF with QuestPDF.

## Requirements

- .NET 10 SDK
- Google Chrome installed locally

## Build

```bash
dotnet restore
dotnet build
```

## Usage

Generate JSON files only:

```bash
dotnet run -- "https://www.songsterr.com/a/wsa/metallica-enter-sandman-tab-s27"
```

Generate PDFs as well:

```bash
dotnet run -- "https://www.songsterr.com/a/wsa/metallica-enter-sandman-tab-s27" --pdf
```

Write output to a custom directory:

```bash
dotnet run -- "https://www.songsterr.com/a/wsa/metallica-enter-sandman-tab-s27" --pdf -o ./output
```

Show the Chrome window while exporting:

```bash
dotnet run -- "https://www.songsterr.com/a/wsa/metallica-enter-sandman-tab-s27" --pdf --no-headless
```

Enable verbose logging:

```bash
dotnet run -- "https://www.songsterr.com/a/wsa/metallica-enter-sandman-tab-s27" --pdf -v
```

## Options

- `-o, --output <dir>`: output directory
- `--pdf`: generate PDF files beside the downloaded JSON
- `--no-headless`: show the browser window
- `-v, --verbose`: verbose logs
- `-h, --help`: show help

## Output

The tool writes:

- one `metadata.json`
- one normalized JSON file per track
- one PDF per track when `--pdf` is enabled

Example:

```text
artist_song/
├── metadata.json
├── 00_lead_guitar_distortion_guitar.json
├── 00_lead_guitar_distortion_guitar.pdf
├── 01_rhythm_guitar_electric_guitar_clean.json
└── 01_rhythm_guitar_electric_guitar_clean.pdf
```

## Notes

- Chrome is used both to discover Songsterr track JSON and to capture Songsterr's rendered SVG rows for the PDF export.
- This is intended for personal use. Respect Songsterr's terms and the underlying rights for the tab content.
