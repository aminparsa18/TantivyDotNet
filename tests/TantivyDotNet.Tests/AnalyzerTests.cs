namespace TantivyDotNet.Tests;

public class AnalyzerTests
{
    private static readonly Analyzer English =
        Analyzer.Simple().LowerCase().AsciiFolding().StopWords(Language.English).Stem(Language.English).RemoveLong(40);

    private static Schema ArticleSchema() => new SchemaBuilder()
        .AddAnalyzer("en_text", English)
        .AddAnalyzer("prefix", Analyzer.NGram(1, 10, prefixOnly: true).LowerCase())
        .AddStringField("id", stored: true)
        .AddTextField("body", stored: true, tokenizer: "en_text")
        .AddTextField("title", stored: true, tokenizer: "prefix")
        .Build();

    private static void AddArticles(IndexWriter writer)
    {
        writer.AddDocument("""{"id": "a1", "title": "Grapes", "body": "The CAFÉS were running out of grapes"}""");
        writer.AddDocument("""{"id": "a2", "title": "Gravity", "body": "Apples fall"}""");
        writer.Commit();
    }

    [Fact]
    public void Analyze_AppliesTheFilterChain()
    {
        using var index = SearchIndex.CreateInRam(ArticleSchema());
        // Tantivy's English stop list is Lucene's short one: "the" and "of" go, "were" and "out" stay.
        Assert.Equal(["cafe", "were", "run", "out", "grape"], index.Analyze("en_text", "The CAFÉS were running out of grapes!"));
        Assert.Equal(["g", "gr", "gra"], index.Analyze("prefix", "Gra"));
        Assert.Equal(["the", "cafés"], index.Analyze("default", "The Cafés"));
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => index.Analyze("nope", "x")).Code);
    }

    [Fact]
    public void CustomAnalyzers_AreUsedForIndexingAndQueries()
    {
        using var index = SearchIndex.CreateInRam(ArticleSchema());
        using (var writer = index.CreateWriter()) AddArticles(writer);
        using var searcher = index.CreateSearcher();

        // Stemming + folding on both sides: "cafe run" matches "CAFÉS were running".
        Assert.Equal(["a1"], Books.Ids(searcher.Search("body:cafe AND body:run")));
        Assert.Equal(["a1"], Books.Ids(searcher.Search(Query.Phrase("body", "Cafés running", slop: 1))));
        // Stop words are gone from the index.
        Assert.Equal(0, searcher.Count("body:the"));
        // Edge n-grams: search-as-you-type on the title.
        Assert.Equal(["a1", "a2"], Books.Ids(searcher.Search(Query.Term("title", "gra"))));
        Assert.Equal(["a1"], Books.Ids(searcher.Search(Query.Term("title", "grap"))));
    }

    [Fact]
    public void Analyzers_ArePersistedWithAnOnDiskIndex()
    {
        using var dir = new TempDirectory();
        using (var index = SearchIndex.Create(dir.Path, ArticleSchema()))
        using (var writer = index.CreateWriter())
            AddArticles(writer);

        using var reopened = SearchIndex.Open(dir.Path);
        Assert.Equal(English, reopened.Schema.Analyzers["en_text"]);
        Assert.Equal(Analyzer.NGram(1, 10, prefixOnly: true).LowerCase(), reopened.Schema.Analyzers["prefix"]);
        Assert.Equal(2, reopened.Schema.Analyzers.Count);
        Assert.Equal(["cafe"], reopened.Analyze("en_text", "Cafés"));

        // A new writer after reopening indexes with the same analyzers.
        using (var writer = reopened.CreateWriter())
        {
            writer.AddDocument("""{"id": "a3", "title": "Graphs", "body": "Running in circles"}""");
            writer.Commit();
        }
        using var searcher = reopened.CreateSearcher();
        Assert.Equal(["a1", "a3"], Books.Ids(searcher.Search("body:runs")));
    }

    [Fact]
    public void IndexWithoutCustomAnalyzers_WritesNoExtraFile()
    {
        using var dir = new TempDirectory();
        using (SearchIndex.Create(dir.Path, Books.Schema())) { }
        Assert.False(File.Exists(Path.Combine(dir.Path, "tantivy-dotnet.json")));
        using var reopened = SearchIndex.Open(dir.Path);
        Assert.Empty(reopened.Schema.Analyzers);
    }

    [Fact]
    public void CustomStopWordList_Regex_AndWhitespace()
    {
        using var index = SearchIndex.CreateInRam(new SchemaBuilder()
            .AddAnalyzer("tags", Analyzer.Regex("[a-z0-9#+-]+").StopWords(["and", "or"]))
            .AddAnalyzer("ws", Analyzer.Whitespace().LowerCase())
            .AddAnalyzer("exact_ci", Analyzer.Raw().LowerCase())
            .AddTextField("t", tokenizer: "tags")
            .Build());
        Assert.Equal(["c#", "c++", "rust"], index.Analyze("tags", "c# and c++ or rust"));
        Assert.Equal(["hello,", "world!"], index.Analyze("ws", "Hello, World!"));
        Assert.Equal(["new york"], index.Analyze("exact_ci", "New York"));
    }

    [Fact]
    public void SchemaBuilder_ValidatesAnalyzerNames()
    {
        Assert.Throws<ArgumentException>(() => new SchemaBuilder().AddAnalyzer("default", Analyzer.Simple()));
        Assert.Throws<ArgumentException>(() => new SchemaBuilder().AddAnalyzer("a", Analyzer.Simple()).AddAnalyzer("a", Analyzer.Raw()));
        var e = Assert.Throws<InvalidOperationException>(() => new SchemaBuilder().AddTextField("body", tokenizer: "missing").Build());
        Assert.Contains("missing", e.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => Analyzer.NGram(3, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => Analyzer.Simple().RemoveLong(0));
    }

    [Fact]
    public void InvalidDefinitions_FailWhenTheIndexIsCreated()
    {
        var badRegex = new SchemaBuilder().AddAnalyzer("r", Analyzer.Regex("([a-z")).AddTextField("t", tokenizer: "r").Build();
        var e = Assert.Throws<TantivyException>(() => SearchIndex.CreateInRam(badRegex));
        Assert.Equal(TantivyErrorCode.InvalidArgument, e.Code);
        Assert.Contains("analyzer 'r'", e.Message);

        var noStopList = new SchemaBuilder().AddAnalyzer("tr", Analyzer.Simple().StopWords(Language.Turkish)).AddTextField("t").Build();
        Assert.Contains("stop-word", Assert.Throws<TantivyException>(() => SearchIndex.CreateInRam(noStopList)).Message);
    }

    [Fact]
    public void Analyzer_EqualityAndDescription()
    {
        Assert.Equal(Analyzer.Simple().LowerCase(), Analyzer.Simple().LowerCase());
        Assert.NotEqual(Analyzer.Simple().LowerCase(), Analyzer.Simple());
        Assert.Equal(Analyzer.Simple().LowerCase().GetHashCode(), Analyzer.Simple().LowerCase().GetHashCode());
        Assert.Equal("simple | lowercase | ascii_folding | stop_words(english) | stem(english) | remove_long(40)", English.ToString());
    }
}
