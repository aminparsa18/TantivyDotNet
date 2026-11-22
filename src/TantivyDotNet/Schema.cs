using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace TantivyDotNet;

/// <summary>The value type of a schema field.</summary>
public enum FieldType
{
    /// <summary>UTF-8 text. Tokenized by default; see <see cref="SchemaBuilder.AddStringField"/> for exact-match strings.</summary>
    Text,
    /// <summary>Unsigned 64-bit integer.</summary>
    U64,
    /// <summary>Signed 64-bit integer.</summary>
    I64,
    /// <summary>64-bit floating point.</summary>
    F64,
    /// <summary>Boolean.</summary>
    Bool,
    /// <summary>Date and time, stored as UTC.</summary>
    Date,
    /// <summary>Raw bytes (base64 in JSON documents).</summary>
    Bytes,
    /// <summary>Hierarchical facet. Not yet creatable from .NET.</summary>
    Facet,
    /// <summary>Dynamic JSON object. Not yet creatable from .NET.</summary>
    Json,
    /// <summary>IP address. Not yet creatable from .NET.</summary>
    IpAddr,
    /// <summary>A field type this version of TantivyDotNet doesn't know.</summary>
    Other,
}

/// <summary>One field of a <see cref="Schema"/>.</summary>
/// <param name="Name">The field name, as used in documents and queries.</param>
/// <param name="Type">The value type.</param>
/// <param name="Stored">Whether the original value is stored and returned in search hits.</param>
/// <param name="Indexed">Whether the field is searchable.</param>
/// <param name="Fast">Whether the field has a columnar "fast field" (used for sorting and aggregations).</param>
/// <param name="Tokenizer">For indexed text fields, the tokenizer name (for example <c>default</c> or <c>raw</c>); otherwise null.</param>
public sealed record FieldDefinition(string Name, FieldType Type, bool Stored, bool Indexed, bool Fast, string? Tokenizer);

/// <summary>
/// The immutable set of fields in an index. Build one with <see cref="SchemaBuilder"/>, or read
/// an existing index's schema from <see cref="SearchIndex.Schema"/>.
/// </summary>
public sealed class Schema
{
    private readonly Dictionary<string, FieldDefinition> _byName;

    internal Schema(IReadOnlyList<FieldDefinition> fields)
    {
        Fields = fields;
        _byName = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
    }

    /// <summary>The fields, in declaration order.</summary>
    public IReadOnlyList<FieldDefinition> Fields { get; }

    /// <summary>Returns the field with this name (case-sensitive).</summary>
    /// <exception cref="KeyNotFoundException">No such field.</exception>
    public FieldDefinition this[string name] =>
        _byName.TryGetValue(name, out var field) ? field : throw new KeyNotFoundException($"Schema has no field named '{name}'.");

    /// <summary>Looks up a field by name (case-sensitive).</summary>
    public bool TryGetField(string name, [MaybeNullWhen(false)] out FieldDefinition field) => _byName.TryGetValue(name, out field);

    // The JSON shape is defined in native/src/schema_spec.rs.

    internal byte[] ToJsonUtf8()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var f in Fields)
            {
                w.WriteStartObject();
                w.WriteString("name", f.Name);
                w.WriteString("type", TypeToJson(f.Type));
                w.WriteBoolean("stored", f.Stored);
                w.WriteBoolean("indexed", f.Indexed);
                w.WriteBoolean("fast", f.Fast);
                if (f.Tokenizer is not null) w.WriteString("tokenizer", f.Tokenizer);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return buffer.ToArray();
    }

    internal static Schema FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var fields = new List<FieldDefinition>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            fields.Add(new FieldDefinition(
                e.GetProperty("name").GetString()!,
                TypeFromJson(e.GetProperty("type").GetString()),
                e.GetProperty("stored").GetBoolean(),
                e.GetProperty("indexed").GetBoolean(),
                e.GetProperty("fast").GetBoolean(),
                e.TryGetProperty("tokenizer", out var t) ? t.GetString() : null));
        }
        return new Schema(fields);
    }

    private static string TypeToJson(FieldType type) => type switch
    {
        FieldType.Text => "text",
        FieldType.U64 => "u64",
        FieldType.I64 => "i64",
        FieldType.F64 => "f64",
        FieldType.Bool => "bool",
        FieldType.Date => "date",
        FieldType.Bytes => "bytes",
        _ => throw new NotSupportedException($"Creating {type} fields isn't supported yet."),
    };

    private static FieldType TypeFromJson(string? type) => type switch
    {
        "text" => FieldType.Text,
        "u64" => FieldType.U64,
        "i64" => FieldType.I64,
        "f64" => FieldType.F64,
        "bool" => FieldType.Bool,
        "date" => FieldType.Date,
        "bytes" => FieldType.Bytes,
        "facet" => FieldType.Facet,
        "json" => FieldType.Json,
        "ip" => FieldType.IpAddr,
        _ => FieldType.Other,
    };
}

/// <summary>
/// Fluent builder for a <see cref="Schema"/>.
/// </summary>
/// <example>
/// <code>
/// var schema = new SchemaBuilder()
///     .AddTextField("title", stored: true)
///     .AddStringField("id", stored: true)
///     .AddU64Field("year", fast: true)
///     .Build();
/// </code>
/// </example>
public sealed class SchemaBuilder
{
    private readonly List<FieldDefinition> _fields = [];

    /// <summary>
    /// Adds a full-text field. Its values are split into terms by <paramref name="tokenizer"/>
    /// (<c>default</c> splits on whitespace and punctuation, lowercases, and drops very long tokens;
    /// <c>en_stem</c> also applies English stemming; <c>raw</c> keeps the whole value as one term).
    /// </summary>
    /// <param name="name">Field name.</param>
    /// <param name="stored">Return the original value in search hits.</param>
    /// <param name="fast">Also store as a fast field (untokenized), for sorting and aggregations.</param>
    /// <param name="tokenizer">Tokenizer name registered in Tantivy.</param>
    public SchemaBuilder AddTextField(string name, bool stored = false, bool fast = false, string tokenizer = "default")
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenizer);
        return Add(new FieldDefinition(name, FieldType.Text, stored, Indexed: true, fast, tokenizer));
    }

    /// <summary>
    /// Adds an untokenized text field: the whole value is a single term. Use it for identifiers,
    /// tags and enum-like values that you match exactly or delete by (<see cref="IndexWriter.DeleteTerm(string, string)"/>).
    /// </summary>
    public SchemaBuilder AddStringField(string name, bool stored = false, bool fast = false) =>
        AddTextField(name, stored, fast, tokenizer: "raw");

    /// <summary>Adds an unsigned 64-bit integer field.</summary>
    /// <param name="name">Field name.</param>
    /// <param name="stored">Return the value in search hits.</param>
    /// <param name="indexed">Make the field searchable (term and range queries).</param>
    /// <param name="fast">Add a fast field, for sorting and aggregations.</param>
    public SchemaBuilder AddU64Field(string name, bool stored = false, bool indexed = true, bool fast = false) =>
        Add(new FieldDefinition(name, FieldType.U64, stored, indexed, fast, null));

    /// <inheritdoc cref="AddU64Field" path="/param"/>
    /// <summary>Adds a signed 64-bit integer field.</summary>
    public SchemaBuilder AddI64Field(string name, bool stored = false, bool indexed = true, bool fast = false) =>
        Add(new FieldDefinition(name, FieldType.I64, stored, indexed, fast, null));

    /// <inheritdoc cref="AddU64Field" path="/param"/>
    /// <summary>Adds a 64-bit floating-point field.</summary>
    public SchemaBuilder AddF64Field(string name, bool stored = false, bool indexed = true, bool fast = false) =>
        Add(new FieldDefinition(name, FieldType.F64, stored, indexed, fast, null));

    /// <inheritdoc cref="AddU64Field" path="/param"/>
    /// <summary>Adds a boolean field.</summary>
    public SchemaBuilder AddBoolField(string name, bool stored = false, bool indexed = true, bool fast = false) =>
        Add(new FieldDefinition(name, FieldType.Bool, stored, indexed, fast, null));

    /// <inheritdoc cref="AddU64Field" path="/param"/>
    /// <summary>
    /// Adds a date field. In JSON documents, dates are RFC 3339 strings with an offset
    /// (for example <c>2024-05-01T12:00:00Z</c>); use <see cref="DateTimeOffset"/> or UTC
    /// <see cref="DateTime"/> values when adding objects. Precision is seconds.
    /// </summary>
    public SchemaBuilder AddDateField(string name, bool stored = false, bool indexed = true, bool fast = false) =>
        Add(new FieldDefinition(name, FieldType.Date, stored, indexed, fast, null));

    /// <inheritdoc cref="AddU64Field" path="/param"/>
    /// <summary>Adds a bytes field. In JSON documents, values are base64 strings.</summary>
    public SchemaBuilder AddBytesField(string name, bool stored = false, bool indexed = true, bool fast = false) =>
        Add(new FieldDefinition(name, FieldType.Bytes, stored, indexed, fast, null));

    /// <summary>Creates the schema. The builder can keep being used afterwards.</summary>
    /// <exception cref="InvalidOperationException">No fields were added.</exception>
    public Schema Build() =>
        _fields.Count == 0
            ? throw new InvalidOperationException("A schema needs at least one field.")
            : new Schema(_fields.ToArray());

    private SchemaBuilder Add(FieldDefinition field)
    {
        ArgumentException.ThrowIfNullOrEmpty(field.Name, "name");
        if (_fields.Any(f => f.Name == field.Name))
            throw new ArgumentException($"A field named '{field.Name}' was already added.", "name");
        _fields.Add(field);
        return this;
    }
}
