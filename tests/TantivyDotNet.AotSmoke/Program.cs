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
        .AddStringField("id", stored: true)
        .AddTextField("title", stored: true)
        .AddTextField("body", stored: true)
        .AddU64Field("year", stored: true, fast: true)
        .AddFacetField("genre", stored: true)
        .Build();

    using (var index = SearchIndex.Create(dir, schema))
    using (var writer = index.CreateWriter(heapBytes: 20_000_000))
    {
        writer.AddDocument(new Book("b1", "Of Mice and Men", "Two drifters dream of a farm with rabbits.", 1937, "/fiction/novella"),
                           SmokeJson.Default.Book);
        writer.AddDocument(new Book("b2", "The Grapes of Wrath", "A family flees the dust bowl for California.", 1939, "/fiction/novel"),
                           SmokeJson.Default.Book);
        writer.AddDocument("""{"id": "b3", "title": "Cannery Row", "body": "Mack and the boys throw a party.", "year": 1945, "genre": "/fiction/novel"}""");
        writer.AddDocument("""{"id": "old", "title": "Draft", "body": "to be deleted", "year": 1900, "genre": "/draft"}""");
        writer.DeleteTerm("id", "old");
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
        OrderBy = SortBy.Field("year", descending: true),
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

    try
    {
        searcher.Search("title:(unclosed", limit: 1);
        Check(false, "query parse error throws");
    }
    catch (QueryParseException)
    {
        Check(true, "query parse error throws QueryParseException");
    }
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
