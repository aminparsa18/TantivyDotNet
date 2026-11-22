using System.Globalization;
using System.Text.Json;

namespace TantivyDotNet;

/// <summary>
/// The stored fields of a search hit. Only fields marked <c>stored</c> in the schema are present.
/// Every field can hold several values; the <c>Get*</c> methods return the first one.
/// </summary>
public sealed class Document
{
    private readonly JsonElement _root;

    internal Document(JsonElement root) => _root = root;

    /// <summary>Names of the fields present in this document.</summary>
    public IEnumerable<string> FieldNames => _root.EnumerateObject().Select(p => p.Name);

    /// <summary>Whether the document has at least one value for <paramref name="field"/>.</summary>
    public bool Contains(string field) => GetValues(field).Count > 0;

    /// <summary>All values of <paramref name="field"/> as raw JSON elements; empty if absent.</summary>
    public IReadOnlyList<JsonElement> GetValues(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (!_root.TryGetProperty(field, out var values)) return [];
        return values.ValueKind == JsonValueKind.Array ? [.. values.EnumerateArray()] : [values];
    }

    /// <summary>First value of a text field, or null if absent.</summary>
    /// <exception cref="InvalidOperationException">The value isn't text.</exception>
    public string? GetString(string field) => First(field, JsonValueKind.String, "text") is { } v ? v.GetString() : null;

    /// <summary>All values of a text field.</summary>
    public IReadOnlyList<string> GetStrings(string field) =>
        [.. GetValues(field).Select(v => Expect(field, v, JsonValueKind.String, "text").GetString()!)];

    /// <summary>First value of a u64 field, or null if absent.</summary>
    public ulong? GetU64(string field) => First(field, JsonValueKind.Number, "number")?.GetUInt64();

    /// <summary>First value of an i64 field, or null if absent.</summary>
    public long? GetI64(string field) => First(field, JsonValueKind.Number, "number")?.GetInt64();

    /// <summary>First value of an f64 field, or null if absent.</summary>
    public double? GetF64(string field) => First(field, JsonValueKind.Number, "number")?.GetDouble();

    /// <summary>First value of a bool field, or null if absent.</summary>
    public bool? GetBool(string field)
    {
        var values = GetValues(field);
        if (values.Count == 0) return null;
        return values[0].ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw WrongType(field, values[0], "bool"),
        };
    }

    /// <summary>First value of a date field (UTC), or null if absent.</summary>
    public DateTimeOffset? GetDate(string field) =>
        First(field, JsonValueKind.String, "date") is { } v
            ? DateTimeOffset.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
            : null;

    /// <summary>First value of a bytes field, or null if absent.</summary>
    public byte[]? GetBytes(string field) => First(field, JsonValueKind.String, "bytes")?.GetBytesFromBase64();

    /// <summary>The document as JSON: <c>{"field": [value, ...], ...}</c>.</summary>
    public string ToJson() => _root.GetRawText();

    /// <inheritdoc/>
    public override string ToString() => ToJson();

    private JsonElement? First(string field, JsonValueKind kind, string typeName)
    {
        var values = GetValues(field);
        return values.Count == 0 ? null : Expect(field, values[0], kind, typeName);
    }

    private static JsonElement Expect(string field, JsonElement value, JsonValueKind kind, string typeName) =>
        value.ValueKind == kind ? value : throw WrongType(field, value, typeName);

    private static InvalidOperationException WrongType(string field, JsonElement value, string typeName) =>
        new($"Field '{field}' holds a {value.ValueKind} value, not {typeName}.");
}
