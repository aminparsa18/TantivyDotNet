using System.Text;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;
using TantivyDotNet.Benchmarks;

switch (args.FirstOrDefault())
{
    case "prepare":
        // prepare [count] [maxDownloadMB]: download the corpus (stops at whichever limit comes first).
        await Corpus.PrepareAsync(
            args.Length > 1 ? int.Parse(args[1]) : Corpus.DefaultCount,
            args.Length > 2 ? int.Parse(args[2]) : 4_000);
        return 0;

    case "size":
        // size: the on-disk size of the shared indexes the query benchmarks use.
        IndexSizes.Print(IndexSizes.Measure());
        return 0;

    case "export":
        // export: the latest BenchmarkDotNet results + index sizes -> benchmarks/results/results.json and summary.svg.
        var report = Report.Export();
        File.WriteAllText(Report.ChartSvg, Report.RenderSvg(report));
        Console.WriteLine($"Wrote {Report.ResultsJson}");
        Console.WriteLine($"Wrote {Report.ChartSvg}");
        return 0;

    case "chart":
        // chart: redraw summary.svg from the committed results.json (no benchmark run needed).
        File.WriteAllText(Report.ChartSvg, Report.RenderSvg(Report.Load()));
        Console.WriteLine($"Wrote {Report.ChartSvg}");
        return 0;

    case "compare":
        // compare: for each benchmark query, the articles only one engine matches (analysis differences).
        foreach (var kind in Enum.GetValues<QueryKind>())
        {
            var bench = new QueryBenchmarks { Kind = kind };
            bench.Setup();
            var (onlyLucene, onlyTantivy) = bench.DiffMatches();
            bench.Cleanup();
            Console.WriteLine($"{kind}: only Lucene {onlyLucene.Count} [{string.Join(", ", onlyLucene.Take(10))}], only Tantivy {onlyTantivy.Count} [{string.Join(", ", onlyTantivy.Take(10))}]");
        }
        return 0;

    default:
        // Anything else goes to BenchmarkDotNet, e.g. --filter '*Query*'. Results (including the JSON
        // that `export` reads) go to artifacts/BenchmarkDotNet/results.
        var config = DefaultConfig.Instance
            .WithArtifactsPath(Report.BdnArtifacts)
            .AddExporter(JsonExporter.Full);
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
        return 0;
}

namespace TantivyDotNet.Benchmarks
{
    public sealed record EngineSize(string Name, long Bytes, int Segments);

    public sealed record SizeReport(int Articles, long RawBytes, List<EngineSize> Engines);

    public static class IndexSizes
    {
        /// <summary>Sizes of the shared indexes (built if missing) and of the corpus text they hold.</summary>
        public static SizeReport Measure()
        {
            var (tantivy, lucene) = SharedIndexes.Ensure();
            var articles = Corpus.Load();
            var rawBytes = articles.Sum(a => (long)Encoding.UTF8.GetByteCount(a.Id) + Encoding.UTF8.GetByteCount(a.Title) + Encoding.UTF8.GetByteCount(a.Body));

            // Tantivy writes one file per segment component, named <segment uuid>.<ext>; Lucene one .si per segment.
            var tantivySegments = Directory.EnumerateFiles(tantivy).Select(Path.GetFileName)
                .OfType<string>()
                .Where(f => f.Contains('.') && f.Length > 32 && !f.StartsWith('.')).Select(f => f[..f.IndexOf('.')]).Distinct().Count();
            var luceneSegments = Directory.EnumerateFiles(lucene, "*.si").Count();

            return new SizeReport(articles.Count, rawBytes,
            [
                new("Lucene.NET", Corpus.DirectorySize(lucene), luceneSegments),
                new("TantivyDotNet", Corpus.DirectorySize(tantivy), tantivySegments),
            ]);
        }

        public static void Print(SizeReport sizes)
        {
            Console.WriteLine($"Corpus: {sizes.Articles:N0} articles, {sizes.RawBytes / 1048576.0:F1} MiB of text (id + title + body, UTF-8)");
            Console.WriteLine();
            Console.WriteLine("| Engine | Index size | vs. raw text | Segments |");
            Console.WriteLine("| --- | ---: | ---: | ---: |");
            foreach (var e in sizes.Engines)
                Console.WriteLine($"| {e.Name} | {e.Bytes / 1048576.0:F1} MiB | {100.0 * e.Bytes / sizes.RawBytes:F0}% | {e.Segments} |");
        }
    }
}
