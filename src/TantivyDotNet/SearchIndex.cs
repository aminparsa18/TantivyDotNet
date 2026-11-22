using TantivyDotNet.Interop;
using TantivyDotNet.Native;

namespace TantivyDotNet;

/// <summary>
/// A Tantivy index, in memory or in a directory. Create documents with <see cref="CreateWriter(IndexWriterOptions)"/>
/// and query them with <see cref="CreateSearcher"/>.
/// </summary>
/// <remarks>
/// This type is thread-safe. Disposing it doesn't invalidate writers or searchers already created
/// from it; each of those must be disposed separately.
/// </remarks>
public sealed unsafe class SearchIndex : IDisposable
{
    private readonly IndexHandle _handle;

    private SearchIndex(TdnIndex* pointer)
    {
        _handle = new IndexHandle(pointer);
        Schema = ReadSchema();
    }

    /// <summary>The index's schema.</summary>
    public Schema Schema { get; }

    /// <summary>Creates an index held entirely in memory. It disappears when disposed.</summary>
    public static SearchIndex CreateInRam(Schema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Ffi.EnsureAbi();
        byte[] schemaJson = schema.ToJsonUtf8();
        TdnIndex* index;
        TdnString err;
        fixed (byte* s = schemaJson)
            Ffi.Check(NativeMethods.tdn_index_create_in_ram(s, (nuint)schemaJson.Length, &index, &err), err);
        return new SearchIndex(index);
    }

    /// <summary>
    /// Creates a new index in <paramref name="directory"/>, creating the directory if needed.
    /// </summary>
    /// <exception cref="TantivyException">With <see cref="TantivyErrorCode.IndexAlreadyExists"/> if the directory already holds an index.</exception>
    public static SearchIndex Create(string directory, Schema schema)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(schema);
        Ffi.EnsureAbi();
        string fullPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullPath);
        byte[] path = Ffi.Utf8(fullPath);
        byte[] schemaJson = schema.ToJsonUtf8();
        TdnIndex* index;
        TdnString err;
        fixed (byte* p = path)
        fixed (byte* s = schemaJson)
            Ffi.Check(NativeMethods.tdn_index_create_in_dir(p, (nuint)path.Length, s, (nuint)schemaJson.Length, &index, &err), err);
        return new SearchIndex(index);
    }

    /// <summary>Opens an existing index directory.</summary>
    /// <exception cref="TantivyException">The directory doesn't exist or doesn't contain an index.</exception>
    public static SearchIndex Open(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Ffi.EnsureAbi();
        byte[] path = Ffi.Utf8(Path.GetFullPath(directory));
        TdnIndex* index;
        TdnString err;
        fixed (byte* p = path)
            Ffi.Check(NativeMethods.tdn_index_open_in_dir(p, (nuint)path.Length, &index, &err), err);
        return new SearchIndex(index);
    }

    /// <summary>
    /// Creates the index's writer. Only one writer can exist per index at a time; dispose the
    /// previous one first.
    /// </summary>
    /// <param name="heapBytes">
    /// Total indexing memory budget, split across indexing threads. Each thread needs at least
    /// 15 MB, so Tantivy uses fewer threads if the budget is small.
    /// </param>
    /// <param name="threadCount">Number of indexing threads, or null to let Tantivy choose (at most 8).</param>
    /// <exception cref="TantivyException">With <see cref="TantivyErrorCode.LockBusy"/> if another writer is open.</exception>
    public IndexWriter CreateWriter(long heapBytes = 50_000_000, int? threadCount = null) =>
        CreateWriter(new IndexWriterOptions { HeapBytes = heapBytes, ThreadCount = threadCount });

    /// <summary>
    /// Creates the index's writer with <paramref name="options"/>. Only one writer can exist per
    /// index at a time; dispose the previous one first.
    /// </summary>
    /// <exception cref="TantivyException">With <see cref="TantivyErrorCode.LockBusy"/> if another writer is open.</exception>
    public IndexWriter CreateWriter(IndexWriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        uint flags = options.IgnoreUnknownFields ? Ffi.WriterIgnoreUnknownFields : 0;
        using var lease = _handle.Lease();
        TdnWriter* writer;
        TdnString err;
        Ffi.Check(NativeMethods.tdn_index_writer((TdnIndex*)lease.Pointer, (nuint)options.HeapBytes, (nuint)(options.ThreadCount ?? 0), flags, &writer, &err), err);
        return new IndexWriter(new WriterHandle(writer));
    }

    /// <summary>
    /// Creates a searcher over the latest committed state of the index. The searcher is a
    /// snapshot: commits made after this call aren't visible to it, so create a new searcher to
    /// see them.
    /// </summary>
    public Searcher CreateSearcher()
    {
        using var lease = _handle.Lease();
        TdnSearcher* searcher;
        TdnString err;
        Ffi.Check(NativeMethods.tdn_index_searcher((TdnIndex*)lease.Pointer, &searcher, &err), err);
        return new Searcher(new SearcherHandle(searcher), Schema);
    }

    /// <summary>Releases the native index. Safe to call more than once.</summary>
    public void Dispose() => _handle.Dispose();

    private Schema ReadSchema()
    {
        using var lease = _handle.Lease();
        TdnString json;
        TdnString err;
        Ffi.Check(NativeMethods.tdn_index_schema((TdnIndex*)lease.Pointer, &json, &err), err);
        return Schema.FromJson(Ffi.TakeString(json));
    }
}
