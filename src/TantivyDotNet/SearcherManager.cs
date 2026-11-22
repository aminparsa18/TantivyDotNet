using TantivyDotNet.Interop;
using TantivyDotNet.Native;

namespace TantivyDotNet;

/// <summary>Settings for <see cref="SearchIndex.CreateSearcherManager"/>.</summary>
public sealed class SearcherManagerOptions
{
    /// <summary>
    /// Whether new commits are picked up automatically. Default false: the searcher only changes on
    /// <see cref="SearcherManager.Refresh"/>.
    /// </summary>
    /// <remarks>
    /// When true, Tantivy watches the index for commits (also commits by other processes) and
    /// reloads in the background. The next <see cref="SearcherManager.Acquire"/> after that
    /// swaps in the new searcher, so a commit becomes visible within a short delay (milliseconds in
    /// memory, up to about a second on disk), not as soon as <see cref="IndexWriter.Commit"/>
    /// returns. To see your own commit right away, call <see cref="SearcherManager.Refresh"/> after it.
    /// </remarks>
    public bool RefreshOnCommit { get; init; }

    /// <summary>
    /// Runs on each new searcher before searches can use it, for example a few typical queries to
    /// load index files into the OS cache so the first real requests aren't slow. It also runs on
    /// the first searcher, inside <see cref="SearchIndex.CreateSearcherManager"/>. If it throws, the
    /// new searcher is discarded, the previous one stays current, and the exception propagates.
    /// </summary>
    public Action<Searcher>? Warmer { get; init; }
}

/// <summary>
/// Shares one <see cref="Searcher"/> across requests and replaces it when the index changes, so
/// requests don't each open a snapshot. Leases keep the searcher they got alive until disposed, even
/// after it's replaced. Created by <see cref="SearchIndex.CreateSearcherManager"/>.
/// </summary>
/// <example>
/// <code>
/// using var manager = index.CreateSearcherManager(new SearcherManagerOptions { RefreshOnCommit = true });
/// // per request:
/// using var lease = manager.Acquire();
/// var hits = lease.Searcher.Search("body:mice");
/// </code>
/// </example>
/// <remarks>
/// <b>Thread-safe.</b> <see cref="Acquire"/> is cheap and lock-free unless it swaps in a new
/// searcher. Disposing the manager doesn't invalidate leases already handed out.
/// </remarks>
public sealed unsafe class SearcherManager : IDisposable
{
    private readonly ReaderHandle _reader;
    private readonly Schema _schema;
    private readonly bool _refreshOnCommit;
    private readonly Action<Searcher>? _warmer;
    private readonly Lock _refreshLock = new();
    private volatile Entry _current;
    private volatile bool _disposed;

    internal SearcherManager(ReaderHandle reader, Schema schema, SearcherManagerOptions options)
    {
        _reader = reader;
        _schema = schema;
        _refreshOnCommit = options.RefreshOnCommit;
        _warmer = options.Warmer;
        try
        {
            _current = new Entry(OpenWarmed(reload: false, current: null)!);
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns a lease on the current searcher. Dispose the lease when the request is done; the
    /// searcher stays valid until then, even if it's replaced meanwhile.
    /// </summary>
    /// <remarks>
    /// With <see cref="SearcherManagerOptions.RefreshOnCommit"/>, this first swaps in a newer snapshot
    /// if Tantivy has reloaded one (running the warmer on this thread). If another thread is already
    /// doing that, this one doesn't wait and gets the current searcher.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The manager was disposed.</exception>
    public SearcherLease Acquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_refreshOnCommit && _refreshLock.TryEnter())
        {
            try
            {
                if (!_disposed) Swap(reload: false);
            }
            finally
            {
                _refreshLock.Exit();
            }
        }
        while (true)
        {
            var entry = _current;
            if (entry.TryAddRef()) return new SearcherLease(entry);
            // The entry was replaced and released between the read and the add-ref; read again.
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    /// <summary>
    /// Reloads the latest commit and, if the index changed, replaces the current searcher with a new
    /// (warmed) one. Blocks while another refresh is running.
    /// </summary>
    /// <returns>Whether the searcher was replaced.</returns>
    /// <exception cref="ObjectDisposedException">The manager was disposed.</exception>
    public bool Refresh()
    {
        lock (_refreshLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Swap(reload: true);
        }
    }

    /// <summary>
    /// Releases the manager's reference to the current searcher. Leases already handed out stay
    /// usable until disposed. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        lock (_refreshLock)
        {
            if (_disposed) return;
            _disposed = true;
            _current.Release();
        }
        _reader.Dispose();
    }

    // Called under _refreshLock.
    private bool Swap(bool reload)
    {
        var old = _current;
        var next = OpenWarmed(reload, old.Searcher);
        if (next is null) return false;
        _current = new Entry(next);
        old.Release();
        return true;
    }

    // The reader's searcher (after reloading, if asked), or null if it's the same snapshot as current.
    private Searcher? OpenWarmed(bool reload, Searcher? current)
    {
        TdnSearcher* pointer;
        using (var readerLease = _reader.Lease())
        {
            TdnString err;
            if (current is null)
            {
                Ffi.Check(NativeMethods.tdn_reader_searcher((TdnReader*)readerLease.Pointer, reload ? 1u : 0u, null, &pointer, &err), err);
            }
            else
            {
                using var currentLease = current.Handle.Lease();
                Ffi.Check(NativeMethods.tdn_reader_searcher(
                    (TdnReader*)readerLease.Pointer, reload ? 1u : 0u, (TdnSearcher*)currentLease.Pointer, &pointer, &err), err);
            }
        }
        if (pointer == null) return null;
        var searcher = new Searcher(new SearcherHandle(pointer), _schema) { OwnedByManager = true };
        try
        {
            _warmer?.Invoke(searcher);
        }
        catch
        {
            searcher.Handle.Dispose();
            throw;
        }
        return searcher;
    }

    /// <summary>A searcher plus the number of references to it: the manager's (while current) and each lease's.</summary>
    internal sealed class Entry(Searcher searcher)
    {
        private int _refs = 1;

        public Searcher Searcher { get; } = searcher;

        public bool TryAddRef()
        {
            while (true)
            {
                int refs = Volatile.Read(ref _refs);
                if (refs == 0) return false;
                if (Interlocked.CompareExchange(ref _refs, refs + 1, refs) == refs) return true;
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refs) == 0) Searcher.Handle.Dispose();
        }
    }
}

/// <summary>
/// A searcher borrowed from a <see cref="SearcherManager"/>. Dispose it when done, typically with
/// <c>using</c>; don't use <see cref="Searcher"/> afterwards.
/// </summary>
public sealed class SearcherLease : IDisposable
{
    private SearcherManager.Entry? _entry;

    internal SearcherLease(SearcherManager.Entry entry)
    {
        _entry = entry;
        Searcher = entry.Searcher;
    }

    /// <summary>The leased searcher. Disposing it does nothing; dispose the lease instead.</summary>
    public Searcher Searcher { get; }

    /// <summary>Returns the searcher to the manager. Safe to call more than once.</summary>
    public void Dispose() => Interlocked.Exchange(ref _entry, null)?.Release();
}
