using System.Text.Json;
using System.Text.Json.Serialization;

namespace TantivyDotNet.Tests;

public sealed record Bucket([property: JsonPropertyName("key")] JsonElement Key, [property: JsonPropertyName("doc_count")] long DocCount);
public sealed record BucketList([property: JsonPropertyName("buckets")] Bucket[] Buckets);
public sealed record Stats(
    [property: JsonPropertyName("count")] long Count,
    [property: JsonPropertyName("min")] double Min,
    [property: JsonPropertyName("max")] double Max,
    [property: JsonPropertyName("avg")] double Avg);
public sealed record ShopAggs(
    [property: JsonPropertyName("by_decade")] BucketList ByDecade,
    [property: JsonPropertyName("price")] Stats Price);

[JsonSerializable(typeof(ShopAggs))]
internal sealed partial class AggJsonContext : JsonSerializerContext;

public class AggregationTests
{
    private const string ShopRequest = """
        {"by_decade": {"histogram": {"field": "year", "interval": 10}},
         "price": {"stats": {"field": "price"}}}
        """;

    private static SearchIndex Shop()
    {
        var schema = new SchemaBuilder()
            .AddStringField("id", stored: true)
            .AddTextField("title")
            .AddStringField("genre", fast: true)
            .AddU64Field("year", fast: true)
            .AddF64Field("price", fast: true)
            .AddDateField("published", fast: true)
            .AddU64Field("not_fast")
            .Build();
        var index = SearchIndex.CreateInRam(schema);
        using var writer = index.CreateWriter();
        writer.AddDocument("""{"id":"a","title":"of mice and men","genre":"novel","year":1937,"price":10,"published":"2024-01-10T00:00:00Z"}""");
        writer.AddDocument("""{"id":"b","title":"east of eden","genre":"novel","year":1952,"price":20,"published":"2024-01-20T00:00:00Z"}""");
        writer.AddDocument("""{"id":"c","title":"travels with charley","genre":"travel","year":1962,"price":5,"published":"2024-02-05T00:00:00Z"}""");
        writer.AddDocument("""{"id":"d","title":"the pearl","genre":"novella","year":1947,"price":7.5,"published":"2024-03-01T00:00:00Z"}""");
        writer.Commit();
        return index;
    }

    private static JsonElement Aggs(Searcher s, Query q, string request) =>
        s.Search(q, new SearchOptions { Limit = 0, Aggregations = request }).Aggregations!.Value;

    [Fact]
    public void Metrics_OverAllMatches()
    {
        using var index = Shop();
        using var searcher = index.CreateSearcher();

        var r = Aggs(searcher, Query.All, """{"avg": {"avg": {"field": "price"}}, "s": {"stats": {"field": "price"}}, "n": {"value_count": {"field": "year"}}}""");
        Assert.Equal(10.625, r.GetProperty("avg").GetProperty("value").GetDouble());
        Assert.Equal(5.0, r.GetProperty("s").GetProperty("min").GetDouble());
        Assert.Equal(20.0, r.GetProperty("s").GetProperty("max").GetDouble());
        Assert.Equal(42.5, r.GetProperty("s").GetProperty("sum").GetDouble());
        Assert.Equal(4, r.GetProperty("n").GetProperty("value").GetDouble());

        // Only matching documents are aggregated.
        var novels = Aggs(searcher, Query.Term("genre", "novel"), """{"avg": {"avg": {"field": "price"}}}""");
        Assert.Equal(15.0, novels.GetProperty("avg").GetProperty("value").GetDouble());
    }

    [Fact]
    public void Buckets_HistogramTermsRangeAndNestedMetrics()
    {
        using var index = Shop();
        using var searcher = index.CreateSearcher();

        var hist = Aggs(searcher, Query.All, """{"h": {"histogram": {"field": "year", "interval": 10}}}""").GetProperty("h").GetProperty("buckets");
        Assert.Equal([(1930.0, 1L), (1940.0, 1L), (1950.0, 1L), (1960.0, 1L)],
            hist.EnumerateArray().Select(b => (b.GetProperty("key").GetDouble(), b.GetProperty("doc_count").GetInt64())));

        var terms = Aggs(searcher, Query.All, """{"g": {"terms": {"field": "genre"}, "aggs": {"p": {"avg": {"field": "price"}}}}}""").GetProperty("g").GetProperty("buckets");
        var novel = terms.EnumerateArray().First();                                // most documents first
        Assert.Equal("novel", novel.GetProperty("key").GetString());
        Assert.Equal(2, novel.GetProperty("doc_count").GetInt64());
        Assert.Equal(15.0, novel.GetProperty("p").GetProperty("value").GetDouble());
        Assert.Equal(3, terms.GetArrayLength());

        var range = Aggs(searcher, Query.All, """{"r": {"range": {"field": "price", "ranges": [{"to": 10}, {"from": 10}]}}}""").GetProperty("r").GetProperty("buckets");
        Assert.Equal([2L, 2L], range.EnumerateArray().Select(b => b.GetProperty("doc_count").GetInt64()));

        var monthly = Aggs(searcher, Query.All, """{"m": {"date_histogram": {"field": "published", "fixed_interval": "30d"}}}""").GetProperty("m").GetProperty("buckets");
        Assert.Equal(4, monthly.EnumerateArray().Sum(b => b.GetProperty("doc_count").GetInt64()));
    }

    [Fact]
    public void TypedResults_SourceGeneratedAndReflection()
    {
        using var index = Shop();
        using var searcher = index.CreateSearcher();
        var results = searcher.Search(Query.All, new SearchOptions { Limit = 0, Aggregations = ShopRequest });

        var typed = results.GetAggregations(AggJsonContext.Default.ShopAggs)!;
        Assert.Equal(4, typed.Price.Count);
        Assert.Equal(10.625, typed.Price.Avg);
        Assert.Equal([1930.0, 1940.0, 1950.0, 1960.0], typed.ByDecade.Buckets.Select(b => b.Key.GetDouble()));

        Assert.Equal(typed.Price, results.GetAggregations<ShopAggs>()!.Price);
    }

    [Fact]
    public void Aggregations_CombineWithHitsSortingAndFacets()
    {
        using var index = Shop();
        using var searcher = index.CreateSearcher();

        var results = searcher.Search("mice OR eden OR pearl", new SearchOptions
        {
            Limit = 2,
            OrderBy = SortBy.Field("price", descending: true),
            Aggregations = """{"max": {"max": {"field": "year"}}}""",
        });
        Assert.Equal(3, results.TotalCount);
        Assert.Equal([20.0, 10.0], results.Select(h => h.SortValue));
        Assert.Equal(1952.0, results.Aggregations!.Value.GetProperty("max").GetProperty("value").GetDouble());

        Assert.Null(searcher.Search("mice").Aggregations);
    }

    [Fact]
    public void Aggregations_Errors()
    {
        using var index = Shop();
        using var searcher = index.CreateSearcher();

        Assert.Throws<ArgumentException>(() => new SearchOptions { Aggregations = "{not json" });
        Assert.Throws<ArgumentException>(() => new SearchOptions { Aggregations = "[1, 2]" });

        var unknownKind = Assert.Throws<TantivyException>(() => Aggs(searcher, Query.All, """{"x": {"median": {"field": "price"}}}"""));
        Assert.Equal(TantivyErrorCode.InvalidArgument, unknownKind.Code);
        Assert.Contains("median", unknownKind.Message);

        var notFast = Assert.Throws<TantivyException>(() => Aggs(searcher, Query.All, """{"x": {"avg": {"field": "not_fast"}}}"""));
        Assert.Equal(TantivyErrorCode.InvalidArgument, notFast.Code);

        // Like Elasticsearch, an unknown field aggregates nothing rather than failing.
        Assert.Equal(JsonValueKind.Null, Aggs(searcher, Query.All, """{"x": {"avg": {"field": "nope"}}}""").GetProperty("x").GetProperty("value").ValueKind);

        Assert.Throws<InvalidOperationException>(() => searcher.Search("mice").GetAggregations(AggJsonContext.Default.ShopAggs));
    }
}
