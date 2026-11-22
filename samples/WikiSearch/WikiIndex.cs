using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TantivyDotNet;

namespace WikiSearch;

/// <summary>One line of the corpus file (the format the benchmarks' <c>prepare</c> command writes).</summary>
public sealed record Article(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("links")] ulong Links);

/// <summary>Owns the index and one shared searcher (searchers are thread-safe).</summary>
public sealed class WikiIndex : IDisposable
{
    private static readonly Schema Schema = new SchemaBuilder()
        .AddStringField("id", stored: true)
        .AddTextField("title", stored: true)
        .AddTextField("body", stored: true)
        .AddU64Field("links", stored: true, fast: true)     // incoming links: filter, sort, aggregate
        .Build();

    // Popularity buckets by incoming links, counted in the same pass as the hits.
    private const string PopularityAggregation = """
        {"popularity": {"range": {"field": "links", "ranges": [
            {"to": 10}, {"from": 10, "to": 100}, {"from": 100, "to": 1000}, {"from": 1000}]}}}
        """;

    private readonly SearchIndex _index;
    private readonly Searcher _searcher;

    private WikiIndex(SearchIndex index)
    {
        _index = index;
        _searcher = index.CreateSearcher();
    }

    public long DocumentCount => _searcher.DocumentCount;

    /// <summary>Opens the index in <paramref name="indexDir"/>, building it from the corpus file first if it doesn't exist.</summary>
    public static WikiIndex OpenOrBuild(string indexDir, string corpusFile, ILogger log)
    {
        if (Directory.Exists(indexDir) && Directory.EnumerateFileSystemEntries(indexDir).Any())
            return new WikiIndex(SearchIndex.Open(indexDir));

        if (!File.Exists(corpusFile))
            throw new FileNotFoundException(
                $"No index at {indexDir} and no corpus at {corpusFile}. Download one with " +
                "`dotnet run -c Release --project benchmarks/TantivyDotNet.Benchmarks -- prepare`, or pass --corpus <file.jsonl>.");

        log.LogInformation("Indexing {Corpus} into {Index}...", corpusFile, indexDir);
        var stopwatch = Stopwatch.StartNew();
        var index = SearchIndex.Create(indexDir, Schema);
        var count = 0;
        using (var writer = index.CreateWriter(heapBytes: 200_000_000))
        {
            foreach (var line in File.ReadLines(corpusFile))
            {
                // Each line is already a JSON document with the schema's field names: add it as is.
                writer.AddDocument(line);
                count++;
            }
            writer.Commit();
        }
        log.LogInformation("Indexed {Count:N0} articles in {Seconds:F1} s", count, stopwatch.Elapsed.TotalSeconds);
        return new WikiIndex(index);
    }

    public SearchResponse Search(string q, int page, string sort, ulong? minLinks, ulong? maxLinks)
    {
        const int PageSize = 10;
        var stopwatch = Stopwatch.StartNew();

        // Query syntax: words, "phrases", AND/OR/NOT, -excluded, title:word, links:>100, word~1 ...
        Query query = string.IsNullOrWhiteSpace(q) ? Query.All : Query.Parse(q, ["title", "body"]);
        if (minLinks is not null || maxLinks is not null)
            query &= Query.Range("links", minLinks, maxLinks, includeUpper: false);

        var options = new SearchOptions
        {
            Limit = PageSize,
            Offset = (Math.Max(page, 1) - 1) * PageSize,
            OrderBy = sort == "links" ? SortBy.Field("links", descending: true) : null,
            Snippets = new SnippetOptions("body") { MaxLength = 240 },
            Aggregations = PopularityAggregation,
        };
        var results = _searcher.Search(query, options);

        // Nothing found for a single word: maybe a typo. Retry allowing one edit (einstien -> einstein).
        // Fuzzy terms aren't analyzed, so lowercase the word as the default tokenizer does.
        var fuzzy = false;
        if (results.TotalCount == 0 && q.Trim() is { Length: >= 4 } word && word.All(char.IsLetter))
        {
            word = word.ToLowerInvariant();
            var typoTolerant = Query.Fuzzy("title", word, distance: 1).Boost(2) | Query.Fuzzy("body", word, distance: 1);
            if (minLinks is not null || maxLinks is not null)
                typoTolerant &= Query.Range("links", minLinks, maxLinks, includeUpper: false);
            results = _searcher.Search(typoTolerant, options);
            fuzzy = true;
        }

        var hits = results.Select(hit =>
        {
            var doc = hit.Document;
            var title = doc.GetString("title")!;
            var snippet = hit.Snippets["body"];
            // No highlighted match in the body (e.g. the match was in the title): show the start of the abstract.
            var html = snippet.IsEmpty ? System.Net.WebUtility.HtmlEncode(Truncate(doc.GetString("body") ?? "", 240)) : snippet.ToHtml("<mark>", "</mark>");
            return new Hit(
                title,
                "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_')),
                html,
                doc.GetU64("links") ?? 0,
                double.IsNaN(hit.Score) ? null : hit.Score);
        }).ToList();

        var buckets = results.Aggregations!.Value.GetProperty("popularity").GetProperty("buckets").EnumerateArray()
            .Select(b => new Bucket(
                b.TryGetProperty("from", out var from) ? (ulong)from.GetDouble() : null,
                b.TryGetProperty("to", out var to) ? (ulong)to.GetDouble() : null,
                b.GetProperty("doc_count").GetInt64()))
            .ToList();

        return new SearchResponse(results.TotalCount, stopwatch.Elapsed.TotalMilliseconds, fuzzy, hits, buckets);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public void Dispose()
    {
        _searcher.Dispose();
        _index.Dispose();
    }
}

public sealed record Hit(string Title, string Url, string SnippetHtml, ulong Links, double? Score);

public sealed record Bucket(ulong? From, ulong? To, long Count);

public sealed record SearchResponse(long Total, double TookMs, bool Fuzzy, IReadOnlyList<Hit> Hits, IReadOnlyList<Bucket> Popularity);

public sealed record ErrorResponse(string Error);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SearchResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class AppJson : JsonSerializerContext;
