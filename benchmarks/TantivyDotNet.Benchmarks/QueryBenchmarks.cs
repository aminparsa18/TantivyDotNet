using BenchmarkDotNet.Attributes;
using Lucene.Net.Index;
using Lucene.Net.Search;
using LuceneQuery = Lucene.Net.Search.Query;
using LuceneTerm = Lucene.Net.Index.Term;
using LuceneTermQuery = Lucene.Net.Search.TermQuery;
using LuceneBooleanQuery = Lucene.Net.Search.BooleanQuery;
using LucenePhraseQuery = Lucene.Net.Search.PhraseQuery;

namespace TantivyDotNet.Benchmarks;

public enum QueryKind
{
    /// <summary>One common term.</summary>
    Term,
    /// <summary>Two terms, both required.</summary>
    And,
    /// <summary>Two terms, either one.</summary>
    Or,
    /// <summary>A two-word phrase.</summary>
    Phrase,
    /// <summary>One common term, top 10 by a numeric column (incoming links) instead of relevance.</summary>
    SortedByField,
}

/// <summary>
/// Latency of one search on an already-open index (single-threaded): the top 10 hits, with the total
/// match count and each hit's stored fields loaded. Queries are built in code once; TantivyDotNet
/// still serializes its query to JSON on every call, which is part of what's measured. The indexes are
/// built once by <see cref="SharedIndexes"/> and reused.
/// </summary>
[MemoryDiagnoser]
public class QueryBenchmarks
{
    private DirectoryReader _luceneReader = null!;
    private IndexSearcher _luceneSearcher = null!;
    private Lucene.Net.Store.Directory _luceneDir = null!;
    private LuceneQuery _luceneQuery = null!;
    private Sort? _luceneSort;

    private SearchIndex _tantivyIndex = null!;
    private Searcher _tantivySearcher = null!;
    private Query _tantivyQuery = null!;
    private SearchOptions _tantivyOptions = null!;

    [Params(QueryKind.Term, QueryKind.And, QueryKind.Or, QueryKind.Phrase, QueryKind.SortedByField)]
    public QueryKind Kind { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var (tantivyDir, luceneDir) = SharedIndexes.Ensure();

        _luceneDir = Engines.OpenLucene(luceneDir);
        _luceneReader = DirectoryReader.Open(_luceneDir);
        _luceneSearcher = new IndexSearcher(_luceneReader);
        _tantivyIndex = SearchIndex.Open(tantivyDir);
        _tantivySearcher = _tantivyIndex.CreateSearcher();
        _tantivyOptions = new SearchOptions { Limit = 10 };

        static LuceneTerm T(string text) => new("body", text);
        switch (Kind)
        {
            case QueryKind.Term or QueryKind.SortedByField:
                _luceneQuery = new LuceneTermQuery(T("science"));
                _tantivyQuery = Query.Term("body", "science");
                if (Kind == QueryKind.SortedByField)
                {
                    _luceneSort = new Sort(new SortField("links", SortFieldType.INT64, reverse: true));
                    _tantivyOptions = new SearchOptions { Limit = 10, OrderBy = SortBy.Field("links", descending: true) };
                }
                break;
            case QueryKind.And:
                _luceneQuery = new LuceneBooleanQuery { { new LuceneTermQuery(T("american")), Occur.MUST }, { new LuceneTermQuery(T("football")), Occur.MUST } };
                _tantivyQuery = Query.Term("body", "american") & Query.Term("body", "football");
                break;
            case QueryKind.Or:
                _luceneQuery = new LuceneBooleanQuery { { new LuceneTermQuery(T("american")), Occur.SHOULD }, { new LuceneTermQuery(T("football")), Occur.SHOULD } };
                _tantivyQuery = Query.Term("body", "american") | Query.Term("body", "football");
                break;
            case QueryKind.Phrase:
                _luceneQuery = new LucenePhraseQuery { T("united"), T("states") };
                _tantivyQuery = Query.Phrase("body", "united states");
                break;
        }

        // Sanity check: both engines must agree on the number of matches.
        var lucene = LuceneSearch();
        var tantivy = TantivySearch();
        if (lucene.Total != tantivy.Total)
            Console.WriteLine($"// WARNING {Kind}: Lucene matched {lucene.Total}, Tantivy {tantivy.Total}");
        else
            Console.WriteLine($"// {Kind}: {lucene.Total} matches in both engines");
    }

    /// <summary>Ids of all articles that only one engine matches (call after <see cref="Setup"/>).</summary>
    public (List<string> OnlyLucene, List<string> OnlyTantivy) DiffMatches()
    {
        var lucene = _luceneSearcher.Search(_luceneQuery, Math.Max(1, _luceneReader.MaxDoc)).ScoreDocs
            .Select(d => _luceneSearcher.Doc(d.Doc).Get("id")).ToHashSet();
        var tantivy = _tantivySearcher.Search(_tantivyQuery, limit: (int)_tantivySearcher.DocumentCount)
            .Select(h => h.Document.GetString("id")!).ToHashSet();
        return (lucene.Except(tantivy).ToList(), tantivy.Except(lucene).ToList());
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _luceneReader.Dispose();
        _luceneDir.Dispose();
        _tantivySearcher.Dispose();
        _tantivyIndex.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Lucene.NET")]
    public (int Total, int Chars) LuceneSearch()
    {
        var top = _luceneSort is null
            ? _luceneSearcher.Search(_luceneQuery, 10)
            : _luceneSearcher.Search(_luceneQuery, null, 10, _luceneSort);
        var chars = 0;
        foreach (var hit in top.ScoreDocs)
            chars += _luceneSearcher.Doc(hit.Doc).Get("title").Length;
        return (top.TotalHits, chars);
    }

    [Benchmark(Description = "TantivyDotNet")]
    public (int Total, int Chars) TantivySearch()
    {
        var results = _tantivySearcher.Search(_tantivyQuery, _tantivyOptions);
        var chars = 0;
        foreach (var hit in results)
            chars += hit.Document.GetString("title")!.Length;
        return ((int)results.TotalCount, chars);
    }
}

/// <summary>
/// The indexes the query benchmarks search: built once per corpus file with 4 threads, kept under
/// artifacts/bench and rebuilt when the corpus changes.
/// </summary>
public static class SharedIndexes
{
    public static (string Tantivy, string Lucene) Ensure()
    {
        var root = Path.Combine(Corpus.WorkDir, "shared");
        var tantivy = Path.Combine(root, "tantivy");
        var lucene = Path.Combine(root, "lucene");
        var stamp = Path.Combine(root, "corpus.stamp");
        var corpus = new FileInfo(Corpus.FilePath);
        var expected = $"{corpus.FullName}|{corpus.Length}|{corpus.LastWriteTimeUtc:O}";
        if (File.Exists(stamp) && File.ReadAllText(stamp) == expected)
            return (tantivy, lucene);

        Console.WriteLine("// Building the shared indexes...");
        Engines.DeleteDir(root);
        var articles = Corpus.Load();
        Engines.BuildTantivy(tantivy, articles, threads: 4);
        Engines.BuildLucene(lucene, articles, threads: 4);
        File.WriteAllText(stamp, expected);
        return (tantivy, lucene);
    }
}
