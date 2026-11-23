using System.Collections.Concurrent;

namespace TantivyDotNet.Tests;

/// <summary>
/// Many threads against one index for a while. <c>TDN_STRESS_SECONDS</c> sets how long each test
/// runs (default 3; CI's stress job runs them for minutes).
/// </summary>
public class StressTests
{
    internal static TimeSpan Duration =>
        TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("TDN_STRESS_SECONDS"), out var s) ? s : 3);

    private static Schema Schema() => new SchemaBuilder()
        .AddU64Field("id", stored: true, fast: true)
        .AddTextField("body", stored: true)
        .AddFacetField("cat")
        .AddI64Field("n", fast: true)
        .Build();

    private static string Doc(ulong id) =>
        $$"""{"id": {{id}}, "body": "w{{id % 7}} common text number {{id}}", "cat": "/c{{id % 3}}/x", "n": {{(long)(id % 11) - 5}}}""";

    /// <summary>Runs <paramref name="workers"/> until the duration has passed (or one fails), then rethrows the first failure.</summary>
    private static void RunFor(TimeSpan duration, params Action<CancellationToken>[] workers)
    {
        using var stop = new CancellationTokenSource(duration);
        var errors = new ConcurrentQueue<Exception>();
        var threads = workers.Select(work => new Thread(() =>
        {
            try { work(stop.Token); }
            catch (Exception e) { errors.Enqueue(e); stop.Cancel(); }
        }) { IsBackground = true }).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) Assert.True(t.Join(duration + TimeSpan.FromMinutes(2)), "a worker hung");
        if (!errors.IsEmpty) throw new AggregateException(errors);
    }

    /// <summary>Checks one snapshot is internally consistent; returns its document count.</summary>
    private static long CheckSnapshot(Searcher searcher, ConcurrentDictionary<long, bool> committedCounts)
    {
        long count = searcher.DocumentCount;
        Assert.True(committedCounts.ContainsKey(count), $"snapshot has {count} documents, which no commit had");
        Assert.Equal(count, searcher.Count(Query.All));

        var page = searcher.Search(Query.All, new SearchOptions
        {
            Limit = 50,
            OrderBy = [SortBy.Field("id")],
            Facets = [new FacetRequest("cat")],
        });
        Assert.Equal(count, page.TotalCount);
        Assert.Equal(Math.Min(50, count), page.Count);
        Assert.Equal(count, page.Facets["cat"].Sum(f => f.Count));
        ulong? previous = null;
        foreach (var hit in page)
        {
            ulong id = hit.Document.GetU64("id")!.Value;
            Assert.True(previous is null || id > previous, "ids ascending");
            Assert.Equal($"w{id % 7} common text number {id}", hit.Document.GetString("body"));
            previous = id;
        }

        var matches = searcher.Search("common", new SearchOptions { Limit = 5, Snippets = new SnippetOptions("body") });
        Assert.Equal(count, matches.TotalCount);
        Assert.All(matches, h => Assert.Contains("common", h.Snippets["body"].ToHtml()));
        return count;
    }

    [Fact]
    public void Searches_during_commits_merges_deletes_and_garbage_collection()
    {
        // This found two bugs (step 9.5): on Windows, Commit failed with "Access is denied" while
        // readers had meta.json open (directory.rs), and a merge then published part of the
        // failed commit, a count no commit had (committing.rs).
        // TDN_STRESS_OFF=reopen,gc,merge,fresh,managed turns parts off, to narrow down a failure.
        var off = (Environment.GetEnvironmentVariable("TDN_STRESS_OFF") ?? "").Split(',', StringSplitOptions.TrimEntries);
        using var dir = new TempDirectory();
        using var index = SearchIndex.Create(dir.Path, Schema());
        var committedCounts = new ConcurrentDictionary<long, bool> { [0] = true };
        using var manager = index.CreateSearcherManager(new SearcherManagerOptions { RefreshOnCommit = true });
        long snapshots = 0;

        void Writer(CancellationToken stop)
        {
            using var writer = index.CreateWriter(new IndexWriterOptions
            {
                HeapBytes = 40_000_000,
                ThreadCount = 2,
                MergePolicy = new LogMergePolicy { MinSegments = 2 },
            });
            var live = new HashSet<ulong>();
            var random = new Random(1);
            ulong next = 0;
            for (int round = 0; !stop.IsCancellationRequested; round++)
            {
                for (int i = 0; i < 40; i++)
                {
                    writer.AddDocument(Doc(next));
                    live.Add(next++);
                }
                for (int i = 0; i < 10 && next > 0; i++)
                {
                    ulong victim = (ulong)random.NextInt64((long)next);
                    writer.DeleteTerm("id", victim);
                    live.Remove(victim);
                }
                ulong updated = (ulong)random.NextInt64((long)next);
                if (live.Contains(updated)) writer.UpdateDocument(Query.Term("id", updated), Doc(updated));
                // Announce the count before it becomes visible.
                committedCounts[live.Count] = true;
                writer.Commit();
                if (round % 15 == 7 && !off.Contains("merge")) writer.ForceMerge(2);
                if (round % 10 == 3 && !off.Contains("gc")) writer.GarbageCollectFiles();
            }
        }

        void FreshSearchers(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                using var searcher = index.CreateSearcher();
                CheckSnapshot(searcher, committedCounts);
                Interlocked.Increment(ref snapshots);
            }
        }

        void Managed(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                using var lease = manager.Acquire();
                CheckSnapshot(lease.Searcher, committedCounts);
                if (Random.Shared.Next(4) == 0) manager.Refresh();
                Interlocked.Increment(ref snapshots);
            }
        }

        // Another SearchIndex on the same directory, opened and closed over and over.
        void Reopen(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                using var other = SearchIndex.Open(dir.Path);
                using var searcher = other.CreateSearcher();
                CheckSnapshot(searcher, committedCounts);
                _ = other.GetStats();
                Interlocked.Increment(ref snapshots);
            }
        }

        var workers = new List<Action<CancellationToken>> { Writer };
        if (!off.Contains("fresh")) workers.AddRange([FreshSearchers, FreshSearchers, FreshSearchers]);
        if (!off.Contains("managed")) workers.AddRange([Managed, Managed]);
        if (!off.Contains("reopen")) workers.Add(Reopen);
        RunFor(Duration, [.. workers]);
        Assert.True(snapshots > 0 || workers.Count == 1);

        // After the writer is gone, the last commit is what everyone sees.
        using var final = index.CreateSearcher();
        CheckSnapshot(final, committedCounts);
    }

    /// <summary>
    /// The smallest version of the test above: commits on disk while other threads read meta.json
    /// (a fresh searcher reloads it; opening the directory again also reads .managed.json).
    /// No merges, no garbage collection.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Commits_on_disk_while_readers_reload(bool reopenDirectory)
    {
        using var dir = new TempDirectory();
        using var index = SearchIndex.Create(dir.Path, Schema());
        long commits = 0;

        void Writer(CancellationToken stop)
        {
            using var writer = index.CreateWriter(new IndexWriterOptions
            {
                HeapBytes = 15_000_000,
                ThreadCount = 1,
                MergePolicy = MergePolicy.None,
            });
            for (ulong id = 0; !stop.IsCancellationRequested; id++)
            {
                writer.AddDocument(Doc(id));
                writer.Commit();
                Interlocked.Increment(ref commits);
            }
        }

        void Reader(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                if (reopenDirectory)
                {
                    using var other = SearchIndex.Open(dir.Path);
                    using var s = other.CreateSearcher();
                    _ = s.DocumentCount;
                }
                else
                {
                    using var s = index.CreateSearcher();
                    _ = s.DocumentCount;
                }
            }
        }

        RunFor(Duration, Writer, Reader, Reader, Reader);
        Assert.True(commits > 0);
    }

    [Fact]
    public async Task Disposing_while_other_threads_use_an_object_only_throws_ObjectDisposedException()
    {
        var deadline = DateTime.UtcNow + Duration;
        int rounds = 0;
        while (DateTime.UtcNow < deadline || rounds < 20)
        {
            rounds++;
            var index = SearchIndex.CreateInRam(Schema());
            var writer = index.CreateWriter(heapBytes: 15_000_000, threadCount: 1);
            for (ulong i = 0; i < 30; i++) writer.AddDocument(Doc(i));
            writer.Commit();
            var searcher = index.CreateSearcher();
            var manager = index.CreateSearcherManager();
            using var go = new Barrier(7);

            void Use(Action action)
            {
                go.SignalAndWait();
                for (int i = 0; i < 200; i++)
                {
                    try { action(); }
                    catch (ObjectDisposedException) { return; }
                }
            }

            var tasks = new[]
            {
                Task.Run(() => Use(() => searcher.Search("common", new SearchOptions { Limit = 3, OrderBy = [SortBy.Field("n")] })), TestContext.Current.CancellationToken),
                Task.Run(() => Use(() => _ = searcher.GetStats()), TestContext.Current.CancellationToken),
                Task.Run(() => Use(() => { using var lease = manager.Acquire(); _ = lease.Searcher.Count(Query.All); manager.Refresh(); }), TestContext.Current.CancellationToken),
                Task.Run(() => Use(() => { writer.AddDocument(Doc(99)); if (Random.Shared.Next(20) == 0) writer.Commit(); }), TestContext.Current.CancellationToken),
                Task.Run(() => Use(() => { using var s = index.CreateSearcher(); _ = s.DocumentCount; }), TestContext.Current.CancellationToken),
                Task.Run(() =>
                {
                    go.SignalAndWait();
                    Thread.SpinWait(Random.Shared.Next(20_000));
                    // Any order, while the others are mid-call.
                    foreach (var d in new IDisposable[] { searcher, writer, manager, index }.OrderBy(_ => Random.Shared.Next()))
                        d.Dispose();
                }, TestContext.Current.CancellationToken),
                Task.Run(() => { go.SignalAndWait(); Thread.SpinWait(Random.Shared.Next(20_000)); searcher.Dispose(); writer.Dispose(); }, TestContext.Current.CancellationToken),
            };
            await Task.WhenAll(tasks); // rethrows anything but ObjectDisposedException
        }
    }

    [Fact]
    public void A_writer_shared_across_threads_loses_nothing()
    {
        // Documented as not thread-safe, but misuse must not corrupt anything: the native mutex
        // serializes the calls.
        using var index = SearchIndex.CreateInRam(Schema());
        using var writer = index.CreateWriter(heapBytes: 30_000_000, threadCount: 2);
        long added = 0;
        var deadline = DateTime.UtcNow + Duration;
        Parallel.For(0, 6, t =>
        {
            for (ulong i = 0; DateTime.UtcNow < deadline || i < 100; i++)
            {
                writer.AddDocument(Doc((ulong)t * 1_000_000_000 + i));
                Interlocked.Increment(ref added);
                if (i % 97 == 0) writer.Commit();
            }
        });
        writer.Commit();
        using var searcher = index.CreateSearcher();
        Assert.Equal(added, searcher.DocumentCount);
        Assert.Equal(added, searcher.Count("common"));
    }
}
