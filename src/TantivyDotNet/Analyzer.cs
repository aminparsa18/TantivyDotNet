using System.Buffers;
using System.Text;
using System.Text.Json;

namespace TantivyDotNet;

/// <summary>A language for <see cref="Analyzer.Stem"/> and <see cref="Analyzer.StopWords(AnalyzerLanguage)"/>.</summary>
public enum AnalyzerLanguage
{
    /// <summary>Arabic (stemming only).</summary>
    Arabic,
    /// <summary>Danish.</summary>
    Danish,
    /// <summary>Dutch.</summary>
    Dutch,
    /// <summary>English.</summary>
    English,
    /// <summary>Finnish.</summary>
    Finnish,
    /// <summary>French.</summary>
    French,
    /// <summary>German.</summary>
    German,
    /// <summary>Greek (stemming only).</summary>
    Greek,
    /// <summary>Hungarian.</summary>
    Hungarian,
    /// <summary>Italian.</summary>
    Italian,
    /// <summary>Norwegian.</summary>
    Norwegian,
    /// <summary>Portuguese.</summary>
    Portuguese,
    /// <summary>Romanian (stemming only).</summary>
    Romanian,
    /// <summary>Russian.</summary>
    Russian,
    /// <summary>Spanish.</summary>
    Spanish,
    /// <summary>Swedish.</summary>
    Swedish,
    /// <summary>Tamil (stemming only).</summary>
    Tamil,
    /// <summary>Turkish (stemming only).</summary>
    Turkish,
}

/// <summary>
/// A custom text analyzer: a tokenizer that splits text into tokens, followed by filters that
/// transform or drop tokens. Register it with <see cref="SchemaBuilder.AddAnalyzer"/> and use its
/// name as a text field's tokenizer.
/// </summary>
/// <remarks>
/// Analyzers are immutable: each filter method returns a new analyzer. Filters run in the order
/// they are added, so add <see cref="LowerCase"/> before <see cref="StopWords(AnalyzerLanguage)"/> and
/// <see cref="Stem"/>, which expect lowercase input. An index created in a directory saves its
/// analyzers there, and <see cref="SearchIndex.Open"/> restores them.
/// </remarks>
/// <example>
/// <code>
/// var schema = new SchemaBuilder()
///     .AddAnalyzer("en_text", Analyzer.Simple().LowerCase().AsciiFolding().StopWords(AnalyzerLanguage.English).Stem(AnalyzerLanguage.English))
///     .AddAnalyzer("prefix", Analyzer.NGram(1, 10, prefixOnly: true).LowerCase())
///     .AddTextField("body", tokenizer: "en_text")
///     .AddTextField("title_prefix", tokenizer: "prefix")
///     .Build();
/// </code>
/// </example>
public sealed class Analyzer : IEquatable<Analyzer>
{
    // Each step is its canonical JSON (see native/src/analyzers.rs); equality compares them.
    private readonly string _tokenizer;
    private readonly string[] _filters;
    private readonly string _description;

    private Analyzer(string tokenizer, string[] filters, string description)
    {
        _tokenizer = tokenizer;
        _filters = filters;
        _description = description;
    }

    /// <summary>Splits on every character that isn't a letter or digit (like the <c>default</c> tokenizer, without its filters).</summary>
    public static Analyzer Simple() => Tokenizer("simple", "simple", _ => { });

    /// <summary>Splits on whitespace only, so punctuation stays part of tokens.</summary>
    public static Analyzer Whitespace() => Tokenizer("whitespace", "whitespace", _ => { });

    /// <summary>Keeps the whole value as one token. Combine with <see cref="LowerCase"/> for case-insensitive exact matching.</summary>
    public static Analyzer Raw() => Tokenizer("raw", "raw", _ => { });

    /// <summary>
    /// Emits every substring of <paramref name="minGram"/> to <paramref name="maxGram"/> characters
    /// (n-grams), for substring and search-as-you-type matching. Whitespace isn't special: n-grams
    /// span it.
    /// </summary>
    /// <param name="minGram">Shortest n-gram, at least 1.</param>
    /// <param name="maxGram">Longest n-gram, at least <paramref name="minGram"/>.</param>
    /// <param name="prefixOnly">Only emit n-grams that start at the beginning of the text (edge n-grams).</param>
    public static Analyzer NGram(int minGram, int maxGram, bool prefixOnly = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minGram);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxGram, minGram);
        return Tokenizer("ngram", $"ngram({minGram}, {maxGram}{(prefixOnly ? ", prefix only" : "")})", w =>
        {
            w.WriteNumber("min_gram", minGram);
            w.WriteNumber("max_gram", maxGram);
            w.WriteBoolean("prefix_only", prefixOnly);
        });
    }

    /// <summary>
    /// Emits every match of <paramref name="pattern"/> as a token (Rust <c>regex</c> syntax, which is
    /// close to .NET's but has no lookaround or backreferences). For example <c>\w+</c> or <c>[A-Za-z0-9_-]+</c>.
    /// </summary>
    /// <remarks>The pattern is validated when the schema is used to create an index.</remarks>
    public static Analyzer Regex(string pattern)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        return Tokenizer("regex", $"regex({pattern})", w => w.WriteString("pattern", pattern));
    }

    /// <summary>Lowercases tokens.</summary>
    public Analyzer LowerCase() => Filter("lowercase", "lowercase", _ => { });

    /// <summary>Converts accented and other non-ASCII letters to their closest ASCII form (<c>café</c> → <c>cafe</c>).</summary>
    public Analyzer AsciiFolding() => Filter("ascii_folding", "ascii_folding", _ => { });

    /// <summary>
    /// Drops the common words of <paramref name="language"/> (Tantivy's built-in lists). Expects
    /// lowercase tokens. Not every language has a list (see <see cref="AnalyzerLanguage"/>); a missing one
    /// fails when the schema is used to create an index.
    /// </summary>
    public Analyzer StopWords(AnalyzerLanguage language) =>
        Filter("stop_words", $"stop_words({LanguageName(language)})", w => w.WriteString("language", LanguageName(language)));

    /// <summary>Drops tokens equal to any of <paramref name="words"/> (exact, case-sensitive match).</summary>
    public Analyzer StopWords(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        string[] list = [.. words];
        if (Array.IndexOf(list, null) >= 0) throw new ArgumentException("Stop words can't be null.", nameof(words));
        return Filter("stop_words", $"stop_words([{list.Length} words])", w =>
        {
            w.WriteStartArray("words");
            foreach (var word in list) w.WriteStringValue(word);
            w.WriteEndArray();
        });
    }

    /// <summary>Reduces tokens to their stem with the Snowball stemmer for <paramref name="language"/> (<c>running</c> → <c>run</c>). Expects lowercase tokens.</summary>
    public Analyzer Stem(AnalyzerLanguage language) =>
        Filter("stemmer", $"stem({LanguageName(language)})", w => w.WriteString("language", LanguageName(language)));

    /// <summary>Drops tokens longer than <paramref name="maxBytes"/> UTF-8 bytes (the <c>default</c> tokenizer uses 40).</summary>
    public Analyzer RemoveLong(int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        return Filter("remove_long", $"remove_long({maxBytes})", w => w.WriteNumber("limit", maxBytes));
    }

    /// <summary>A readable description, for example <c>simple | lowercase | stem(english)</c>.</summary>
    public override string ToString() => _description;

    /// <inheritdoc/>
    public bool Equals(Analyzer? other) =>
        other is not null && _tokenizer == other._tokenizer && _filters.AsSpan().SequenceEqual(other._filters);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as Analyzer);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_tokenizer);
        foreach (var f in _filters) hash.Add(f);
        return hash.ToHashCode();
    }

    // {"tokenizer": {"type": ...}, "filters": [{"type": ...}, ...]} (see native/src/analyzers.rs).
    internal void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WritePropertyName("tokenizer");
        w.WriteRawValue(_tokenizer, skipInputValidation: true);
        w.WriteStartArray("filters");
        foreach (var f in _filters) w.WriteRawValue(f, skipInputValidation: true);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    internal static Analyzer FromJson(JsonElement json)
    {
        var t = json.GetProperty("tokenizer");
        var analyzer = t.GetProperty("type").GetString() switch
        {
            "simple" => Simple(),
            "whitespace" => Whitespace(),
            "raw" => Raw(),
            "ngram" => NGram(t.GetProperty("min_gram").GetInt32(), t.GetProperty("max_gram").GetInt32(),
                t.TryGetProperty("prefix_only", out var p) && p.GetBoolean()),
            "regex" => Regex(t.GetProperty("pattern").GetString()!),
            var other => throw new InvalidOperationException($"Unknown tokenizer type '{other}'."),
        };
        if (json.TryGetProperty("filters", out var filters))
        {
            foreach (var f in filters.EnumerateArray())
            {
                analyzer = f.GetProperty("type").GetString() switch
                {
                    "lowercase" => analyzer.LowerCase(),
                    "ascii_folding" => analyzer.AsciiFolding(),
                    "stop_words" when f.TryGetProperty("language", out var l) => analyzer.StopWords(ParseLanguage(l.GetString())),
                    "stop_words" => analyzer.StopWords(f.GetProperty("words").EnumerateArray().Select(x => x.GetString()!)),
                    "stemmer" => analyzer.Stem(ParseLanguage(f.GetProperty("language").GetString())),
                    "remove_long" => analyzer.RemoveLong(f.GetProperty("limit").GetInt32()),
                    var other => throw new InvalidOperationException($"Unknown token filter type '{other}'."),
                };
            }
        }
        return analyzer;
    }

    private static Analyzer Tokenizer(string type, string description, Action<Utf8JsonWriter> write) =>
        new(Step(type, write), [], description);

    private Analyzer Filter(string type, string description, Action<Utf8JsonWriter> write) =>
        new(_tokenizer, [.. _filters, Step(type, write)], $"{_description} | {description}");

    private static string Step(string type, Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", type);
            write(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string LanguageName(AnalyzerLanguage language) =>
        Enum.IsDefined(language)
            ? language.ToString().ToLowerInvariant()
            : throw new ArgumentOutOfRangeException(nameof(language), language, "Unknown language.");

    private static AnalyzerLanguage ParseLanguage(string? name) =>
        Enum.TryParse<AnalyzerLanguage>(name, ignoreCase: true, out var l) ? l : throw new InvalidOperationException($"Unknown language '{name}'.");
}
