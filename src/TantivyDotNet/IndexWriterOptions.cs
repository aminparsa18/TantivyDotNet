namespace TantivyDotNet;

/// <summary>Settings for <see cref="SearchIndex.CreateWriter(IndexWriterOptions)"/>.</summary>
public sealed class IndexWriterOptions
{
    /// <summary>
    /// Total indexing memory budget in bytes, split across indexing threads. Each thread needs at
    /// least 15 MB, so Tantivy uses fewer threads if the budget is small. Default 50 MB.
    /// </summary>
    public long HeapBytes
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = 50_000_000;

    /// <summary>Number of indexing threads, or null (the default) to let Tantivy choose (at most 8).</summary>
    public int? ThreadCount
    {
        get;
        init
        {
            if (value is { } t) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(t, nameof(ThreadCount));
            field = value;
        }
    }

    /// <summary>
    /// Whether document properties that aren't schema fields are silently dropped. Default false:
    /// adding such a document throws <see cref="TantivyException"/> with
    /// <see cref="TantivyErrorCode.InvalidArgument"/>, which catches typos and casing mistakes
    /// (field names are case-sensitive, so <c>new { Title = "x" }</c> doesn't fill a <c>title</c> field).
    /// </summary>
    public bool IgnoreUnknownFields { get; init; }
}
