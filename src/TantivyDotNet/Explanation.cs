using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TantivyDotNet;

/// <summary>
/// How a document's score was computed: a tree whose nodes each contribute a value. Returned by
/// <see cref="Searcher.Explain"/>, for debugging relevance.
/// </summary>
/// <remarks>The descriptions come from Tantivy and may change between versions; don't parse them.</remarks>
public sealed class Explanation
{
    private Explanation(float value, string description, IReadOnlyList<Explanation> details, IReadOnlyList<string> context)
    {
        Value = value;
        Description = description;
        Details = details;
        Context = context;
    }

    /// <summary>This node's value; for the root, the document's score.</summary>
    public float Value { get; }

    /// <summary>What the value is, for example <c>TermQuery, product of...</c> or <c>idf, computed as ...</c>.</summary>
    public string Description { get; }

    /// <summary>The values this one was computed from.</summary>
    public IReadOnlyList<Explanation> Details { get; }

    /// <summary>Extra notes about this node.</summary>
    public IReadOnlyList<string> Context { get; }

    /// <summary>The tree as indented lines, one node per line, like <c>0.693 = idf, computed as ...</c>.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        Append(sb, 0);
        return sb.ToString().TrimEnd();
    }

    private void Append(StringBuilder sb, int depth)
    {
        sb.Append(' ', depth * 2).Append(Value.ToString("G6", CultureInfo.InvariantCulture)).Append(" = ").Append(Description);
        if (Context.Count > 0) sb.Append(" (").Append(string.Join("; ", Context)).Append(')');
        sb.AppendLine();
        foreach (var d in Details) d.Append(sb, depth + 1);
    }

    // Tantivy's explanation JSON: {"value": f, "description": "...", "details"?: [...], "context"?: [...]}.
    internal static Explanation Parse(JsonElement json) => new(
        json.GetProperty("value").GetSingle(),
        json.GetProperty("description").GetString() ?? "",
        json.TryGetProperty("details", out var d) ? [.. d.EnumerateArray().Select(Parse)] : [],
        json.TryGetProperty("context", out var c) ? [.. c.EnumerateArray().Select(x => x.GetString() ?? "")] : []);
}
