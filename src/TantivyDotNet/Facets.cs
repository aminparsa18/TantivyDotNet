using System.Text.Json;

namespace TantivyDotNet;

/// <summary>
/// Asks <see cref="Searcher.Search(Query, int, int, SnippetOptions?, IEnumerable{FacetRequest}?)"/>
/// to count the matching documents in each child category of some facet paths. The counts appear
/// in <see cref="SearchResults.Facets"/> under <see cref="Field"/>.
/// </summary>
/// <example>
/// <code>
/// var results = searcher.Search(query, facets: [new FacetRequest("genre", "/")]);
/// foreach (var (path, count) in results.Facets["genre"]) Console.WriteLine($"{path}: {count}");
/// // /fiction: 12
/// // /history: 3
/// </code>
/// </example>
public sealed class FacetRequest
{
    private readonly string[] _paths;

    /// <summary>Counts the children of each of <paramref name="paths"/> in facet field <paramref name="field"/>.</summary>
    /// <param name="field">A facet field (<see cref="SchemaBuilder.AddFacetField"/>).</param>
    /// <param name="paths">
    /// Parent categories whose direct children are counted. None means the root, <c>/</c>, so the
    /// top-level categories.
    /// </param>
    public FacetRequest(string field, params IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(paths);
        Field = field;
        _paths = [.. paths];
        if (_paths.Length == 0) _paths = ["/"];
        foreach (var p in _paths)
        {
            if (p is null || !p.StartsWith('/'))
                throw new ArgumentException($"Facet paths start with '/', like \"/genre/fiction\"; got '{p}'.", nameof(paths));
        }
    }

    /// <summary>The facet field.</summary>
    public string Field { get; }

    /// <summary>The parent categories whose children are counted.</summary>
    public IReadOnlyList<string> Paths => _paths;

    /// <summary>
    /// If set, return only this many children per path, those with the highest counts, highest
    /// first. If null (the default), return every child that has matches, in path order.
    /// </summary>
    public int? Top
    {
        get;
        init
        {
            if (value is { } v) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(v);
            field = value;
        }
    }

    internal void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("field", Field);
        w.WriteStartArray("paths");
        foreach (var p in _paths) w.WriteStringValue(p);
        w.WriteEndArray();
        if (Top is { } top) w.WriteNumber("top", top);
        w.WriteEndObject();
    }
}

/// <summary>The number of matching documents in one facet category (including its subcategories).</summary>
/// <param name="Path">The category, for example <c>/genre/fiction</c>.</param>
/// <param name="Count">How many matching documents are in it.</param>
public sealed record FacetCount(string Path, long Count);
