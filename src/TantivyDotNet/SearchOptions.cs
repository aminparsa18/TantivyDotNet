using System.Buffers;
using System.Text.Json;

namespace TantivyDotNet;

/// <summary>
/// Everything about a search besides the query: paging, ordering, returned fields, snippets, facet counts and aggregations.
/// Pass it to <see cref="Searcher.Search(Query, SearchOptions)"/>.
/// </summary>
/// <example>
/// <code>
/// var results = searcher.Search(query, new SearchOptions
/// {
///     Limit = 20,
///     Offset = 40,
///     OrderBy = [SortBy.Field("year", descending: true)],
///     Snippets = new SnippetOptions("body"),
///     Facets = [new FacetRequest("genre")],
/// });
/// </code>
/// </example>
public sealed class SearchOptions
{
    internal static readonly SearchOptions Default = new();

    /// <summary>Maximum number of hits to return. 0 returns no hits but still computes <see cref="SearchResults.TotalCount"/> and facets. Default 10.</summary>
    public int Limit
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 10;

    /// <summary>Number of top hits to skip, for paging. Default 0.</summary>
    public int Offset
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    }

    /// <summary>
    /// The stored fields to return in each hit's <see cref="SearchHit.Document"/>, or null (the
    /// default) for all of them. Fewer fields means less to decode and copy per hit; an empty list
    /// returns no fields and skips the document store entirely (unless <see cref="Snippets"/> are requested).
    /// </summary>
    /// <remarks>Each name must be a stored field, or the search throws <see cref="TantivyException"/> with <see cref="TantivyErrorCode.InvalidArgument"/>.</remarks>
    public IReadOnlyList<string>? Fields
    {
        get;
        init
        {
            if (value is null)
            {
                field = null;
                return;
            }
            string[] copy = [.. value];
            if (Array.IndexOf(copy, null) >= 0) throw new ArgumentException("Field names can't be null.", nameof(Fields));
            field = copy;
        }
    }

    /// <summary>
    /// How to order hits: one or more keys, each breaking ties of the previous ones, for example
    /// <c>[SortBy.Field("year", descending: true), SortBy.Score()]</c>. Null or empty (the default)
    /// orders by relevance, best first. Remaining ties keep index order.
    /// </summary>
    public IReadOnlyList<SortBy>? OrderBy
    {
        get;
        init
        {
            if (value is null)
            {
                field = null;
                return;
            }
            SortBy[] copy = [.. value];
            if (Array.IndexOf(copy, null) >= 0) throw new ArgumentException("Sort keys can't be null.", nameof(OrderBy));
            field = copy;
        }
    }

    /// <summary>
    /// Returns only hits that come after the hit whose <see cref="SearchHit.Cursor"/> this is, for
    /// deep paging: unlike <see cref="Offset"/>, the cost doesn't grow with the page number. Null
    /// (the default) starts from the first hit.
    /// </summary>
    /// <remarks>
    /// The cursor must come from a search with the same <see cref="OrderBy"/>, and <see cref="Offset"/>
    /// must be 0. Pages are exact when every page is read from the same <see cref="Searcher"/>; with
    /// a newer searcher, paging continues by sort value, but documents tied on every key may be
    /// skipped or repeated. <see cref="SearchResults.TotalCount"/> still counts all matches.
    /// </remarks>
    /// <example>
    /// <code>
    /// var options = new SearchOptions { Limit = 100, OrderBy = [SortBy.Field("date", descending: true)] };
    /// for (var page = searcher.Search(query, options); page.Count > 0;
    ///      page = searcher.Search(query, new SearchOptions { Limit = 100, OrderBy = options.OrderBy, SearchAfter = page[^1].Cursor }))
    /// {
    ///     // ...
    /// }
    /// </code>
    /// </example>
    public string? SearchAfter { get; init; }

    /// <summary>Fields to compute <see cref="SearchHit.Snippets"/> for, or null for none.</summary>
    public SnippetOptions? Snippets { get; init; }

    /// <summary>
    /// Facet categories to count over all matching documents (not just the returned page), or null
    /// for none. Results are in <see cref="SearchResults.Facets"/>.
    /// </summary>
    public IReadOnlyList<FacetRequest>? Facets
    {
        get;
        init
        {
            if (value is null)
            {
                field = null;
                return;
            }
            FacetRequest[] copy = [.. value];
            if (Array.IndexOf(copy, null) >= 0) throw new ArgumentException("Facet requests can't be null.", nameof(Facets));
            field = copy;
        }
    }

    /// <summary>
    /// Aggregations to compute over all matching documents, as a JSON object in Tantivy's
    /// Elasticsearch-compatible format, or null for none. Results are in
    /// <see cref="SearchResults.Aggregations"/>. Aggregated fields must be fast fields.
    /// </summary>
    /// <example>
    /// <code>
    /// Aggregations = """
    ///     {"by_decade": {"histogram": {"field": "year", "interval": 10}},
    ///      "avg_price": {"avg": {"field": "price"}}}
    ///     """
    /// </code>
    /// Supported kinds include <c>terms</c>, <c>range</c>, <c>histogram</c>, <c>date_histogram</c>,
    /// <c>avg</c>, <c>sum</c>, <c>min</c>, <c>max</c>, <c>stats</c>, <c>percentiles</c>,
    /// <c>cardinality</c> and <c>top_hits</c>, with nested <c>aggs</c>; see Tantivy's aggregation documentation.
    /// </example>
    /// <exception cref="ArgumentException">The value isn't a JSON object.</exception>
    public string? Aggregations
    {
        get;
        init
        {
            if (value is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(value);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("Aggregations must be a JSON object, like {\"name\": {\"avg\": {\"field\": \"price\"}}}.", nameof(Aggregations));
                }
                catch (JsonException e)
                {
                    throw new ArgumentException($"Aggregations isn't valid JSON: {e.Message}", nameof(Aggregations), e);
                }
            }
            field = value;
        }
    }

    /// <summary>Identifies the sort order inside cursors, for example <c>year desc,_score</c>.</summary>
    internal string OrderSignature => OrderBy is { Count: > 0 } keys ? string.Join(",", keys) : "_score";

    // Shape: {"query": {...}, "limit": n, "offset": n, "sort"?: [...], "search_after"?: {...}, "fields"?: [...], "snippets"?: {...}, "facets"?: [...],
    // "aggregations"?: {...}} (see native/src/search.rs).
    internal byte[] ToRequestJson(Query query)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WritePropertyName("query");
            query.WriteJson(w);
            w.WriteNumber("limit", Limit);
            w.WriteNumber("offset", Offset);
            if (OrderBy is { Count: > 0 })
            {
                w.WriteStartArray("sort");
                foreach (var key in OrderBy) key.WriteJson(w);
                w.WriteEndArray();
            }
            if (SearchAfter is not null)
            {
                if (Offset != 0) throw new ArgumentException("Offset must be 0 when SearchAfter is set.");
                var cursor = HitCursor.Decode(SearchAfter);
                if (cursor.OrderSignature != OrderSignature)
                    throw new ArgumentException(
                        $"SearchAfter is a cursor for order '{cursor.OrderSignature}', but this search orders by '{OrderSignature}'.");
                w.WriteStartObject("search_after");
                w.WritePropertyName("keys");
                w.WriteRawValue(cursor.SortKeysJson, skipInputValidation: true);   // validated by Decode
                w.WriteStartArray("addr");
                w.WriteNumberValue(cursor.SegmentOrd);
                w.WriteNumberValue(cursor.DocId);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            if (Fields is not null)
            {
                w.WriteStartArray("fields");
                foreach (var f in Fields) w.WriteStringValue(f);
                w.WriteEndArray();
            }
            if (Snippets is not null)
            {
                w.WritePropertyName("snippets");
                Snippets.WriteJson(w);
            }
            if (Facets is not null)
            {
                w.WriteStartArray("facets");
                foreach (var f in Facets) f.WriteJson(w);
                w.WriteEndArray();
            }
            if (Aggregations is not null)
            {
                w.WritePropertyName("aggregations");
                w.WriteRawValue(Aggregations, skipInputValidation: true);   // validated in the setter
            }
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}

/// <summary>
/// A sort key for <see cref="SearchOptions.OrderBy"/>. Create one with <see cref="Field"/> or <see cref="Score"/>.
/// </summary>
public sealed class SortBy
{
    private static readonly SortBy s_score = new(null, descending: true);

    private SortBy(string? field, bool descending)
    {
        FieldName = field;
        Descending = descending;
    }

    /// <summary>
    /// Orders hits by the value of a fast field (one created with <c>fast: true</c>): u64, i64,
    /// f64, bool, date, bytes, or text (ordinal, byte-wise order).
    /// </summary>
    /// <param name="field">The fast field to sort by.</param>
    /// <param name="descending">Largest values first. Default false: smallest first.</param>
    /// <remarks>
    /// Documents without a value come last in either order. Unless <see cref="Score"/> is also a key,
    /// relevance isn't computed and <see cref="SearchHit.Score"/> is <see cref="float.NaN"/>. The
    /// values sorted by are in <see cref="SearchHit.SortValues"/>. For a multi-valued field, the
    /// first value is used.
    /// </remarks>
    public static SortBy Field(string field, bool descending = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(field);
        return new SortBy(field, descending);
    }

    /// <summary>Orders hits by relevance, best first. Useful as a tie-breaker after a field, or before one.</summary>
    public static SortBy Score() => s_score;

    /// <summary>The field sorted by, or null for <see cref="Score"/>.</summary>
    public string? FieldName { get; }

    /// <summary>Whether the largest values come first (always true for <see cref="Score"/>).</summary>
    public bool Descending { get; }

    /// <summary>For example <c>year desc</c>, or <c>_score</c>.</summary>
    public override string ToString() => FieldName is null ? "_score" : $"{FieldName} {(Descending ? "desc" : "asc")}";

    internal void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        if (FieldName is null)
        {
            w.WriteString("type", "score");
        }
        else
        {
            w.WriteString("type", "field");
            w.WriteString("field", FieldName);
            w.WriteString("order", Descending ? "desc" : "asc");
        }
        w.WriteEndObject();
    }
}

/// <summary>
/// The opaque <see cref="SearchHit.Cursor"/>: base64url of <c>{"o": order signature, "k": [tagged sort values], "a": [segment, doc]}</c>.
/// </summary>
internal readonly record struct HitCursor(string OrderSignature, string SortKeysJson, uint SegmentOrd, uint DocId)
{
    public string Encode()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("o", OrderSignature);
            w.WritePropertyName("k");
            w.WriteRawValue(SortKeysJson, skipInputValidation: true);
            w.WriteStartArray("a");
            w.WriteNumberValue(SegmentOrd);
            w.WriteNumberValue(DocId);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Convert.ToBase64String(buffer.WrittenSpan).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static HitCursor Decode(string cursor)
    {
        try
        {
            string b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
            var root = doc.RootElement;
            var keys = root.GetProperty("k");
            if (keys.ValueKind != JsonValueKind.Array) throw new FormatException();
            var addr = root.GetProperty("a");
            return new HitCursor(root.GetProperty("o").GetString()!, keys.GetRawText(), addr[0].GetUInt32(), addr[1].GetUInt32());
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new ArgumentException("SearchAfter isn't a cursor returned by SearchHit.Cursor.", nameof(SearchOptions.SearchAfter), e);
        }
    }
}
