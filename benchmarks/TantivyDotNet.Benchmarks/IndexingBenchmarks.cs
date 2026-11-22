using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace TantivyDotNet.Benchmarks;

/// <summary>
/// Time to index the whole corpus into a new on-disk index, commit, and close the writer (which waits
/// for background merges in both engines). The articles are already in memory as objects; turning them
/// into each engine's documents (JSON for TantivyDotNet, Field objects for Lucene) is part of the time.
/// </summary>
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 5)]
public class IndexingBenchmarks
{
    private List<Article> _articles = null!;
    private string _dir = null!;

    /// <summary>Indexing threads: Tantivy's writer threads, or Lucene's calling threads.</summary>
    [Params(1, 4)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup() => _articles = Corpus.Load();

    [IterationSetup]
    public void IterationSetup() => _dir = Engines.NewDir($"indexing-{Guid.NewGuid():N}");

    [IterationCleanup]
    public void IterationCleanup() => Engines.DeleteDir(_dir);

    [Benchmark(Baseline = true, Description = "Lucene.NET")]
    public void Lucene() => Engines.BuildLucene(_dir, _articles, Threads);

    [Benchmark(Description = "TantivyDotNet")]
    public void Tantivy() => Engines.BuildTantivy(_dir, _articles, Threads);
}
