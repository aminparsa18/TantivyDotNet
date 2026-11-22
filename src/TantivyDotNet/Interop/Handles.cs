using System.Runtime.InteropServices;
using TantivyDotNet.Native;

namespace TantivyDotNet.Interop;

/// <summary>
/// Base for native handles. Calls go through <see cref="Lease"/>, which add-refs the handle so a
/// concurrent <see cref="SafeHandle.Dispose()"/> can't free the native object mid-call; the free
/// then runs when the last in-flight call finishes.
/// </summary>
internal abstract class TdnHandle : SafeHandle
{
    private readonly string _ownerName;

    protected TdnHandle(IntPtr pointer, string ownerName) : base(IntPtr.Zero, ownsHandle: true)
    {
        _ownerName = ownerName;
        SetHandle(pointer);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>Pins the handle for the duration of one native call.</summary>
    /// <exception cref="ObjectDisposedException">The owning object was disposed.</exception>
    internal HandleLease Lease()
    {
        if (IsClosed) throw new ObjectDisposedException(_ownerName);
        bool added = false;
        try
        {
            DangerousAddRef(ref added);
        }
        catch (ObjectDisposedException)
        {
            // Lost a race with Dispose; report it with the public type name.
            throw new ObjectDisposedException(_ownerName);
        }
        return new HandleLease(this);
    }

    internal readonly struct HandleLease(TdnHandle owner) : IDisposable
    {
        public IntPtr Pointer => owner.DangerousGetHandle();
        public void Dispose() => owner.DangerousRelease();
    }
}

internal sealed unsafe class IndexHandle(TdnIndex* pointer) : TdnHandle((IntPtr)pointer, nameof(SearchIndex))
{
    protected override bool ReleaseHandle()
    {
        NativeMethods.tdn_index_free((TdnIndex*)handle);
        return true;
    }
}

internal sealed unsafe class WriterHandle(TdnWriter* pointer) : TdnHandle((IntPtr)pointer, nameof(IndexWriter))
{
    protected override bool ReleaseHandle()
    {
        NativeMethods.tdn_writer_free((TdnWriter*)handle);
        return true;
    }
}

internal sealed unsafe class SearcherHandle(TdnSearcher* pointer) : TdnHandle((IntPtr)pointer, nameof(Searcher))
{
    protected override bool ReleaseHandle()
    {
        NativeMethods.tdn_searcher_free((TdnSearcher*)handle);
        return true;
    }
}
