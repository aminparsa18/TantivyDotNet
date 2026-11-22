using System.Diagnostics;

namespace TantivyDotNet.Tests;

/// <summary>
/// Kills a child process (tests/TantivyDotNet.CrashHelper) in the middle of indexing, then checks
/// that the index opens at its last complete commit and that the writer lock was released.
/// </summary>
public class CrashTests
{
    private const int BatchSize = 2000; // must match CrashHelper

    private sealed class Helper : IDisposable
    {
        private readonly Process _process;

        public Helper(string mode, string directory)
        {
            string dll = Path.Combine(AppContext.BaseDirectory, "TantivyDotNet.CrashHelper.dll");
            // Run the dll with the same dotnet host as the tests (set by the dotnet CLI), else from PATH.
            string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet";
            var info = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add(dll);
            info.ArgumentList.Add(mode);
            info.ArgumentList.Add(directory);
            _process = Process.Start(info)!;
        }

        /// <summary>The next line the helper printed; fails if it exited instead.</summary>
        public string ReadLine()
        {
            string? line = _process.StandardOutput.ReadLine();
            if (line is null)
            {
                _process.WaitForExit();
                Assert.Fail($"helper exited with {_process.ExitCode}: {_process.StandardError.ReadToEnd()}");
            }
            return line;
        }

        public void Kill()
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }

        public void Dispose()
        {
            if (!_process.HasExited) Kill();
            _process.Dispose();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(25)]
    [InlineData(80)]
    public void An_index_survives_a_process_killed_during_commits(int delayMs)
    {
        using var dir = new TempDirectory();
        long lastReported;
        using (var helper = new Helper("commit-loop", dir.Path))
        {
            helper.ReadLine();
            lastReported = long.Parse(helper.ReadLine().Split(' ')[1]);
            // The helper is now adding the next batch or committing it.
            Thread.Sleep(delayMs);
            helper.Kill();
        }

        using var index = SearchIndex.Open(dir.Path);
        long count;
        using (var searcher = index.CreateSearcher()) count = searcher.DocumentCount;
        // Commits are all-or-nothing: a whole number of batches, at least the last one reported.
        Assert.Equal(0, count % BatchSize);
        Assert.True(count >= lastReported, $"{count} < {lastReported}");

        // The killed process held the writer lock; the OS released it with the process.
        using (var writer = index.CreateWriter())
        {
            writer.AddDocument("""{"id": "after", "body": "after the crash"}""");
            writer.Commit();
            // Files the crashed process left behind (an unfinished segment) are unused, and can be deleted.
            writer.GarbageCollectFiles();
        }
        using var after = index.CreateSearcher();
        Assert.Equal(count + 1, after.DocumentCount);
        Assert.Equal(1, after.Count(Query.Term("id", "after")));
    }

    [Fact]
    public void A_killed_process_releases_the_writer_lock_and_loses_uncommitted_documents()
    {
        using var dir = new TempDirectory();
        using (var created = SearchIndex.Create(dir.Path, new SchemaBuilder().AddStringField("id", stored: true).AddTextField("body", stored: true).Build()))
        using (var writer = created.CreateWriter())
        {
            writer.AddDocument("""{"id": "committed", "body": "before"}""");
            writer.Commit();
        }

        using var index = SearchIndex.Open(dir.Path);
        using (var helper = new Helper("hold-writer", dir.Path))
        {
            Assert.Equal("ready", helper.ReadLine());
            var busy = Assert.Throws<TantivyException>(() => index.CreateWriter().Dispose());
            Assert.Equal(TantivyErrorCode.LockBusy, busy.Code);
            helper.Kill();
        }

        using (var writer = index.CreateWriter()) { }
        using var searcher = index.CreateSearcher();
        Assert.Equal(1, searcher.DocumentCount);
    }
}
