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
    /// values, for example <c>{"title": "Of Mice and Men", "year": 1937}</c>. Unknown fields are ignored.
    /// </summary>
    /// <exception cref="TantivyException">With <see cref="TantivyErrorCode.InvalidArgument"/> if the JSON is invalid or a value doesn't match its field type.</exception>
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
    public void DeleteTerm(string field, string value)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);
        byte[] f = Ffi.Utf8(field), v = Ffi.Utf8(value);
        using var lease = _handle.Lease();
        TdnString err;
        fixed (byte* fp = f)
        fixed (byte* vp = v)
            Ffi.Check(NativeMethods.tdn_writer_delete_text(Ptr(lease), fp, (nuint)f.Length, vp, (nuint)v.Length, &err), err);
    }

    /// <summary>Deletes every document whose indexed u64 field <paramref name="field"/> equals <paramref name="value"/>.</summary>
    public void DeleteTerm(string field, ulong value)
    {
        ArgumentNullException.ThrowIfNull(field);
        byte[] f = Ffi.Utf8(field);
        using var lease = _handle.Lease();
        TdnString err;
        fixed (byte* fp = f)
            Ffi.Check(NativeMethods.tdn_writer_delete_u64(Ptr(lease), fp, (nuint)f.Length, value, &err), err);
    }

    /// <summary>Deletes every document whose indexed i64 field <paramref name="field"/> equals <paramref name="value"/>.</summary>
    public void DeleteTerm(string field, long value)
    {
        ArgumentNullException.ThrowIfNull(field);
        byte[] f = Ffi.Utf8(field);
        using var lease = _handle.Lease();
        TdnString err;
        fixed (byte* fp = f)
            Ffi.Check(NativeMethods.tdn_writer_delete_i64(Ptr(lease), fp, (nuint)f.Length, value, &err), err);
    }

    /// <summary>Deletes all documents (takes effect on <see cref="Commit"/>).</summary>
    public void DeleteAllDocuments() => Run(&NativeMethods.tdn_writer_delete_all);

    /// <summary>Makes all changes since the last commit durable and visible to new searchers.</summary>
    public void Commit() => Run(&NativeMethods.tdn_writer_commit);

    /// <summary>Discards all changes since the last commit.</summary>
    public void Rollback() => Run(&NativeMethods.tdn_writer_rollback);

    /// <summary>
    /// Releases the writer and its index lock <b>without committing</b>. Safe to call more than once.
    /// </summary>
    /// <remarks>
    /// Blocks until background segment merges started by earlier commits have finished, as Lucene's
    /// <c>IndexWriter.Dispose()</c> does, so their work isn't lost. After a large commit this can take
    /// a while.
    /// </remarks>
    public void Dispose() => _handle.Dispose();

    private void AddDocumentUtf8(ReadOnlySpan<byte> json)
    {
        using var lease = _handle.Lease();
        TdnString err;
        fixed (byte* p = json)
            Ffi.Check(NativeMethods.tdn_writer_add_json(Ptr(lease), p, (nuint)json.Length, &err), err);
    }

    private void Run(delegate*<TdnWriter*, TdnString*, int> op)
    {
        using var lease = _handle.Lease();
        TdnString err;
        Ffi.Check(op(Ptr(lease), &err), err);
    }

    private static TdnWriter* Ptr(TdnHandle.HandleLease lease) => (TdnWriter*)lease.Pointer;
}
