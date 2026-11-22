namespace TantivyDotNet.Tests;

public class QueryParserOptionsTests
{
    [Fact]
    public void DefaultOperator_And_RequiresEveryTerm()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        Assert.Equal(["b1", "b4"], Books.Ids(searcher.Search(Query.Parse("mice men"))));
        Assert.Equal(["b1"], Books.Ids(searcher.Search(Query.Parse("mice men", new QueryParserOptions { DefaultOperator = QueryOperator.And }))));
    }

    [Fact]
    public void FieldBoosts_ScaleScores()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        float plain = searcher.Search(Query.Parse("body:mice"))[0].Score;
        float boosted = searcher.Search(Query.Parse("body:mice", new QueryParserOptions { FieldBoosts = new Dictionary<string, float> { ["body"] = 2.5f } }))[0].Score;
        Assert.Equal(plain * 2.5f, boosted, 3);
    }

    [Fact]
    public void FuzzyFields_MatchMisspellings()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        var fuzzy = new QueryParserOptions
        {
            DefaultFields = ["body"],
            FuzzyFields = new Dictionary<string, FuzzyOptions> { ["body"] = new FuzzyOptions(distance: 1) },
        };
        Assert.Equal(0, searcher.Count(Query.Parse("mise", ["body"])));
        Assert.Equal(["b1", "b4"], Books.Ids(searcher.Search(Query.Parse("mise", fuzzy))));

        var prefix = new QueryParserOptions { FuzzyFields = new Dictionary<string, FuzzyOptions> { ["title"] = new(distance: 0, prefix: true) } };
        Assert.Equal(["b2"], Books.Ids(searcher.Search(Query.Parse("title:grap", prefix))));
    }

    [Fact]
    public void Lenient_DropsWhatItCantParse()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        Assert.Throws<QueryParseException>(() => searcher.Search(Query.Parse("title:(mice")));
        Assert.Throws<QueryParseException>(() => searcher.Search(Query.Parse("nope:x mice")));

        var lenient = new QueryParserOptions { Lenient = true };
        Assert.Contains("b1", Books.Ids(searcher.Search(Query.Parse("title:(mice", lenient))));
        Assert.Equal(["b1", "b4"], Books.Ids(searcher.Search(Query.Parse("nope:x mice", lenient))));
    }

    [Fact]
    public void InvalidOptions_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FuzzyOptions(distance: 3));
        Assert.Throws<ArgumentException>(() => new QueryParserOptions { FieldBoosts = new Dictionary<string, float> { ["a"] = float.NaN } });
        Assert.Throws<ArgumentException>(() => new QueryParserOptions { DefaultFields = [null!] });

        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        var numericFuzzy = new QueryParserOptions { FuzzyFields = new Dictionary<string, FuzzyOptions> { ["year"] = new() } };
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => searcher.Search(Query.Parse("x", numericFuzzy))).Code);
        var unknownBoost = new QueryParserOptions { FieldBoosts = new Dictionary<string, float> { ["nope"] = 2 } };
        Assert.Throws<TantivyException>(() => searcher.Search(Query.Parse("x", unknownBoost)));
    }

    [Fact]
    public void ParsedQuery_ExposesItsOptions()
    {
        var options = new QueryParserOptions { DefaultFields = ["title"], Lenient = true };
        var q = Query.Parse("x", options);
        Assert.Same(options, q.Options);
        Assert.Equal(["title"], q.DefaultFields!);
        Assert.Null(Query.Parse("x").DefaultFields);
    }
}
