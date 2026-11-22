namespace TantivyDotNet.Tests;

/// <summary>Queries built in code (<see cref="Query"/>), against the <see cref="Books"/> fixture unless noted.</summary>
public class QueryTests
{
    private static string?[] Ids(SearchResults results) => [.. Books.Ids(results)];

    private static string?[] RankedIds(SearchResults results) => [.. results.Select(h => h.Document.GetString("id"))];

    [Fact]
    public void Term_MatchesExactUnanalyzedTerms()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();

        Assert.Equal(["b1", "b4"], Ids(searcher.Search(Query.Term("body", "mice"))));
        Assert.Empty(searcher.Search(Query.Term("body", "Mice")));            // not lowercased for you
        Assert.Equal(["b3"], Ids(searcher.Search(Query.Term("id", "b3"))));     // raw string field
        Assert.Equal(["b2"], Ids(searcher.Search(Query.Term("year", 1939))));   // int literal on a u64 field
        Assert.Equal(["b2"], Ids(searcher.Search(Query.Term("year", 1939UL))));
        Assert.Equal(1, searcher.Count(Query.Term("title", "eden")));
    }

    [Fact]
    public void Term_TypedFields()
    {
        var schema = new SchemaBuilder()
            .AddStringField("id", stored: true)
            .AddI64Field("delta")
            .AddF64Field("price")
            .AddBoolField("active")
            .AddDateField("published")
            .AddBytesField("hash")
            .Build();
        using var index = SearchIndex.CreateInRam(schema);
        using (var writer = index.CreateWriter())
        {
            writer.AddDocument("""{"id":"a","delta":-5,"price":9.5,"active":true,"published":"2024-03-01T12:00:00Z","hash":"AQID"}""");
            writer.AddDocument("""{"id":"b","delta":7,"price":20,"active":false,"published":"2025-01-15T00:00:00Z","hash":"BAUG"}""");
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();
        string? Only(Query q) => Assert.Single(searcher.Search(q)).Document.GetString("id");

        Assert.Equal("a", Only(Query.Term("delta", -5)));
        Assert.Equal("b", Only(Query.Term("delta", 7UL)));          // u64 value on an i64 field
        Assert.Equal("a", Only(Query.Term("price", 9.5)));
        Assert.Equal("b", Only(Query.Term("price", 20)));           // integer value on an f64 field
        Assert.Equal("b", Only(Query.Term("active", false)));
        Assert.Equal("a", Only(Query.Term("published", new DateTimeOffset(2024, 3, 1, 14, 0, 0, TimeSpan.FromHours(2)))));
        Assert.Equal("b", Only(Query.Term("published", new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Unspecified))));
        Assert.Equal("a", Only(Query.Term("hash", [1, 2, 3])));
    }

    [Fact]
    public void Term_TypeMismatchAndUnknownField_ThrowInvalidArgument()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();

        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => searcher.Search(Query.Term("year", "1937"))).Code);
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => searcher.Search(Query.Term("year", -1))).Code);
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => searcher.Search(Query.Term("title", 5))).Code);
        var unknown = Assert.Throws<TantivyException>(() => searcher.Search(Query.Term("nope", "x")));
        Assert.Equal(TantivyErrorCode.InvalidArgument, unknown.Code);
        Assert.Contains("nope", unknown.Message);
        Assert.Throws<ArgumentNullException>(() => Query.Term(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => Query.Term("title", (string)null!));
    }

    [Fact]
    public void Fuzzy_DistanceTranspositionsAndPrefix()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();

        Assert.Equal(["b1", "b4"], Ids(searcher.Search(Query.Fuzzy("body", "mise"))));            // 1 substitution
        Assert.Empty(searcher.Search(Query.Fuzzy("body", "mise", distance: 0)));
        Assert.Equal(["b1", "b4"], Ids(searcher.Search(Query.Fuzzy("body", "imce"))));            // 1 transposition
        Assert.Empty(searcher.Search(Query.Fuzzy("body", "imce", transpositions: false)));        // = 2 edits
        Assert.Equal(["b1", "b4"], Ids(searcher.Search(Query.Fuzzy("body", "imce", distance: 2, transpositions: false))));
        Assert.Equal(["b1"], Ids(searcher.Search(Query.Fuzzy("body", "rabit", prefix: true))));  // "rabbits" starts with ~"rabit"
        Assert.Empty(searcher.Search(Query.Fuzzy("body", "rabit", distance: 0, prefix: true)));

        Assert.Throws<ArgumentOutOfRangeException>(() => Query.Fuzzy("body", "x", distance: 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Query.Fuzzy("body", "x", distance: -1));
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => searcher.Search(Query.Fuzzy("year", "1937"))).Code);
    }

    [Fact]
    public void Phrase_AnalyzesTextAndHonorsOrderAndSlop()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();

        Assert.Equal(["b1"], Ids(searcher.Search(Query.Phrase("title", "Of MICE"))));   // tokenized + lowercased
        Assert.Empty(searcher.Search(Query.Phrase("title", "mice of")));                // order matters
        Assert.Empty(searcher.Search(Query.Phrase("body", "george lennie")));           // "George and Lennie"
        Assert.Equal(["b1"], Ids(searcher.Search(Query.Phrase("body", "george lennie", slop: 1))));
        Assert.Equal(["b1"], Ids(searcher.Search(Query.Phrase("body", "lennie george", slop: 3))));
        Assert.Equal(["b1", "b4"], Ids(searcher.Search(Query.Phrase("body", "Mice!"))));  // one token = term query
        Assert.Empty(searcher.Search(Query.Phrase("body", " ,; ")));                     // no tokens = nothing

        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => searcher.Search(Query.Phrase("year", "1937 1939"))).Code);
        Assert.Throws<ArgumentOutOfRangeException>(() => Query.Phrase("body", "a b", slop: -1));
    }

    [Fact]
    public void Boolean_MustShouldMustNot()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();

        Assert.Equal(["b4"], Ids(searcher.Search(Query.Boolean(must: [Query.Term("body", "mice"), Query.Term("body", "frogs")]))));
        Assert.Equal(["b1", "b3"], Ids(searcher.Search(Query.Boolean(should: [Query.Term("body", "rabbits"), Query.Term("title", "eden")]))));
        Assert.Equal(["b1"], Ids(searcher.Search(Query.Boolean(must: [Query.Term("body", "mice")], mustNot: [Query.Term("body", "frogs")]))));

        // With a must clause, should clauses are optional and only change scores.
        var ranked = searcher.Search(Query.Boolean(must: [Query.Term("body", "mice")], should: [Query.Term("body", "frogs")]));
        Assert.Equal(["b4", "b1"], RankedIds(ranked));

        // Only negative clauses: everything else.
        Assert.Equal(["b2", "b3"], Ids(searcher.Search(Query.Boolean(mustNot: [Query.Term("body", "mice")]))));
        // No clauses: nothing.
        Assert.Empty(searcher.Search(Query.Boolean()));

        Assert.Throws<ArgumentException>(() => Query.Boolean(must: [null!]));
    }

    [Fact]
    public void Operators_CombineAndFlatten()
    {
        Query a = Query.Term("body", "mice"), b = Query.Term("body", "frogs"), c = Query.Term("title", "row");

        var and = Assert.IsType<BooleanQuery>(a & b & !c);
        Assert.Equal([a, b], and.Must);
        Assert.Equal([c], and.MustNot);
        Assert.Empty(and.Should);

        var or = Assert.IsType<BooleanQuery>(a | b | c);
        Assert.Equal([a, b, c], or.Should);
        Assert.Empty(or.Must);

        var mixed = Assert.IsType<BooleanQuery>((a | b) & c);        // an OR stays a nested clause
        Assert.Equal(2, mixed.Must.Count);
        Assert.IsType<BooleanQuery>(mixed.Must[0]);

        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        Assert.Equal(["b1"], Ids(searcher.Search(a & !b)));
        Assert.Equal(["b1", "b4"], Ids(searcher.Search(a | b)));
        Assert.Equal(["b2", "b3"], Ids(searcher.Search(!a)));
        Assert.Equal(["b4"], Ids(searcher.Search((a | b) & c)));
        Assert.Equal(4, searcher.Count(!Query.Boolean()));            // NOT nothing = everything
    }

    [Fact]
    public void Range_NumericBoundsAndOpenEnds()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();

        Assert.Equal(["b1", "b2"], Ids(searcher.Search(Query.Range("year", 1937, 1939))));
        Assert.Equal(["b1"], Ids(searcher.Search(Query.Range("year", 1937, 1939, includeUpper: false))));
        Assert.Equal(["b2"], Ids(searcher.Search(Query.Range("year", 1937, 1939, includeLower: false))));
        Assert.Equal(["b3", "b4"], Ids(searcher.Search(Query.Range("year", 1940, null))));
        Assert.Equal(["b1"], Ids(searcher.Search(Query.Range("year", null, 1938UL))));
        Assert.Equal(["b2", "b3", "b4"], Ids(searcher.Search(Query.Range("id", "b2", null))));   // string field

        Assert.Throws<ArgumentException>(() => Query.Range("year", (long?)null, null));
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => searcher.Search(Query.Range("year", "a", "z"))).Code);
    }

    [Fact]
    public void Range_FloatAndDateFields()
    {
        var schema = new SchemaBuilder()
            .AddStringField("id", stored: true)
            .AddF64Field("price", fast: true)
            .AddDateField("published", indexed: true)
            .Build();
        using var index = SearchIndex.CreateInRam(schema);
        using (var writer = index.CreateWriter())
        {
            writer.AddDocument("""{"id":"a","price":9.5,"published":"2024-03-01T12:00:00Z"}""");
            writer.AddDocument("""{"id":"b","price":20.25,"published":"2025-01-15T00:00:00Z"}""");
            writer.AddDocument("""{"id":"c","price":-1,"published":"2023-06-30T23:59:59Z"}""");
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();

        Assert.Equal(["a", "b"], Ids2(searcher.Search(Query.Range("price", 0.0, null))));
        Assert.Equal(["a", "c"], Ids2(searcher.Search(Query.Range("price", null, 20.25, includeUpper: false))));
        Assert.Equal(["a"], Ids2(searcher.Search(Query.Range("published", new DateTime(2024, 1, 1), new DateTime(2025, 1, 1)))));
        Assert.Equal(["b"], Ids2(searcher.Search(Query.Range("published", (DateTimeOffset?)new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero), null))));
        Assert.Equal(["c"], Ids2(searcher.Search(Query.Range("published", null, new DateTime(2024, 1, 1)))));

        static string?[] Ids2(SearchResults r) => [.. r.Select(h => h.Document.GetString("id")).Order()];
    }

    [Fact]
    public void Parse_CombinesWithBuiltQueries()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();

        Assert.Equal(["b4"], Ids(searcher.Search(Query.Parse("mice") & Query.Range("year", 1940, null))));
        Assert.Equal(["b1"], Ids(searcher.Search(Query.Parse("rabbits OR frogs", defaultFields: ["body"]) & !Query.Term("id", "b4"))));
        Assert.Empty(searcher.Search(Query.Parse("rabbits", defaultFields: ["title"])));

        Assert.Throws<QueryParseException>(() => searcher.Search(Query.Term("body", "mice") & Query.Parse("title:(unclosed")));
        Assert.Throws<ArgumentException>(() => Query.Parse("x", defaultFields: [null!]));

        // The string overload is the same thing.
        Assert.Equal(Ids(searcher.Search("mice -frogs")), Ids(searcher.Search(Query.Parse("mice -frogs"))));
    }

    [Fact]
    public void Boost_ChangesRanking()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        Query rabbits = Query.Term("body", "rabbits"), frogs = Query.Term("body", "frogs");

        Assert.Equal(["b1", "b4"], RankedIds(searcher.Search(rabbits.Boost(10) | frogs.Boost(0.1f))));
        Assert.Equal(["b4", "b1"], RankedIds(searcher.Search(rabbits.Boost(0.1f) | frogs.Boost(10))));

        var hit = Assert.Single(searcher.Search(rabbits));
        var boosted = Assert.Single(searcher.Search(rabbits.Boost(3)));
        Assert.Equal(hit.Score * 3, boosted.Score, precision: 4);

        Assert.Throws<ArgumentOutOfRangeException>(() => rabbits.Boost(float.NaN));
    }

    [Fact]
    public void AllQuery_AndQueryReuseAcrossThreads()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        Assert.Equal(4, searcher.Count(Query.All));

        Query q = Query.Phrase("title", "of mice") | Query.Fuzzy("body", "frog") | Query.Range("year", 1950, null);
        long expected = searcher.Count(q);
        Assert.Equal(3, expected);
        Parallel.For(0, 64, _ => Assert.Equal(expected, searcher.Count(q)));
    }

    [Fact]
    public void ToString_DescribesTheTree()
    {
        string s = (Query.Term("year", 1937UL) & !Query.Phrase("body", "a b")).ToString();
        Assert.Contains("\"must_not\"", s);
        Assert.Contains("\"u64\":1937", s);
    }
}
