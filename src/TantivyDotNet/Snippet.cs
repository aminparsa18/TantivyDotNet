using System.Text;
using System.Text.Json;

namespace TantivyDotNet;

/// <summary>
/// Asks <see cref="Searcher.Search(Query, SearchOptions)"/> to compute a
/// <see cref="Snippet"/> for each hit, for each of <see cref="Fields"/>.
/// </summary>
/// <example>
/// <code>
/// var results = searcher.Search(query, new SearchOptions { Snippets = new SnippetOptions("body") { MaxLength = 200 } });
/// string html = results[0].Snippets["body"].ToHtml();
/// </code>
/// </example>
public sealed class SnippetOptions
{
    private readonly string[] _fields;

    /// <summary>Requests snippets for <paramref name="fields"/>.</summary>
    /// <param name="fields">Stored text fields. Snippets for other fields throw <see cref="TantivyException"/>.</param>
    public SnippetOptions(params IEnumerable<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        _fields = [.. fields];
        if (_fields.Length == 0) throw new ArgumentException("Request at least one field.", nameof(fields));
        if (Array.IndexOf(_fields, null) >= 0) throw new ArgumentException("Field names can't be null.", nameof(fields));
    }

    /// <summary>The fields to make snippets of.</summary>
    public IReadOnlyList<string> Fields => _fields;

    /// <summary>
    /// The longest fragment to return, measured in UTF-8 bytes of the field text (so text that
    /// isn't ASCII gets fewer characters). Default 150.
    /// </summary>
    public int MaxLength
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = 150;

    internal void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteStartArray("fields");
        foreach (var f in _fields) w.WriteStringValue(f);
        w.WriteEndArray();
        w.WriteNumber("max_length", MaxLength);
        w.WriteEndObject();
    }
}

/// <summary>
/// An excerpt of a field around the words that matched the query, with the matched words marked.
/// </summary>
/// <remarks>
/// Highlighting covers the terms of term, phrase and parsed queries. Fuzzy and range queries don't
/// report terms, so they produce no highlights. If the field contains none of the query's terms,
/// the snippet is empty (<see cref="IsEmpty"/>).
/// </remarks>
public sealed class Snippet
{
    internal static readonly Snippet Empty = new(string.Empty, []);

    internal Snippet(string fragment, Range[] highlights)
    {
        Fragment = fragment;
        Highlights = highlights;
    }

    /// <summary>The excerpt, as plain text. Empty when the field contains no query term.</summary>
    public string Fragment { get; }

    /// <summary>
    /// The matched words, as ranges into <see cref="Fragment"/> (so <c>Fragment[range]</c> is a
    /// matched word). They are sorted and don't overlap.
    /// </summary>
    public IReadOnlyList<Range> Highlights { get; }

    /// <summary>True if nothing in the field matched the query.</summary>
    public bool IsEmpty => Highlights.Count == 0;

    /// <summary>
    /// The fragment as HTML: the text is HTML-encoded and each match is wrapped in
    /// <paramref name="open"/> and <paramref name="close"/>, which are inserted as-is.
    /// </summary>
    public string ToHtml(string open = "<b>", string close = "</b>")
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(close);
        var sb = new StringBuilder(Fragment.Length + Highlights.Count * (open.Length + close.Length) + 16);
        int pos = 0;
        foreach (var r in Highlights)
        {
            var (start, length) = r.GetOffsetAndLength(Fragment.Length);
            HtmlEncode(sb, Fragment.AsSpan(pos, start - pos));
            sb.Append(open);
            HtmlEncode(sb, Fragment.AsSpan(start, length));
            sb.Append(close);
            pos = start + length;
        }
        HtmlEncode(sb, Fragment.AsSpan(pos));
        return sb.ToString();
    }

    /// <summary>Returns <see cref="Fragment"/>.</summary>
    public override string ToString() => Fragment;

    // Minimal encoding (& < > " '), leaving other characters as they are, like Tantivy's to_html.
    private static void HtmlEncode(StringBuilder sb, ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#x27;"); break;
                default: sb.Append(c); break;
            }
        }
    }

    // Shape: {"fragment": "...", "highlights": [[start, end], ...]} in UTF-16 offsets (native/src/snippets.rs).
    internal static Snippet Parse(JsonElement json)
    {
        string fragment = json.GetProperty("fragment").GetString()!;
        var arr = json.GetProperty("highlights");
        var ranges = new Range[arr.GetArrayLength()];
        int i = 0;
        foreach (var r in arr.EnumerateArray())
            ranges[i++] = new Range(r[0].GetInt32(), r[1].GetInt32());
        return ranges.Length == 0 && fragment.Length == 0 ? Empty : new Snippet(fragment, ranges);
    }
}
