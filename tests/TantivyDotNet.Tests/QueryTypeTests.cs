namespace TantivyDotNet.Tests;

public class QueryTypeTests
{
    private static SearchIndex WithExtraDocument()
    {
        var index = Books.InRam();
        using var writer = index.CreateWriter();
        writer.AddDocument("""{"id": "b5", "title": "Untitled draft", "body": "no year here"}""");
        writer.Commit();
        return index;
    }

    [Fact]
    public void Regex_MatchesWholeTerms()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        Assert.Equal(["b2"], Books.Ids(searcher.Search(Query.Regex("title", "gr.*s"))));
        Assert.Equal(["b1", "b4"], Books.Ids(searcher.Search(Query.Regex("body", "mic?e"))));
        Assert.Empty(searcher.Search(Query.Regex("title", "gr")));   // anchored
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => searcher.Search(Query.Regex("title", "("))).Code);
        Assert.Throws<TantivyException>(() => searcher.Search(Query.Regex("year", "1.*")));
    }

    [Fact]
    public void Exists_MatchesDocumentsWithAValue()
    {
        using var index = WithExtraDocument();
        using var searcher = index.CreateSearcher();
        Assert.Equal(["b1", "b2", "b3", "b4"], Books.Ids(searcher.Search(Query.Exists("year"))));
        Assert.Equal(["b5"], Books.Ids(searcher.Search(!Query.Exists("year"))));
        var notFast = Assert.Throws<TantivyException>(() => searcher.Search(Query.Exists("title")));
        Assert.Contains("fast", notFast.Message);
    }

    [Fact]
    public void TermSet_MatchesAnyValue()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        Assert.Equal(["b1", "b3"], Books.Ids(searcher.Search(Query.TermSet("id", ["b1", "b3", "zz"]))));
        Assert.Equal(["b2", "b4"], Books.Ids(searcher.Search(Query.TermSet("year", [1939UL, 1945UL]))));
        Assert.Equal(["b2"], Books.Ids(searcher.Search(Query.TermSet("year", [1939L]))));
        Assert.Empty(searcher.Search(Query.TermSet("id", Array.Empty<string>())));
        Assert.Throws<TantivyException>(() => searcher.Search(Query.TermSet("year", ["x"])));
    }

    [Fact]
    public void DisjunctionMax_ScoresByTheBestClause()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        var title = Query.Phrase("title", "mice");
        var body = Query.Phrase("body", "mice");
        var onlyB1 = Query.Term("id", "b1").ConstantScore(0);   // filter without adding to the score
        float titleScore = searcher.Search(title & onlyB1)[0].Score;
        float bodyScore = searcher.Search(body & onlyB1)[0].Score;

        var max = searcher.Search(Query.DisjunctionMax([title, body]) & onlyB1)[0];
        Assert.Equal(Math.Max(titleScore, bodyScore), max.Score, 3);
        var half = searcher.Search(Query.DisjunctionMax([title, body], tieBreaker: 0.5f) & onlyB1)[0];
        Assert.Equal(Math.Max(titleScore, bodyScore) + 0.5f * Math.Min(titleScore, bodyScore), half.Score, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => Query.DisjunctionMax([title], 1.5f));
    }

    [Fact]
    public void ConstantScore_GivesEveryMatchTheSameScore()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        var results = searcher.Search(Query.Parse("mice").ConstantScore(2.5f));
        Assert.Equal(2, results.Count);
        Assert.All(results, h => Assert.Equal(2.5f, h.Score));
        Assert.Throws<ArgumentOutOfRangeException>(() => Query.All.ConstantScore(float.NaN));
    }

    [Fact]
    public void MoreLikeThis_FromTextAndFromAHit()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        var options = new MoreLikeThisOptions { MinDocFrequency = 1, MinTermFrequency = 1 };

        var fromText = searcher.Search(Query.MoreLikeThis(new Dictionary<string, string> { ["body"] = "mice and frogs at a party" }, options));
        Assert.Equal("b4", fromText[0].Document.GetString("id"));
        Assert.Contains("b1", Books.Ids(fromText));

        var b2 = searcher.Search(Query.Term("id", "b2"))[0];
        var similar = searcher.Search(Query.MoreLikeThis(b2.Document, options) & !Query.Term("id", "b2"));
        Assert.DoesNotContain("b2", Books.Ids(similar));
        Assert.NotEmpty(similar);   // shares "of" with other titles

        var empty = Assert.Throws<TantivyException>(() => searcher.Search(Query.MoreLikeThis(new Dictionary<string, string>())));
        Assert.Equal(TantivyErrorCode.InvalidArgument, empty.Code);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MoreLikeThisOptions { MaxQueryTerms = -1 });
    }
}
