namespace TantivyDotNet.Tests;

public class SnippetTests
{
    private static SearchIndex Articles(params string[] bodies)
    {
        var schema = new SchemaBuilder()
            .AddStringField("id", stored: true)
            .AddTextField("title", stored: true)
            .AddTextField("body", stored: true)
            .AddU64Field("n", stored: true)
            .Build();
        var index = SearchIndex.CreateInRam(schema);
        using var writer = index.CreateWriter();
        for (int i = 0; i < bodies.Length; i++)
            writer.AddDocument(new Dictionary<string, object> { ["id"] = $"a{i}", ["title"] = $"Article {i}", ["body"] = bodies[i], ["n"] = i });
        writer.Commit();
        return index;
    }

    private static string[] Marked(Snippet s) => [.. s.Highlights.Select(r => s.Fragment[r])];

    [Fact]
    public void Snippet_HighlightsMatchedTerms()
    {
        using var index = Articles("George and Lennie dream of a farm with rabbits and mice");
        using var searcher = index.CreateSearcher();

        var hit = Assert.Single(searcher.Search(Query.Term("body", "mice"), new SearchOptions { Snippets = new SnippetOptions("body") }));
        var s = hit.Snippets["body"];
        Assert.False(s.IsEmpty);
        Assert.Contains("rabbits and mice", s.Fragment);
        Assert.Equal(["mice"], Marked(s));
        Assert.EndsWith("rabbits and <b>mice</b>", s.ToHtml());
        Assert.EndsWith("<mark>mice</mark>", s.ToHtml("<mark>", "</mark>"));
        Assert.Equal(s.Fragment, s.ToString());
    }

    [Fact]
    public void Snippet_ParsedAndPhraseQueries_HighlightEveryTermInOrder()
    {
        using var index = Articles("George and Lennie dream of a farm with rabbits and mice");
        using var searcher = index.CreateSearcher();

        var s = Assert.Single(searcher.Search("mice OR George OR rabbits", new SearchOptions { Snippets = new SnippetOptions("body") })).Snippets["body"];
        Assert.Equal(["George", "rabbits", "mice"], Marked(s));               // original case, sorted by position
        Assert.True(s.Highlights.Zip(s.Highlights.Skip(1)).All(p => p.First.End.Value <= p.Second.Start.Value));

        s = Assert.Single(searcher.Search(Query.Phrase("body", "Lennie dream"), new SearchOptions { Snippets = new SnippetOptions("body") })).Snippets["body"];
        Assert.Equal(["Lennie", "dream"], Marked(s));
    }

    [Fact]
    public void Snippet_OffsetsAreUtf16_ForNonAsciiText()
    {
        // Multi-byte UTF-8 (é, Cyrillic, CJK) and a surrogate pair (😀) before the matches.
        using var index = Articles("Café 😀 Преступление и наказание 東京 naïve mice résumé");
        using var searcher = index.CreateSearcher();

        var s = Assert.Single(searcher.Search("mice наказание résumé", new SearchOptions { Snippets = new SnippetOptions("body") })).Snippets["body"];
        Assert.Equal(["наказание", "mice", "résumé"], Marked(s));
        Assert.Contains("😀 Преступление и <b>наказание</b> 東京 naïve <b>mice</b> <b>résumé</b>", s.ToHtml());
    }

    [Fact]
    public void Snippet_MaxLengthPicksTheBestShortFragment()
    {
        string filler = string.Join(' ', Enumerable.Repeat("lorem ipsum dolor", 40));
        using var index = Articles($"{filler} the rabbits ran {filler}");
        using var searcher = index.CreateSearcher();

        var s = Assert.Single(searcher.Search(Query.Term("body", "rabbits"), new SearchOptions { Snippets = new SnippetOptions("body") { MaxLength = 40 } })).Snippets["body"];
        Assert.InRange(s.Fragment.Length, 7, 40);
        Assert.Equal(["rabbits"], Marked(s));

        var longer = Assert.Single(searcher.Search(Query.Term("body", "rabbits"), new SearchOptions { Snippets = new SnippetOptions("body") })).Snippets["body"];
        Assert.True(longer.Fragment.Length > 40);
        Assert.InRange(longer.Fragment.Length, 41, 150);
    }

    [Fact]
    public void Snippet_SeveralFields_AndEmptyWhenFieldDoesNotMatch()
    {
        using var index = Articles("mice everywhere", "no rodents here");
        using var searcher = index.CreateSearcher();

        var results = searcher.Search(Query.Term("body", "mice") | Query.Term("title", "1"), new SearchOptions { Snippets = new SnippetOptions("title", "body") });
        var byId = results.ToDictionary(h => h.Document.GetString("id")!);

        Assert.Equal(["mice"], Marked(byId["a0"].Snippets["body"]));
        Assert.True(byId["a0"].Snippets["title"].IsEmpty);
        Assert.Equal("", byId["a0"].Snippets["title"].Fragment);
        Assert.Equal("", byId["a0"].Snippets["title"].ToHtml());
        Assert.Equal(["1"], Marked(byId["a1"].Snippets["title"]));
        Assert.True(byId["a1"].Snippets["body"].IsEmpty);
    }

    [Fact]
    public void Snippet_NotRequested_IsEmptyDictionary()
    {
        using var index = Articles("mice");
        using var searcher = index.CreateSearcher();
        Assert.Empty(Assert.Single(searcher.Search("mice")).Snippets);
    }

    [Fact]
    public void Snippet_HtmlEncodesFragmentButNotMarkers()
    {
        using var index = Articles("Tom & Jerry <script>alert('x')</script> \"mice\"");
        using var searcher = index.CreateSearcher();

        var s = Assert.Single(searcher.Search("mice", new SearchOptions { Snippets = new SnippetOptions("body") })).Snippets["body"];
        Assert.Equal("Tom &amp; Jerry &lt;script&gt;alert(&#x27;x&#x27;)&lt;/script&gt; &quot;<em>mice</em>", s.ToHtml("<em>", "</em>"));
    }

    [Fact]
    public void Snippet_FuzzyAndRangeQueries_HaveNoHighlights()
    {
        // Pins a Tantivy limitation documented on Snippet: these queries don't report their terms.
        using var index = Articles("mice everywhere");
        using var searcher = index.CreateSearcher();

        Assert.True(Assert.Single(searcher.Search(Query.Fuzzy("body", "mise"), new SearchOptions { Snippets = new SnippetOptions("body") })).Snippets["body"].IsEmpty);
        Assert.True(Assert.Single(searcher.Search(Query.Range("n", 0, 5), new SearchOptions { Snippets = new SnippetOptions("body") })).Snippets["body"].IsEmpty);
    }

    [Fact]
    public void Snippet_InvalidFields_Throw()
    {
        using var books = Books.InRam();                // body is indexed but not stored
        using var searcher = books.CreateSearcher();

        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => searcher.Search("mice", new SearchOptions { Snippets = new SnippetOptions("body") })).Code);
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => searcher.Search("mice", new SearchOptions { Snippets = new SnippetOptions("year") })).Code);
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => searcher.Search("mice", new SearchOptions { Snippets = new SnippetOptions("nope") })).Code);
        // Validated even when nothing matches.
        Assert.Throws<TantivyException>(() => searcher.Search("zebra", new SearchOptions { Snippets = new SnippetOptions("nope") }));

        Assert.Throws<ArgumentException>(() => new SnippetOptions());
        Assert.Throws<ArgumentException>(() => new SnippetOptions("title", null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnippetOptions("title") { MaxLength = 0 });
    }
}
