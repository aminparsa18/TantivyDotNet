using System.Text.Json;

namespace TantivyDotNet;

/// <summary>
/// Statistics of one index snapshot, from <see cref="Searcher.GetStats"/> or <see cref="SearchIndex.GetStats"/>.
/// </summary>
/// <remarks>
/// Sizes are the bytes of the snapshot's segment files, without filesystem overhead or the small
/// index metadata files. Files of older snapshots that haven't been deleted yet aren't counted.
/// </remarks>
public sealed class IndexStats
{
    private IndexStats(IReadOnlyList<SegmentStats> segments, IReadOnlyList<FieldStats> fields)
    {
        Segments = segments;
        Fields = fields;
        foreach (var s in segments)
        {
            DocumentCount += s.DocumentCount;
            DeletedDocumentCount += s.DeletedDocumentCount;
            SizeBytes += s.SizeBytes;
        }
    }

    /// <summary>Number of segments. Every search visits each segment, so fewer is faster.</summary>
    public int SegmentCount => Segments.Count;

    /// <summary>Number of live (non-deleted) documents.</summary>
    public long DocumentCount { get; }

    /// <summary>
    /// Number of deleted documents still taking up space. Merges drop them (see
    /// <see cref="IndexWriter.ForceMerge(int)"/> and <see cref="LogMergePolicy.DeletedDocsRatio"/>).
    /// </summary>
    public long DeletedDocumentCount { get; }

    /// <summary>Total size of the segment files in bytes.</summary>
    public long SizeBytes { get; }

    /// <summary>The segments, in search order.</summary>
    public IReadOnlyList<SegmentStats> Segments { get; }

    /// <summary>
    /// Bytes used by each field across all segments, ordered by field name. Stored fields aren't
    /// included, because the doc store isn't split by field (see <see cref="SegmentStats.StoreBytes"/>).
    /// </summary>
    public IReadOnlyList<FieldStats> Fields { get; }

    /// <summary>For example <c>3 segments, 1000 docs (12 deleted), 1234567 bytes</c>.</summary>
    public override string ToString() =>
        $"{SegmentCount} segments, {DocumentCount} docs ({DeletedDocumentCount} deleted), {SizeBytes} bytes";

    // Shape: see native/src/stats.rs.
    internal static IndexStats Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var segments = root.GetProperty("segments").EnumerateArray().Select(s =>
        {
            long maxDoc = s.GetProperty("max_doc").GetInt64(), deleted = s.GetProperty("deleted").GetInt64();
            return new SegmentStats(
                s.GetProperty("id").GetString()!, maxDoc - deleted, deleted,
                s.GetProperty("bytes").GetInt64(), s.GetProperty("store").GetInt64());
        });
        var fields = root.GetProperty("fields").EnumerateArray().Select(f => new FieldStats(
            f.GetProperty("name").GetString()!,
            f.GetProperty("termdict").GetInt64(),
            f.GetProperty("postings").GetInt64(),
            f.GetProperty("positions").GetInt64(),
            f.GetProperty("fast").GetInt64(),
            f.GetProperty("fieldnorms").GetInt64()));
        return new IndexStats([.. segments], [.. fields]);
    }
}

/// <summary>Statistics of one segment, in <see cref="IndexStats.Segments"/>.</summary>
/// <param name="Id">The segment's identifier (a UUID without dashes), which also prefixes its file names.</param>
/// <param name="DocumentCount">Live (non-deleted) documents.</param>
/// <param name="DeletedDocumentCount">Deleted documents still stored in the segment.</param>
/// <param name="SizeBytes">Total size of the segment's files in bytes.</param>
/// <param name="StoreBytes">Bytes of <see cref="SizeBytes"/> used by stored fields (the doc store).</param>
public sealed record SegmentStats(string Id, long DocumentCount, long DeletedDocumentCount, long SizeBytes, long StoreBytes);

/// <summary>Bytes used by one field across all segments, in <see cref="IndexStats.Fields"/>. Components the field doesn't have are 0.</summary>
/// <param name="Name">The field name.</param>
/// <param name="TermDictionaryBytes">The term dictionary (indexed fields).</param>
/// <param name="PostingsBytes">Posting lists: which documents contain each term, with frequencies.</param>
/// <param name="PositionsBytes">Term positions, used by phrase queries (text fields indexed with positions).</param>
/// <param name="FastFieldBytes">Columnar values of a fast field.</param>
/// <param name="FieldNormBytes">Field lengths, used for scoring.</param>
public sealed record FieldStats(string Name, long TermDictionaryBytes, long PostingsBytes, long PositionsBytes, long FastFieldBytes, long FieldNormBytes)
{
    /// <summary>The sum of all components.</summary>
    public long TotalBytes => TermDictionaryBytes + PostingsBytes + PositionsBytes + FastFieldBytes + FieldNormBytes;
}
