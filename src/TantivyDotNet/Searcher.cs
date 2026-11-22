using System.Buffers;
using System.Collections;
using System.Text.Json;
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
    /// <param name="snippets">Fields to compute <see cref="SearchHit.Snippets"/> for, or null for none.</param>
    /// <param name="facets">Facet categories to count over all matching documents (not just the returned page), or null for none. See <see cref="SearchResults.Facets"/>.</param>
    /// <exception cref="QueryParseException">The query is malformed or references an unknown field.</exception>
    public SearchResults Search(string query, int limit = 10, int offset = 0, IEnumerable<string>? defaultFields = null,
        SnippetOptions? snippets = null, IEnumerable<FacetRequest>? facets = null) =>
        Search(Query.Parse(query, defaultFields), limit, offset, snippets, facets);

    /// <summary>Runs a query built in code and returns the best-scoring documents.</summary>
    /// <param name="query">The query; see <see cref="Query"/>.</param>
    /// <param name="limit">Maximum number of hits to return. 0 returns no hits but still computes <see cref="SearchResults.TotalCount"/>.</param>
    /// <param name="offset">Number of top hits to skip, for paging.</param>
    /// <param name="snippets">Fields to compute <see cref="SearchHit.Snippets"/> for, or null for none.</param>
    /// <param name="facets">Facet categories to count over all matching documents (not just the returned page), or null for none. See <see cref="SearchResults.Facets"/>.</param>
    /// <exception cref="TantivyException">
    /// A field doesn't exist or doesn't fit the query (for example a phrase on a numeric field),
    /// a snippet field isn't a stored text field, a facet request names a non-facet field or the
    /// same field twice, or a <see cref="ParsedQuery"/> inside the query
    /// is malformed (<see cref="QueryParseException"/>).
    /// </exception>
    public SearchResults Search(Query query, int limit = 10, int offset = 0, SnippetOptions? snippets = null, IEnumerable<FacetRequest>? facets = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        FacetRequest[]? facetArray = facets?.ToArray();
        if (facetArray is not null && Array.IndexOf(facetArray, null) >= 0)
            throw new ArgumentException("Facet requests can't be null.", nameof(facets));
        byte[] request = SearchRequest(query, limit, offset, snippets, facetArray);
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
    /// <inheritdoc cref="Search(string, int, int, IEnumerable{string}?, SnippetOptions?, IEnumerable{FacetRequest}?)" path="/param[@name='query']|/param[@name='defaultFields']|/exception"/>
    public long Count(string query, IEnumerable<string>? defaultFields = null) =>
        Search(query, limit: 0, defaultFields: defaultFields).TotalCount;

    /// <summary>Counts the documents matching <paramref name="query"/> without retrieving them.</summary>
    /// <inheritdoc cref="Search(Query, int, int, SnippetOptions?, IEnumerable{FacetRequest}?)" path="/param[@name='query']|/exception"/>
    public long Count(Query query) => Search(query, limit: 0).TotalCount;

    /// <summary>Releases the snapshot. Safe to call more than once.</summary>
    public void Dispose() => _handle.Dispose();

    // Shape: {"query": {...}, "limit": n, "offset": n, "snippets"?: {...}, "facets"?: [...]} (see tdn_searcher_search in native/src/lib.rs).
    private static byte[] SearchRequest(Query query, int limit, int offset, SnippetOptions? snippets, FacetRequest[]? facets)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WritePropertyName("query");
            query.WriteJson(w);
            w.WriteNumber("limit", limit);
            w.WriteNumber("offset", offset);
            if (snippets is not null)
            {
                w.WritePropertyName("snippets");
                snippets.WriteJson(w);
            }
            if (facets is not null)
            {
                w.WriteStartArray("facets");
                foreach (var f in facets) f.WriteJson(w);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}

/// <summary>The hits returned by <see cref="Searcher.Search(Query, int, int, SnippetOptions?, IEnumerable{FacetRequest}?)"/>, best first.</summary>
public sealed class SearchResults : IReadOnlyList<SearchHit>
{
    private readonly SearchHit[] _hits;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<FacetCount>> NoFacets = new Dictionary<string, IReadOnlyList<FacetCount>>();

    private SearchResults(SearchHit[] hits, long totalCount, IReadOnlyDictionary<string, IReadOnlyList<FacetCount>> facets)
    {
        _hits = hits;
        TotalCount = totalCount;
        Facets = facets;
    }

    /// <summary>
    /// Facet counts keyed by facet field, for the <see cref="FacetRequest"/>s passed to the search:
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
            hits[i++] = new SearchHit(h.GetProperty("score").GetSingle(), new Document(h.GetProperty("doc").Clone()))
            {
                Snippets = h.TryGetProperty("snippets", out var sn) ? ParseSnippets(sn) : SearchHit.NoSnippets,
            };
        var facets = root.TryGetProperty("facets", out var fj) ? ParseFacets(fj) : NoFacets;
        return new SearchResults(hits, root.GetProperty("count").GetInt64(), facets);
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

    private static Dictionary<string, Snippet> ParseSnippets(JsonElement json)
    {
        var map = new Dictionary<string, Snippet>();
        foreach (var p in json.EnumerateObject()) map[p.Name] = Snippet.Parse(p.Value);
        return map;
    }
}

/// <summary>One search hit.</summary>
/// <param name="Score">Relevance score (BM25); higher is better.</param>
/// <param name="Document">The hit's stored fields.</param>
public sealed record SearchHit(float Score, Document Document)
{
    internal static readonly IReadOnlyDictionary<string, Snippet> NoSnippets = new Dictionary<string, Snippet>();

    /// <summary>
    /// Snippets keyed by field name, for the fields requested with <see cref="SnippetOptions"/>;
    /// empty if none were requested.
    /// </summary>
    public IReadOnlyDictionary<string, Snippet> Snippets { get; init; } = NoSnippets;
}
