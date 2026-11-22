namespace TantivyDotNet.Tests;

public class SortTests
{
    private static SearchIndex Catalog()
    {
        var schema = new SchemaBuilder()
            .AddStringField("id", stored: true, fast: true)
            .AddTextField("title", stored: true)
            .AddStringField("name", fast: true)
            .AddU64Field("year", fast: true)
            .AddI64Field("delta", fast: true)
            .AddF64Field("price", fast: true)
            .AddBoolField("active", fast: true)
            .AddDateField("published", fast: true)
            .AddFacetField("genre")
            .Build();
        var index = SearchIndex.CreateInRam(schema);
        using var writer = index.CreateWriter();
        writer.AddDocument("""{"id":"a","title":"mice and men","name":"Mice","year":1937,"delta":-5,"price":9.5,"active":true,"published":"2024-03-01T12:00:00Z"}""");
        writer.AddDocument("""{"id":"b","title":"east of eden","name":"eden","year":1952,"delta":7,"price":20.25,"active":false,"published":"2025-01-15T00:00:00Z"}""");
        writer.AddDocument("""{"id":"c","title":"grapes and mice","name":"Grapes","year":1939,"delta":0,"price":-1,"active":true,"published":"2023-06-30T23:59:59Z"}""");
        writer.AddDocument("""{"id":"d","title":"no values here"}""");
        writer.Commit();
        return index;
    }

    private static string?[] Order(SearchResults r) => [.. r.Select(h => h.Document.GetString("id"))];

    private static SearchResults Sorted(Searcher s, string field, bool descending = false, Query? query = null) =>
        s.Search(query ?? Query.All, new SearchOptions { OrderBy = SortBy.Field(field, descending) });

    [Fact]
    public void SortByU64_AscendingAndDescending_MissingValuesLast()
    {
        using var index = Catalog();
        using var searcher = index.CreateSearcher();

        var asc = Sorted(searcher, "year");
        Assert.Equal(["a", "c", "b", "d"], Order(asc));
        Assert.Equal([1937UL, 1939UL, 1952UL, null], asc.Select(h => h.SortValue));
        Assert.All(asc, h => Assert.True(float.IsNaN(h.Score)));

        Assert.Equal(["b", "c", "a", "d"], Order(Sorted(searcher, "year", descending: true)));
    }

    [Fact]
    public void SortByOtherTypes_ReturnsTypedSortValues()
    {
        using var index = Catalog();
        using var searcher = index.CreateSearcher();

        var delta = Sorted(searcher, "delta");
        Assert.Equal(["a", "c", "b", "d"], Order(delta));
        Assert.Equal([-5L, 0L, 7L, null], delta.Select(h => h.SortValue));

        var price = Sorted(searcher, "price", descending: true);
        Assert.Equal(["b", "a", "c", "d"], Order(price));
        Assert.Equal([20.25, 9.5, -1.0, null], price.Select(h => h.SortValue));

        var published = Sorted(searcher, "published");
        Assert.Equal(["c", "a", "b", "d"], Order(published));
        Assert.Equal(new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero), published[1].SortValue);

        var active = Sorted(searcher, "active", descending: true);
        Assert.Equal([true, true, false, null], active.Select(h => h.SortValue));

        var name = Sorted(searcher, "name");                                  // ordinal: uppercase first
        Assert.Equal(["c", "a", "b", "d"], Order(name));
        Assert.Equal(["Grapes", "Mice", "eden", null], name.Select(h => h.SortValue));
    }

    [Fact]
    public void Sort_WithQueryPagingSnippetsAndFacets()
    {
        using var index = Catalog();
        using var searcher = index.CreateSearcher();

        Assert.Equal(["b", "c", "a"], Order(Sorted(searcher, "year", descending: true, query: Query.Range("year", 0, null))));
        Assert.Equal(["c", "a"], Order(Sorted(searcher, "year", descending: true, query: Query.Term("title", "mice"))));

        var page = searcher.Search(Query.All, new SearchOptions { OrderBy = SortBy.Field("year", descending: true), Limit = 2, Offset = 1 });
        Assert.Equal(["c", "a"], Order(page));
        Assert.Equal(4, page.TotalCount);

        var countOnly = searcher.Search("mice", new SearchOptions { OrderBy = SortBy.Field("year"), Limit = 0 });
        Assert.Empty(countOnly);
        Assert.Equal(2, countOnly.TotalCount);

        var withSnippets = searcher.Search("mice", new SearchOptions { OrderBy = SortBy.Field("year"), Snippets = new SnippetOptions("title") });
        Assert.Equal(["a", "c"], Order(withSnippets));
        Assert.Equal("mice", withSnippets[0].Snippets["title"].Fragment[withSnippets[0].Snippets["title"].Highlights[0]]);
    }

    [Fact]
    public void RelevanceOrder_IsTheDefault()
    {
        using var index = Catalog();
        using var searcher = index.CreateSearcher();

        foreach (var results in new[] { searcher.Search("mice"), searcher.Search("mice", new SearchOptions()), searcher.Search(Query.Parse("mice"), limit: 5) })
        {
            Assert.Equal(2, results.Count);
            Assert.All(results, h => Assert.Null(h.SortValue));
            Assert.All(results, h => Assert.True(h.Score > 0));
        }
    }

    [Fact]
    public void Sort_InvalidFieldsAndOptions_Throw()
    {
        using var index = Catalog();
        using var searcher = index.CreateSearcher();

        var notFast = Assert.Throws<TantivyException>(() => Sorted(searcher, "title"));
        Assert.Equal(TantivyErrorCode.InvalidArgument, notFast.Code);
        Assert.Contains("fast", notFast.Message);
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => Sorted(searcher, "genre")).Code);
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => Sorted(searcher, "nope")).Code);

        Assert.Throws<ArgumentException>(() => SortBy.Field(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchOptions { Limit = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchOptions { Offset = -1 });
        Assert.Throws<ArgumentNullException>(() => searcher.Search(Query.All, (SearchOptions)null!));
        Assert.Throws<ArgumentException>(() => new SearchOptions { Facets = [null!] });
        Assert.Equal("year desc", SortBy.Field("year", descending: true).ToString());
    }
}
