// End-to-end check that TantivyDotNet works in a NativeAOT app: every feature area goes through
// the native library at least once, using only the trim/AOT-safe overloads. Exits non-zero on failure.

using System.Text.Json.Serialization;
using TantivyDotNet;

var failures = 0;
void Check(bool condition, string what)
{
    Console.WriteLine($"{(condition ? "ok  " : "FAIL")} {what}");
    if (!condition) failures++;
}

var dir = Path.Combine(Path.GetTempPath(), "tdn-aot-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    var schema = new SchemaBuilder()
        .AddAnalyzer("en", Analyzer.Simple().LowerCase().StopWords(AnalyzerLanguage.English).Stem(AnalyzerLanguage.English))
        .AddStringField("id", stored: true, fast: true)
        .AddTextField("title", stored: true)
        .AddTextField("body", stored: true, tokenizer: "en")
        .AddJsonField("attrs", stored: true, fast: true)
        .AddIpAddressField("ip", stored: true, fast: true)
        .AddU64Field("year", stored: true, fast: true)
        .AddFacetField("genre", stored: true)
        .Build();

    using (var index = SearchIndex.Create(dir, schema))
    using (var writer = index.CreateWriter(new IndexWriterOptions { HeapBytes = 20_000_000 }))
    {
        writer.AddDocument(new Book("b1", "Of Mice and Men", "Two drifters dream of a farm with rabbits.", 1937, "/fiction/novella"),
                           SmokeJson.Default.Book);
        writer.AddDocument(new Book("b2", "The Grapes of Wrath", "A family flees the dust bowl for California.", 1939, "/fiction/novel"),
                           SmokeJson.Default.Book);
        writer.AddDocument("""{"id": "b3", "title": "Cannery Row", "body": "Mack and the boys throw a party.", "year": 1945, "genre": "/fiction/novel"}""");
        writer.AddDocument("""{"id": "old", "title": "Draft", "body": "to be deleted", "year": 1900, "genre": "/draft"}""");
        writer.DeleteTerm("id", "old");
        writer.AddDocument("""{"id": "tmp", "title": "Temp", "body": "x", "year": 1800, "genre": "/draft"}""");
        writer.DeleteDocuments(Query.Range("year", null, 1850L));
        writer.UpdateDocument(Query.Term("id", "b3"), """{"id": "b3", "title": "Cannery Row", "body": "Mack and the boys throw parties.", "year": 1945, "genre": "/fiction/novel", "attrs": {"pages": 185}, "ip": "10.0.0.3"}""");
        try
        {
            writer.AddDocument("""{"id": "x", "Title": "wrong case"}""");
            Check(false, "unknown field is rejected");
        }
        catch (TantivyException)
        {
            Check(true, "unknown field is rejected");
        }
        writer.Commit();
    }

    // Reopen from disk: exercises the on-disk round trip as well.
    using var reopened = SearchIndex.Open(dir);
    using var searcher = reopened.CreateSearcher();
    Check(searcher.DocumentCount == 3, $"document count after delete = {searcher.DocumentCount}");

    var parsed = searcher.Search("rabbits", limit: 5, defaultFields: ["body"]);
    Check(parsed.Count == 1 && parsed[0].Document.GetString("id") == "b1", "parsed query finds b1");

    var query = (Query.Term("title", "grapes") | Query.Fuzzy("title", "canery", distance: 1))
                & Query.Range("year", 1930, 1950);
    var results = searcher.Search(query, new SearchOptions
    {
        Limit = 10,
        OrderBy = [SortBy.Field("year", descending: true)],
        Snippets = new SnippetOptions("body"),
        Facets = [new FacetRequest("genre", "/fiction")],
        Aggregations = """{"avg_year": {"avg": {"field": "year"}}}""",
    });
    Check(results.TotalCount == 2, $"query tree matches 2 (got {results.TotalCount})");
    Check(results.Count == 2 && results[0].Document.GetU64("year") == 1945, "sorted by year descending");
    Check(results.Facets["genre"].Any(f => f.Path == "/fiction/novel" && f.Count == 2), "facet counts");
    var aggs = results.GetAggregations(SmokeJson.Default.YearAggs);
    Check(aggs?.AvgYear?.Value == 1942, $"typed aggregations (avg = {aggs?.AvgYear?.Value})");

    var phrase = searcher.Search(Query.Phrase("body", "dust bowl"), new SearchOptions { Snippets = new SnippetOptions("body") });
    Check(phrase.Count == 1 && phrase[0].Snippets["body"].ToHtml().Contains("<b>dust</b>"), "phrase query with snippet");

    Check(reopened.Analyze("en", "The parties") is ["parti"], "persisted analyzer restored by Open");
    Check(searcher.Count("body:party") == 1, "custom analyzer stems at query time");
    Check(searcher.Count(Query.Term("attrs.pages", 185L)) == 1, "JSON path term query");
    Check(searcher.Count(Query.Term("ip", System.Net.IPAddress.Parse("10.0.0.3"))) == 1, "IP term query");
    Check(searcher.Count(Query.Regex("title", "gr.*") | Query.TermSet("id", ["b1"]) | Query.Exists("attrs")) == 3, "regex, term set, exists");
    Check(searcher.Count(Query.DisjunctionMax([Query.Phrase("title", "row"), Query.Phrase("body", "row")]).ConstantScore()) == 1, "dis-max and constant score");
    var mlt = searcher.Search(Query.MoreLikeThis(new Dictionary<string, string> { ["body"] = "a family and the dust" },
        new MoreLikeThisOptions { MinDocFrequency = 1, MinTermFrequency = 1 }));
    Check(mlt.Count > 0 && mlt[0].Document.GetString("id") == "b2", "more like this");

    SortBy[] order = [SortBy.Field("year"), SortBy.Score()];
    var page1 = searcher.Search(Query.All, new SearchOptions { Limit = 2, Fields = ["id"], OrderBy = order });
    var page2 = searcher.Search(Query.All, new SearchOptions { Limit = 2, Fields = ["id"], OrderBy = order, SearchAfter = page1[^1].Cursor });
    Check(string.Join(",", page1.Concat(page2).Select(h => h.Document.GetString("id"))) == "b1,b2,b3", "multi-key sort with cursor paging and field selection");
    Check(searcher.Explain(Query.Parse("rabbits"), parsed[0]).Value > 0, "explain");
    var parser = new QueryParserOptions { DefaultOperator = QueryOperator.And, Lenient = true, FuzzyFields = new Dictionary<string, FuzzyOptions> { ["title"] = new(1) } };
    Check(searcher.Count(Query.Parse("canery row (", parser)) == 1, "query parser options");

    try
    {
        searcher.Search("title:(unclosed", limit: 1);
        Check(false, "query parse error throws");
    }
    catch (QueryParseException)
    {
        Check(true, "query parse error throws QueryParseException");
    }

    // Index lifecycle: settings, merge policy, force merge, GC, stats, searcher manager.
    string lifecycleDir = Path.Combine(dir, "lifecycle");
    var settings = new IndexSettings { DocStoreCompression = DocStoreCompression.Zstd, ZstdLevel = 5 };
    using (var lifecycle = SearchIndex.Create(lifecycleDir, new SchemaBuilder().AddStringField("id", stored: true).Build(), settings))
    {
        using var manager = lifecycle.CreateSearcherManager(new SearcherManagerOptions { Warmer = s => s.Search(Query.All, limit: 1) });
        using (var w = lifecycle.CreateWriter(new IndexWriterOptions { MergePolicy = MergePolicy.None, ThreadCount = 1 }))
        {
            for (int i = 0; i < 3; i++)
            {
                w.AddDocument($$"""{"id": "l{{i}}"}""");
                w.Commit();
            }
            Check(lifecycle.GetStats().SegmentCount == 3, "one segment per commit without merges");
            w.ForceMerge();
            w.GarbageCollectFiles();
        }
        var stats = lifecycle.GetStats();
        Check(stats.SegmentCount == 1 && stats.DocumentCount == 3 && stats.SizeBytes > 0, $"stats after force merge: {stats}");
        Check(manager.Refresh(), "searcher manager refresh");
        using var lease = manager.Acquire();
        Check(lease.Searcher.DocumentCount == 3, "leased searcher sees the commits");
    }
    using (var lifecycleReopened = SearchIndex.Open(lifecycleDir))
        Check(lifecycleReopened.Settings.DocStoreCompression == DocStoreCompression.Zstd && lifecycleReopened.Settings.ZstdLevel == 5, "settings saved with the index");
}
catch (Exception e)
{
    Console.WriteLine($"FAIL unexpected exception: {e}");
    failures++;
}
finally
{
    try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
}

Console.WriteLine(failures == 0 ? "AOT smoke test passed" : $"AOT smoke test: {failures} failure(s)");
return failures == 0 ? 0 : 1;

record Book(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("year")] ulong Year,
    [property: JsonPropertyName("genre")] string Genre);

record YearAggs([property: JsonPropertyName("avg_year")] MetricValue? AvgYear);
record MetricValue([property: JsonPropertyName("value")] double? Value);

[JsonSerializable(typeof(Book))]
[JsonSerializable(typeof(YearAggs))]
partial class SmokeJson : JsonSerializerContext;
