namespace TantivyDotNet.Tests;

public class PagingTests
{
    private sealed record Row(string Id, ulong? Group, string? Name, string Text);

    private static readonly Row[] Rows = BuildRows();

    private static Row[] BuildRows()
    {
        var rows = new List<Row>();
        for (int i = 0; i < 60; i++)
        {
            ulong? group = i % 11 == 0 ? null : (ulong)(i % 4);              // ties, plus some missing values
            string? name = i % 13 == 0 ? null : $"n{(i * 7) % 23:D2}";        // names repeat; each segment has a subset
            string text = i % 3 == 0 ? "mice mice and men" : i % 3 == 1 ? "mice in the field" : "a story about men";
            rows.Add(new Row($"r{i:D2}", group, name, text));
        }
        return [.. rows];
    }

    /// <summary>Indexed in 4 commits, so the index has several segments.</summary>
    private static SearchIndex Index()
    {
        var index = SearchIndex.CreateInRam(new SchemaBuilder()
            .AddStringField("id", stored: true, fast: true)
            .AddU64Field("group", stored: true, fast: true)
            .AddStringField("name", stored: true, fast: true)
            .AddTextField("text")
            .Build());
        using var writer = index.CreateWriter();
        foreach (var chunk in Rows.Chunk(15))
        {
            foreach (var r in chunk)
            {
                var fields = new List<string> { $"\"id\":\"{r.Id}\"", $"\"text\":\"{r.Text}\"" };
                if (r.Group is { } g) fields.Add($"\"group\":{g}");
                if (r.Name is { } n) fields.Add($"\"name\":\"{n}\"");
                writer.AddDocument("{" + string.Join(",", fields) + "}");
            }
            writer.Commit();
        }
        return index;
    }

    private static string?[] Ids(SearchResults r) => [.. r.Select(h => h.Document.GetString("id"))];

    private static List<string?> PageThrough(Searcher searcher, Query query, IReadOnlyList<SortBy>? order, int pageSize)
    {
        var ids = new List<string?>();
        string? cursor = null;
        while (true)
        {
            var page = searcher.Search(query, new SearchOptions { Limit = pageSize, OrderBy = order, SearchAfter = cursor });
            Assert.Equal(searcher.Count(query), page.TotalCount);   // the count ignores the cursor
            if (page.Count == 0) return ids;
            ids.AddRange(Ids(page));
            cursor = page[^1].Cursor;
        }
    }

    [Fact]
    public void MultiKeySort_OrdersByEachKeyInTurn_MissingLast()
    {
        using var index = Index();
        using var searcher = index.CreateSearcher();
        var results = searcher.Search(Query.All, new SearchOptions
        {
            Limit = 100,
            OrderBy = [SortBy.Field("group"), SortBy.Field("name", descending: true)],
        });

        var expected = Rows
            .OrderBy(r => r.Group is null).ThenBy(r => r.Group)
            .ThenBy(r => r.Name is null).ThenByDescending(r => r.Name, StringComparer.Ordinal)
            .Select(r => r.Group is null || r.Name is null ? null : r.Id)   // ties among missing values keep index order
            .ToArray();
        string?[] actual = Ids(results);
        for (int i = 0; i < expected.Length; i++)
            if (expected[i] is not null) Assert.Equal(expected[i], actual[i]);

        var first = results[0];
        Assert.Equal(2, first.SortValues.Count);
        Assert.Equal(0UL, first.SortValues[0]);
        Assert.IsType<string>(first.SortValues[1]);
        Assert.True(float.IsNaN(first.Score));
        Assert.Equal([null, null], results[^1].SortValues);
    }

    [Fact]
    public void ScoreAsSecondaryKey_IsComputedAndReturned()
    {
        using var index = Index();
        using var searcher = index.CreateSearcher();
        var results = searcher.Search(Query.Parse("mice"), new SearchOptions
        {
            Limit = 100,
            OrderBy = [SortBy.Field("group", descending: true), SortBy.Score()],
        });
        Assert.Equal(40, results.Count);
        Assert.All(results, h => Assert.Equal(h.Score, Assert.IsType<float>(h.SortValues[1])));
        // Within a group, better matches ("mice mice ...") come first.
        var group3 = results.Where(h => Equals(h.SortValues[0], 3UL)).Select(h => h.Score).ToArray();
        Assert.Equal(group3.OrderDescending(), group3);
    }

    public static TheoryData<string> Orders => ["relevance", "group", "group+name desc", "name", "score+id desc"];

    private static IReadOnlyList<SortBy>? OrderFor(string name) => name switch
    {
        "relevance" => null,
        "group" => [SortBy.Field("group")],
        "group+name desc" => [SortBy.Field("group"), SortBy.Field("name", descending: true)],
        "name" => [SortBy.Field("name")],
        "score+id desc" => [SortBy.Score(), SortBy.Field("id", descending: true)],
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(Orders))]
    public void CursorPaging_VisitsEveryHitOnceInOrder(string orderName)
    {
        using var index = Index();
        using var searcher = index.CreateSearcher();
        var order = OrderFor(orderName);
        foreach (var query in new[] { Query.All, Query.Parse("mice") })
        {
            var all = Ids(searcher.Search(query, new SearchOptions { Limit = 1000, OrderBy = order }));
            foreach (int pageSize in new[] { 1, 7, 25 })
                Assert.Equal(all, PageThrough(searcher, query, order, pageSize));
        }
    }

    [Fact]
    public void Cursor_MismatchedOrderOffsetAndGarbage_Throw()
    {
        using var index = Index();
        using var searcher = index.CreateSearcher();
        var hit = searcher.Search(Query.All, new SearchOptions { OrderBy = [SortBy.Field("group")] })[0];

        var e = Assert.Throws<ArgumentException>(() => searcher.Search(Query.All, new SearchOptions { SearchAfter = hit.Cursor }));
        Assert.Contains("group asc", e.Message);
        Assert.Throws<ArgumentException>(() => searcher.Search(Query.All,
            new SearchOptions { OrderBy = [SortBy.Field("group")], SearchAfter = hit.Cursor, Offset = 5 }));
        Assert.Throws<ArgumentException>(() => searcher.Search(Query.All, new SearchOptions { SearchAfter = "not a cursor" }));
        Assert.Throws<ArgumentException>(() => new SearchOptions { OrderBy = [null!] });
    }

    [Fact]
    public void SortBy_Score_Describes()
    {
        Assert.Equal("_score", SortBy.Score().ToString());
        Assert.Null(SortBy.Score().FieldName);
        Assert.True(SortBy.Score().Descending);
    }
}
