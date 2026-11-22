using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TantivyDotNet.Interop;
using TantivyDotNet.Native;

namespace TantivyDotNet;

/// <summary>
/// Runs queries against a point-in-time snapshot of an index. Created by
/// <see cref="SearchIndex.CreateSearcher"/>.
/// </summary>
/// <remarks>
/// <b>Thread-safe:</b> one searcher can serve concurrent queries from many threads. It never sees
/// commits made after it was created; create a new searcher to pick those up. Searchers are cheap
/// to create, but they keep their snapshot's files alive until disposed.
/// </remarks>
public sealed unsafe class Searcher : IDisposable
{
    private readonly SearcherHandle _handle;

    internal Searcher(SearcherHandle handle, Schema schema)
    {
        _handle = handle;
        Schema = schema;
    }

    /// <summary>The schema of the searched index.</summary>
    public Schema Schema { get; }

    /// <summary>Number of live (non-deleted) documents in this snapshot.</summary>
    public long DocumentCount
    {
        get
        {
            using var lease = _handle.Lease();
            return (long)NativeMethods.tdn_searcher_num_docs((TdnSearcher*)lease.Pointer);
        }
    }

    /// <summary>
    /// Parses <paramref name="query"/> with Tantivy's query language and returns the best-scoring
    /// documents. Shorthand for <c>Search(Query.Parse(query, defaultFields), limit, offset)</c>.
    /// </summary>
    /// <param name="query">
    /// For example <c>body:mice AND year:[1930 TO 1940]</c>, <c>"exact phrase"</c>, <c>title:rust -java</c>.
    /// Terms without a <c>field:</c> prefix search <paramref name="defaultFields"/>. Terms are combined with OR by default.
    /// </param>
    /// <param name="limit">Maximum number of hits to return. 0 returns no hits but still computes <see cref="SearchResults.TotalCount"/>.</param>
    /// <param name="offset">Number of top hits to skip, for paging.</param>
    /// <param name="defaultFields">Fields searched by unprefixed terms. Null means every indexed text field.</param>
    /// <exception cref="QueryParseException">The query is malformed or references an unknown field.</exception>
    public SearchResults Search(string query, int limit = 10, int offset = 0, IEnumerable<string>? defaultFields = null) =>
        Search(Query.Parse(query, defaultFields), limit, offset);

    /// <summary>
    /// Parses <paramref name="query"/> with Tantivy's query language (unprefixed terms search every
    /// indexed text field) and runs it with <paramref name="options"/>. To choose the default fields,
    /// use <see cref="Search(Query, SearchOptions)"/> with <see cref="Query.Parse"/>.
    /// </summary>
    /// <param name="query">The query text; see <see cref="Search(string, int, int, IEnumerable{string}?)"/>.</param>
    /// <param name="options">Paging, ordering, snippets and facets.</param>
    /// <exception cref="QueryParseException">The query is malformed or references an unknown field.</exception>
    /// <exception cref="TantivyException">An option doesn't fit the schema; see <see cref="Search(Query, SearchOptions)"/>.</exception>
    public SearchResults Search(string query, SearchOptions options) => Search(Query.Parse(query), options);

    /// <summary>Runs a query built in code and returns the best-scoring documents.</summary>
    /// <param name="query">The query; see <see cref="Query"/>.</param>
    /// <param name="limit">Maximum number of hits to return. 0 returns no hits but still computes <see cref="SearchResults.TotalCount"/>.</param>
    /// <param name="offset">Number of top hits to skip, for paging.</param>
    /// <exception cref="TantivyException">
    /// A field doesn't exist or doesn't fit the query (for example a phrase on a numeric field),
    /// or a <see cref="ParsedQuery"/> inside the query is malformed (<see cref="QueryParseException"/>).
    /// </exception>
    public SearchResults Search(Query query, int limit = 10, int offset = 0) =>
        Search(query, limit == 10 && offset == 0 ? SearchOptions.Default : new SearchOptions { Limit = limit, Offset = offset });

    /// <summary>Runs a query built in code with paging, ordering, snippets and facets.</summary>
    /// <param name="query">The query; see <see cref="Query"/>.</param>
    /// <param name="options">Paging, ordering, snippets and facets.</param>
    /// <exception cref="TantivyException">
    /// A field doesn't exist or doesn't fit the query (for example a phrase on a numeric field);
    /// the sort field isn't a fast field of a sortable type; a snippet field isn't a stored text
    /// field; a facet request names a non-facet field or the same field twice; or a
    /// <see cref="ParsedQuery"/> inside the query is malformed (<see cref="QueryParseException"/>).
    /// </exception>
    public SearchResults Search(Query query, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);

        byte[] request = options.ToRequestJson(query);
        byte[] resultJson;
        using (var lease = _handle.Lease())
        {
            TdnString result, err;
            fixed (byte* rp = request)
            {
                Ffi.Check(NativeMethods.tdn_searcher_search(
                    (TdnSearcher*)lease.Pointer, rp, (nuint)request.Length, &result, &err), err);
            }
            resultJson = Ffi.TakeBytes(result);
        }
        return SearchResults.Parse(resultJson);
    }

    /// <summary>Counts the documents matching <paramref name="query"/> without retrieving them.</summary>
    /// <inheritdoc cref="Search(string, int, int, IEnumerable{string}?)" path="/param[@name='query']|/param[@name='defaultFields']|/exception"/>
    public long Count(string query, IEnumerable<string>? defaultFields = null) =>
        Search(query, limit: 0, defaultFields: defaultFields).TotalCount;

    /// <summary>Counts the documents matching <paramref name="query"/> without retrieving them.</summary>
    /// <inheritdoc cref="Search(Query, int, int)" path="/param[@name='query']|/exception"/>
    public long Count(Query query) => Search(query, limit: 0).TotalCount;

    /// <summary>Releases the snapshot. Safe to call more than once.</summary>
    public void Dispose() => _handle.Dispose();
}

/// <summary>The hits returned by <see cref="Searcher.Search(Query, SearchOptions)"/>, best first.</summary>
public sealed class SearchResults : IReadOnlyList<SearchHit>
{
    private readonly SearchHit[] _hits;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<FacetCount>> NoFacets = new Dictionary<string, IReadOnlyList<FacetCount>>();

    private SearchResults(SearchHit[] hits, long totalCount, IReadOnlyDictionary<string, IReadOnlyList<FacetCount>> facets, JsonElement? aggregations)
    {
        _hits = hits;
        TotalCount = totalCount;
        Facets = facets;
        Aggregations = aggregations;
    }

    /// <summary>
    /// The results of <see cref="SearchOptions.Aggregations"/>, keyed by the names used in the
    /// request (Tantivy's Elasticsearch-style result JSON), or null if none were requested.
    /// </summary>
    /// <example>
    /// <c>{"by_decade": {"buckets": [{"key": 1930.0, "doc_count": 2}, ...]}, "avg_price": {"value": 9.5}}</c>
    /// </example>
    public JsonElement? Aggregations { get; }

    /// <summary>Deserializes <see cref="Aggregations"/> into <typeparamref name="T"/> (AOT-safe).</summary>
    /// <param name="typeInfo">Source-generated metadata for <typeparamref name="T"/>, for example <c>MyJsonContext.Default.MyAggs</c>.</param>
    /// <exception cref="InvalidOperationException">No aggregations were requested.</exception>
    public T? GetAggregations<T>(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return RequireAggregations().Deserialize(typeInfo);
    }

    /// <summary>
    /// Deserializes <see cref="Aggregations"/> into <typeparamref name="T"/> with reflection-based
    /// System.Text.Json. For trimmed or NativeAOT apps, use <see cref="GetAggregations{T}(JsonTypeInfo{T})"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No aggregations were requested.</exception>
    [RequiresUnreferencedCode("Reflection-based JSON deserialization. Use GetAggregations<T>(JsonTypeInfo<T>) for trimmed apps.")]
    [RequiresDynamicCode("Reflection-based JSON deserialization. Use GetAggregations<T>(JsonTypeInfo<T>) for NativeAOT apps.")]
    public T? GetAggregations<T>() => RequireAggregations().Deserialize<T>();

    private JsonElement RequireAggregations() =>
        Aggregations ?? throw new InvalidOperationException("No aggregations were requested; set SearchOptions.Aggregations.");

    /// <summary>
    /// Facet counts keyed by facet field, for the <see cref="SearchOptions.Facets"/> requested:
    /// the children of each requested path, with the number of matching documents in each. Children
    /// with no matches are left out. Empty if no facets were requested.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<FacetCount>> Facets { get; }

    /// <summary>Total number of matching documents, which can exceed the number of hits returned.</summary>
    public long TotalCount { get; }

    /// <summary>Number of hits returned (at most the requested limit).</summary>
    public int Count => _hits.Length;

    /// <inheritdoc/>
    public SearchHit this[int index] => _hits[index];

    /// <inheritdoc/>
    public IEnumerator<SearchHit> GetEnumerator() => ((IEnumerable<SearchHit>)_hits).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // Shape: {"count": n, "hits": [{"score": f, "doc": {...}, "snippets"?: {...}}]} (see tdn_searcher_search).
    internal static SearchResults Parse(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var hitsJson = root.GetProperty("hits");
        var hits = new SearchHit[hitsJson.GetArrayLength()];
        int i = 0;
        foreach (var h in hitsJson.EnumerateArray())
        {
            var score = h.GetProperty("score");
            hits[i++] = new SearchHit(score.ValueKind == JsonValueKind.Null ? float.NaN : score.GetSingle(), new Document(h.GetProperty("doc").Clone()))
            {
                Snippets = h.TryGetProperty("snippets", out var sn) ? ParseSnippets(sn) : SearchHit.NoSnippets,
                SortValue = h.TryGetProperty("sort", out var sv) ? ParseSortValue(sv) : null,
            };
        }
        var facets = root.TryGetProperty("facets", out var fj) ? ParseFacets(fj) : NoFacets;
        JsonElement? aggregations = root.TryGetProperty("aggregations", out var aj) ? aj.Clone() : null;
        return new SearchResults(hits, root.GetProperty("count").GetInt64(), facets, aggregations);
    }

    // Shape: {"field": [{"path": "/a", "count": n}, ...]} (see native/src/facets.rs).
    private static Dictionary<string, IReadOnlyList<FacetCount>> ParseFacets(JsonElement json)
    {
        var map = new Dictionary<string, IReadOnlyList<FacetCount>>();
        foreach (var field in json.EnumerateObject())
        {
            var counts = new FacetCount[field.Value.GetArrayLength()];
            int i = 0;
            foreach (var c in field.Value.EnumerateArray())
                counts[i++] = new FacetCount(c.GetProperty("path").GetString()!, c.GetProperty("count").GetInt64());
            map[field.Name] = counts;
        }
        return map;
    }

    // Tagged like query values: {"u64": 5}, {"date": micros}, ... or null (see native/src/sorting.rs).
    private static object? ParseSortValue(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Null) return null;
        var p = json.EnumerateObject().Single();
        return p.Name switch
        {
            "str" => p.Value.GetString(),
            "u64" => p.Value.GetUInt64(),
            "i64" => p.Value.GetInt64(),
            "f64" => p.Value.GetDouble(),
            "bool" => p.Value.GetBoolean(),
            "date" => DateTimeOffset.UnixEpoch.AddTicks(p.Value.GetInt64() * TimeSpan.TicksPerMicrosecond),
            "bytes" => p.Value.EnumerateArray().Select(b => b.GetByte()).ToArray(),
            _ => throw new InvalidOperationException($"Unexpected sort value kind '{p.Name}'."),
        };
    }

    private static Dictionary<string, Snippet> ParseSnippets(JsonElement json)
    {
        var map = new Dictionary<string, Snippet>();
        foreach (var p in json.EnumerateObject()) map[p.Name] = Snippet.Parse(p.Value);
        return map;
    }
}

/// <summary>One search hit.</summary>
/// <param name="Score">Relevance score (BM25); higher is better. <see cref="float.NaN"/> when sorted by a field (<see cref="SortBy"/>), since scoring is skipped.</param>
/// <param name="Document">The hit's stored fields.</param>
public sealed record SearchHit(float Score, Document Document)
{
    internal static readonly IReadOnlyDictionary<string, Snippet> NoSnippets = new Dictionary<string, Snippet>();

    /// <summary>
    /// When sorted with <see cref="SortBy.Field"/>, the hit's value of the sort field (a
    /// <see cref="string"/>, <see cref="ulong"/>, <see cref="long"/>, <see cref="double"/>,
    /// <see cref="bool"/>, <see cref="DateTimeOffset"/> or <see cref="T:byte[]"/>), or null if the
    /// document has none. Null when sorted by relevance.
    /// </summary>
    public object? SortValue { get; init; }

    /// <summary>
    /// Snippets keyed by field name, for the fields requested in <see cref="SearchOptions.Snippets"/>;
    /// empty if none were requested.
    /// </summary>
    public IReadOnlyDictionary<string, Snippet> Snippets { get; init; } = NoSnippets;
}
