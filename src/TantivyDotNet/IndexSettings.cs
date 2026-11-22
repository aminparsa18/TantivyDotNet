using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace TantivyDotNet;

/// <summary>How stored fields (the doc store) are compressed. See <see cref="IndexSettings.DocStoreCompression"/>.</summary>
public enum DocStoreCompression
{
    /// <summary>LZ4: fast, moderate compression. The default.</summary>
    Lz4,
    /// <summary>Zstandard: smaller, slower to read and write. Level in <see cref="IndexSettings.ZstdLevel"/>.</summary>
    Zstd,
    /// <summary>No compression.</summary>
    None,
}

/// <summary>
/// Settings chosen when an index is created (<see cref="SearchIndex.Create(string, Schema, IndexSettings?)"/>,
/// <see cref="SearchIndex.CreateInRam(Schema, IndexSettings?)"/>). They're saved with the index and
/// can't be changed later; <see cref="SearchIndex.Settings"/> reports them.
/// </summary>
public sealed class IndexSettings
{
    /// <summary>The settings used when none are given.</summary>
    public static IndexSettings Default { get; } = new();

    /// <summary>How stored fields are compressed. Default <see cref="TantivyDotNet.DocStoreCompression.Lz4"/>.</summary>
    public DocStoreCompression DocStoreCompression { get; init; }

    /// <summary>
    /// The Zstandard level, 1 (fastest) to 22 (smallest), or null for zstd's default (3). Only valid
    /// with <see cref="TantivyDotNet.DocStoreCompression.Zstd"/>.
    /// </summary>
    public int? ZstdLevel
    {
        get;
        init
        {
            if (value is { } level)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(level, 1, nameof(ZstdLevel));
                ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 22, nameof(ZstdLevel));
            }
            field = value;
        }
    }

    /// <summary>
    /// Uncompressed size in bytes of each doc store block. Larger blocks compress better, but loading
    /// one hit's stored fields decompresses its whole block. Default 16 KB.
    /// </summary>
    public int DocStoreBlockSize
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = 16_384;

    /// <summary>
    /// Stores documents in the order of a fast field (u64, i64, f64, date, text or bytes) instead of
    /// insertion order, or null (the default) for insertion order. Searches sorted by that field can
    /// then stop early. Must be <see cref="TantivyDotNet.SortBy.Field(string, bool)"/>, not a score.
    /// </summary>
    /// <remarks>
    /// Experimental: Tantivy removed index sorting in 0.22 and restored it in 0.27, so it may change
    /// or go away in a future version.
    /// </remarks>
    [Experimental("TDN0001")]
    public SortBy? SortBy
    {
        get;
        init
        {
            if (value is { FieldName: null })
                throw new ArgumentException("An index can only be sorted by a field, not by score.", nameof(SortBy));
            field = value;
        }
    }

    /// <summary>
    /// For example <c>DocStoreCompression = Zstd, ZstdLevel = 9, DocStoreBlockSize = 16384, SortBy = year desc</c>.
    /// </summary>
#pragma warning disable TDN0001
    public override string ToString() =>
        $"DocStoreCompression = {DocStoreCompression}" +
        (ZstdLevel is { } l ? $", ZstdLevel = {l}" : "") +
        $", DocStoreBlockSize = {DocStoreBlockSize}" +
        (SortBy is { } s ? $", SortBy = {s}" : "");

    internal byte[] ToJson()
    {
        if (ZstdLevel is not null && DocStoreCompression != DocStoreCompression.Zstd)
            throw new ArgumentException("ZstdLevel requires DocStoreCompression.Zstd.");
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("compression", DocStoreCompression switch
            {
                DocStoreCompression.Zstd => "zstd",
                DocStoreCompression.None => "none",
                _ => "lz4",
            });
            if (ZstdLevel is { } level) w.WriteNumber("zstd_level", level);
            w.WriteNumber("block_size", DocStoreBlockSize);
            if (SortBy is { } sort)
            {
                w.WriteStartObject("sort_by");
                w.WriteString("field", sort.FieldName);
                w.WriteBoolean("descending", sort.Descending);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    // Shape: see native/src/settings.rs.
    internal static IndexSettings FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new IndexSettings
        {
            DocStoreCompression = root.GetProperty("compression").GetString() switch
            {
                "zstd" => DocStoreCompression.Zstd,
                "none" => DocStoreCompression.None,
                _ => DocStoreCompression.Lz4,
            },
            ZstdLevel = root.TryGetProperty("zstd_level", out var z) ? z.GetInt32() : null,
            DocStoreBlockSize = root.GetProperty("block_size").GetInt32(),
            SortBy = root.TryGetProperty("sort_by", out var s)
                ? TantivyDotNet.SortBy.Field(s.GetProperty("field").GetString()!, s.GetProperty("descending").GetBoolean())
                : null,
        };
    }
#pragma warning restore TDN0001
}
