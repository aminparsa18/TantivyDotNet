using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TantivyDotNet.Interop;
using TantivyDotNet.Native;

namespace TantivyDotNet;

/// <summary>
/// Adds and deletes documents. Changes become visible to new searchers only after
/// <see cref="Commit"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not thread-safe.</b> Use a writer from one thread at a time. (Tantivy already parallelizes
/// indexing internally across <c>threadCount</c> threads.) Concurrent calls won't corrupt memory,
/// because the native side serializes them, but the order of operations and commits is then
/// undefined.
/// </para>
/// <para>
/// <see cref="Dispose"/> does <b>not</b> commit: anything added or deleted since the last
/// <see cref="Commit"/> is discarded. Only one writer may exist per index at a time.
/// </para>
/// </remarks>
public sealed unsafe class IndexWriter : IDisposable
{
    private readonly WriterHandle _handle;

    internal IndexWriter(WriterHandle handle) => _handle = handle;

    /// <summary>
    /// Adds a document given as a JSON object mapping field names to a value or an array of
    /// values, for example <c>{"title": "Of Mice and Men", "year": 1937}</c>. A property that isn't a
    /// schema field is an error unless the writer was created with <see cref="IndexWriterOptions.IgnoreUnknownFields"/>.
    /// </summary>
    /// <exception cref="TantivyException">With <see cref="TantivyErrorCode.InvalidArgument"/> if the JSON is invalid, a value doesn't match its field type, or a field is unknown.</exception>
    public void AddDocument(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        AddDocumentUtf8(Ffi.Utf8(json));
    }

    /// <inheritdoc cref="AddDocument(string)"/>
    public void AddDocument(ReadOnlySpan<byte> utf8Json) => AddDocumentUtf8(utf8Json);

    /// <summary>
    /// Serializes <paramref name="document"/> with System.Text.Json source-generated metadata and
    /// adds it. This overload is trimming- and NativeAOT-safe. JSON property names must match the
    /// schema's field names.
    /// </summary>
    public void AddDocument<T>(T document, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(typeInfo);
        AddDocumentUtf8(JsonSerializer.SerializeToUtf8Bytes(document, typeInfo));
    }

    /// <summary>
    /// Serializes <paramref name="document"/> (for example an anonymous object) with reflection-based
    /// System.Text.Json and adds it. Property names must match the schema's field names exactly.
    /// For trimmed or NativeAOT apps, use <see cref="AddDocument{T}(T, JsonTypeInfo{T})"/> instead.
    /// </summary>
    [RequiresUnreferencedCode("Reflection-based JSON serialization. Use AddDocument<T>(T, JsonTypeInfo<T>) for trimmed apps.")]
    [RequiresDynamicCode("Reflection-based JSON serialization. Use AddDocument<T>(T, JsonTypeInfo<T>) for NativeAOT apps.")]
    public void AddDocument(object document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document is string json)
        {
            AddDocument(json);
            return;
        }
        AddDocumentUtf8(JsonSerializer.SerializeToUtf8Bytes(document, document.GetType()));
    }

    /// <summary>
    /// Deletes every document whose text field <paramref name="field"/> contains the term
    /// <paramref name="value"/>. The value isn't tokenized: for a field using the <c>default</c>
    /// tokenizer, pass a single lowercase word. To delete by ID, use a field added with
    /// <see cref="SchemaBuilder.AddStringField"/>.
    /// </summary>
    public void DeleteTerm(string field, string value) => DeleteTerm(Query.Term(field, value));

    /// <summary>Deletes every document whose indexed u64 field <paramref name="field"/> equals <paramref name="value"/>.</summary>
    public void DeleteTerm(string field, ulong value) => DeleteTerm(Query.Term(field, value));

    /// <summary>Deletes every document whose indexed i64 field <paramref name="field"/> equals <paramref name="value"/>.</summary>
    public void DeleteTerm(string field, long value) => DeleteTerm(Query.Term(field, value));

    /// <summary>
    /// Deletes every document matching <paramref name="query"/>, among those added before this call
    /// (documents added later aren't affected). Takes effect on <see cref="Commit"/>.
    /// </summary>
    /// <example><c>writer.DeleteDocuments(Query.Range("year", null, 1900L));</c></example>
    /// <exception cref="TantivyException">The query doesn't fit the schema (see <see cref="Searcher.Search(Query, SearchOptions)"/>).</exception>
    public void DeleteDocuments(Query query)
    {
        ArgumentNullException.ThrowIfNull(query);
        byte[] q = query.ToJson();
        using var lease = _handle.Lease();
        TdnString err;
        fixed (byte* qp = q)
            Ffi.Check(NativeMethods.tdn_writer_delete_query(Ptr(lease), qp, (nuint)q.Length, &err), err);
    }

    /// <summary>
    /// Replaces the documents containing <paramref name="term"/> with a new document given as JSON:
    /// deletes them and adds <paramref name="json"/> in one operation, so a commit includes both or
    /// neither. Typically <paramref name="term"/> is an ID: <c>Query.Term("id", "b42")</c>.
    /// </summary>
    /// <param name="term">An exact term of an indexed field. It isn't analyzed (see <see cref="Query.Term(string, string)"/>).</param>
    /// <param name="json">The new document; see <see cref="AddDocument(string)"/>.</param>
    /// <exception cref="TantivyException">With <see cref="TantivyErrorCode.InvalidArgument"/> if the term doesn't fit the schema or the document is invalid. Nothing is deleted then.</exception>
    public void UpdateDocument(TermQuery term, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        UpdateDocumentUtf8(term, Ffi.Utf8(json));
    }

    /// <inheritdoc cref="UpdateDocument(TermQuery, string)"/>
    /// <param name="term">An exact term of an indexed field.</param>
    /// <param name="utf8Json">The new document as UTF-8 JSON.</param>
    public void UpdateDocument(TermQuery term, ReadOnlySpan<byte> utf8Json) => UpdateDocumentUtf8(term, utf8Json);

    /// <inheritdoc cref="UpdateDocument(TermQuery, string)"/>
    /// <param name="term">An exact term of an indexed field.</param>
    /// <param name="document">The new document, serialized as in <see cref="AddDocument{T}(T, JsonTypeInfo{T})"/>.</param>
    /// <param name="typeInfo">Source-generated metadata for <typeparamref name="T"/>.</param>
    public void UpdateDocument<T>(TermQuery term, T document, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(typeInfo);
        UpdateDocumentUtf8(term, JsonSerializer.SerializeToUtf8Bytes(document, typeInfo));
    }

    /// <inheritdoc cref="UpdateDocument(TermQuery, string)"/>
    /// <param name="term">An exact term of an indexed field.</param>
    /// <param name="document">The new document, serialized as in <see cref="AddDocument(object)"/>.</param>
    [RequiresUnreferencedCode("Reflection-based JSON serialization. Use UpdateDocument<T>(TermQuery, T, JsonTypeInfo<T>) for trimmed apps.")]
    [RequiresDynamicCode("Reflection-based JSON serialization. Use UpdateDocument<T>(TermQuery, T, JsonTypeInfo<T>) for NativeAOT apps.")]
    public void UpdateDocument(TermQuery term, object document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document is string json)
        {
            UpdateDocument(term, json);
            return;
        }
        UpdateDocumentUtf8(term, JsonSerializer.SerializeToUtf8Bytes(document, document.GetType()));
    }

    /// <summary>Deletes all documents (takes effect on <see cref="Commit"/>).</summary>
    public void DeleteAllDocuments() => Run(&NativeMethods.tdn_writer_delete_all);

    /// <summary>Makes all changes since the last commit durable and visible to new searchers.</summary>
    /// <remarks>
    /// A commit is all or nothing. If it fails (for example with an IO error), it throws and the
    /// writer is rolled back as if <see cref="Rollback"/> had been called: the changes since the
    /// last successful commit are discarded, and the writer can be used again. Add them again
    /// to retry.
    /// </remarks>
    /// <exception cref="TantivyException">The commit failed; the writer was rolled back.</exception>
    public void Commit() => Run(&NativeMethods.tdn_writer_commit);

    /// <summary>Discards all changes since the last commit.</summary>
    public void Rollback() => Run(&NativeMethods.tdn_writer_rollback);

    /// <summary>
    /// Merges the committed segments until at most <paramref name="maxSegments"/> remain, and rewrites
    /// segments that contain deleted documents to drop them. Blocks until done. Typically called once
    /// after building an index that is then only read: fewer segments make searches faster.
    /// </summary>
    /// <remarks>
    /// Only committed segments are merged; call <see cref="Commit"/> first. Uncommitted changes stay
    /// pending. Background merges are paused meanwhile. Merging rewrites the index, so it temporarily
    /// needs up to twice the disk space, and the replaced files remain until no searcher uses them
    /// (see <see cref="GarbageCollectFiles"/>). Searchers created before the merge keep their snapshot.
    /// </remarks>
    /// <param name="maxSegments">The number of segments to merge down to. Default 1.</param>
    public void ForceMerge(int maxSegments = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSegments);
        using var lease = _handle.Lease();
        TdnString err;
        Ffi.Check(NativeMethods.tdn_writer_force_merge(Ptr(lease), (uint)maxSegments, &err), err);
    }

    /// <summary>
    /// Deletes the index files that neither the index nor an open searcher uses anymore, such as
    /// segments replaced by a merge. Tantivy also does this after commits and merges, so this is
    /// rarely needed; it's useful to reclaim disk space right after disposing old searchers.
    /// </summary>
    /// <returns>The files deleted, and those that couldn't be deleted (on Windows, files still mapped by a searcher).</returns>
    public GarbageCollectionResult GarbageCollectFiles()
    {
        string json;
        using (var lease = _handle.Lease())
        {
            TdnString result, err;
            Ffi.Check(NativeMethods.tdn_writer_garbage_collect(Ptr(lease), &result, &err), err);
            json = Ffi.TakeString(result);
        }
        using var doc = JsonDocument.Parse(json);
        static string[] Names(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetString()!)];
        return new GarbageCollectionResult(Names(doc.RootElement.GetProperty("deleted")), Names(doc.RootElement.GetProperty("failed")));
    }

    /// <summary>
    /// Releases the writer and its index lock <b>without committing</b>. Safe to call more than once.
    /// </summary>
    /// <remarks>
    /// Blocks until background segment merges started by earlier commits have finished, as Lucene's
    /// <c>IndexWriter.Dispose()</c> does, so their work isn't lost. After a large commit this can take
    /// a while. A writer that is never disposed is released by its finalizer, which doesn't wait:
    /// running merges are abandoned (their files are cleaned up later) and the lock is released.
    /// </remarks>
    public void Dispose() => _handle.DisposeExplicitly();

    private void AddDocumentUtf8(ReadOnlySpan<byte> json)
    {
        using var lease = _handle.Lease();
        TdnString err;
        fixed (byte* p = json)
            Ffi.Check(NativeMethods.tdn_writer_add_json(Ptr(lease), p, (nuint)json.Length, &err), err);
    }

    private void DeleteTerm(TermQuery term)
    {
        byte[] t = term.ToJson();
        using var lease = _handle.Lease();
        TdnString err;
        fixed (byte* tp = t)
            Ffi.Check(NativeMethods.tdn_writer_delete_term(Ptr(lease), tp, (nuint)t.Length, &err), err);
    }

    private void UpdateDocumentUtf8(TermQuery term, ReadOnlySpan<byte> json)
    {
        ArgumentNullException.ThrowIfNull(term);
        byte[] t = term.ToJson();
        using var lease = _handle.Lease();
        TdnString err;
        fixed (byte* tp = t)
        fixed (byte* jp = json)
            Ffi.Check(NativeMethods.tdn_writer_update_json(Ptr(lease), tp, (nuint)t.Length, jp, (nuint)json.Length, &err), err);
    }

    private void Run(delegate*<TdnWriter*, TdnString*, int> op)
    {
        using var lease = _handle.Lease();
        TdnString err;
        Ffi.Check(op(Ptr(lease), &err), err);
    }

    private static TdnWriter* Ptr(TdnHandle.HandleLease lease) => (TdnWriter*)lease.Pointer;
}

/// <summary>The result of <see cref="IndexWriter.GarbageCollectFiles"/>.</summary>
/// <param name="DeletedFiles">Names of the files deleted, relative to the index directory.</param>
/// <param name="FailedFiles">
/// Names of unused files that couldn't be deleted, typically on Windows because a searcher still
/// maps them. A later collection retries them.
/// </param>
public sealed record GarbageCollectionResult(IReadOnlyList<string> DeletedFiles, IReadOnlyList<string> FailedFiles);
