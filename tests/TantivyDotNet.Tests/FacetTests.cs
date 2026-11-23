namespace TantivyDotNet.Tests;

public class FacetTests
{
    private static SearchIndex Library()
    {
        var schema = new SchemaBuilder()
            .AddStringField("id", stored: true)
            .AddTextField("title", stored: true)
            .AddFacetField("genre", stored: true)
            .AddU64Field("year", fast: true)
            .Build();
        var index = SearchIndex.CreateInRam(schema);
        using var writer = index.CreateWriter();
        writer.AddDocument("""{"id": "d1", "title": "Of Mice and Men", "genre": "/fiction/novella", "year": 1937}""");
        writer.AddDocument("""{"id": "d2", "title": "The Grapes of Wrath", "genre": ["/fiction/novel", "/award/pulitzer"], "year": 1939}""");
        writer.AddDocument("""{"id": "d3", "title": "East of Eden", "genre": "/fiction/novel", "year": 1952}""");
        writer.AddDocument("""{"id": "d4", "title": "The Log from the Sea of Cortez", "genre": "/nonfiction/travel", "year": 1951}""");
        writer.AddDocument("""{"id": "d5", "title": "Travels with Charley", "genre": "/nonfiction/travel", "year": 1962}""");
        writer.Commit();
        return index;
    }

    private static (string, long)[] Counts(SearchResults r, string field) => [.. r.Facets[field].Select(c => (c.Path, c.Count))];

    [Fact]
    public void FacetCounts_RootAndChildren()
    {
        using var index = Library();
        using var searcher = index.CreateSearcher();

        var root = searcher.Search(Query.All, new SearchOptions { Facets = [new FacetRequest("genre")] });
        Assert.Equal([("/award", 1L), ("/fiction", 3L), ("/nonfiction", 2L)], Counts(root, "genre"));

        var fiction = searcher.Search(Query.All, new SearchOptions { Facets = [new FacetRequest("genre", "/fiction", "/nonfiction")] });
        Assert.Equal([("/fiction/novel", 2L), ("/fiction/novella", 1L), ("/nonfiction/travel", 2L)], Counts(fiction, "genre"));

        // A path together with one of its ancestors (Tantivy's collector forbids that in one collector).
        var both = searcher.Search(Query.All, new SearchOptions { Facets = [new FacetRequest("genre", "/", "/fiction")] });
        Assert.Equal(5, both.Facets["genre"].Count);
    }

    [Fact]
    public void FacetCounts_RootWithOneLetterOrSimilarNames()
    {
        // Tantivy's own collector miscounts these at the root (counted per leaf / merged).
        var schema = new SchemaBuilder().AddFacetField("c").Build();
        using var index = SearchIndex.CreateInRam(schema);
        using (var writer = index.CreateWriter())
        {
            foreach (var c in new[] { "/a/x", "/a/y", "/a/y", "/bat/1", "/cat/2" })
                writer.AddDocument($$"""{"c": "{{c}}"}""");
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();
        var root = searcher.Search(Query.All, new SearchOptions { Facets = [new FacetRequest("c")] });
        Assert.Equal([("/a", 3L), ("/bat", 1L), ("/cat", 1L)], Counts(root, "c"));
    }

    [Fact]
    public void FacetCounts_CoverAllMatches_NotJustThePage()
    {
        using var index = Library();
        using var searcher = index.CreateSearcher();

        var results = searcher.Search(Query.Range("year", 1950, null), new SearchOptions { Limit = 1, Facets = [new FacetRequest("genre")] });
        Assert.Single(results);
        Assert.Equal(3, results.TotalCount);
        Assert.Equal([("/fiction", 1L), ("/nonfiction", 2L)], Counts(results, "genre"));   // no matches under /award: left out

        var countOnly = searcher.Search("mice", new SearchOptions { Limit = 0, Facets = [new FacetRequest("genre", "/fiction")] });
        Assert.Empty(countOnly);
        Assert.Equal([("/fiction/novella", 1L)], Counts(countOnly, "genre"));

        var none = searcher.Search("zebra", new SearchOptions { Facets = [new FacetRequest("genre")] });
        Assert.Empty(none.Facets["genre"]);
    }

    [Fact]
    public void FacetCounts_TopReturnsHighestCountsFirst()
    {
        using var index = Library();
        using var searcher = index.CreateSearcher();

        var top = searcher.Search(Query.All, new SearchOptions { Facets = [new FacetRequest("genre") { Top = 2 }] });
        Assert.Equal([("/fiction", 3L), ("/nonfiction", 2L)], Counts(top, "genre"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FacetRequest("genre") { Top = 0 });
    }

    [Fact]
    public void FacetQuery_MatchesCategoryAndDescendants()
    {
        using var index = Library();
        using var searcher = index.CreateSearcher();

        Assert.Equal(3, searcher.Count(Query.Facet("genre", "/fiction")));
        Assert.Equal(2, searcher.Count(Query.Facet("genre", "/fiction/novel")));
        Assert.Equal(0, searcher.Count(Query.Facet("genre", "/fiction/nov")));             // whole segments only
        Assert.Equal(1, searcher.Count(Query.Facet("genre", "/fiction") & Query.Facet("genre", "/award")));
        Assert.Equal(1, searcher.Count(Query.Facet("genre", "/nonfiction") & Query.Range("year", 1955, null)));
        Assert.Equal(3, searcher.Count("genre:/fiction"));                                  // query language too
        Assert.Equal(1, searcher.Count(Query.Term("genre", "/award")));                     // a term on a facet field is a facet path

        Assert.Throws<ArgumentException>(() => Query.Facet("genre", "fiction"));
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => searcher.Search(Query.Term("genre", "fiction"))).Code);
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => searcher.Search(Query.Facet("title", "/x"))).Code);
    }

    [Fact]
    public void FacetField_StoredValuesAndSchemaRoundTrip()
    {
        using var index = Library();
        Assert.Equal(new FieldDefinition("genre", FieldType.Facet, stored: true, indexed: true, fast: true, null), index.Schema["genre"]);

        using var searcher = index.CreateSearcher();
        var d2 = Assert.Single(searcher.Search(Query.Term("id", "d2"))).Document;
        Assert.Equal(["/fiction/novel", "/award/pulitzer"], d2.GetStrings("genre"));
    }

    [Fact]
    public void FacetPaths_WithEscapedSlashes()
    {
        var schema = new SchemaBuilder().AddFacetField("size", stored: true).Build();
        using var index = SearchIndex.CreateInRam(schema);
        using (var writer = index.CreateWriter())
        {
            writer.AddDocument("""{"size": "/inch/1\\/2"}""");     // segment "1/2"
            writer.AddDocument("""{"size": "/inch/3\\/4"}""");
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();

        var r = searcher.Search(Query.Facet("size", "/inch/1\\/2"), new SearchOptions { Facets = [new FacetRequest("size", "/inch")] });
        Assert.Equal("/inch/1\\/2", Assert.Single(r).Document.GetString("size"));
        Assert.Equal([("/inch/1\\/2", 1L)], Counts(r, "size"));
    }

    [Fact]
    public void FacetRequests_Validated()
    {
        using var index = Library();
        using var searcher = index.CreateSearcher();

        Assert.Empty(searcher.Search(Query.All).Facets);
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => searcher.Search(Query.All, new SearchOptions { Facets = [new FacetRequest("title")] })).Code);
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => searcher.Search(Query.All, new SearchOptions { Facets = [new FacetRequest("nope")] })).Code);
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(
            () => searcher.Search(Query.All, new SearchOptions { Facets = [new FacetRequest("genre", "/fiction"), new FacetRequest("genre", "/award")] })).Code);

        Assert.Throws<ArgumentException>(() => new FacetRequest("genre", "fiction"));
        Assert.Throws<ArgumentException>(() => new FacetRequest("genre", [null!]));
        Assert.Throws<ArgumentException>(() => searcher.Search(Query.All, new SearchOptions { Facets = [null!] }));
    }
}
