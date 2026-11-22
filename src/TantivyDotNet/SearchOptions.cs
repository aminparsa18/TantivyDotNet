using System.Buffers;
using System.Text.Json;

namespace TantivyDotNet;

/// <summary>
/// Everything about a search besides the query: paging, ordering, snippets and facet counts.
/// Pass it to <see cref="Searcher.Search(Query, SearchOptions)"/>.
/// </summary>
/// <example>
/// <code>
/// var results = searcher.Search(query, new SearchOptions
/// {
///     Limit = 20,
///     Offset = 40,
///     OrderBy = SortBy.Field("year", descending: true),
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

    /// <summary>How to order hits. Null (the default) orders by relevance, best first.</summary>
    public SortBy? OrderBy { get; init; }

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

    // Shape: {"query": {...}, "limit": n, "offset": n, "sort"?: {...}, "snippets"?: {...}, "facets"?: [...]}
    // (see tdn_searcher_search in native/src/lib.rs).
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
            if (OrderBy is not null)
            {
                w.WritePropertyName("sort");
                OrderBy.WriteJson(w);
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
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}

/// <summary>An order for search hits other than relevance. Create one with <see cref="Field"/>.</summary>
public sealed class SortBy
{
    private SortBy(string field, bool descending)
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
    /// Documents without a value come last in either order; ties keep index order. Relevance is
    /// not computed, so <see cref="SearchHit.Score"/> is <see cref="float.NaN"/>; the value sorted
    /// by is in <see cref="SearchHit.SortValue"/>. For a multi-valued field, the first value is used.
    /// </remarks>
    public static SortBy Field(string field, bool descending = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(field);
        return new SortBy(field, descending);
    }

    /// <summary>The field sorted by.</summary>
    public string FieldName { get; }

    /// <summary>Whether the largest values come first.</summary>
    public bool Descending { get; }

    /// <summary>For example <c>year desc</c>.</summary>
    public override string ToString() => $"{FieldName} {(Descending ? "desc" : "asc")}";

    internal void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("field", FieldName);
        w.WriteString("order", Descending ? "desc" : "asc");
        w.WriteEndObject();
    }
}
