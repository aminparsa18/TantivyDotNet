using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TantivyDotNet.Benchmarks;

/// <summary>One Wikipedia article: its title and lead section ("abstract").</summary>
/// <param name="Id">Wikipedia page id.</param>
/// <param name="Links">Number of incoming links, used as a fast field for sorting.</param>
public sealed record Article(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("links")] ulong Links);

[JsonSerializable(typeof(Article))]
internal sealed partial class CorpusJson : JsonSerializerContext;

public static class Corpus
{
    /// <summary>
    /// English Wikipedia CirrusSearch dump of 2025-12-29: one JSON line per article, with the lead
    /// section already extracted as <c>opening_text</c>. (The old <c>abstract.xml</c> dumps are gone.)
    /// It is 43 GB, but it's streamed and only the first N articles are read.
    /// </summary>
    public const string DumpUrl =
        "https://dumps.wikimedia.org/other/cirrussearch/20251229/enwiki-20251229-cirrussearch-content.json.gz";

    public const int DefaultCount = 300_000;

    public static string RepoRoot { get; } = FindRepoRoot();

    public static string DefaultPath => Path.Combine(RepoRoot, "artifacts", "corpus", "wiki-abstracts.jsonl");

    /// <summary>The corpus file: $TDN_BENCH_CORPUS, or artifacts/corpus/wiki-abstracts.jsonl.</summary>
    public static string FilePath => Environment.GetEnvironmentVariable("TDN_BENCH_CORPUS") is { Length: > 0 } p ? p : DefaultPath;

    /// <summary>Scratch directory for benchmark indexes (artifacts/bench).</summary>
    public static string WorkDir => Path.Combine(RepoRoot, "artifacts", "bench");

    public static List<Article> Load(int? max = null)
    {
        if (!File.Exists(FilePath))
            throw new FileNotFoundException(
                $"Corpus not found at {FilePath}. Run `dotnet run -c Release --project benchmarks/TantivyDotNet.Benchmarks -- prepare` first.");
        var articles = new List<Article>();
        foreach (var line in File.ReadLines(FilePath))
        {
            if (articles.Count == max) break;
            articles.Add(JsonSerializer.Deserialize(line, CorpusJson.Default.Article)!);
        }
        return articles;
    }

    /// <summary>
    /// Streams the dump and writes the first <paramref name="count"/> articles with a non-empty lead.
    /// Stops early, keeping what it has, once <paramref name="maxDownloadMB"/> of compressed data has
    /// been read. Nothing but the output file is written to disk.
    /// </summary>
    public static async Task PrepareAsync(int count, int maxDownloadMB)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        Console.WriteLine($"Streaming {DumpUrl} (at most {maxDownloadMB:N0} MB)");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TantivyDotNet-benchmarks/1.0");
        await using var response = await http.GetStreamAsync(DumpUrl);
        await using var counting = new CountingStream(response);
        await using var gzip = new GZipStream(counting, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);

        var tmp = FilePath + ".tmp";
        var written = 0;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        void Report(string prefix = "  ")
        {
            var mb = counting.BytesRead / 1e6;
            Console.WriteLine($"{prefix}{written:N0} articles, {mb:N0} MB downloaded, {mb / started.Elapsed.TotalSeconds:F1} MB/s, {started.Elapsed:hh\\:mm\\:ss}");
        }
        await using (var output = new StreamWriter(tmp))
        {
            while (written < count && await reader.ReadLineAsync() is { } line)
            {
                if (started.Elapsed - lastReport >= TimeSpan.FromSeconds(10))
                {
                    lastReport = started.Elapsed;
                    Report();
                }
                if (counting.BytesRead > maxDownloadMB * 1_000_000L)
                {
                    Console.WriteLine($"  Download cap of {maxDownloadMB:N0} MB reached; stopping early.");
                    break;
                }
                // Bulk format: an {"index": ...} action line, then the page.
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("title", out var title)
                    || !root.TryGetProperty("opening_text", out var opening)
                    || opening.ValueKind != JsonValueKind.String
                    || opening.GetString() is not { Length: > 0 } body)
                    continue;
                var links = root.TryGetProperty("incoming_links", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetUInt64() : 0;
                var id = root.GetProperty("page_id").ToString();
                var article = new Article(id, title.GetString()!, body, links);
                await output.WriteLineAsync(JsonSerializer.Serialize(article, CorpusJson.Default.Article));
                written++;
            }
        }
        Report("Done: ");
        File.Move(tmp, FilePath, overwrite: true);
        Console.WriteLine($"Wrote {written:N0} articles ({new FileInfo(FilePath).Length / 1048576.0:F0} MiB) to {FilePath}");
    }

    public static long DirectorySize(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    /// <summary>Read-only pass-through stream that counts the bytes read from the network.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Count(await inner.ReadAsync(buffer, ct));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }

        private int Count(int n) { BytesRead += n; return n; }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "TantivyDotNet.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Run the benchmarks from inside the TantivyDotNet repository.");
    }
}
