using System.Buffers;
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
/// to create, but they keep their snapshot's files alive until disposed. A
/// <see cref="SearcherManager"/> shares one searcher and replaces it after commits.
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

    /// <summary>Set for searchers owned by a <see cref="SearcherManager"/>: <see cref="Dispose"/> is then a no-op.</summary>
    internal bool OwnedByManager { get; init; }

    internal SearcherHandle Handle => _handle;

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
    /// use <see cref="Search(Query, SearchOptions)"/> with <see cref="Query.Parse(string, QueryParserOptions)"/>.
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

        var request = RequestBuffer.Rent();
        try
        {
            options.WriteRequest(request.Writer, query);
            JsonElement root;
            using (var lease = _handle.Lease())
            {
                TdnString result, err;
                fixed (byte* rp = request.Buffer.WrittenSpan)
                {
                    Ffi.Check(NativeMethods.tdn_searcher_search(
                        (TdnSearcher*)lease.Pointer, rp, (nuint)request.Buffer.WrittenCount, &result, &err), err);
                }
                root = Ffi.TakeJson(result);
            }
            return SearchResults.Parse(root, this, options);
        }
        finally
        {
            RequestBuffer.Return(request);
        }
    }

    /// <summary>
    /// A per-thread buffer and writer for search requests, so a search doesn't allocate them. A
    /// buffer that grew past 64 KB (a huge query) is dropped rather than kept.
    /// </summary>
    private sealed class RequestBuffer
    {
        [ThreadStatic] private static RequestBuffer? t_cached;

        public readonly ArrayBufferWriter<byte> Buffer = new(1024);
        public readonly Utf8JsonWriter Writer;

        private RequestBuffer() => Writer = new Utf8JsonWriter(Buffer);

        public static RequestBuffer Rent()
        {
            var b = t_cached ?? new RequestBuffer();
            t_cached = null;   // not reentrant: a nested search on this thread gets its own
            return b;
        }

        public static void Return(RequestBuffer b)
        {
            if (b.Buffer.Capacity > 64 * 1024) return;
            b.Buffer.ResetWrittenCount();
            b.Writer.Reset(b.Buffer);
            t_cached = b;
        }
    }

    /// <summary>
    /// Explains how <paramref name="query"/> scores the document of <paramref name="hit"/>, for
    /// debugging relevance. The hit must come from this searcher.
    /// </summary>
    /// <example><c>Console.WriteLine(searcher.Explain(query, results[0]));</c></example>
    /// <exception cref="ArgumentException"><paramref name="hit"/> came from another searcher.</exception>
    /// <exception cref="TantivyException">
    /// With <see cref="TantivyErrorCode.InvalidArgument"/> if the query doesn't match the document,
    /// or doesn't fit the schema.
    /// </exception>
    public Explanation Explain(Query query, SearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(hit);
        if (!ReferenceEquals(hit.Origin, this))
            throw new ArgumentException("The hit comes from a different searcher; explain it with the searcher that returned it.", nameof(hit));
        byte[] q = query.ToJson();
        string json;
        using (var lease = _handle.Lease())
        {
            TdnString result, err;
            fixed (byte* qp = q)
                Ffi.Check(NativeMethods.tdn_searcher_explain((TdnSearcher*)lease.Pointer, qp, (nuint)q.Length, hit.SegmentOrd, hit.DocId, &result, &err), err);
            json = Ffi.TakeString(result);
        }
        using var doc = JsonDocument.Parse(json);
        return Explanation.Parse(doc.RootElement);
    }

    /// <summary>Counts the documents matching <paramref name="query"/> without retrieving them.</summary>
    /// <inheritdoc cref="Search(string, int, int, IEnumerable{string}?)" path="/param[@name='query']|/param[@name='defaultFields']|/exception"/>
    public long Count(string query, IEnumerable<string>? defaultFields = null) =>
        Search(query, limit: 0, defaultFields: defaultFields).TotalCount;

    /// <summary>Counts the documents matching <paramref name="query"/> without retrieving them.</summary>
    /// <inheritdoc cref="Search(Query, int, int)" path="/param[@name='query']|/exception"/>
    public long Count(Query query) => Search(query, limit: 0).TotalCount;

    /// <summary>
    /// Statistics of this searcher's snapshot: segments, document counts, and sizes on disk per
    /// segment and per field.
    /// </summary>
    /// <exception cref="TantivyException">
    /// With <see cref="TantivyErrorCode.Io"/> if the snapshot is outdated and its files were deleted
    /// after a merge replaced them. (The searcher can still search, because it keeps its files open,
    /// but sizes are read by reopening them.) Use a newer searcher or <see cref="SearchIndex.GetStats"/>.
    /// </exception>
    public IndexStats GetStats()
    {
        string json;
        using (var lease = _handle.Lease())
        {
            TdnString result, err;
            Ffi.Check(NativeMethods.tdn_searcher_stats((TdnSearcher*)lease.Pointer, &result, &err), err);
            json = Ffi.TakeString(result);
        }
        return IndexStats.Parse(json);
    }

    /// <summary>
    /// Releases the snapshot. Safe to call more than once. Does nothing for a searcher leased from a
    /// <see cref="SearcherManager"/>: dispose the <see cref="SearcherLease"/> instead.
    /// </summary>
    public void Dispose()
    {
        if (!OwnedByManager) _handle.Dispose();
    }
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

    // Shape: {"count": n, "hits": [{"score": f, "addr": [s, d], "doc": {...}, "snippets"?: {...}}]} (see native/src/search.rs).
    // `root` is self-contained (see Ffi.TakeJson), so hits keep elements of it without copying.
    internal static SearchResults Parse(JsonElement root, Searcher origin, SearchOptions options)
    {
        string signature = options.OrderSignature;
        bool sorted = options.OrderBy is { Count: > 0 };
        var hitsJson = root.GetProperty("hits");
        var hits = new SearchHit[hitsJson.GetArrayLength()];
        int i = 0;
        foreach (var h in hitsJson.EnumerateArray())
        {
            var score = h.GetProperty("score");
            var addr = h.GetProperty("addr");
            var sort = h.GetProperty("sort");
            uint segmentOrd = addr[0].GetUInt32(), docId = addr[1].GetUInt32();
            hits[i++] = new SearchHit(score.ValueKind == JsonValueKind.Null ? float.NaN : score.GetSingle(), new Document(h.GetProperty("doc")))
            {
                Origin = origin,
                SegmentOrd = segmentOrd,
                DocId = docId,
                Snippets = h.TryGetProperty("snippets", out var sn) ? ParseSnippets(sn) : SearchHit.NoSnippets,
                SortValues = sorted ? ParseSortValues(sort) : SearchHit.NoSortValues,
                CursorData = new HitCursor(signature, sort, segmentOrd, docId),
            };
        }
        var facets = root.TryGetProperty("facets", out var fj) ? ParseFacets(fj) : NoFacets;
        JsonElement? aggregations = root.TryGetProperty("aggregations", out var aj) ? aj : null;
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

    private static object?[] ParseSortValues(JsonElement sort)
    {
        var values = new object?[sort.GetArrayLength()];
        int i = 0;
        foreach (var v in sort.EnumerateArray()) values[i++] = ParseSortValue(v);
        return values;
    }

    // Tagged like query values: {"u64": 5}, {"date": micros}, {"score": f}, ... or null (see native/src/sorting.rs).
    private static object? ParseSortValue(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Null) return null;
        var p = json.EnumerateObject().Single();
        return p.Name switch
        {
            "score" => p.Value.GetSingle(),
            "str" => p.Value.GetString(),
            "u64" => p.Value.GetUInt64(),
            "i64" => p.Value.GetInt64(),
            "f64" => p.Value.GetDouble(),
            "bool" => p.Value.GetBoolean(),
            "date" => DateTimeOffset.UnixEpoch.AddTicks(p.Value.GetInt64() * TimeSpan.TicksPerMicrosecond),
            "bytes" => p.Value.EnumerateArray().Select(b => b.GetByte()).ToArray(),
            "ip" => System.Net.IPAddress.Parse(p.Value.GetString()!),
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

/// <summary>One search hit, from <see cref="SearchResults"/>.</summary>
public sealed class SearchHit
{
    internal SearchHit(float score, Document document)
    {
        Score = score;
        Document = document;
    }

    /// <summary>
    /// Relevance score (BM25); higher is better. <see cref="float.NaN"/> when sorted by fields only
    /// (<see cref="SearchOptions.OrderBy"/> without <see cref="SortBy.Score"/>), since scoring is skipped.
    /// </summary>
    public float Score { get; }

    /// <summary>The hit's stored fields (or those selected by <see cref="SearchOptions.Fields"/>).</summary>
    public Document Document { get; }

    internal static readonly IReadOnlyDictionary<string, Snippet> NoSnippets = new Dictionary<string, Snippet>();

    internal static readonly IReadOnlyList<object?> NoSortValues = [];

    /// <summary>
    /// When <see cref="SearchOptions.OrderBy"/> is set, the hit's value for each sort key, in order:
    /// a <see cref="string"/>, <see cref="ulong"/>, <see cref="long"/>, <see cref="double"/>,
    /// <see cref="bool"/>, <see cref="DateTimeOffset"/>, <see cref="T:byte[]"/> or <see cref="System.Net.IPAddress"/> for a field (null
    /// if the document has none), or a <see cref="float"/> for <see cref="SortBy.Score"/>. Empty when
    /// sorted by relevance only.
    /// </summary>
    public IReadOnlyList<object?> SortValues { get; init; } = NoSortValues;

    /// <summary>
    /// An opaque token marking this hit's position in the result order. Pass it as
    /// <see cref="SearchOptions.SearchAfter"/> (with the same <see cref="SearchOptions.OrderBy"/>) to
    /// get the hits that follow it.
    /// </summary>
    public string Cursor => CursorData.Encode();

    /// <summary>
    /// Snippets keyed by field name, for the fields requested in <see cref="SearchOptions.Snippets"/>;
    /// empty if none were requested.
    /// </summary>
    public IReadOnlyDictionary<string, Snippet> Snippets { get; init; } = NoSnippets;

    // Where the hit came from, for Searcher.Explain and Cursor. Not part of the public contract.
    internal HitCursor CursorData { get; init; }
    internal Searcher? Origin { get; init; }
    internal uint SegmentOrd { get; init; }
    internal uint DocId { get; init; }
}
