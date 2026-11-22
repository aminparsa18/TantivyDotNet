using Lucene.Net.Analysis.Standard;
using Lucene.Net.Analysis.Util;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Lucene.Net.Util;
using LuceneDocument = Lucene.Net.Documents.Document;
using LuceneDirectory = Lucene.Net.Store.Directory;
using LuceneIndexWriter = Lucene.Net.Index.IndexWriter;

namespace TantivyDotNet.Benchmarks;

/// <summary>
/// Builds the same index with both engines. Both use: id (exact, stored), title and body (tokenized,
/// stored), links (a column for sorting: a Tantivy fast field / a Lucene NumericDocValues field).
/// Analysis is as close as the two allow without custom code: Tantivy's default tokenizer (split on
/// non-alphanumerics, lowercase, drop tokens over 40 bytes) and Lucene's StandardAnalyzer with no stop
/// words (Unicode word breaks, lowercase). Neither stems.
/// </summary>
public static class Engines
{
    /// <summary>Indexing memory budget, the same for both: Tantivy's writer heap, Lucene's RAM buffer.</summary>
    public const int IndexingBufferMB = 256;

    public static Schema TantivySchema { get; } = new SchemaBuilder()
        .AddStringField("id", stored: true)
        .AddTextField("title", stored: true)
        .AddTextField("body", stored: true)
        .AddU64Field("links", indexed: false, fast: true)
        .Build();

    /// <summary>Indexes with <paramref name="threads"/> Tantivy indexing threads, fed from one thread (the writer isn't thread-safe).</summary>
    public static void BuildTantivy(string dir, List<Article> articles, int threads)
    {
        using var index = SearchIndex.Create(dir, TantivySchema);
        using var writer = index.CreateWriter(heapBytes: IndexingBufferMB * 1024L * 1024, threadCount: threads);
        foreach (var article in articles)
            writer.AddDocument(article, CorpusJson.Default.Article);
        writer.Commit();
        // Dispose waits for background merges, as Lucene's does.
    }

    public static LuceneVersion Version => LuceneVersion.LUCENE_48;

    public static StandardAnalyzer CreateAnalyzer() => new(Version, CharArraySet.Empty);

    /// <summary>
    /// Indexes from <paramref name="threads"/> threads calling <c>AddDocument</c> concurrently (Lucene
    /// indexes on the calling threads, so this is how it uses several cores).
    /// </summary>
    public static void BuildLucene(string dir, List<Article> articles, int threads)
    {
        using var directory = FSDirectory.Open(dir);
        using var analyzer = CreateAnalyzer();
        var config = new IndexWriterConfig(Version, analyzer)
        {
            OpenMode = OpenMode.CREATE,
            RAMBufferSizeMB = IndexingBufferMB,
        };
        using var writer = new LuceneIndexWriter(directory, config);
        if (threads == 1)
        {
            AddRange(writer, articles, 0, articles.Count);
        }
        else
        {
            var chunk = (articles.Count + threads - 1) / threads;
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
                AddRange(writer, articles, t * chunk, Math.Min(articles.Count, (t + 1) * chunk)));
        }
        writer.Commit();
        // Dispose waits for merges by default.
    }

    private static void AddRange(LuceneIndexWriter writer, List<Article> articles, int start, int end)
    {
        // Reuse one Document and its fields per thread, as the Lucene docs recommend for throughput.
        var id = new StringField("id", "", Field.Store.YES);
        var title = new TextField("title", "", Field.Store.YES);
        var body = new TextField("body", "", Field.Store.YES);
        var links = new NumericDocValuesField("links", 0);
        var doc = new LuceneDocument { id, title, body, links };
        for (var i = start; i < end; i++)
        {
            var a = articles[i];
            id.SetStringValue(a.Id);
            title.SetStringValue(a.Title);
            body.SetStringValue(a.Body);
            links.SetInt64Value((long)a.Links);
            writer.AddDocument(doc);
        }
    }

    public static LuceneDirectory OpenLucene(string dir) => FSDirectory.Open(dir);

    /// <summary>Returns a fresh, empty directory under artifacts/bench.</summary>
    public static string NewDir(string name)
    {
        var dir = Path.Combine(Corpus.WorkDir, name);
        DeleteDir(dir);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    public static void DeleteDir(string dir)
    {
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
    }
}
