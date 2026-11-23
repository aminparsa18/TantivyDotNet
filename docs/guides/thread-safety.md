# Thread safety and lifetime

## Who can be used from several threads

| Type | Thread-safe | Notes |
| --- | --- | --- |
| [`SearchIndex`](xref:TantivyDotNet.SearchIndex) | Yes | Open it once per process and share it |
| [`Searcher`](xref:TantivyDotNet.Searcher) | Yes | Share one across threads; it's a snapshot of one commit |
| [`SearcherManager`](xref:TantivyDotNet.SearcherManager) | Yes | `Acquire` is cheap and doesn't take a lock in the common case |
| [`IndexWriter`](xref:TantivyDotNet.IndexWriter) | No | Use it from one thread at a time. It indexes on several threads internally |
| `Schema`, `Query`, `SearchOptions`, `Analyzer` | Yes | Immutable |
| `SearchResults`, `SearchHit`, `Document` | Yes, for reading | Plain data, not tied to the searcher |

Calling a writer from several threads at once doesn't corrupt anything, because the native side serializes the calls. But the order of adds, deletes and commits is then up to the scheduler. To feed one writer from many producers, put a channel in front of it and consume it on one thread:

```csharp
var channel = Channel.CreateBounded<string>(10_000);
var indexing = Task.Run(async () =>
{
    using var writer = index.CreateWriter(new IndexWriterOptions { HeapBytes = 200_000_000 });
    await foreach (var json in channel.Reader.ReadAllAsync())
        writer.AddDocument(json);
    writer.Commit();
});
```

## Snapshots and commits

A searcher sees the index as of the last commit before it was created. Later commits don't change it: queries on one searcher are consistent with each other, and paging with `SearchAfter` stays exact. To see new commits, get a new searcher:

- **`index.CreateSearcher()`** is cheap (it reloads the list of segments) and suits scripts, tests and batch jobs.
- **A `SearcherManager`** suits servers. It hands out one shared searcher and replaces it after commits. Each request takes a lease, which keeps its searcher alive until disposed, even after it's been replaced:

```csharp
// at startup
var manager = index.CreateSearcherManager(new SearcherManagerOptions
{
    RefreshOnCommit = true,                               // also sees commits from other processes
    Warmer = s => s.Search("common query", limit: 1),     // runs before a new searcher serves requests
});

// per request
using (var lease = manager.Acquire())
{
    var results = lease.Searcher.Search(query);
}
```

With `RefreshOnCommit`, a commit becomes visible shortly after `Commit()` returns: milliseconds for an index in memory, up to about a second on disk. To see your own commit right away, call `manager.Refresh()` after it.

## One writer per index

An index allows one open writer at a time. A second `CreateWriter`, in the same process or another one, throws [`TantivyException`](xref:TantivyDotNet.TantivyException) with code `LockBusy`. The lock is an OS file lock, so it's released when the process exits, even if it crashes. Searchers in other processes can read the index while one process writes it.

## Commits and crashes

- **A commit is atomic.** After a crash, even in the middle of `Commit()`, reopening the index gives the last completed commit. Documents added since then are lost.
- **A failed commit rolls back.** If `Commit()` throws (for example the disk is full), the writer discards the changes since the last successful commit, as `Rollback()` does, and stays usable. Add the documents again to retry.
- **`Dispose()` doesn't commit.** Uncommitted changes are discarded. Commit first.

## Disposal

Every `SearchIndex`, `IndexWriter`, `Searcher`, `SearcherManager` and `SearcherLease` owns native resources: memory, memory-mapped files, and for writers the lock and indexing threads. Dispose them, ideally with `using`. All of them can be disposed more than once, and using one after disposing it throws `ObjectDisposedException`.

- **Disposal order doesn't matter.** Disposing a `SearchIndex` doesn't invalidate the writers and searchers created from it, and disposing a `SearcherManager` doesn't invalidate leases already handed out. Each keeps what it needs alive.
- **Disposing is safe while other threads use the object.** A search that has started finishes; calls that start afterwards throw `ObjectDisposedException`.
- **`IndexWriter.Dispose()` waits for background merges** to finish, so their work isn't lost. After a large commit, that can take a while.
- **A forgotten object is cleaned up by its finalizer.** A writer's finalizer doesn't wait for merges: it abandons them and releases the lock. Relying on this delays releasing memory and files, so dispose explicitly.
- **Old searchers keep their files.** A searcher keeps the segment files of its snapshot alive, so disk space freed by merges is only reclaimed after the searchers that use those files are disposed. On Windows, those files can't be deleted until then; `GarbageCollectFiles()` reports them in `FailedFiles` and a later collection retries.
