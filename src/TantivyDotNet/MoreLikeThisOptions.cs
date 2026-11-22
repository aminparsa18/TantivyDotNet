using System.Text.Json;

namespace TantivyDotNet;

/// <summary>
/// How <see cref="Query.MoreLikeThis(Document, MoreLikeThisOptions?)"/> picks terms from the
/// example document. Null properties keep Tantivy's defaults.
/// </summary>
public sealed class MoreLikeThisOptions
{
    internal static readonly MoreLikeThisOptions Default = new();

    /// <summary>Ignore terms found in fewer documents than this (Tantivy default 5). Lower it for small indexes.</summary>
    public long? MinDocFrequency { get; init => field = NonNegative(value); }

    /// <summary>Ignore terms found in more documents than this (default: no limit).</summary>
    public long? MaxDocFrequency { get; init => field = NonNegative(value); }

    /// <summary>Ignore terms occurring fewer times than this in the example document (default 2).</summary>
    public int? MinTermFrequency { get; init => field = NonNegative(value); }

    /// <summary>Maximum number of terms in the generated query (default 25).</summary>
    public int? MaxQueryTerms { get; init => field = NonNegative(value); }

    /// <summary>Ignore words shorter than this many characters (default: no limit).</summary>
    public int? MinWordLength { get; init => field = NonNegative(value); }

    /// <summary>Ignore words longer than this many characters (default: no limit).</summary>
    public int? MaxWordLength { get; init => field = NonNegative(value); }

    /// <summary>Boosts each term by its relative TF-IDF weight times this factor (default 1).</summary>
    public float? BoostFactor
    {
        get;
        init
        {
            if (value is { } v && !float.IsFinite(v)) throw new ArgumentOutOfRangeException(nameof(BoostFactor), v, "Must be finite.");
            field = value;
        }
    }

    /// <summary>Words never used as query terms (default none).</summary>
    public IReadOnlyList<string>? StopWords
    {
        get;
        init
        {
            if (value is not null && value.Contains(null!)) throw new ArgumentException("Stop words can't be null.", nameof(StopWords));
            field = value is null ? null : [.. value];
        }
    }

    private static T? NonNegative<T>(T? value) where T : struct, System.Numerics.INumber<T>
    {
        if (value is { } v && T.IsNegative(v)) throw new ArgumentOutOfRangeException(nameof(value), v, "Must not be negative.");
        return value;
    }

    internal void WriteJson(Utf8JsonWriter w)
    {
        if (MinDocFrequency is { } a) w.WriteNumber("min_doc_frequency", a);
        if (MaxDocFrequency is { } b) w.WriteNumber("max_doc_frequency", b);
        if (MinTermFrequency is { } c) w.WriteNumber("min_term_frequency", c);
        if (MaxQueryTerms is { } d) w.WriteNumber("max_query_terms", d);
        if (MinWordLength is { } e) w.WriteNumber("min_word_length", e);
        if (MaxWordLength is { } f) w.WriteNumber("max_word_length", f);
        if (BoostFactor is { } g) w.WriteNumber("boost_factor", g);
        if (StopWords is not null)
        {
            w.WriteStartArray("stop_words");
            foreach (var s in StopWords) w.WriteStringValue(s);
            w.WriteEndArray();
        }
    }
}
