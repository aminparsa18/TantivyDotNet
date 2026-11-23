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
    /// <summary>Hierarchical category path such as <c>/genre/fiction</c>; see <see cref="SchemaBuilder.AddFacetField"/>.</summary>
    Facet,
    /// <summary>Schemaless JSON object; see <see cref="SchemaBuilder.AddJsonField"/>.</summary>
    Json,
    /// <summary>IPv4 or IPv6 address; see <see cref="SchemaBuilder.AddIpAddressField"/>.</summary>
    IpAddress,
    /// <summary>A field type this version of TantivyDotNet doesn't know.</summary>
    Other,
}

/// <summary>One field of a <see cref="Schema"/>. Add fields with <see cref="SchemaBuilder"/>.</summary>
public sealed record FieldDefinition
{
    internal FieldDefinition(string name, FieldType type, bool stored, bool indexed, bool fast, string? tokenizer)
    {
        Name = name;
        Type = type;
        Stored = stored;
        Indexed = indexed;
        Fast = fast;
        Tokenizer = tokenizer;
    }

    /// <summary>The field name, as used in documents and queries.</summary>
    public string Name { get; }

    /// <summary>The value type.</summary>
    public FieldType Type { get; }

    /// <summary>Whether the original value is stored and returned in search hits.</summary>
    public bool Stored { get; }

    /// <summary>Whether the field is searchable.</summary>
    public bool Indexed { get; }

    /// <summary>Whether the field has a columnar "fast field" (used for sorting and aggregations).</summary>
    public bool Fast { get; }

    /// <summary>For indexed text and JSON fields, the tokenizer name (for example <c>default</c> or <c>raw</c>); otherwise null.</summary>
    public string? Tokenizer { get; }
}

/// <summary>
/// The immutable set of fields in an index. Build one with <see cref="SchemaBuilder"/>, or read
/// an existing index's schema from <see cref="SearchIndex.Schema"/>.
/// </summary>
public sealed class Schema
{
    private readonly Dictionary<string, FieldDefinition> _byName;

    /// <summary>Tokenizers every index has; custom analyzers can't use these names.</summary>
    internal static readonly string[] BuiltInTokenizers = ["default", "raw", "en_stem", "whitespace"];

    internal Schema(IReadOnlyList<FieldDefinition> fields, IReadOnlyDictionary<string, Analyzer> analyzers)
    {
        Fields = fields;
        Analyzers = analyzers;
        _byName = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
    }

    /// <summary>The fields, in declaration order.</summary>
    public IReadOnlyList<FieldDefinition> Fields { get; }

    /// <summary>
    /// The custom analyzers added with <see cref="SchemaBuilder.AddAnalyzer"/>, by name. Built-in
    /// tokenizers (<c>default</c>, <c>raw</c>, <c>en_stem</c>, <c>whitespace</c>) aren't listed.
    /// </summary>
    public IReadOnlyDictionary<string, Analyzer> Analyzers { get; }

    /// <summary>Returns the field with this name (case-sensitive).</summary>
    /// <exception cref="KeyNotFoundException">No such field.</exception>
    public FieldDefinition this[string name] =>
        _byName.TryGetValue(name, out var field) ? field : throw new KeyNotFoundException($"Schema has no field named '{name}'.");

    /// <summary>Looks up a field by name (case-sensitive).</summary>
    public bool TryGetField(string name, [MaybeNullWhen(false)] out FieldDefinition field) => _byName.TryGetValue(name, out field);

    // The JSON shape is defined in native/src/schema_spec.rs:
    // {"fields": [{"name", "type", "stored", "indexed", "fast", "tokenizer"?}], "analyzers": {"name": {...}}}

    internal byte[] ToJsonUtf8()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartArray("fields");
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
            w.WriteStartObject("analyzers");
            foreach (var (name, analyzer) in Analyzers)
            {
                w.WritePropertyName(name);
                analyzer.WriteJson(w);
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    internal static Schema FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var fields = new List<FieldDefinition>();
        foreach (var e in doc.RootElement.GetProperty("fields").EnumerateArray())
        {
            fields.Add(new FieldDefinition(
                e.GetProperty("name").GetString()!,
                TypeFromJson(e.GetProperty("type").GetString()),
                e.GetProperty("stored").GetBoolean(),
                e.GetProperty("indexed").GetBoolean(),
                e.GetProperty("fast").GetBoolean(),
                e.TryGetProperty("tokenizer", out var t) ? t.GetString() : null));
        }
        var analyzers = new Dictionary<string, Analyzer>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("analyzers", out var a))
            foreach (var p in a.EnumerateObject()) analyzers[p.Name] = Analyzer.FromJson(p.Value);
        return new Schema(fields, analyzers.AsReadOnly());
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
        FieldType.Facet => "facet",
        FieldType.Json => "json",
        FieldType.IpAddress => "ip",
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
        "ip" => FieldType.IpAddress,
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
    private readonly Dictionary<string, Analyzer> _analyzers = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a custom <paramref name="analyzer"/> under <paramref name="name"/>, for use as the
    /// <c>tokenizer</c> of text fields. The definition is part of the schema: an index created in a
    /// directory saves it, and <see cref="SearchIndex.Open"/> restores it.
    /// </summary>
    /// <param name="name">The name text fields refer to. Can't be a built-in tokenizer name (<c>default</c>, <c>raw</c>, <c>en_stem</c>, <c>whitespace</c>).</param>
    /// <param name="analyzer">The analyzer; see <see cref="Analyzer"/>.</param>
    /// <remarks>
    /// Changing an analyzer later would make existing documents and new queries disagree on terms,
    /// so an index's analyzers can't be changed after creation; reindex into a new index instead.
    /// </remarks>
    public SchemaBuilder AddAnalyzer(string name, Analyzer analyzer)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(analyzer);
        if (Array.IndexOf(Schema.BuiltInTokenizers, name) >= 0)
            throw new ArgumentException($"'{name}' is a built-in tokenizer and can't be redefined.", nameof(name));
        if (!_analyzers.TryAdd(name, analyzer))
            throw new ArgumentException($"An analyzer named '{name}' was already added.", nameof(name));
        return this;
    }

    /// <summary>
    /// Adds a full-text field. Its values are split into terms by <paramref name="tokenizer"/>
    /// (<c>default</c> splits on whitespace and punctuation, lowercases, and drops very long tokens;
    /// <c>en_stem</c> also applies English stemming; <c>raw</c> keeps the whole value as one term).
    /// </summary>
    /// <param name="name">Field name.</param>
    /// <param name="stored">Return the original value in search hits.</param>
    /// <param name="fast">Also store as a fast field (untokenized), for sorting and aggregations.</param>
    /// <param name="tokenizer">A built-in tokenizer (<c>default</c>, <c>raw</c>, <c>en_stem</c>, <c>whitespace</c>) or the name of an analyzer added with <see cref="AddAnalyzer"/>.</param>
    public SchemaBuilder AddTextField(string name, bool stored = false, bool fast = false, string tokenizer = "default")
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenizer);
        return Add(new FieldDefinition(name, FieldType.Text, stored, indexed: true, fast, tokenizer));
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

    /// <summary>
    /// Adds a facet field: hierarchical category paths like <c>/genre/fiction/classic</c>, used for
    /// filtering (<see cref="Query.Facet"/>) and counting (<see cref="FacetRequest"/>). In JSON
    /// documents a value is a path string, or an array of them for several categories. Paths start
    /// with <c>/</c>; a <c>/</c> inside a segment is escaped as <c>\/</c>. Facet fields are always
    /// indexed and fast.
    /// </summary>
    /// <param name="name">Field name.</param>
    /// <param name="stored">Return the paths in search hits.</param>
    public SchemaBuilder AddFacetField(string name, bool stored = false) =>
        Add(new FieldDefinition(name, FieldType.Facet, stored, indexed: true, fast: true, null));

    /// <summary>
    /// Adds a JSON field: each document can put any JSON object in it, without declaring the keys.
    /// Query a key by its path: <c>Query.Term("attrs.color", "red")</c>, <c>Query.Range("attrs.size", 2L, 5L)</c>,
    /// or <c>attrs.color:red</c> in parsed queries. Strings are tokenized with <paramref name="tokenizer"/>;
    /// numbers, booleans and RFC 3339 dates keep their type.
    /// </summary>
    /// <param name="name">Field name.</param>
    /// <param name="stored">Return the object in search hits.</param>
    /// <param name="fast">Also store values as fast fields (strings untokenized), for range queries, <see cref="Query.Exists"/> and aggregations on paths like <c>attrs.price</c>.</param>
    /// <param name="tokenizer">The tokenizer for string values; see <see cref="AddTextField"/>.</param>
    public SchemaBuilder AddJsonField(string name, bool stored = false, bool fast = false, string tokenizer = "default")
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenizer);
        return Add(new FieldDefinition(name, FieldType.Json, stored, indexed: true, fast, tokenizer));
    }

    /// <inheritdoc cref="AddU64Field" path="/param"/>
    /// <summary>
    /// Adds an IP address field. In JSON documents, values are strings like <c>192.168.0.1</c> or
    /// <c>2001:db8::1</c>; IPv4 addresses are stored as IPv4-mapped IPv6 and returned as IPv4.
    /// Query with <see cref="Query.Term(string, System.Net.IPAddress)"/> and ranges.
    /// </summary>
    public SchemaBuilder AddIpAddressField(string name, bool stored = false, bool indexed = true, bool fast = false) =>
        Add(new FieldDefinition(name, FieldType.IpAddress, stored, indexed, fast, null));

    /// <summary>Creates the schema. The builder can keep being used afterwards.</summary>
    /// <exception cref="InvalidOperationException">No fields were added, or a text field uses a tokenizer that is neither built in nor added with <see cref="AddAnalyzer"/>.</exception>
    public Schema Build()
    {
        if (_fields.Count == 0) throw new InvalidOperationException("A schema needs at least one field.");
        foreach (var f in _fields)
        {
            if (f.Tokenizer is { } t && Array.IndexOf(Schema.BuiltInTokenizers, t) < 0 && !_analyzers.ContainsKey(t))
                throw new InvalidOperationException(
                    $"Field '{f.Name}' uses tokenizer '{t}', which is neither built in ({string.Join(", ", Schema.BuiltInTokenizers)}) nor added with AddAnalyzer.");
        }
        return new Schema(_fields.ToArray(), new Dictionary<string, Analyzer>(_analyzers, StringComparer.Ordinal).AsReadOnly());
    }

    private SchemaBuilder Add(FieldDefinition field)
    {
        ArgumentException.ThrowIfNullOrEmpty(field.Name, "name");
        // Tantivy rejects these (a leading '-' would read as a negation in the query parser).
        if (field.Name.StartsWith('-'))
            throw new ArgumentException($"Field name '{field.Name}' can't start with '-'.", "name");
        if (_fields.Any(f => f.Name == field.Name))
            throw new ArgumentException($"A field named '{field.Name}' was already added.", "name");
        _fields.Add(field);
        return this;
    }
}
