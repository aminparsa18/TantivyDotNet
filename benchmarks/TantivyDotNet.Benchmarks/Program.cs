using System.Text;
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
        PrintSizes();
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
        // Anything else goes to BenchmarkDotNet, e.g. --filter '*Query*'.
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
}

static void PrintSizes()
{
    var (tantivy, lucene) = SharedIndexes.Ensure();
    var articles = Corpus.Load();
    var rawBytes = articles.Sum(a => (long)Encoding.UTF8.GetByteCount(a.Id) + Encoding.UTF8.GetByteCount(a.Title) + Encoding.UTF8.GetByteCount(a.Body));

    // Tantivy writes one file per segment component, named <segment uuid>.<ext>; Lucene one .si per segment.
    var tantivySegments = Directory.EnumerateFiles(tantivy).Select(Path.GetFileName)
        .OfType<string>()
        .Where(f => f.Contains('.') && f.Length > 32 && !f.StartsWith('.')).Select(f => f[..f.IndexOf('.')]).Distinct().Count();
    var luceneSegments = Directory.EnumerateFiles(lucene, "*.si").Count();

    Console.WriteLine($"Corpus: {articles.Count:N0} articles, {rawBytes / 1048576.0:F1} MiB of text (id + title + body, UTF-8)");
    Console.WriteLine();
    Console.WriteLine("| Engine | Index size | vs. raw text | Segments |");
    Console.WriteLine("| --- | ---: | ---: | ---: |");
    foreach (var (name, dir, segments) in new[] { ("Lucene.NET", lucene, luceneSegments), ("TantivyDotNet", tantivy, tantivySegments) })
    {
        var size = Corpus.DirectorySize(dir);
        Console.WriteLine($"| {name} | {size / 1048576.0:F1} MiB | {100.0 * size / rawBytes:F0}% | {segments} |");
    }
}
