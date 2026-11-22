namespace TantivyDotNet.Tests;

public class SearcherManagerTests
{
    private static void AddAndCommit(SearchIndex index, string id)
    {
        using var writer = index.CreateWriter();
        writer.AddDocument($$"""{"id": "{{id}}", "title": "added {{id}}", "year": 2000}""");
        writer.Commit();
    }

    private static long Count(SearcherManager manager)
    {
        using var lease = manager.Acquire();
        return lease.Searcher.DocumentCount;
    }

    [Fact]
    public void Acquire_shares_one_searcher_until_Refresh()
    {
        using var index = Books.InRam();
        using var manager = index.CreateSearcherManager();
        using (var a = manager.Acquire())
        using (var b = manager.Acquire())
            Assert.Same(a.Searcher, b.Searcher);

        AddAndCommit(index, "n1");
        Assert.Equal(4, Count(manager));
        Assert.True(manager.Refresh());
        Assert.Equal(5, Count(manager));
        Assert.False(manager.Refresh()); // nothing changed
    }

    [Fact]
    public void A_lease_keeps_its_searcher_after_it_is_replaced()
    {
        using var index = Books.InRam();
        using var manager = index.CreateSearcherManager();
        using var old = manager.Acquire();
        AddAndCommit(index, "n1");
        manager.Refresh();
        using var current = manager.Acquire();
        Assert.NotSame(old.Searcher, current.Searcher);
        Assert.Equal(4, old.Searcher.Search(Query.All, limit: 10).Count);
        Assert.Equal(5, current.Searcher.Search(Query.All, limit: 10).Count);
    }

    [Fact]
    public void A_released_replaced_searcher_is_disposed()
    {
        using var index = Books.InRam();
        using var manager = index.CreateSearcherManager();
        var lease = manager.Acquire();
        var searcher = lease.Searcher;
        searcher.Dispose(); // no-op for a leased searcher
        Assert.Equal(4, searcher.DocumentCount);
        AddAndCommit(index, "n1");
        manager.Refresh();
        Assert.Equal(4, searcher.DocumentCount); // still leased
        lease.Dispose();
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => searcher.DocumentCount);
    }

    [Fact]
    public void Disposing_the_manager_leaves_outstanding_leases_usable()
    {
        using var index = Books.InRam();
        var manager = index.CreateSearcherManager();
        using var lease = manager.Acquire();
        manager.Dispose();
        manager.Dispose();
        Assert.Throws<ObjectDisposedException>(() => manager.Acquire());
        Assert.Throws<ObjectDisposedException>(() => manager.Refresh());
        Assert.Equal(4, lease.Searcher.Search("title:eden").TotalCount + 3);
    }

    [Fact]
    public void The_warmer_runs_on_each_new_searcher()
    {
        using var index = Books.InRam();
        var warmed = new List<long>();
        using var manager = index.CreateSearcherManager(new SearcherManagerOptions
        {
            Warmer = s => { s.Search("title:mice"); warmed.Add(s.DocumentCount); },
        });
        Assert.Equal([4], warmed);
        AddAndCommit(index, "n1");
        manager.Refresh();
        manager.Refresh();
        Assert.Equal([4, 5], warmed);
    }

    [Fact]
    public void A_failing_warmer_keeps_the_previous_searcher()
    {
        using var index = Books.InRam();
        bool fail = false;
        using var manager = index.CreateSearcherManager(new SearcherManagerOptions
        {
            Warmer = _ => { if (fail) throw new InvalidOperationException("warm failed"); },
        });
        AddAndCommit(index, "n1");
        fail = true;
        Assert.Throws<InvalidOperationException>(() => manager.Refresh());
        Assert.Equal(4, Count(manager));
        fail = false;
        Assert.True(manager.Refresh());
        Assert.Equal(5, Count(manager));
    }

    [Fact]
    public void A_warmer_failing_on_the_first_searcher_fails_creation()
    {
        using var index = Books.InRam();
        Assert.Throws<InvalidOperationException>(() => index.CreateSearcherManager(new SearcherManagerOptions
        {
            Warmer = _ => throw new InvalidOperationException("warm failed"),
        }));
    }

    private static void WaitForCount(SearcherManager manager, long expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (Count(manager) != expected)
        {
            Assert.True(DateTime.UtcNow < deadline, $"the manager didn't pick up the commit (count {Count(manager)}, expected {expected})");
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void RefreshOnCommit_picks_up_commits_in_memory()
    {
        using var index = Books.InRam();
        using var manager = index.CreateSearcherManager(new SearcherManagerOptions { RefreshOnCommit = true });
        AddAndCommit(index, "n1");
        WaitForCount(manager, 5);
        AddAndCommit(index, "n2");
        WaitForCount(manager, 6);
    }

    [Fact]
    public void RefreshOnCommit_picks_up_commits_on_disk_from_another_index_instance()
    {
        using var dir = new TempDirectory();
        using (var created = SearchIndex.Create(dir.Path, Books.Schema())) AddAndCommit(created, "n0");
        using var reading = SearchIndex.Open(dir.Path);
        using var writing = SearchIndex.Open(dir.Path);
        using var manager = reading.CreateSearcherManager(new SearcherManagerOptions { RefreshOnCommit = true });
        Assert.Equal(1, Count(manager));
        AddAndCommit(writing, "n1");
        WaitForCount(manager, 2);
    }

    [Fact]
    public void Concurrent_acquires_during_commits_and_refreshes()
    {
        using var index = Books.InRam();
        using var manager = index.CreateSearcherManager(new SearcherManagerOptions { RefreshOnCommit = true });
        using var writer = index.CreateWriter();
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var lease = manager.Acquire();
                long count = lease.Searcher.Search(Query.All, limit: 0).TotalCount;
                Assert.InRange(count, 4, 34);
            }
        })).ToArray();
        for (int i = 0; i < 30; i++)
        {
            writer.AddDocument($$"""{"id": "c{{i}}", "title": "concurrent"}""");
            writer.Commit();
            if (i % 3 == 0) manager.Refresh();
        }
        manager.Refresh();
        stop.Cancel();
        Task.WaitAll(readers);
        Assert.Equal(34, Count(manager));
    }
}
