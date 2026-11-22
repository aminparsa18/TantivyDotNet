using System.Globalization;
using System.Text.Json;

namespace TantivyDotNet;

/// <summary>
/// Decides when a writer merges segments in the background, after commits. Set it with
/// <see cref="IndexWriterOptions.MergePolicy"/>. It isn't saved with the index: each writer uses its own.
/// </summary>
/// <remarks>
/// Each commit adds at least one segment. Searches visit every segment, so merging keeps them
/// fast; merging also drops deleted documents. For an index that's built once and then only read,
/// <see cref="IndexWriter.ForceMerge(int)"/> merges everything at the end instead.
/// </remarks>
public abstract class MergePolicy
{
    private protected MergePolicy() { }

    /// <summary>Tantivy's default: a <see cref="LogMergePolicy"/> with default settings.</summary>
    public static MergePolicy Default { get; } = new LogMergePolicy();

    /// <summary>Never merges in the background. Segments are only merged by <see cref="IndexWriter.ForceMerge(int)"/>.</summary>
    public static MergePolicy None { get; } = new NoMergePolicy();

    internal byte[] ToJson()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            WriteJson(w);
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private protected abstract void WriteJson(Utf8JsonWriter w);

    private sealed class NoMergePolicy : MergePolicy
    {
        private protected override void WriteJson(Utf8JsonWriter w) => w.WriteString("type", "none");

        public override string ToString() => "None";
    }
}

/// <summary>
/// Merges segments of similar size: segments are grouped into levels by document count (each
/// level <see cref="LevelLogSize"/> times larger in log scale), and a level with at least
/// <see cref="MinSegments"/> segments is merged into one. This is Tantivy's default policy.
/// </summary>
/// <example><c>new IndexWriterOptions { MergePolicy = new LogMergePolicy { MinSegments = 4 } }</c></example>
public sealed class LogMergePolicy : MergePolicy
{
    /// <summary>Minimum number of segments merged together. Default 8; at least 2.</summary>
    public int MinSegments
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 2);
            field = value;
        }
    } = 8;

    /// <summary>
    /// Segments with more documents than this aren't merged further (though merging smaller ones can
    /// produce a larger segment). Default 10,000,000.
    /// </summary>
    public int MaxDocsBeforeMerge
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = 10_000_000;

    /// <summary>Segments with fewer documents than this all count as the lowest level. Default 10,000.</summary>
    public int MinLayerSize
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 10_000;

    /// <summary>Size ratio between levels, in log scale. Default 0.75.</summary>
    public double LevelLogSize
    {
        get;
        init
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(LevelLogSize), value, "Must be a positive number.");
            field = value;
        }
    } = 0.75;

    /// <summary>
    /// If a segment's share of deleted documents exceeds this ratio (0 exclusive to 1), its level is
    /// merged even with fewer than <see cref="MinSegments"/> segments, which drops the deleted
    /// documents. Default 1.0: deletes don't trigger merges.
    /// </summary>
    public double DeletedDocsRatio
    {
        get;
        init
        {
            if (!(value > 0 && value <= 1))
                throw new ArgumentOutOfRangeException(nameof(DeletedDocsRatio), value, "Must be greater than 0 and at most 1.");
            field = value;
        }
    } = 1.0;

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"Log(MinSegments = {MinSegments}, MaxDocsBeforeMerge = {MaxDocsBeforeMerge}, MinLayerSize = {MinLayerSize}, LevelLogSize = {LevelLogSize}, DeletedDocsRatio = {DeletedDocsRatio})");

    private protected override void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("type", "log");
        w.WriteNumber("min_segments", MinSegments);
        w.WriteNumber("max_docs_before_merge", MaxDocsBeforeMerge);
        w.WriteNumber("min_layer_size", MinLayerSize);
        w.WriteNumber("level_log_size", LevelLogSize);
        w.WriteNumber("del_docs_ratio", (float)DeletedDocsRatio);
    }
}
