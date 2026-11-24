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
            // A cursor that doesn't advance would page forever; fail instead.
            Assert.True(ids.Count <= page.TotalCount, $"Paging returned more than the {page.TotalCount} matches.");
            cursor = page[^1].Cursor;
        }
    }

    /// <summary>
    /// Sorting by one numeric field (the native fast path) pages correctly for every numeric type in
    /// both orders: negative values, ties, missing values last, cursors across segments, and a
    /// segment with no value for the field at all.
    /// </summary>
    [Theory]
    [InlineData("i", false)]
    [InlineData("i", true)]
    [InlineData("f", false)]
    [InlineData("f", true)]
    [InlineData("d", false)]
    [InlineData("d", true)]
    [InlineData("b", false)]
    [InlineData("b", true)]
    public void SingleNumericKey_PagesLikeLinqForEveryType(string field, bool descending)
    {
        var index = SearchIndex.CreateInRam(new SchemaBuilder()
            .AddStringField("id", stored: true)
            .AddI64Field("i", fast: true)
            .AddF64Field("f", fast: true)
            .AddDateField("d", fast: true)
            .AddBoolField("b", fast: true)
            .Build());
        var values = new Dictionary<string, IComparable?>();
        // One indexing thread and no merges: documents get addresses in insertion order.
        using (var writer = index.CreateWriter(new IndexWriterOptions { ThreadCount = 1, MergePolicy = MergePolicy.None }))
        {
            for (int i = 0; i < 50; i++)
            {
                string id = $"r{i:D2}";
                // Segment 3 (rows 30-39) has no numeric values at all; elsewhere every 7th row is missing.
                bool missing = i is >= 30 and < 40 || i % 7 == 0;
                long v = (i * 37 % 19) - 9;                                   // -9..9, with ties
                IComparable? value = missing ? null : field switch
                {
                    "i" => v,
                    "f" => v * 1.5,
                    "d" => new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(v),
                    _ => v > 0,
                };
                values[id] = value;
                string json = value switch
                {
                    null => $$"""{"id":"{{id}}"}""",
                    DateTimeOffset d => $$"""{"id":"{{id}}","{{field}}":"{{d:yyyy-MM-ddTHH:mm:ssZ}}"}""",
                    bool b => $$"""{"id":"{{id}}","{{field}}":{{(b ? "true" : "false")}}}""",
                    double x => $$"""{"id":"{{id}}","{{field}}":{{x.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""",
                    _ => $$"""{"id":"{{id}}","{{field}}":{{value}}}""",
                };
                writer.AddDocument(json);
                if (i % 10 == 9) writer.Commit();
            }
        }
        using var searcher = index.CreateSearcher();

        // The full result: every document once, in value order, missing values last. (Ties are
        // broken by document address, which follows the segment order Tantivy chooses.)
        var order = new[] { SortBy.Field(field, descending) };
        var all = Ids(searcher.Search(Query.All, new SearchOptions { Limit = 50, OrderBy = order }));
        Assert.Equal(values.Keys.Order(), all.Order());
        var sortedValues = all.Select(id => values[id!]).ToList();
        int firstMissing = sortedValues.IndexOf(null);
        Assert.Equal(values.Count(p => p.Value is null), sortedValues.Count - firstMissing);
        Assert.All(sortedValues.Skip(firstMissing), Assert.Null);
        var present = sortedValues.Take(firstMissing).ToList();
        var expectedValues = descending ? present.OrderDescending().ToList() : present.Order().ToList();
        Assert.Equal(expectedValues, present);

        // Paging with cursors (several page sizes, so pages end inside runs of ties and at segment
        // boundaries) and with offsets gives exactly the same order.
        foreach (var pageSize in new[] { 1, 3, 7 })
            Assert.Equal(all, PageThrough(searcher, Query.All, order, pageSize));
        foreach (var offset in new[] { 5, 44 })
            Assert.Equal(all.Skip(offset).Take(4), Ids(searcher.Search(Query.All, new SearchOptions { Limit = 4, Offset = offset, OrderBy = order })));
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

    /// <summary>
    /// The case the paging docs describe: page 1 comes from one searcher, then the index commits
    /// (adds and deletes) and merges, and the next pages come from the refreshed searcher of a
    /// <see cref="SearcherManager"/>. With a unique field as the last sort key, the cursor still
    /// works: the rest of the pages are exactly the new snapshot's hits that sort after it.
    /// </summary>
    [Fact]
    public void Cursor_FromAnOlderSnapshot_ContinuesBySortValue_WithAUniqueLastKey()
    {
        using var index = Index();
        using var manager = index.CreateSearcherManager();
        IReadOnlyList<SortBy> order = [SortBy.Field("group"), SortBy.Field("id")];

        string cursor;
        List<string?> page1;
        using (var lease = manager.Acquire())
        {
            var page = lease.Searcher.Search(Query.All, new SearchOptions { Limit = 10, OrderBy = order });
            page1 = [.. Ids(page)];
            cursor = page[^1].Cursor;
        }

        using (var writer = index.CreateWriter())
        {
            writer.DeleteTerm("id", page1[3]!);   // already returned
            writer.DeleteTerm("id", "r57");       // not returned yet
            for (int i = 60; i < 70; i++)         // new documents on both sides of the cursor
                writer.AddDocument($$"""{"id":"n{{i}}","group":{{i % 4}},"text":"mice"}""");
            writer.Commit();
            writer.ForceMerge(1);                 // renumbers every document
        }
        Assert.True(manager.Refresh());

        using (var lease = manager.Acquire())
        {
            var searcher = lease.Searcher;
            var all = Ids(searcher.Search(Query.All, new SearchOptions { Limit = 1000, OrderBy = order })).ToList();
            var expected = all.Skip(all.IndexOf(page1[^1]) + 1).ToList();

            var rest = new List<string?>();
            for (var page = searcher.Search(Query.All, new SearchOptions { Limit = 7, OrderBy = order, SearchAfter = cursor });
                 page.Count > 0;
                 page = searcher.Search(Query.All, new SearchOptions { Limit = 7, OrderBy = order, SearchAfter = page[^1].Cursor }))
                rest.AddRange(Ids(page));

            Assert.Equal(expected, rest);
            Assert.DoesNotContain("r57", rest);
            Assert.Empty(rest.Intersect(page1));
            // New documents that sort before the cursor aren't seen: n60 and n64 are in group 0,
            // before r40 (the last hit of page 1). n61 is in group 1, after it.
            Assert.DoesNotContain("n60", rest);
            Assert.Contains("n61", rest);
        }
    }

    /// <summary>
    /// Without a unique last key, a cursor from an older snapshot can't tell the documents tied
    /// with it apart, so it skips them all instead of repeating some: page 1 ends inside group 0,
    /// and after a merge the next page starts at group 1.
    /// </summary>
    [Fact]
    public void Cursor_FromAnOlderSnapshot_SkipsDocumentsTiedWithIt()
    {
        using var index = Index();
        using var manager = index.CreateSearcherManager();
        IReadOnlyList<SortBy> order = [SortBy.Field("group")];

        string cursor;
        using (var lease = manager.Acquire())
        {
            var page = lease.Searcher.Search(Query.All, new SearchOptions { Limit = 10, OrderBy = order });
            Assert.All(page, h => Assert.Equal(0UL, h.SortValues[0]));
            cursor = page[^1].Cursor;
        }
        using (var writer = index.CreateWriter())
            writer.ForceMerge(1);
        Assert.True(manager.Refresh());

        using var newer = manager.Acquire();
        var next = newer.Searcher.Search(Query.All, new SearchOptions { Limit = 10, OrderBy = order, SearchAfter = cursor });
        Assert.Equal(1UL, next[0].SortValues[0]);

        // A cursor used on the snapshot it came from still continues inside group 0.
        var first = newer.Searcher.Search(Query.All, new SearchOptions { Limit = 10, OrderBy = order });
        var exact = newer.Searcher.Search(Query.All, new SearchOptions { Limit = 10, OrderBy = order, SearchAfter = first[^1].Cursor });
        Assert.Equal(0UL, exact[0].SortValues[0]);
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
