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
    /// <exception cref="QueryParseException">The query is malformed or references an unknown field.</exception>
    public SearchResults Search(string query, int limit = 10, int offset = 0, IEnumerable<string>? defaultFields = null) =>
        Search(Query.Parse(query, defaultFields), limit, offset);

    /// <summary>Runs a query built in code and returns the best-scoring documents.</summary>
    /// <param name="query">The query; see <see cref="Query"/>.</param>
    /// <param name="limit">Maximum number of hits to return. 0 returns no hits but still computes <see cref="SearchResults.TotalCount"/>.</param>
    /// <param name="offset">Number of top hits to skip, for paging.</param>
    /// <exception cref="TantivyException">
    /// A field doesn't exist or doesn't fit the query (for example a phrase on a numeric field),
    /// or a <see cref="ParsedQuery"/> inside it is malformed (<see cref="QueryParseException"/>).
    /// </exception>
    public SearchResults Search(Query query, int limit = 10, int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        byte[] request = SearchRequest(query, limit, offset);
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

    // Shape: {"query": {...}, "limit": n, "offset": n} (see tdn_searcher_search in native/src/lib.rs).
    private static byte[] SearchRequest(Query query, int limit, int offset)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WritePropertyName("query");
            query.WriteJson(w);
            w.WriteNumber("limit", limit);
            w.WriteNumber("offset", offset);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}

/// <summary>The hits returned by <see cref="Searcher.Search(Query, int, int)"/>, best first.</summary>
public sealed class SearchResults : IReadOnlyList<SearchHit>
{
    private readonly SearchHit[] _hits;

    private SearchResults(SearchHit[] hits, long totalCount)
    {
        _hits = hits;
        TotalCount = totalCount;
    }

    /// <summary>Total number of matching documents, which can exceed the number of hits returned.</summary>
    public long TotalCount { get; }

    /// <summary>Number of hits returned (at most the requested limit).</summary>
    public int Count => _hits.Length;

    /// <inheritdoc/>
    public SearchHit this[int index] => _hits[index];

    /// <inheritdoc/>
    public IEnumerator<SearchHit> GetEnumerator() => ((IEnumerable<SearchHit>)_hits).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // Shape: {"count": n, "hits": [{"score": f, "doc": {...}}]} (see tdn_searcher_search).
    internal static SearchResults Parse(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var hitsJson = root.GetProperty("hits");
        var hits = new SearchHit[hitsJson.GetArrayLength()];
        int i = 0;
        foreach (var h in hitsJson.EnumerateArray())
            hits[i++] = new SearchHit(h.GetProperty("score").GetSingle(), new Document(h.GetProperty("doc").Clone()));
        return new SearchResults(hits, root.GetProperty("count").GetInt64());
    }
}

/// <summary>One search hit.</summary>
/// <param name="Score">Relevance score (BM25); higher is better.</param>
/// <param name="Document">The hit's stored fields.</param>
public sealed record SearchHit(float Score, Document Document);
