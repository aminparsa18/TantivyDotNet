using System.Diagnostics;

namespace TantivyDotNet.Tests;

/// <summary>
/// Opens, indexes, searches and disposes in a loop, checking that native memory, open files and
/// threads stay flat. A leaked writer (its indexing arena and threads), reader (its threads and
/// mmaps), searcher or result buffer would show up as steady growth.
/// </summary>
[Collection(nameof(LeakTests))]
public class LeakTests
{
    private record Usage(long ResidentBytes, int IndexFiles, int Threads)
    {
        public static Usage Now()
        {
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            using var process = Process.GetCurrentProcess();
            // The thread pool grows and shrinks on its own (more on many-core CI machines), so its
            // workers aren't counted: a leaked writer or reader leaks threads of its own.
            return new Usage(process.WorkingSet64, IndexFileCount(process), process.Threads.Count - ThreadPool.ThreadCount);
        }

        /// <summary>On Linux, the process's thread names with counts (for a failure message).</summary>
        public static string ThreadNames()
        {
            if (!OperatingSystem.IsLinux()) return "";
            var names = Directory.GetDirectories("/proc/self/task")
                .Select(t => { try { return File.ReadAllText(Path.Combine(t, "comm")).Trim(); } catch (IOException) { return "?"; } })
                .GroupBy(n => n).OrderBy(g => g.Key).Select(g => $"{g.Key} x{g.Count()}");
            return " Threads: " + string.Join(", ", names);
        }

        /// <summary>
        /// Open descriptors and memory mappings of files under the tests' temp folder (a leaked
        /// writer keeps its lock file open, a leaked reader its mmaps). Counting every descriptor
        /// would also count the runtime's lazily loaded assemblies and sockets. Windows has no
        /// cheap per-file listing, so there it's the process's handle count.
        /// </summary>
        private static int IndexFileCount(Process process)
        {
            if (OperatingSystem.IsWindows()) return process.HandleCount;
            if (OperatingSystem.IsLinux())
            {
                int fds = new DirectoryInfo("/proc/self/fd").GetFileSystemInfos()
                    .Count(f => f.LinkTarget?.Contains("tdn-tests", StringComparison.Ordinal) == true);
                return fds + File.ReadLines("/proc/self/maps").Count(l => l.Contains("tdn-tests", StringComparison.Ordinal));
            }
            // macOS: lsof lists open files and mappings ("txt"), with paths under /private/var.
            using var lsof = Process.Start(new ProcessStartInfo("lsof", ["-n", "-P", "-Fn", "-p", Environment.ProcessId.ToString()])
            {
                RedirectStandardOutput = true,
            })!;
            int count = 0;
            while (lsof.StandardOutput.ReadLine() is { } line)
                if (line.StartsWith('n') && line.Contains("tdn-tests", StringComparison.Ordinal)) count++;
            lsof.WaitForExit();
            return count;
        }

        public override string ToString() => $"{ResidentBytes / 1_048_576.0:F1} MB resident, {IndexFiles} index files open, {Threads} threads outside the thread pool";
    }

    private static readonly Schema Schema = new SchemaBuilder()
        .AddU64Field("id", stored: true, fast: true)
        .AddTextField("body", stored: true)
        .AddFacetField("cat")
        .Build();

    private static void Cycle(int round)
    {
        using var dir = new TempDirectory();
        using (var index = SearchIndex.Create(dir.Path, Schema))
        {
            using (var writer = index.CreateWriter(heapBytes: 15_000_000, threadCount: 1))
            {
                for (int i = 0; i < 200; i++)
                    writer.AddDocument($$"""{"id": {{i}}, "body": "round {{round}} doc {{i}} lorem ipsum", "cat": "/c{{i % 4}}"}""");
                writer.Commit();
                writer.DeleteTerm("id", 3UL);
                writer.Commit();
            }
            using (var searcher = index.CreateSearcher())
            {
                var r = searcher.Search("lorem", new SearchOptions
                {
                    Limit = 20,
                    OrderBy = [SortBy.Field("id", descending: true)],
                    Snippets = new SnippetOptions("body"),
                    Facets = [new FacetRequest("cat")],
                    Aggregations = """{"ids": {"stats": {"field": "id"}}}""",
                });
                Assert.Equal(199, r.TotalCount);
                _ = searcher.Explain(Query.Parse("lorem"), r[0]);
                _ = searcher.GetStats();
            }
            using var manager = index.CreateSearcherManager(new SearcherManagerOptions { RefreshOnCommit = round % 2 == 0 });
            using (var lease = manager.Acquire())
                Assert.Equal(199, lease.Searcher.DocumentCount);
        }
        using (var reopened = SearchIndex.Open(dir.Path))
        using (var searcher = reopened.CreateSearcher())
            Assert.Equal(199, searcher.Count("ipsum"));

        // And the in-RAM path, which never touches the file system.
        using var ram = Books.InRam();
        using var ramSearcher = ram.CreateSearcher();
        Assert.Equal(2, ramSearcher.Count("mice"));
    }

    [Fact]
    public void Repeated_open_index_search_dispose_does_not_leak()
    {
        int rounds = Math.Max(60, (int)(StressTests.Duration.TotalSeconds * 15));
        for (int i = 0; i < 20; i++) Cycle(i); // warm up pools, caches and the JIT
        var before = Usage.Now();

        for (int i = 0; i < rounds; i++) Cycle(i);
        var after = Usage.Now();
        // Tantivy's worker threads exit asynchronously after Dispose, so right after the last cycle
        // a few may still be shutting down. Give them up to 5 seconds; leaked threads never exit.
        for (var settle = Stopwatch.StartNew(); after.Threads - before.Threads >= 8 && settle.Elapsed < TimeSpan.FromSeconds(5);)
        {
            Thread.Sleep(100);
            after = Usage.Now();
        }


        // Each cycle allocates tens of MB natively (a 15 MB writer arena alone), so a leak per cycle
        // would add hundreds of MB to GB; the margins only absorb allocator and runtime noise.
        Assert.True(after.ResidentBytes - before.ResidentBytes < 100 * 1_048_576, $"memory grew over {rounds} rounds: {before} -> {after}");
        // Index files are exact (none should stay open); Windows counts every handle, so it gets a margin.
        int fileMargin = OperatingSystem.IsWindows() ? 30 : 1;
        Assert.True(after.IndexFiles - before.IndexFiles < fileMargin, $"index files stayed open over {rounds} rounds: {before} -> {after}");
        Assert.True(after.Threads - before.Threads < 8, $"threads grew over {rounds} rounds: {before} -> {after}.{Usage.ThreadNames()}");
    }
}

/// <summary>Runs <see cref="LeakTests"/> alone: other tests' threads, files and allocations would be counted.</summary>
[CollectionDefinition(nameof(LeakTests), DisableParallelization = true)]
public class LeakTestsCollection;
