namespace TantivyDotNet.Tests;

public class FieldsAndExplainTests
{
    [Fact]
    public void Fields_ReturnsOnlyTheRequestedStoredFields()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();

        var hit = Assert.Single(searcher.Search(Query.Term("id", "b1"), new SearchOptions { Fields = ["title"] }));
        Assert.Equal("Of Mice and Men", hit.Document.GetString("title"));
        Assert.Null(hit.Document.GetString("id"));
        Assert.Null(hit.Document.GetU64("year"));

        var all = Assert.Single(searcher.Search(Query.Term("id", "b1")));
        Assert.Equal(1937UL, all.Document.GetU64("year"));
    }

    [Fact]
    public void Fields_Empty_ReturnsScoresWithoutDocuments()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        var results = searcher.Search(Query.Parse("mice"), new SearchOptions { Fields = [] });
        Assert.Equal(2, results.Count);
        Assert.All(results, h =>
        {
            Assert.True(h.Score > 0);
            Assert.Equal("{}", h.Document.ToJson());
        });
    }

    [Fact]
    public void Fields_StillAllowSnippetsOfOtherFields()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        var hit = Assert.Single(searcher.Search(Query.Phrase("title", "grapes"),
            new SearchOptions { Fields = ["id"], Snippets = new SnippetOptions("title") }));
        Assert.Equal("b2", hit.Document.GetString("id"));
        Assert.Null(hit.Document.GetString("title"));
        Assert.Equal("The <b>Grapes</b> of Wrath", hit.Snippets["title"].ToHtml());
    }

    [Fact]
    public void Fields_UnknownOrNotStored_Throw()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        var notStored = Assert.Throws<TantivyException>(() => searcher.Search(Query.All, new SearchOptions { Fields = ["body"] }));
        Assert.Equal(TantivyErrorCode.InvalidArgument, notStored.Code);
        Assert.Contains("isn't stored", notStored.Message);
        Assert.Throws<TantivyException>(() => searcher.Search(Query.All, new SearchOptions { Fields = ["nope"] }));
        Assert.Throws<ArgumentException>(() => new SearchOptions { Fields = [null!] });
    }

    [Fact]
    public void Explain_DescribesTheScore()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        var query = Query.Parse("body:mice title:men");
        var results = searcher.Search(query);
        var top = results[0];
        Assert.Equal("b1", top.Document.GetString("id"));

        var explanation = searcher.Explain(query, top);
        Assert.Equal(top.Score, explanation.Value, 4);
        Assert.NotEmpty(explanation.Details);
        string text = explanation.ToString();
        Assert.Contains("TermQuery", text);
        Assert.Contains("idf", text);
        Assert.Contains(Environment.NewLine + "  ", text);   // indented children
    }

    [Fact]
    public void Explain_RejectsForeignHitsAndNonMatchingQueries()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        using var other = index.CreateSearcher();
        var hit = searcher.Search(Query.Term("id", "b1"))[0];

        Assert.Throws<ArgumentException>(() => other.Explain(Query.Term("id", "b1"), hit));
        var e = Assert.Throws<TantivyException>(() => searcher.Explain(Query.Term("id", "b2"), hit));
        Assert.Equal(TantivyErrorCode.InvalidArgument, e.Code);
    }
}
