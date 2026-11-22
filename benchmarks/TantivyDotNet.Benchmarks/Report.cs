using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;

namespace TantivyDotNet.Benchmarks;

/// <summary>One comparison: the same measurement for both engines (lower is better).</summary>
/// <remarks>Allocation figures (managed bytes per operation, from MemoryDiagnoser) are set for queries only.</remarks>
public sealed record ReportRow(string Label, string? Detail, double Lucene, double Tantivy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? LuceneAllocatedBytes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TantivyAllocatedBytes = null);

/// <summary>A group of rows that share a unit ("s", "µs" or "MiB").</summary>
public sealed record ReportSection(string Title, string Unit, List<ReportRow> Rows);

/// <summary>
/// The published results (benchmarks/results/results.json): what the README chart is drawn from.
/// Written by <c>export</c> from BenchmarkDotNet's JSON exports plus the index sizes.
/// </summary>
public sealed record BenchReport(
    string Date, string Machine, string Os, string Runtime, string BenchmarkDotNet, string Corpus,
    List<ReportSection> Sections);

[JsonSerializable(typeof(BenchReport))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ReportJson : JsonSerializerContext;

public static class Report
{
    public static string BdnArtifacts => Path.Combine(Corpus.RepoRoot, "artifacts", "BenchmarkDotNet");
    public static string ResultsDir => Path.Combine(Corpus.RepoRoot, "benchmarks", "results");
    public static string ResultsJson => Path.Combine(ResultsDir, "results.json");
    public static string ChartSvg => Path.Combine(ResultsDir, "summary.svg");

    private static readonly Dictionary<string, (string Label, string Detail)> QueryLabels = new()
    {
        ["Term"] = ("Term", "science"),
        ["And"] = ("AND", "american AND football"),
        ["Or"] = ("OR", "american OR football"),
        ["Phrase"] = ("Phrase", "\"united states\""),
        ["SortedByField"] = ("Term, sorted", "science, by a numeric field"),
    };

    /// <summary>Collects the latest BenchmarkDotNet runs and the index sizes into results.json.</summary>
    public static BenchReport Export()
    {
        var indexing = LoadBdn("IndexingBenchmarks");
        var queries = LoadBdn("QueryBenchmarks");

        var sections = new List<ReportSection>
        {
            new("Indexing all articles (on disk, commit, close)", "s",
                Pair(indexing.Rows, "Threads", v => (v == "1" ? "1 thread" : $"{v} threads", null), 1e-9, withAllocations: false)),
            new("Query: top 10 hits with stored fields, plus total count", "µs",
                Pair(queries.Rows, "Kind", v => QueryLabels.TryGetValue(v, out var l) ? l : (v, null), 1e-3, withAllocations: true)),
        };

        var sizes = IndexSizes.Measure();
        var lucene = sizes.Engines.Single(e => e.Name == "Lucene.NET").Bytes;
        var tantivy = sizes.Engines.Single(e => e.Name == "TantivyDotNet").Bytes;
        sections.Add(new("Index size", "MiB",
            [new("On disk", $"{sizes.RawBytes / 1048576.0:F0} MiB of text", Math.Round(lucene / 1048576.0, 1), Math.Round(tantivy / 1048576.0, 1))]));

        var env = queries.Env;
        var report = new BenchReport(
            Date: DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Machine: env.GetProperty("ProcessorName").GetString()!.Trim(),
            Os: Before(env.GetProperty("OsVersion").GetString()!, " ["),         // drop " [Darwin 25.5.0]"
            Runtime: Before(env.GetProperty("RuntimeVersion").GetString()!, " ("),  // drop the build number
            BenchmarkDotNet: env.GetProperty("BenchmarkDotNetVersion").GetString()!,
            Corpus: $"{sizes.Articles:N0} English Wikipedia articles",
            Sections: sections);

        Directory.CreateDirectory(ResultsDir);
        // Readable output: "µs" and quotes unescaped (the file is only read by `chart` and people).
        var options = new JsonSerializerOptions(ReportJson.Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(ResultsJson, JsonSerializer.Serialize(report, new ReportJson(options).BenchReport) + "\n");
        return report;
    }

    public static BenchReport Load() =>
        JsonSerializer.Deserialize(File.ReadAllText(ResultsJson), ReportJson.Default.BenchReport)
        ?? throw new InvalidDataException($"{ResultsJson} is empty.");

    private static string Before(string s, string marker) => s.IndexOf(marker, StringComparison.Ordinal) is var i and >= 0 ? s[..i] : s;

    private sealed record BdnRow(string Method, string Parameters, double MeanNs, long? AllocatedBytes);

    /// <summary>The newest JSON export of one benchmark class (BenchmarkDotNet's JsonExporter.Full).</summary>
    private static (List<BdnRow> Rows, JsonElement Env) LoadBdn(string type)
    {
        var dir = Path.Combine(BdnArtifacts, "results");
        var file = (Directory.Exists(dir) ? Directory.GetFiles(dir, $"*.{type}-report-full.json") : [])
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            ?? throw new FileNotFoundException($"No {type} results under {dir}. Run the benchmarks first (--filter '*{type}*').");
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var rows = doc.RootElement.GetProperty("Benchmarks").EnumerateArray()
            .Select(b => new BdnRow(
                b.GetProperty("Method").GetString()!,
                b.GetProperty("Parameters").GetString()!,
                b.GetProperty("Statistics").GetProperty("Mean").GetDouble(),
                b.TryGetProperty("Memory", out var m) && m.ValueKind == JsonValueKind.Object
                    && m.TryGetProperty("BytesAllocatedPerOperation", out var a) && a.ValueKind == JsonValueKind.Number
                    ? a.GetInt64() : null))
            .ToList();
        return (rows, doc.RootElement.GetProperty("HostEnvironmentInfo").Clone());
    }

    /// <summary>Pairs each parameter value's Lucene and Tantivy rows, in run order.</summary>
    private static double Round3(double v) => v == 0 ? 0 : Math.Round(v, Math.Max(0, 2 - (int)Math.Floor(Math.Log10(Math.Abs(v)))));

    private static List<ReportRow> Pair(List<BdnRow> rows, string param, Func<string, (string, string?)> label, double scale, bool withAllocations)
    {
        string Value(BdnRow r) => r.Parameters.Split('&').Select(p => p.Split('=')).Single(p => p[0] == param)[1];
        return rows.Select(Value).Distinct().Select(v =>
        {
            var lucene = rows.Single(r => Value(r) == v && r.Method.StartsWith("Lucene", StringComparison.Ordinal));
            var tantivy = rows.Single(r => Value(r) == v && r.Method.StartsWith("Tantivy", StringComparison.Ordinal));
            var (l, d) = label(v);
            // Rounded to 3 significant digits: more is noise between runs.
            return new ReportRow(l, d, Round3(lucene.MeanNs * scale), Round3(tantivy.MeanNs * scale),
                withAllocations ? lucene.AllocatedBytes : null, withAllocations ? tantivy.AllocatedBytes : null);
        }).ToList();
    }

    public static string FormatValue(double v, string unit) => unit switch
    {
        "s" => $"{v.ToString("F1", CultureInfo.InvariantCulture)} s",
        "µs" => $"{v.ToString("N0", CultureInfo.InvariantCulture)} µs",
        "MiB" => $"{v.ToString("F1", CultureInfo.InvariantCulture)} MiB",
        _ => $"{v.ToString("G3", CultureInfo.InvariantCulture)} {unit}",
    };

    /// <summary>"2.0× faster" / "1.1× slower", or "4% larger" for sizes.</summary>
    public static string FormatRatio(ReportRow row, string unit)
    {
        var ratio = row.Tantivy / row.Lucene;
        if (unit == "MiB")
        {
            var percent = Math.Abs(ratio - 1) * 100;
            return percent < 0.5 ? "same size" : $"{percent.ToString("F0", CultureInfo.InvariantCulture)}% {(ratio < 1 ? "smaller" : "larger")}";
        }
        if (Math.Abs(ratio - 1) < 0.05) return "about as fast";
        return ratio < 1
            ? $"{(1 / ratio).ToString("F1", CultureInfo.InvariantCulture)}× faster"
            : $"{ratio.ToString("F1", CultureInfo.InvariantCulture)}× slower";
    }

    /// <summary>
    /// Renders the README chart: one row per measurement, a bar per engine, bar length relative to
    /// Lucene.NET (so rows with very different units and magnitudes share one scale), with each
    /// engine's absolute value at the bar tip. A self-contained SVG card with its own background and a
    /// dark variant, so it reads on light and dark pages (GitHub, nuget.org).
    /// </summary>
    public static string RenderSvg(BenchReport report)
    {
        const double width = 800, pad = 28, labelW = 196, valueW = 190;
        const double barH = 14, barGap = 2, rowH = 52, sectionHeadH = 34, sectionGap = 18;
        var x0 = pad + labelW;
        var plotW = width - x0 - valueW - pad;
        var maxRatio = Math.Max(1, report.Sections.SelectMany(s => s.Rows).Max(r => r.Tantivy / r.Lucene));

        var body = new StringBuilder();
        var y = 0.0;

        // Header: title, subtitle, legend.
        y += pad + 18;
        body.Append(Text(pad, y, "TantivyDotNet vs Lucene.NET", "title"));
        y += 22;
        body.Append(Text(pad, y, $"{report.Corpus}. Lower is better; bars are relative to Lucene.NET.", "sub"));
        y += 26;
        var lx = pad;
        foreach (var (name, cls) in new[] { ("TantivyDotNet", "s1"), ("Lucene.NET 4.8", "s2") })
        {
            body.Append(CultureInfo.InvariantCulture, $"""<rect class="{cls}" x="{lx}" y="{y - 10}" width="12" height="12" rx="3"/>""");
            body.Append(Text(lx + 18, y, name, "legend"));
            lx += 18 + name.Length * 7.4 + 24;
        }
        y += 18;

        foreach (var section in report.Sections)
        {
            y += sectionGap;
            body.Append(CultureInfo.InvariantCulture, $"""<line class="rule" x1="{pad}" x2="{width - pad}" y1="{y}" y2="{y}"/>""");
            y += sectionHeadH - 10;
            body.Append(Text(pad, y, section.Title, "section"));
            y += 12;
            var top = y;
            foreach (var row in section.Rows)
            {
                var barsTop = y + (rowH - (2 * barH + barGap)) / 2;
                body.Append(Text(pad, barsTop + barH - 2, row.Label, "label"));
                if (row.Detail is { } detail)
                    body.Append(Text(pad, barsTop + 2 * barH + barGap - 1, detail, "detail"));

                // Tantivy first (the subject), then Lucene, each with its value at the tip.
                var ratio = FormatRatio(row, section.Unit);
                var bars = new[]
                {
                    ("s1", row.Tantivy / row.Lucene, FormatValue(row.Tantivy, section.Unit), ratio, "TantivyDotNet"),
                    ("s2", 1.0, FormatValue(row.Lucene, section.Unit), (string?)null, "Lucene.NET"),
                };
                var by = barsTop;
                foreach (var (cls, rel, value, note, engine) in bars)
                {
                    var w = Math.Max(2, plotW * rel / maxRatio);
                    body.Append("<g>");
                    body.Append(CultureInfo.InvariantCulture, $"<title>{Esc($"{row.Label}, {engine}: {value}")}</title>");
                    body.Append(CultureInfo.InvariantCulture, $"""<path class="{cls}" d="{BarPath(x0, by, w, barH)}"/>""");
                    body.Append(CultureInfo.InvariantCulture, $"""<text class="value" x="{F(x0 + w + 8)}" y="{F(by + barH - 2.5)}">{Esc(value)}""");
                    if (note is not null)
                        body.Append(CultureInfo.InvariantCulture, $"""<tspan class="ratio" dx="8">{Esc(note)}</tspan>""");
                    body.Append("</text></g>");
                    by += barH + barGap;
                }
                y += rowH;
            }
            // The shared baseline every bar grows from.
            body.Append(CultureInfo.InvariantCulture, $"""<line class="axis" x1="{F(x0)}" x2="{F(x0)}" y1="{F(top + 6)}" y2="{F(y - 6)}"/>""");
        }

        y += 14;
        body.Append(CultureInfo.InvariantCulture, $"""<line class="rule" x1="{pad}" x2="{width - pad}" y1="{F(y)}" y2="{F(y)}"/>""");
        y += 22;
        body.Append(Text(pad, y, $"{report.Machine} · {report.Os} · {report.Runtime} · BenchmarkDotNet {report.BenchmarkDotNet} · {report.Date}", "foot"));
        var height = y + pad - 6;

        var summary = string.Join("; ", report.Sections.SelectMany(s => s.Rows.Select(r =>
            $"{r.Label}: TantivyDotNet {FormatValue(r.Tantivy, s.Unit)}, Lucene.NET {FormatValue(r.Lucene, s.Unit)}")));

        return $$"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{{F(width)}}" height="{{F(height)}}" viewBox="0 0 {{F(width)}} {{F(height)}}" role="img" aria-labelledby="t d">
            <title id="t">TantivyDotNet vs Lucene.NET benchmarks</title>
            <desc id="d">{{Esc(summary)}}</desc>
            <style>
              svg { --surface:#fcfcfb; --border:#e4e3df; --ink:#0b0b0b; --ink-2:#52514e; --ink-3:#6f6e69; --grid:#d9d8d3; --s1:#2a78d6; --s2:#eb6834; }
              @media (prefers-color-scheme: dark) { svg { --surface:#1a1a19; --border:#33332f; --ink:#f0efec; --ink-2:#c3c2b7; --ink-3:#9c9b92; --grid:#3a3a36; --s1:#3987e5; --s2:#d95926; } }
              text { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif; fill: var(--ink); }
              .card { fill: var(--surface); stroke: var(--border); }
              .title { font-size: 20px; font-weight: 650; }
              .sub { font-size: 13px; fill: var(--ink-2); }
              .legend { font-size: 13px; fill: var(--ink-2); }
              .section { font-size: 14px; font-weight: 600; }
              .label { font-size: 13.5px; font-weight: 500; }
              .detail { font-size: 11.5px; fill: var(--ink-3); }
              .value { font-size: 12.5px; fill: var(--ink-2); font-variant-numeric: tabular-nums; }
              .ratio { font-weight: 600; fill: var(--ink); }
              .foot { font-size: 11px; fill: var(--ink-3); }
              .rule { stroke: var(--grid); stroke-width: 1; }
              .axis { stroke: var(--ink-3); stroke-width: 1; }
              .s1 { fill: var(--s1); }
              .s2 { fill: var(--s2); }
            </style>
            <rect class="card" x="0.5" y="0.5" width="{{F(width - 1)}}" height="{{F(height - 1)}}" rx="12"/>
            {{body}}
            </svg>

            """;
    }

    /// <summary>A horizontal bar, square at the baseline, with a 4px rounded data end.</summary>
    private static string BarPath(double x, double y, double w, double h)
    {
        var r = Math.Min(4, w / 2);
        return $"M{F(x)},{F(y)}H{F(x + w - r)}Q{F(x + w)},{F(y)} {F(x + w)},{F(y + r)}V{F(y + h - r)}Q{F(x + w)},{F(y + h)} {F(x + w - r)},{F(y + h)}H{F(x)}Z";
    }

    private static string Text(double x, double y, string s, string cls) =>
        $"""<text class="{cls}" x="{F(x)}" y="{F(y)}">{Esc(s)}</text>""";

    private static string F(double v) => Math.Round(v, 1).ToString(CultureInfo.InvariantCulture);

    private static string Esc(string s) => SecurityElement.Escape(s);
}
