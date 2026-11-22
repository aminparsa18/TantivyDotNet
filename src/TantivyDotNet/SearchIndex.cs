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
        Settings = IndexSettings.FromJson(ReadString(&NativeMethods.tdn_index_settings));
    }

    /// <summary>The index's schema.</summary>
    public Schema Schema { get; }

    /// <summary>The settings the index was created with.</summary>
    public IndexSettings Settings { get; }

    /// <summary>Creates an index held entirely in memory. It disappears when disposed.</summary>
    /// <param name="schema">The index's fields.</param>
    /// <param name="settings">Doc store compression and other settings, or null for <see cref="IndexSettings.Default"/>.</param>
    /// <exception cref="TantivyException">With <see cref="TantivyErrorCode.InvalidArgument"/> if the settings don't fit the schema (for example <see cref="IndexSettings.SortBy"/> names a field that isn't fast).</exception>
    public static SearchIndex CreateInRam(Schema schema, IndexSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Ffi.EnsureAbi();
        byte[] schemaJson = schema.ToJsonUtf8();
        byte[] settingsJson = (settings ?? IndexSettings.Default).ToJson();
        TdnIndex* index;
        TdnString err;
        fixed (byte* s = schemaJson)
        fixed (byte* st = settingsJson)
            Ffi.Check(NativeMethods.tdn_index_create_in_ram(s, (nuint)schemaJson.Length, st, (nuint)settingsJson.Length, &index, &err), err);
        return new SearchIndex(index);
    }

    /// <summary>
    /// Creates a new index in <paramref name="directory"/>, creating the directory if needed.
    /// </summary>
    /// <param name="directory">The index directory.</param>
    /// <param name="schema">The index's fields.</param>
    /// <param name="settings">Doc store compression and other settings, or null for <see cref="IndexSettings.Default"/>. They're saved with the index.</param>
    /// <exception cref="TantivyException">
    /// With <see cref="TantivyErrorCode.IndexAlreadyExists"/> if the directory already holds an index, or
    /// <see cref="TantivyErrorCode.InvalidArgument"/> if the settings don't fit the schema.
    /// </exception>
    public static SearchIndex Create(string directory, Schema schema, IndexSettings? settings = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(schema);
        Ffi.EnsureAbi();
        byte[] settingsJson = (settings ?? IndexSettings.Default).ToJson();
        string fullPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullPath);
        byte[] path = Ffi.Utf8(fullPath);
        byte[] schemaJson = schema.ToJsonUtf8();
        TdnIndex* index;
        TdnString err;
        fixed (byte* p = path)
        fixed (byte* s = schemaJson)
        fixed (byte* st = settingsJson)
            Ffi.Check(NativeMethods.tdn_index_create_in_dir(p, (nuint)path.Length, s, (nuint)schemaJson.Length, st, (nuint)settingsJson.Length, &index, &err), err);
        return new SearchIndex(index);
    }

    /// <summary>Opens an existing index directory. Custom analyzers saved with the index are restored.</summary>
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
        byte[] mergePolicy = options.MergePolicy.ToJson();
        using var lease = _handle.Lease();
        TdnWriter* writer;
        TdnString err;
        fixed (byte* mp = mergePolicy)
        {
            Ffi.Check(NativeMethods.tdn_index_writer(
                (TdnIndex*)lease.Pointer, (nuint)options.HeapBytes, (nuint)(options.ThreadCount ?? 0), flags,
                mp, (nuint)mergePolicy.Length, &writer, &err), err);
        }
        return new IndexWriter(new WriterHandle(writer));
    }

    /// <summary>
    /// Creates a searcher over the latest committed state of the index. The searcher is a
    /// snapshot: commits made after this call aren't visible to it, so create a new searcher to
    /// see them. To share one snapshot across requests and refresh it after commits, use
    /// <see cref="CreateSearcherManager"/>.
    /// </summary>
    public Searcher CreateSearcher()
    {
        using var lease = _handle.Lease();
        TdnSearcher* searcher;
        TdnString err;
        Ffi.Check(NativeMethods.tdn_index_searcher((TdnIndex*)lease.Pointer, &searcher, &err), err);
        return new Searcher(new SearcherHandle(searcher), Schema);
    }

    /// <summary>
    /// Creates a <see cref="SearcherManager"/>, which shares one searcher across requests and
    /// replaces it when the index changes. Dispose it when done.
    /// </summary>
    /// <param name="options">Refresh mode and warmer, or null for the defaults (manual <see cref="SearcherManager.Refresh"/>, no warmer).</param>
    public SearcherManager CreateSearcherManager(SearcherManagerOptions? options = null)
    {
        options ??= new SearcherManagerOptions();
        uint flags = options.RefreshOnCommit ? Ffi.ReaderReloadOnCommit : 0;
        TdnReader* reader;
        using (var lease = _handle.Lease())
        {
            TdnString err;
            Ffi.Check(NativeMethods.tdn_index_reader((TdnIndex*)lease.Pointer, flags, &reader, &err), err);
        }
        return new SearcherManager(new ReaderHandle(reader), Schema, options);
    }

    /// <summary>
    /// Statistics of the latest commit: segments, documents and sizes. Shorthand for
    /// <see cref="Searcher.GetStats"/> on a new searcher.
    /// </summary>
    public IndexStats GetStats()
    {
        for (int attempt = 1; ; attempt++)
        {
            using var searcher = CreateSearcher();
            try
            {
                return searcher.GetStats();
            }
            catch (TantivyException e) when (e.Code == TantivyErrorCode.Io && attempt < 3)
            {
                // A merge replaced and deleted the new snapshot's files in between; take a newer one.
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="text"/> through a tokenizer of this index and returns the resulting
    /// terms, to check what a field indexes or how query text is analyzed.
    /// </summary>
    /// <param name="tokenizer">A built-in tokenizer or a custom analyzer of the schema (see <see cref="Schema.Analyzers"/>).</param>
    /// <param name="text">The text to analyze.</param>
    /// <exception cref="TantivyException">With <see cref="TantivyErrorCode.InvalidArgument"/> if there is no such tokenizer.</exception>
    public IReadOnlyList<string> Analyze(string tokenizer, string text)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(text);
        byte[] n = Ffi.Utf8(tokenizer), t = Ffi.Utf8(text);
        string json;
        using (var lease = _handle.Lease())
        {
            TdnString result, err;
            fixed (byte* np = n)
            fixed (byte* tp = t)
                Ffi.Check(NativeMethods.tdn_index_analyze((TdnIndex*)lease.Pointer, np, (nuint)n.Length, tp, (nuint)t.Length, &result, &err), err);
            json = Ffi.TakeString(result);
        }
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(e => e.GetString()!)];
    }

    /// <summary>Releases the native index. Safe to call more than once.</summary>
    public void Dispose() => _handle.Dispose();

    private Schema ReadSchema() => Schema.FromJson(ReadString(&NativeMethods.tdn_index_schema));

    private string ReadString(delegate*<TdnIndex*, TdnString*, TdnString*, int> export)
    {
        using var lease = _handle.Lease();
        TdnString json;
        TdnString err;
        Ffi.Check(export((TdnIndex*)lease.Pointer, &json, &err), err);
        return Ffi.TakeString(json);
    }
}
