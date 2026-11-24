namespace TantivyDotNet;

/// <summary>How unprefixed terms of a parsed query are combined.</summary>
public enum QueryOperator
{
    /// <summary><c>a b</c> means <c>a OR b</c> (the default).</summary>
    Or,
    /// <summary><c>a b</c> means <c>a AND b</c>.</summary>
    And,
}

/// <summary>Fuzzy matching for a field in <see cref="QueryParserOptions.FuzzyFields"/>; see <see cref="Query.Fuzzy"/>.</summary>
public sealed record FuzzyOptions
{
    /// <summary>Creates fuzzy options.</summary>
    /// <param name="distance">Maximum edit distance, 0 to 2.</param>
    /// <param name="transpositions">Count swapping two adjacent characters as one edit.</param>
    /// <param name="prefix">Match terms that start with something within the distance (search-as-you-type).</param>
    public FuzzyOptions(int distance = 1, bool transpositions = true, bool prefix = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(distance);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(distance, 2);
        Distance = distance;
        Transpositions = transpositions;
        Prefix = prefix;
    }

    /// <summary>Maximum edit distance, 0 to 2.</summary>
    public int Distance { get; }

    /// <summary>Whether swapping two adjacent characters counts as one edit.</summary>
    public bool Transpositions { get; }

    /// <summary>Whether terms only need to start with something within the distance.</summary>
    public bool Prefix { get; }
}

/// <summary>
/// Settings for parsing query text with <see cref="Query.Parse(string, QueryParserOptions)"/>.
/// Field names are checked when the query runs.
/// </summary>
/// <example>
/// <code>
/// var parser = new QueryParserOptions
/// {
///     DefaultFields = ["title", "body"],
///     DefaultOperator = QueryOperator.And,
///     FieldBoosts = new Dictionary&lt;string, float&gt; { ["title"] = 3f },
///     FuzzyFields = new Dictionary&lt;string, FuzzyOptions&gt; { ["body"] = new FuzzyOptions(distance: 1) },
///     Lenient = true,
/// };
/// var results = searcher.Search(Query.Parse(userInput, parser));
/// </code>
/// </example>
public sealed class QueryParserOptions
{
    internal static readonly QueryParserOptions Default = new();

    /// <summary>Fields searched by terms without a <c>field:</c> prefix. Null (the default) means every indexed text field.</summary>
    public IReadOnlyList<string>? DefaultFields
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
            if (Array.IndexOf(copy, null) >= 0) throw new ArgumentException("Field names can't be null.", nameof(DefaultFields));
            field = copy;
        }
    }

    /// <summary>How terms without an explicit <c>AND</c>/<c>OR</c> are combined. Default <see cref="QueryOperator.Or"/>.</summary>
    public QueryOperator DefaultOperator
    {
        get;
        init
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(DefaultOperator), value, null);
            field = value;
        }
    }

    /// <summary>
    /// Score multipliers per field, applied to every term matched in that field (multiplied with
    /// boosts written in the query, like <c>title:rust^2</c>). Null (the default) for none.
    /// </summary>
    public IReadOnlyDictionary<string, float>? FieldBoosts
    {
        get;
        init
        {
            if (value is not null)
            {
                foreach (var (name, boost) in value)
                    if (!float.IsFinite(boost)) throw new ArgumentException($"Boost for field '{name}' must be finite.", nameof(FieldBoosts));
                value = new Dictionary<string, float>(value, StringComparer.Ordinal).AsReadOnly();
            }
            field = value;
        }
    }

    /// <summary>
    /// Text fields whose terms are matched approximately: every term the query text searches in
    /// such a field becomes a fuzzy term (Tantivy has no per-term <c>word~1</c> syntax; <c>~</c> sets
    /// a phrase's slop). Phrases stay exact. Null (the default) for none.
    /// </summary>
    public IReadOnlyDictionary<string, FuzzyOptions>? FuzzyFields
    {
        get;
        init
        {
            if (value is not null)
            {
                foreach (var (name, options) in value)
                    if (options is null) throw new ArgumentException($"Fuzzy options for field '{name}' can't be null.", nameof(FuzzyFields));
                value = new Dictionary<string, FuzzyOptions>(value, StringComparer.Ordinal).AsReadOnly();
            }
            field = value;
        }
    }

    /// <summary>
    /// Whether to drop the parts of the text that can't be parsed (unbalanced quotes or
    /// parentheses, unknown fields, bad ranges) instead of throwing <see cref="QueryParseException"/>.
    /// Use it for text typed by end users. Default false.
    /// </summary>
    /// <remarks>
    /// Text that nests parentheses or <c>NOT</c> more than 32 levels deep isn't parsed at all: it
    /// matches nothing when lenient, and throws <see cref="QueryParseException"/> otherwise.
    /// </remarks>
    public bool Lenient { get; init; }

    internal void WriteJson(System.Text.Json.Utf8JsonWriter w)
    {
        if (DefaultFields is not null)
        {
            w.WriteStartArray("default_fields");
            foreach (var f in DefaultFields) w.WriteStringValue(f);
            w.WriteEndArray();
        }
        if (DefaultOperator == QueryOperator.And) w.WriteBoolean("conjunction", true);
        if (FieldBoosts is { Count: > 0 })
        {
            w.WriteStartObject("boosts");
            foreach (var (name, boost) in FieldBoosts) w.WriteNumber(name, boost);
            w.WriteEndObject();
        }
        if (FuzzyFields is { Count: > 0 })
        {
            w.WriteStartObject("fuzzy");
            foreach (var (name, f) in FuzzyFields)
            {
                w.WriteStartObject(name);
                w.WriteNumber("distance", f.Distance);
                w.WriteBoolean("transpositions", f.Transpositions);
                w.WriteBoolean("prefix", f.Prefix);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        if (Lenient) w.WriteBoolean("lenient", true);
    }
}
