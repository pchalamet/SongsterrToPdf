using System.Globalization;
using System.Text;
using System.Text.Json;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace SongsterrToPdf;

internal static class TrackPdfExporter
{
    private const int MinMeasuresPerRow = 3;
    private const int MaxMeasuresPerRow = 13;
    private const double TargetRowComplexity = 8.75d;
    private const float ViewWidth = 1180f;
    private const float LeftMargin = 42f;
    private const float RightMargin = 10f;
    private const float TopMargin = 28f;
    private const float StaffTop = 44f;
    private const float StringSpacing = 15f;
    private const float EffectsTopOffset = 18f;
    private const float RhythmTopOffset = 26f;
    private const float BottomPadding = 34f;

    private static readonly Dictionary<int, string> MidiToNote = new()
    {
        [28] = "E",
        [33] = "A",
        [38] = "D",
        [43] = "G",
        [48] = "C",
        [40] = "E",
        [45] = "A",
        [50] = "D",
        [55] = "G",
        [59] = "B",
        [64] = "e",
        [36] = "C",
        [41] = "F",
        [46] = "B"
    };

    public static void Export(string jsonPath, string pdfPath, SongInfo songInfo, TrackInfo? trackInfo)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = document.RootElement;

        var instrument = ResolveInstrument(root, trackInfo);
        var isDrums = instrument.Contains("drum", StringComparison.OrdinalIgnoreCase);
        var stringCount = ResolveStringCount(root, trackInfo, isDrums);
        var tuning = trackInfo?.Tuning.Count > 0 ? trackInfo.Tuning : ReadIntArray(root, "tuning");
        var automations = root.TryGetProperty("automations", out var automationsElement) ? automationsElement : default;
        var measures = root.TryGetProperty("measures", out var measuresElement) && measuresElement.ValueKind == JsonValueKind.Array
            ? measuresElement.EnumerateArray().ToList()
            : new List<JsonElement>();

        var rowLayouts = BuildRowLayouts(measures, stringCount, tuning, automations, isDrums);

        Document.Create(container =>
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(18);
                page.DefaultTextStyle(static style => style.FontSize(9).FontFamily("Helvetica"));

                page.Content().Column(column =>
                {
                    column.Spacing(10);
                    column.Item().Text($"{songInfo.Artist} - {songInfo.Title}").Bold().FontSize(16);
                    column.Item().Text(instrument).FontSize(11).FontColor("#444444");

                    if (!isDrums && tuning.Count > 0)
                    {
                        var tuningText = string.Join(" ", tuning.Reverse().Select(midi => MidiToNote.TryGetValue(midi, out var note) ? note : "?"));
                        column.Item().Text($"Tuning: {tuningText}").FontSize(9).FontColor("#666666");
                    }

                    foreach (var row in rowLayouts)
                    {
                        column.Item().Svg(row.Svg).FitWidth();
                    }
                });
            }))
            .GeneratePdf(pdfPath);
    }

    private static string ResolveInstrument(JsonElement root, TrackInfo? trackInfo)
    {
        var instrument = trackInfo?.Title;
        if (string.IsNullOrWhiteSpace(instrument))
        {
            instrument = trackInfo?.Instrument;
        }

        if (string.IsNullOrWhiteSpace(instrument))
        {
            instrument = TryGetString(root, "instrument") ?? "Instrument";
        }

        return instrument;
    }

    private static int ResolveStringCount(JsonElement root, TrackInfo? trackInfo, bool isDrums)
    {
        if (trackInfo?.Tuning.Count > 0)
        {
            return trackInfo.Tuning.Count;
        }

        if (root.TryGetProperty("strings", out var stringsProperty) &&
            stringsProperty.ValueKind == JsonValueKind.Number &&
            stringsProperty.TryGetInt32(out var stringCount))
        {
            return stringCount;
        }

        if (!isDrums)
        {
            return 6;
        }

        if (root.TryGetProperty("measures", out var measures) && measures.ValueKind == JsonValueKind.Array)
        {
            var maxString = 0;
            foreach (var measure in measures.EnumerateArray().Take(30))
            {
                foreach (var note in EnumerateNotes(measure))
                {
                    maxString = Math.Max(maxString, GetStringIndex(note) + 1);
                }
            }

            return Math.Max(maxString, 5);
        }

        return 5;
    }

    private static IReadOnlyList<RowLayout> BuildRowLayouts(
        IReadOnlyList<JsonElement> measures,
        int stringCount,
        IReadOnlyList<int> tuning,
        JsonElement automations,
        bool isDrums)
    {
        var labels = BuildStringLabels(stringCount, tuning, isDrums);
        var measureLayouts = new List<MeasureLayout>();

        for (var i = 0; i < measures.Count; i++)
        {
            measureLayouts.Add(BuildMeasureLayout(measures[i], i, stringCount, automations));
        }

        var rows = new List<RowLayout>();
        foreach (var chunk in PlanRows(measureLayouts))
        {
            rows.Add(new RowLayout(BuildRowSvg(chunk, labels, stringCount), ComputeRowHeight(stringCount)));
        }

        return rows;
    }

    private static MeasureLayout BuildMeasureLayout(JsonElement measure, int measureNumber, int stringCount, JsonElement automations)
    {
        var beats = GetPrimaryVoiceBeats(measure);
        var beatLayouts = new List<BeatLayout>();
        var totalDuration = beats.Sum(GetBeatDurationRatio);
        if (totalDuration <= 0)
        {
            totalDuration = 1d;
        }

        var running = 0d;
        for (var i = 0; i < beats.Count; i++)
        {
            var beat = beats[i];
            var ratio = GetBeatDurationRatio(beat) / totalDuration;
            if (ratio <= 0)
            {
                ratio = 1d / Math.Max(1, beats.Count);
            }

            var start = running;
            var width = ratio;
            running += ratio;

            var notes = new List<NoteLayout>();
            if (beat.TryGetProperty("notes", out var notesElement) && notesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var note in notesElement.EnumerateArray())
                {
                    if (note.TryGetProperty("rest", out var noteRest) && noteRest.ValueKind == JsonValueKind.True)
                    {
                        continue;
                    }

                    var fret = TryGetInt(note, "fret");
                    if (fret is null)
                    {
                        continue;
                    }

                    var stringIndex = GetStringIndex(note);
                    if (stringIndex < 0 || stringIndex >= stringCount)
                    {
                        continue;
                    }

                    var bend = ReadBendLabel(note);
                    notes.Add(new NoteLayout(
                        stringIndex,
                        fret.Value.ToString(CultureInfo.InvariantCulture),
                        note.TryGetProperty("tie", out var tie) && tie.ValueKind == JsonValueKind.True,
                        TryGetString(note, "slide"),
                        note.TryGetProperty("vibrato", out var vibrato) && vibrato.ValueKind == JsonValueKind.True,
                        bend));
                }
            }

            var isRestBeat = beat.TryGetProperty("rest", out var beatRest) && beatRest.ValueKind == JsonValueKind.True;
            beatLayouts.Add(new BeatLayout(
                start,
                width,
                TryGetInt(beat, "type") ?? 4,
                notes,
                isRestBeat));
        }

        return new MeasureLayout(
            measureNumber,
            GetTempoAtMeasure(automations, measureNumber),
            beatLayouts,
            CalculateMeasureComplexity(beatLayouts));
    }

    private static string BuildRowSvg(IReadOnlyList<MeasureLayout> measures, IReadOnlyList<string> stringLabels, int stringCount)
    {
        var rowHeight = ComputeRowHeight(stringCount);
        var contentWidth = ViewWidth - LeftMargin - RightMargin;
        var widthWeights = ComputeMeasureWidthWeights(measures);
        var staffBottom = StaffTop + (stringCount - 1) * StringSpacing;
        var effectsTop = StaffTop - EffectsTopOffset;
        var rhythmTop = staffBottom + RhythmTopOffset;

        var builder = new StringBuilder();
        builder.Append($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {Fmt(ViewWidth)} {Fmt(rowHeight)}'>");
        builder.Append("<rect x='0' y='0' width='100%' height='100%' fill='white'/>");

        for (var stringIndex = 0; stringIndex < stringCount; stringIndex++)
        {
            var y = StaffTop + stringIndex * StringSpacing + 4;
            builder.Append($"<text x='{Fmt(LeftMargin - 14)}' y='{Fmt(y)}' fill='#666' font-size='12' font-family='Helvetica' text-anchor='end'>{Escape(stringLabels[stringIndex])}</text>");
        }

        for (var measureOffset = 0; measureOffset < measures.Count; measureOffset++)
        {
            var measure = measures[measureOffset];
            var xRatioStart = widthWeights.Take(measureOffset).Sum();
            var xRatioEnd = xRatioStart + widthWeights[measureOffset];
            var measureStart = LeftMargin + contentWidth * xRatioStart;
            var measureEnd = LeftMargin + contentWidth * xRatioEnd;
            var innerPadding = 8f;
            var innerStart = measureStart + innerPadding;
            var innerWidth = (measureEnd - measureStart) - innerPadding * 2;

            for (var stringIndex = 0; stringIndex < stringCount; stringIndex++)
            {
                var y = StaffTop + stringIndex * StringSpacing;
                builder.Append($"<line x1='{Fmt(measureStart)}' y1='{Fmt(y)}' x2='{Fmt(measureEnd)}' y2='{Fmt(y)}' stroke='#b7bcc3' stroke-width='1'/>");
            }

            builder.Append($"<line x1='{Fmt(measureStart)}' y1='{Fmt(StaffTop)}' x2='{Fmt(measureStart)}' y2='{Fmt(staffBottom)}' stroke='#adb3bb' stroke-width='1'/>");
            builder.Append($"<line x1='{Fmt(measureEnd)}' y1='{Fmt(StaffTop)}' x2='{Fmt(measureEnd)}' y2='{Fmt(staffBottom)}' stroke='#adb3bb' stroke-width='1'/>");
            builder.Append($"<text x='{Fmt(measureStart + 3)}' y='{Fmt(TopMargin)}' fill='#888' font-size='11' font-family='Helvetica'>{measure.Number + 1}</text>");

            if (measureOffset == 0 && measure.Tempo is not null)
            {
                builder.Append($"<text x='{Fmt(measureStart + 18)}' y='{Fmt(TopMargin - 8)}' fill='#666' font-size='14' font-family='Helvetica'>♩ = {measure.Tempo}</text>");
            }

            for (var beatIndex = 0; beatIndex < measure.Beats.Count; beatIndex++)
            {
                var beat = measure.Beats[beatIndex];
                var beatStart = innerStart + (float)(beat.StartRatio * innerWidth);
                var beatWidth = (float)(beat.WidthRatio * innerWidth);
                var centerX = beatStart + beatWidth / 2f;

                if (beat.IsRestBeat)
                {
                    builder.Append($"<rect x='{Fmt(centerX - 4)}' y='{Fmt(StaffTop + StringSpacing * 1.6f)}' width='8' height='6' fill='#222' rx='0.5'/>");
                }

                foreach (var note in beat.Notes)
                {
                    var noteY = StaffTop + note.StringIndex * StringSpacing;
                    var label = note.IsTie ? $"({note.FretText})" : note.FretText;
                    var textWidth = Math.Max(12, label.Length * 8);
                    var rectX = centerX - textWidth / 2f;
                    var rectY = noteY - 11;

                    builder.Append($"<rect x='{Fmt(rectX)}' y='{Fmt(rectY)}' width='{Fmt(textWidth)}' height='15' fill='white' rx='2'/>");
                    builder.Append($"<text x='{Fmt(centerX)}' y='{Fmt(noteY + 1)}' fill='#1d1d1d' font-size='14' font-family='Helvetica' font-weight='600' text-anchor='middle'>{Escape(label)}</text>");

                    if (note.Vibrato)
                    {
                        builder.Append($"<path d='{BuildVibratoPath(centerX - textWidth / 2f, effectsTop + 2, textWidth + 14, 3.5f)}' fill='none' stroke='#666' stroke-width='1.2'/>");
                    }

                    if (note.BendLabel is not null)
                    {
                        var bendStartX = centerX + textWidth / 2f - 1f;
                        var bendStartY = noteY - 8;
                        var bendPeakX = bendStartX + Math.Max(22, beatWidth * 0.55f);
                        var bendPeakY = bendStartY - 26f;
                        builder.Append($"<path d='M {Fmt(bendStartX)} {Fmt(bendStartY)} Q {Fmt((bendStartX + bendPeakX) / 2f)} {Fmt(bendPeakY)} {Fmt(bendPeakX)} {Fmt(bendPeakY)}' fill='none' stroke='#555' stroke-width='1' stroke-dasharray='4 3'/>");
                        builder.Append($"<line x1='{Fmt(bendPeakX)}' y1='{Fmt(bendPeakY)}' x2='{Fmt(bendPeakX)}' y2='{Fmt(bendPeakY + 10)}' stroke='#555' stroke-width='1'/>");
                        builder.Append($"<polygon points='{Fmt(bendPeakX - 3)},{Fmt(bendPeakY + 7)} {Fmt(bendPeakX + 3)},{Fmt(bendPeakY + 7)} {Fmt(bendPeakX)},{Fmt(bendPeakY + 12)}' fill='#555'/>");
                        builder.Append($"<text x='{Fmt(bendPeakX - 2)}' y='{Fmt(bendPeakY - 4)}' fill='#666' font-size='10' font-family='Helvetica'>{Escape(note.BendLabel)}</text>");
                    }
                }

                DrawSlideOrTie(measure.Beats, beatIndex, beat, innerStart, innerWidth, builder);
                DrawRhythmGlyph(builder, centerX, rhythmTop, beat.DurationType, beat.IsRestBeat);
            }
        }

        builder.Append("</svg>");
        return builder.ToString();
    }

    private static IReadOnlyList<List<MeasureLayout>> PlanRows(IReadOnlyList<MeasureLayout> measures)
    {
        var rows = new List<List<MeasureLayout>>();
        var index = 0;

        while (index < measures.Count)
        {
            var row = new List<MeasureLayout>();
            var rowComplexity = 0d;

            while (index < measures.Count && row.Count < MaxMeasuresPerRow)
            {
                var next = measures[index];
                var remainingMeasures = measures.Count - index;
                var proposedComplexity = rowComplexity + next.Complexity;

                if (row.Count >= MinMeasuresPerRow &&
                    proposedComplexity > TargetRowComplexity)
                {
                    break;
                }

                if (row.Count > 0 && remainingMeasures < MinMeasuresPerRow)
                {
                    while (index < measures.Count)
                    {
                        row.Add(measures[index++]);
                    }

                    break;
                }

                row.Add(next);
                rowComplexity = proposedComplexity;
                index++;
            }

            if (row.Count == 0)
            {
                row.Add(measures[index++]);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static float[] ComputeMeasureWidthWeights(IReadOnlyList<MeasureLayout> measures)
    {
        var raw = measures
            .Select(measure => (float)Math.Clamp(0.8d + measure.Complexity * 0.45d, 0.9d, 3.2d))
            .ToArray();

        var total = raw.Sum();
        if (total <= 0)
        {
            return Enumerable.Repeat(1f / measures.Count, measures.Count).ToArray();
        }

        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] /= total;
        }

        return raw;
    }

    private static double CalculateMeasureComplexity(IReadOnlyList<BeatLayout> beats)
    {
        if (beats.Count == 0)
        {
            return 0.25d;
        }

        var noteCount = beats.Sum(beat => beat.Notes.Count);
        var chordCount = beats.Count(beat => beat.Notes.Count > 1);
        var effectCount = beats.Sum(beat => beat.Notes.Count(note =>
            note.SlideType is not null || note.Vibrato || note.BendLabel is not null || note.IsTie));
        var shortestDurationPenalty = beats.Any(beat => beat.DurationType >= 16) ? 0.75d :
            beats.Any(beat => beat.DurationType >= 8) ? 0.35d : 0d;
        var restOnly = noteCount == 0;

        if (restOnly)
        {
            return 0.28d + beats.Count * 0.03d;
        }

        return 0.55d
            + beats.Count * 0.08d
            + noteCount * 0.28d
            + chordCount * 0.24d
            + effectCount * 0.22d
            + shortestDurationPenalty;
    }

    private static void DrawSlideOrTie(
        IReadOnlyList<BeatLayout> beats,
        int currentIndex,
        BeatLayout currentBeat,
        float innerStart,
        float innerWidth,
        StringBuilder builder)
    {
        if (currentIndex + 1 >= beats.Count)
        {
            return;
        }

        var nextBeat = beats[currentIndex + 1];
        var currentCenterX = innerStart + (float)((currentBeat.StartRatio + currentBeat.WidthRatio / 2d) * innerWidth);
        var nextCenterX = innerStart + (float)((nextBeat.StartRatio + nextBeat.WidthRatio / 2d) * innerWidth);

        foreach (var note in currentBeat.Notes)
        {
            var paired = nextBeat.Notes.FirstOrDefault(candidate => candidate.StringIndex == note.StringIndex);
            if (paired is null)
            {
                continue;
            }

            var y = StaffTop + note.StringIndex * StringSpacing - 8;
            if (note.SlideType is not null)
            {
                builder.Append($"<path d='M {Fmt(currentCenterX + 6)} {Fmt(y + 2)} Q {Fmt((currentCenterX + nextCenterX) / 2f)} {Fmt(y - 12)} {Fmt(nextCenterX - 6)} {Fmt(y + 2)}' fill='none' stroke='#444' stroke-width='1.2'/>");
            }
            else if (paired.IsTie || note.IsTie)
            {
                builder.Append($"<path d='M {Fmt(currentCenterX + 6)} {Fmt(y + 3)} Q {Fmt((currentCenterX + nextCenterX) / 2f)} {Fmt(y + 10)} {Fmt(nextCenterX - 6)} {Fmt(y + 3)}' fill='none' stroke='#666' stroke-width='1'/>");
            }
        }
    }

    private static void DrawRhythmGlyph(StringBuilder builder, float centerX, float topY, int durationType, bool isRest)
    {
        var stemHeight = durationType switch
        {
            >= 16 => 20f,
            8 => 16f,
            _ => 14f
        };

        if (isRest)
        {
            builder.Append($"<line x1='{Fmt(centerX)}' y1='{Fmt(topY + 2)}' x2='{Fmt(centerX)}' y2='{Fmt(topY + stemHeight)}' stroke='#a4a7ad' stroke-width='1'/>");
            builder.Append($"<circle cx='{Fmt(centerX)}' cy='{Fmt(topY + stemHeight + 4)}' r='1.2' fill='#a4a7ad'/>");
            return;
        }

        builder.Append($"<line x1='{Fmt(centerX)}' y1='{Fmt(topY)}' x2='{Fmt(centerX)}' y2='{Fmt(topY + stemHeight)}' stroke='#9ea2a8' stroke-width='1'/>");

        if (durationType >= 8)
        {
            builder.Append($"<line x1='{Fmt(centerX)}' y1='{Fmt(topY)}' x2='{Fmt(centerX + 10)}' y2='{Fmt(topY + 4)}' stroke='#9ea2a8' stroke-width='1'/>");
        }

        if (durationType >= 16)
        {
            builder.Append($"<line x1='{Fmt(centerX)}' y1='{Fmt(topY + 5)}' x2='{Fmt(centerX + 10)}' y2='{Fmt(topY + 9)}' stroke='#9ea2a8' stroke-width='1'/>");
        }
    }

    private static float ComputeRowHeight(int stringCount)
    {
        return StaffTop + (stringCount - 1) * StringSpacing + BottomPadding + RhythmTopOffset;
    }

    private static IReadOnlyList<string> BuildStringLabels(int stringCount, IReadOnlyList<int> tuning, bool isDrums)
    {
        if (isDrums)
        {
            var drumLabels = new[] { "HH", "SD", "BD", "T1", "T2", "CR", "RD", "CH" };
            return Enumerable.Range(0, stringCount)
                .Select(index => index < drumLabels.Length ? drumLabels[index] : (index + 1).ToString(CultureInfo.InvariantCulture))
                .ToArray();
        }

        if (tuning.Count > 0)
        {
            return tuning.Select(midi => MidiToNote.TryGetValue(midi, out var note) ? note : "?").ToArray();
        }

        return new[] { "e", "B", "G", "D", "A", "E" }.Take(stringCount).ToArray();
    }

    private static List<JsonElement> GetPrimaryVoiceBeats(JsonElement measure)
    {
        if (!measure.TryGetProperty("voices", out var voices) || voices.ValueKind != JsonValueKind.Array)
        {
            return new List<JsonElement>();
        }

        foreach (var voice in voices.EnumerateArray())
        {
            if (voice.TryGetProperty("beats", out var beats) && beats.ValueKind == JsonValueKind.Array)
            {
                return beats.EnumerateArray().ToList();
            }
        }

        return new List<JsonElement>();
    }

    private static IEnumerable<JsonElement> EnumerateNotes(JsonElement measure)
    {
        if (!measure.TryGetProperty("voices", out var voices) || voices.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var voice in voices.EnumerateArray())
        {
            if (!voice.TryGetProperty("beats", out var beats) || beats.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var beat in beats.EnumerateArray())
            {
                if (!beat.TryGetProperty("notes", out var notes) || notes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var note in notes.EnumerateArray())
                {
                    yield return note;
                }
            }
        }
    }

    private static double GetBeatDurationRatio(JsonElement beat)
    {
        if (!beat.TryGetProperty("duration", out var duration) || duration.ValueKind != JsonValueKind.Array)
        {
            return 1d;
        }

        var values = duration.EnumerateArray().Take(2).Select(item => item.GetDouble()).ToArray();
        if (values.Length != 2 || Math.Abs(values[1]) < double.Epsilon)
        {
            return 1d;
        }

        var ratio = values[0] / values[1];
        if (beat.TryGetProperty("dots", out var dotsProperty) && dotsProperty.ValueKind == JsonValueKind.Number && dotsProperty.TryGetInt32(out var dots))
        {
            var dotBonus = 0d;
            var factor = 0.5d;
            for (var i = 0; i < dots; i++)
            {
                dotBonus += factor;
                factor /= 2d;
            }

            ratio *= 1d + dotBonus;
        }

        return ratio;
    }

    private static int? GetTempoAtMeasure(JsonElement automations, int measureIndex)
    {
        if (automations.ValueKind != JsonValueKind.Object ||
            !automations.TryGetProperty("tempo", out var tempoArray) ||
            tempoArray.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        int? currentBpm = null;
        foreach (var tempo in tempoArray.EnumerateArray())
        {
            var measure = TryGetInt(tempo, "measure") ?? 0;
            if (measure > measureIndex)
            {
                break;
            }

            currentBpm = TryGetInt(tempo, "bpm") ?? currentBpm;
        }

        return currentBpm;
    }

    private static int GetStringIndex(JsonElement note)
    {
        if (!note.TryGetProperty("string", out var stringElement))
        {
            return 0;
        }

        return stringElement.ValueKind switch
        {
            JsonValueKind.Number when stringElement.TryGetInt32(out var number) => number,
            JsonValueKind.String when double.TryParse(stringElement.GetString(), CultureInfo.InvariantCulture, out var parsed) => (int)parsed,
            _ => 0
        };
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static IReadOnlyList<int> ReadIntArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<int>();
        }

        var values = new List<int>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number))
            {
                values.Add(number);
            }
        }

        return values;
    }

    private static string? ReadBendLabel(JsonElement note)
    {
        if (!note.TryGetProperty("bend", out var bend) || bend.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var tone = TryGetInt(bend, "tone") ?? 0;
        return tone switch
        {
            >= 100 => "full",
            >= 75 => "3/4",
            >= 50 => "1/2",
            >= 25 => "1/4",
            _ => null
        };
    }

    private static string BuildVibratoPath(float startX, float baselineY, float width, float amplitude)
    {
        var builder = new StringBuilder();
        builder.Append($"M {Fmt(startX)} {Fmt(baselineY)}");

        const float segment = 7f;
        var endX = startX + width;
        var goingUp = true;
        for (var x = startX; x < endX; x += segment)
        {
            var nextX = Math.Min(endX, x + segment);
            var controlX = (x + nextX) / 2f;
            var controlY = baselineY + (goingUp ? -amplitude : amplitude);
            builder.Append($" Q {Fmt(controlX)} {Fmt(controlY)} {Fmt(nextX)} {Fmt(baselineY)}");
            goingUp = !goingUp;
        }

        return builder.ToString();
    }

    private static string Escape(string value)
    {
        return System.Security.SecurityElement.Escape(value) ?? string.Empty;
    }

    private static string Fmt(float value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}

internal sealed record RowLayout(string Svg, float Height);

internal sealed record MeasureLayout(int Number, int? Tempo, IReadOnlyList<BeatLayout> Beats, double Complexity);

internal sealed record BeatLayout(
    double StartRatio,
    double WidthRatio,
    int DurationType,
    IReadOnlyList<NoteLayout> Notes,
    bool IsRestBeat);

internal sealed record NoteLayout(
    int StringIndex,
    string FretText,
    bool IsTie,
    string? SlideType,
    bool Vibrato,
    string? BendLabel);
