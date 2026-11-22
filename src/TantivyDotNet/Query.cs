using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;

namespace TantivyDotNet;

/// <summary>
/// A query built in code, passed to <see cref="Searcher.Search(Query, SearchOptions)"/>. Create one with
/// the static factories (<see cref="Term(string, string)"/>, <see cref="Phrase"/>, <see cref="Fuzzy"/>,
/// <see cref="Boolean"/>, <see cref="Range(string, long?, long?, bool, bool)"/>, <see cref="Parse(string, QueryParserOptions)"/>, ...)
/// and combine them with <c>&amp;</c>, <c>|</c> and <c>!</c>.
/// </summary>
/// <remarks>
/// Queries are immutable, hold no native resources, and can be reused across searches, searchers
/// and threads. Field names and value types are checked when the query runs; a mismatch throws
/// <see cref="TantivyException"/> with <see cref="TantivyErrorCode.InvalidArgument"/>.
/// </remarks>
/// <example>
/// <code>
/// var q = Query.Phrase("body", "of mice") &amp; Query.Range("year", 1930, 1940) &amp; !Query.Term("id", "b4");
/// var hits = searcher.Search(q);
/// </code>
/// </example>
public abstract class Query
{
    private protected Query() { }

    internal abstract void WriteJson(Utf8JsonWriter w);

    /// <summary>Matches every document, all with the same score.</summary>
    public static Query All { get; } = new AllQuery();

    /// <summary>
    /// Matches documents whose <paramref name="field"/> contains exactly <paramref name="value"/>.
    /// For a JSON field, <paramref name="field"/> is a path such as <c>attrs.color</c>.
    /// </summary>
    /// <remarks>
    /// The value is <b>not</b> analyzed: with the default tokenizer, text is indexed lowercased, so
    /// <c>Term("title", "Mice")</c> matches nothing while <c>Term("title", "mice")</c> does. Use
    /// <see cref="Phrase"/> or <see cref="Parse(string, QueryParserOptions)"/> to have the text analyzed, or a string field
    /// (<see cref="SchemaBuilder.AddStringField"/>) for exact identifiers.
    /// </remarks>
    public static TermQuery Term(string field, string value) => new(field, NotNull(value));

    /// <inheritdoc cref="Term(string, string)" path="/summary"/>
    public static TermQuery Term(string field, ulong value) => new(field, value);

    /// <summary>
    /// Matches documents whose <paramref name="field"/> contains exactly <paramref name="value"/>.
    /// Also works on u64 fields (for non-negative values) and f64 fields.
    /// </summary>
    public static TermQuery Term(string field, long value) => new(field, value);

    /// <inheritdoc cref="Term(string, string)" path="/summary"/>
    public static TermQuery Term(string field, double value) => new(field, value);

    /// <inheritdoc cref="Term(string, string)" path="/summary"/>
    public static TermQuery Term(string field, bool value) => new(field, value);

    /// <summary>
    /// Matches documents whose date <paramref name="field"/> equals <paramref name="value"/>, at the
    /// index's date precision (seconds by default).
    /// </summary>
    public static TermQuery Term(string field, DateTimeOffset value) => new(field, value);

    /// <inheritdoc cref="Term(string, DateTimeOffset)"/>
    /// <remarks>A <see cref="DateTimeKind.Unspecified"/> value is treated as UTC.</remarks>
    public static TermQuery Term(string field, DateTime value) => new(field, ToOffset(value));

    /// <inheritdoc cref="Term(string, string)" path="/summary"/>
    public static TermQuery Term(string field, ReadOnlySpan<byte> value) => new(field, value.ToArray());

    /// <summary>Matches documents whose IP address <paramref name="field"/> equals <paramref name="value"/> (IPv4 also matches its IPv4-mapped IPv6 form).</summary>
    public static TermQuery Term(string field, IPAddress value) => new(field, NotNull(value));

    /// <summary>
    /// Matches documents in the facet category <paramref name="path"/> or any category below it:
    /// <c>Facet("genre", "/fiction")</c> matches <c>/fiction</c> and <c>/fiction/classic</c>.
    /// </summary>
    /// <param name="field">A facet field (<see cref="SchemaBuilder.AddFacetField"/>).</param>
    /// <param name="path">A path starting with <c>/</c>.</param>
    public static FacetQuery Facet(string field, string path)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(path);
        if (!path.StartsWith('/')) throw new ArgumentException("Facet paths start with '/', like \"/genre/fiction\".", nameof(path));
        return new FacetQuery(field, path);
    }

    /// <summary>
    /// Matches terms of a text <paramref name="field"/> within <paramref name="distance"/> edits
    /// (insertions, deletions, substitutions) of <paramref name="value"/>.
    /// </summary>
    /// <param name="field">An indexed text field.</param>
    /// <param name="value">The term to approximate. Not analyzed (see <see cref="Term(string, string)"/>), so pass it lowercased for the default tokenizer.</param>
    /// <param name="distance">Maximum edit distance, 0 to 2.</param>
    /// <param name="transpositions">Count swapping two adjacent characters as one edit instead of two.</param>
    /// <param name="prefix">Match terms that <i>start</i> with something within the distance (search-as-you-type).</param>
    public static FuzzyQuery Fuzzy(string field, string value, int distance = 1, bool transpositions = true, bool prefix = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(distance);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(distance, 2);
        return new FuzzyQuery(NotNull(field), NotNull(value), distance, transpositions, prefix);
    }

    /// <summary>
    /// Matches documents where the words of <paramref name="text"/> appear in order in
    /// <paramref name="field"/>. The text is analyzed with the field's tokenizer.
    /// </summary>
    /// <param name="field">A text field indexed with positions (all text fields created by <see cref="SchemaBuilder"/> are).</param>
    /// <param name="text">The phrase. A single word becomes a term query; text with no words matches nothing.</param>
    /// <param name="slop">
    /// How many positions words may be moved in total and still match. 0 means the exact phrase;
    /// <c>"a b"</c> with slop 1 matches <c>"a x b"</c>, and swapping two words costs 2.
    /// </param>
    public static PhraseQuery Phrase(string field, string text, int slop = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slop);
        return new PhraseQuery(NotNull(field), NotNull(text), slop);
    }

    /// <summary>Combines clauses, like <c>+a b -c</c> in the query language.</summary>
    /// <param name="must">Every one of these must match.</param>
    /// <param name="should">
    /// If there are no <paramref name="must"/> clauses, at least one of these must match.
    /// Otherwise they are optional and only raise the score of documents they match.
    /// </param>
    /// <param name="mustNot">None of these may match. If these are the only clauses, the query matches every other document.</param>
    /// <remarks>A query with no clauses at all matches nothing.</remarks>
    public static BooleanQuery Boolean(IEnumerable<Query>? must = null, IEnumerable<Query>? should = null, IEnumerable<Query>? mustNot = null) =>
        new(Clauses(must, nameof(must)), Clauses(should, nameof(should)), Clauses(mustNot, nameof(mustNot)));

    /// <summary>
    /// Matches documents whose <paramref name="field"/> value lies between the bounds. A null bound
    /// is open; at least one must be set. The field must be indexed or fast (fast is quicker).
    /// </summary>
    /// <param name="field">An integer or f64 field.</param>
    /// <param name="lower">Lower bound, or null for none.</param>
    /// <param name="upper">Upper bound, or null for none.</param>
    /// <param name="includeLower">Whether <paramref name="lower"/> itself matches.</param>
    /// <param name="includeUpper">Whether <paramref name="upper"/> itself matches. Both default to inclusive, like <c>[a TO b]</c>.</param>
    public static RangeQuery Range(string field, long? lower, long? upper, bool includeLower = true, bool includeUpper = true) =>
        RangeOf(field, lower, upper, includeLower, includeUpper);

    /// <inheritdoc cref="Range(string, long?, long?, bool, bool)"/>
    public static RangeQuery Range(string field, ulong? lower, ulong? upper, bool includeLower = true, bool includeUpper = true) =>
        RangeOf(field, lower, upper, includeLower, includeUpper);

    /// <inheritdoc cref="Range(string, long?, long?, bool, bool)"/>
    public static RangeQuery Range(string field, double? lower, double? upper, bool includeLower = true, bool includeUpper = true) =>
        RangeOf(field, lower, upper, includeLower, includeUpper);

    /// <inheritdoc cref="Range(string, long?, long?, bool, bool)"/>
    /// <remarks>Dates are compared at the index's date precision (seconds by default).</remarks>
    public static RangeQuery Range(string field, DateTimeOffset? lower, DateTimeOffset? upper, bool includeLower = true, bool includeUpper = true) =>
        RangeOf(field, lower, upper, includeLower, includeUpper);

    /// <inheritdoc cref="Range(string, long?, long?, bool, bool)"/>
    /// <remarks>A <see cref="DateTimeKind.Unspecified"/> value is treated as UTC.</remarks>
    public static RangeQuery Range(string field, DateTime? lower, DateTime? upper, bool includeLower = true, bool includeUpper = true) =>
        RangeOf(field, lower is { } l ? ToOffset(l) : null, upper is { } u ? ToOffset(u) : null, includeLower, includeUpper);

    /// <inheritdoc cref="Range(string, long?, long?, bool, bool)"/>
    /// <remarks>For an IP address field. IPv4 bounds compare as IPv4-mapped IPv6, so an IPv4 range doesn't include IPv6 addresses.</remarks>
    public static RangeQuery Range(string field, IPAddress? lower, IPAddress? upper, bool includeLower = true, bool includeUpper = true) =>
        RangeOf(field, lower, upper, includeLower, includeUpper);

    /// <summary>
    /// Matches terms of a text <paramref name="field"/> that sort between the bounds (ordinal,
    /// byte-wise). Mostly useful on string fields. A null bound is open.
    /// </summary>
    /// <inheritdoc cref="Range(string, long?, long?, bool, bool)" path="/param[@name='includeLower']|/param[@name='includeUpper']"/>
    public static RangeQuery Range(string field, string? lower, string? upper, bool includeLower = true, bool includeUpper = true) =>
        RangeOf(field, lower, upper, includeLower, includeUpper);

    /// <summary>
    /// A query in Tantivy's query language, for example <c>body:mice AND year:[1930 TO 1940]</c>.
    /// Combine it with built queries like any other query.
    /// </summary>
    /// <param name="text">The query text; see <see cref="Searcher.Search(string, int, int, IEnumerable{string}?)"/>.</param>
    /// <param name="defaultFields">Fields searched by terms without a <c>field:</c> prefix. Null means every indexed text field.</param>
    /// <remarks>Syntax errors throw <see cref="QueryParseException"/> when the query runs.</remarks>
    public static ParsedQuery Parse(string text, IEnumerable<string>? defaultFields = null) =>
        new(NotNull(text), defaultFields is null ? QueryParserOptions.Default : new QueryParserOptions { DefaultFields = [.. defaultFields] });

    /// <summary>
    /// A query in Tantivy's query language, parsed with <paramref name="options"/>: default fields
    /// and operator, per-field boosts and fuzziness, and lenient parsing for user input.
    /// </summary>
    /// <param name="text">The query text; see <see cref="Searcher.Search(string, int, int, IEnumerable{string}?)"/>.</param>
    /// <param name="options">The parser settings.</param>
    /// <remarks>Unless <see cref="QueryParserOptions.Lenient"/>, syntax errors throw <see cref="QueryParseException"/> when the query runs.</remarks>
    public static ParsedQuery Parse(string text, QueryParserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ParsedQuery(NotNull(text), options);
    }

    /// <summary>
    /// Matches documents with a term of the text <paramref name="field"/> that matches
    /// <paramref name="pattern"/> as a whole (Rust <c>regex</c> syntax, anchored at both ends):
    /// <c>Regex("title", "gr.*s")</c> matches the term <c>grapes</c>. Terms are the indexed,
    /// analyzed tokens (lowercase with the default tokenizer).
    /// </summary>
    /// <remarks>An invalid pattern throws <see cref="TantivyException"/> with <see cref="TantivyErrorCode.InvalidArgument"/> when the query runs. All matches score the same.</remarks>
    public static RegexQuery Regex(string field, string pattern) => new(NotNull(field), NotNull(pattern));

    /// <summary>
    /// Matches documents that have at least one value in <paramref name="field"/>, which must be a
    /// fast field (<c>fast: true</c>). All matches score the same.
    /// </summary>
    public static ExistsQuery Exists(string field) => new(NotNull(field));

    /// <summary>
    /// Matches documents whose <paramref name="field"/> contains any of <paramref name="values"/>
    /// exactly. Faster than an OR of many <see cref="Term(string, string)"/> queries; all matches
    /// score the same. Values aren't analyzed.
    /// </summary>
    public static TermSetQuery TermSet(string field, IEnumerable<string> values) => TermSetOf(field, values);

    /// <inheritdoc cref="TermSet(string, IEnumerable{string})"/>
    public static TermSetQuery TermSet(string field, IEnumerable<ulong> values) => TermSetOf(field, values);

    /// <inheritdoc cref="TermSet(string, IEnumerable{string})"/>
    public static TermSetQuery TermSet(string field, IEnumerable<long> values) => TermSetOf(field, values);

    /// <inheritdoc cref="TermSet(string, IEnumerable{string})"/>
    public static TermSetQuery TermSet(string field, IEnumerable<IPAddress> values) => TermSetOf(field, values);

    /// <summary>
    /// Matches documents matching any of <paramref name="queries"/>, scored by the best-scoring one
    /// plus <paramref name="tieBreaker"/> times the scores of the others. Use it to search the same
    /// words in several fields without rewarding documents that repeat them everywhere.
    /// </summary>
    /// <param name="queries">The alternatives.</param>
    /// <param name="tieBreaker">Weight of the non-best scores, 0 (the default: only the best counts) to 1 (a plain sum).</param>
    public static DisjunctionMaxQuery DisjunctionMax(IEnumerable<Query> queries, float tieBreaker = 0)
    {
        if (!(tieBreaker >= 0 && tieBreaker <= 1)) throw new ArgumentOutOfRangeException(nameof(tieBreaker), tieBreaker, "Tie breaker must be between 0 and 1.");
        return new DisjunctionMaxQuery(Clauses(NotNull(queries), nameof(queries)), tieBreaker);
    }

    /// <summary>
    /// Matches documents similar to <paramref name="document"/>: its most distinctive terms (by
    /// TF-IDF, in text, numeric, date and facet fields) become a query. Typically
    /// <c>hit.Document</c>, which holds only stored fields. Exclude the document itself with
    /// <c>&amp; !Query.Term("id", ...)</c>.
    /// </summary>
    /// <param name="document">The example document's fields.</param>
    /// <param name="options">Term selection settings, or null for Tantivy's defaults.</param>
    /// <remarks>Fails when it runs if the document has no fields.</remarks>
    public static MoreLikeThisQuery MoreLikeThis(Document document, MoreLikeThisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new MoreLikeThisQuery(document.Root.GetRawText(), options ?? MoreLikeThisOptions.Default);
    }

    /// <summary>
    /// Matches documents similar to the given text per field, for example a draft that isn't indexed:
    /// <c>MoreLikeThis(new Dictionary&lt;string, string&gt; { ["body"] = draft })</c>.
    /// </summary>
    /// <param name="fieldTexts">Field name to text; the text is analyzed with the field's tokenizer.</param>
    /// <param name="options">Term selection settings, or null for Tantivy's defaults.</param>
    public static MoreLikeThisQuery MoreLikeThis(IReadOnlyDictionary<string, string> fieldTexts, MoreLikeThisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fieldTexts);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var (field, text) in fieldTexts)
            {
                if (text is null) throw new ArgumentException($"Text for field '{field}' can't be null.", nameof(fieldTexts));
                w.WriteString(field, text);
            }
            w.WriteEndObject();
        }
        return new MoreLikeThisQuery(Encoding.UTF8.GetString(buffer.WrittenSpan), options ?? MoreLikeThisOptions.Default);
    }

    /// <summary>
    /// Gives every document this query matches the same <paramref name="score"/>, for filters
    /// that shouldn't affect ranking, or to rank by clause rather than by term statistics.
    /// </summary>
    public ConstantScoreQuery ConstantScore(float score = 1)
    {
        if (!float.IsFinite(score)) throw new ArgumentOutOfRangeException(nameof(score), score, "Score must be finite.");
        return new ConstantScoreQuery(this, score);
    }

    /// <summary>Multiplies this query's scores by <paramref name="factor"/>, to weight it within a <see cref="Boolean"/>.</summary>
    public BoostQuery Boost(float factor)
    {
        if (!float.IsFinite(factor)) throw new ArgumentOutOfRangeException(nameof(factor), factor, "Boost factor must be finite.");
        return new BoostQuery(this, factor);
    }

    /// <summary>Both queries must match. Nested <c>&amp;</c> chains are flattened into one <see cref="BooleanQuery"/>.</summary>
    public static Query operator &(Query left, Query right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        // A boolean without should-clauses is "all of must, none of mustNot", so two of them merge.
        var (lm, ln) = Conjuncts(left);
        var (rm, rn) = Conjuncts(right);
        return new BooleanQuery([.. lm, .. rm], [], [.. ln, .. rn]);
    }

    private static (IReadOnlyList<Query> Must, IReadOnlyList<Query> MustNot) Conjuncts(Query q) =>
        q is BooleanQuery { Should.Count: 0 } b && (b.Must.Count + b.MustNot.Count) > 0 ? (b.Must, b.MustNot) : ([q], []);

    /// <summary>Either query must match. Nested <c>|</c> chains are flattened into one <see cref="BooleanQuery"/>.</summary>
    public static Query operator |(Query left, Query right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        IReadOnlyList<Query> l = left is BooleanQuery { Must.Count: 0, MustNot.Count: 0, Should.Count: > 0 } lb ? lb.Should : [left];
        IReadOnlyList<Query> r = right is BooleanQuery { Must.Count: 0, MustNot.Count: 0, Should.Count: > 0 } rb ? rb.Should : [right];
        return new BooleanQuery([], [.. l, .. r], []);
    }

    /// <summary>Every document the query does not match. <c>a &amp; !b</c> means "a but not b".</summary>
    public static Query operator !(Query query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new BooleanQuery([], [], [query]);
    }

    /// <summary>A diagnostic description of the query tree. The format may change between versions.</summary>
    public override string ToString() => Encoding.UTF8.GetString(ToJson());

    internal byte[] ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) WriteJson(w);
        return buffer.WrittenSpan.ToArray();
    }

    private static RangeQuery RangeOf(string field, object? lower, object? upper, bool includeLower, bool includeUpper)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (lower is null && upper is null)
            throw new ArgumentException("A range needs at least one bound. Use Query.All to match everything.");
        return new RangeQuery(field, lower, upper, includeLower, includeUpper);
    }

    private static TermSetQuery TermSetOf<T>(string field, IEnumerable<T> values) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(values);
        object[] array = [.. values.Select(v => (object)v)];
        if (Array.IndexOf(array, null) >= 0) throw new ArgumentException("Values can't be null.", nameof(values));
        return new TermSetQuery(field, array);
    }

    private static Query[] Clauses(IEnumerable<Query>? clauses, string paramName)
    {
        if (clauses is null) return [];
        var array = clauses.ToArray();
        if (Array.IndexOf(array, null) >= 0) throw new ArgumentException("Clauses can't be null.", paramName);
        return array;
    }

    private static DateTimeOffset ToOffset(DateTime value) => value.Kind switch
    {
        DateTimeKind.Unspecified => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
        _ => new DateTimeOffset(value.ToUniversalTime()),
    };

    private protected static T NotNull<T>(T value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? name = null) where T : class =>
        value ?? throw new ArgumentNullException(name);

    /// <summary>Writes a term value tagged with its kind: <c>{"u64": 5}</c>. See native/src/query_spec.rs.</summary>
    private protected static void WriteValue(Utf8JsonWriter w, object value)
    {
        w.WriteStartObject();
        switch (value)
        {
            case string s: w.WriteString("str", s); break;
            case ulong u: w.WriteNumber("u64", u); break;
            case long l: w.WriteNumber("i64", l); break;
            case double d:
                if (!double.IsFinite(d)) throw new ArgumentException($"Query values must be finite numbers, got {d}.");
                w.WriteNumber("f64", d);
                break;
            case bool b: w.WriteBoolean("bool", b); break;
            case DateTimeOffset dto: w.WriteNumber("date", (dto.UtcTicks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMicrosecond); break;
            case IPAddress ip: w.WriteString("ip", ip.ToString()); break;
            case byte[] bytes:
                w.WriteStartArray("bytes");
                foreach (byte x in bytes) w.WriteNumberValue(x);
                w.WriteEndArray();
                break;
            default: throw new InvalidOperationException($"Unsupported query value type {value.GetType()}.");
        }
        w.WriteEndObject();
    }
}

/// <summary>Matches every document. Use <see cref="Query.All"/>.</summary>
public sealed class AllQuery : Query
{
    internal AllQuery() { }

    internal override void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "all");
        w.WriteEndObject();
    }
}

/// <summary>An exact-term match. Created by <see cref="Query.Term(string, string)"/> and its overloads.</summary>
public sealed class TermQuery : Query
{
    internal TermQuery(string field, object value)
    {
        Field = NotNull(field);
        Value = value;
    }

    /// <summary>The field searched.</summary>
    public string Field { get; }

    /// <summary>The value: a <see cref="string"/>, <see cref="ulong"/>, <see cref="long"/>, <see cref="double"/>, <see cref="bool"/>, <see cref="DateTimeOffset"/>, <see cref="T:byte[]"/> or <see cref="IPAddress"/>.</summary>
    public object Value { get; }

    internal override void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "term");
        w.WriteString("field", Field);
        w.WritePropertyName("value");
        WriteValue(w, Value);
        w.WriteEndObject();
    }
}

/// <summary>A facet-category match. Created by <see cref="Query.Facet"/>.</summary>
public sealed class FacetQuery : Query
{
    internal FacetQuery(string field, string path)
    {
        Field = field;
        Path = path;
    }

    /// <summary>The facet field searched.</summary>
    public string Field { get; }

    /// <summary>The category path; documents in it or below it match.</summary>
    public string Path { get; }

    internal override void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "term");
        w.WriteString("field", Field);
        w.WriteStartObject("value");
        w.WriteString("facet", Path);
        w.WriteEndObject();
        w.WriteEndObject();
    }
}

/// <summary>An approximate-term match. Created by <see cref="Query.Fuzzy"/>.</summary>
public sealed class FuzzyQuery : Query
{
    internal FuzzyQuery(string field, string value, int distance, bool transpositions, bool prefix)
    {
        Field = field;
        Value = value;
        Distance = distance;
        Transpositions = transpositions;
        Prefix = prefix;
    }

    /// <summary>The field searched.</summary>
    public string Field { get; }

    /// <summary>The term to approximate.</summary>
    public string Value { get; }

    /// <summary>Maximum edit distance (0 to 2).</summary>
    public int Distance { get; }

    /// <summary>Whether swapping two adjacent characters counts as one edit.</summary>
    public bool Transpositions { get; }

    /// <summary>Whether terms only need to start with something within the distance.</summary>
    public bool Prefix { get; }

    internal override void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "fuzzy");
        w.WriteString("field", Field);
        w.WriteString("value", Value);
        w.WriteNumber("distance", Distance);
        w.WriteBoolean("transpositions", Transpositions);
        w.WriteBoolean("prefix", Prefix);
        w.WriteEndObject();
    }
}

/// <summary>A phrase match. Created by <see cref="Query.Phrase"/>.</summary>
public sealed class PhraseQuery : Query
{
    internal PhraseQuery(string field, string text, int slop)
    {
        Field = field;
        Text = text;
        Slop = slop;
    }

    /// <summary>The field searched.</summary>
    public string Field { get; }

    /// <summary>The phrase, before analysis.</summary>
    public string Text { get; }

    /// <summary>How far words may move and still match.</summary>
    public int Slop { get; }

    internal override void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "phrase");
        w.WriteString("field", Field);
        w.WriteString("text", Text);
        w.WriteNumber("slop", Slop);
        w.WriteEndObject();
    }
}

/// <summary>A combination of clauses. Created by <see cref="Query.Boolean"/> or the <c>&amp;</c>, <c>|</c> and <c>!</c> operators.</summary>
public sealed class BooleanQuery : Query
{
    internal BooleanQuery(Query[] must, Query[] should, Query[] mustNot)
    {
        Must = must;
        Should = should;
        MustNot = mustNot;
    }

    /// <summary>Clauses that must all match.</summary>
    public IReadOnlyList<Query> Must { get; }

    /// <summary>Optional clauses; see <see cref="Query.Boolean"/>.</summary>
    public IReadOnlyList<Query> Should { get; }

    /// <summary>Clauses that must not match.</summary>
    public IReadOnlyList<Query> MustNot { get; }

    internal override void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "boolean");
        WriteClauses(w, "must", Must);
        WriteClauses(w, "should", Should);
        WriteClauses(w, "must_not", MustNot);
        w.WriteEndObject();
    }

    private static void WriteClauses(Utf8JsonWriter w, string name, IReadOnlyList<Query> clauses)
    {
        if (clauses.Count == 0) return;
        w.WriteStartArray(name);
        foreach (var q in clauses) q.WriteJson(w);
        w.WriteEndArray();
    }
}

/// <summary>A value-range match. Created by <see cref="Query.Range(string, long?, long?, bool, bool)"/> and its overloads.</summary>
public sealed class RangeQuery : Query
{
    internal RangeQuery(string field, object? lower, object? upper, bool includeLower, bool includeUpper)
    {
        Field = field;
        Lower = lower;
        Upper = upper;
        IncludeLower = includeLower;
        IncludeUpper = includeUpper;
    }

    /// <summary>The field searched.</summary>
    public string Field { get; }

    /// <summary>The lower bound, or null if open.</summary>
    public object? Lower { get; }

    /// <summary>The upper bound, or null if open.</summary>
    public object? Upper { get; }

    /// <summary>Whether <see cref="Lower"/> itself matches.</summary>
    public bool IncludeLower { get; }

    /// <summary>Whether <see cref="Upper"/> itself matches.</summary>
    public bool IncludeUpper { get; }

    internal override void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "range");
        w.WriteString("field", Field);
        WriteBound(w, "lower", Lower, IncludeLower);
        WriteBound(w, "upper", Upper, IncludeUpper);
        w.WriteEndObject();
    }

    private static void WriteBound(Utf8JsonWriter w, string name, object? value, bool inclusive)
    {
        if (value is null) return;
        w.WriteStartObject(name);
        w.WritePropertyName("value");
        WriteValue(w, value);
        w.WriteBoolean("inclusive", inclusive);
        w.WriteEndObject();
    }
}

/// <summary>A query whose scores are scaled. Created by <see cref="Query.Boost"/>.</summary>
public sealed class BoostQuery : Query
{
    internal BoostQuery(Query query, float factor)
    {
        Query = query;
        Factor = factor;
    }

    /// <summary>The boosted query.</summary>
    public Query Query { get; }

    /// <summary>The score multiplier.</summary>
    public float Factor { get; }

    internal override void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "boost");
        w.WritePropertyName("query");
        Query.WriteJson(w);
        w.WriteNumber("factor", Factor);
        w.WriteEndObject();
    }
}

/// <summary>A regular-expression term match. Created by <see cref="Query.Regex"/>.</summary>
public sealed class RegexQuery : Query
{
    internal RegexQuery(string field, string pattern)
    {
        Field = field;
        Pattern = pattern;
    }

    /// <summary>The field searched.</summary>
    public string Field { get; }

    /// <summary>The pattern terms must match.</summary>
    public string Pattern { get; }

    internal override void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "regex");
        w.WriteString("field", Field);
        w.WriteString("pattern", Pattern);
        w.WriteEndObject();
    }
}

/// <summary>A has-a-value match. Created by <see cref="Query.Exists"/>.</summary>
public sealed class ExistsQuery : Query
{
    internal ExistsQuery(string field) => Field = field;

    /// <summary>The fast field checked.</summary>
    public string Field { get; }

    internal override void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "exists");
        w.WriteString("field", Field);
        w.WriteEndObject();
    }
}

/// <summary>An any-of-these-terms match. Created by <see cref="Query.TermSet(string, IEnumerable{string})"/> and its overloads.</summary>
public sealed class TermSetQuery : Query
{
    internal TermSetQuery(string field, object[] values)
    {
        Field = field;
        Values = values;
    }

    /// <summary>The field searched.</summary>
    public string Field { get; }

    /// <summary>The values; see <see cref="TermQuery.Value"/>.</summary>
    public IReadOnlyList<object> Values { get; }

    internal override void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "term_set");
        w.WriteString("field", Field);
        w.WriteStartArray("values");
        foreach (var v in Values) WriteValue(w, v);
        w.WriteEndArray();
        w.WriteEndObject();
    }
}

/// <summary>A best-of-alternatives match. Created by <see cref="Query.DisjunctionMax"/>.</summary>
public sealed class DisjunctionMaxQuery : Query
{
    internal DisjunctionMaxQuery(Query[] queries, float tieBreaker)
    {
        Queries = queries;
        TieBreaker = tieBreaker;
    }

    /// <summary>The alternatives.</summary>
    public IReadOnlyList<Query> Queries { get; }

    /// <summary>Weight of the non-best scores.</summary>
    public float TieBreaker { get; }

    internal override void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "disjunction_max");
        w.WriteStartArray("queries");
        foreach (var q in Queries) q.WriteJson(w);
        w.WriteEndArray();
        w.WriteNumber("tie_breaker", TieBreaker);
        w.WriteEndObject();
    }
}

/// <summary>A query whose matches all get one score. Created by <see cref="Query.ConstantScore"/>.</summary>
public sealed class ConstantScoreQuery : Query
{
    internal ConstantScoreQuery(Query query, float score)
    {
        Query = query;
        Score = score;
    }

    /// <summary>The wrapped query.</summary>
    public Query Query { get; }

    /// <summary>The score every match gets.</summary>
    public float Score { get; }

    internal override void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "const_score");
        w.WritePropertyName("query");
        Query.WriteJson(w);
        w.WriteNumber("score", Score);
        w.WriteEndObject();
    }
}

/// <summary>A similar-documents match. Created by <see cref="Query.MoreLikeThis(Document, MoreLikeThisOptions?)"/>.</summary>
public sealed class MoreLikeThisQuery : Query
{
    private readonly string _documentJson;

    internal MoreLikeThisQuery(string documentJson, MoreLikeThisOptions options)
    {
        _documentJson = documentJson;
        Options = options;
    }

    /// <summary>The term selection settings.</summary>
    public MoreLikeThisOptions Options { get; }

    internal override void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "more_like_this");
        w.WritePropertyName("document");
        w.WriteRawValue(_documentJson, skipInputValidation: true);
        w.WriteStartObject("options");
        Options.WriteJson(w);
        w.WriteEndObject();
        w.WriteEndObject();
    }
}

/// <summary>A query in Tantivy's query language. Created by <see cref="Query.Parse(string, QueryParserOptions)"/>.</summary>
public sealed class ParsedQuery : Query
{
    internal ParsedQuery(string text, QueryParserOptions options)
    {
        Text = text;
        Options = options;
    }

    /// <summary>The query text.</summary>
    public string Text { get; }

    /// <summary>The parser settings.</summary>
    public QueryParserOptions Options { get; }

    /// <summary>Fields searched by unprefixed terms, or null for every indexed text field.</summary>
    public IReadOnlyList<string>? DefaultFields => Options.DefaultFields;

    internal override void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "parse");
        w.WriteString("text", Text);
        Options.WriteJson(w);
        w.WriteEndObject();
    }
}
