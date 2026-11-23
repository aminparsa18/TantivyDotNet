using System.Text;

namespace TantivyDotNet.Tests;

/// <summary>Limits and odd inputs: huge documents, empty indexes, Unicode and long paths, read-only directories, extreme values.</summary>
public class EdgeCaseTests
{
    private static TantivyErrorCode ErrorOf(Action action) => Assert.Throws<TantivyException>(action).Code;

    [Fact]
    public void Very_large_documents_and_fields()
    {
        var schema = new SchemaBuilder()
            .AddStringField("id", stored: true)
            .AddTextField("body", stored: true)
            .AddU64Field("n", stored: true)
            .Build();
        using var index = SearchIndex.CreateInRam(schema);

        // ~8 MB of text with one rare word at the very end.
        var body = new StringBuilder();
        for (int i = 0; body.Length < 8_000_000; i++) body.Append("word").Append(i % 5000).Append(' ');
        body.Append("needle");
        // One token far longer than Tantivy's 65530-byte term limit, and 50k values in one field.
        string longToken = new('x', 100_000);
        string manyValues = string.Join(',', Enumerable.Range(0, 50_000));

        using (var writer = index.CreateWriter(heapBytes: 100_000_000, threadCount: 1))
        {
            writer.AddDocument($$"""{"id": "big", "body": "{{body}}"}""");
            writer.AddDocument($$"""{"id": "long", "body": "before {{longToken}} after"}""");
            writer.AddDocument($$"""{"id": "many", "n": [{{manyValues}}]}""");
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();

        var hit = Assert.Single(searcher.Search("needle", new SearchOptions { Snippets = new SnippetOptions("body") }));
        Assert.Equal(body.Length, hit.Document.GetString("body")!.Length);
        Assert.Contains("<b>needle</b>", hit.Snippets["body"].ToHtml());

        // The over-long token is dropped by the tokenizer; its neighbours are still indexed.
        Assert.Equal(1, searcher.Count("after"));
        Assert.Equal(1, searcher.Count(Query.Term("n", 49_999L)));
        Assert.Equal(50_000, searcher.Search(Query.Term("id", "many")).Single().Document.GetValues("n").Count);
    }

    [Fact]
    public void Deeply_nested_documents_are_rejected_not_crashed()
    {
        var schema = new SchemaBuilder().AddJsonField("j", stored: true).Build();
        using var index = SearchIndex.CreateInRam(schema);
        using var writer = index.CreateWriter();
        string deep = string.Concat(Enumerable.Repeat("""{"a":""", 1000)) + "1" + new string('}', 1000);
        Assert.Equal(TantivyErrorCode.InvalidArgument, ErrorOf(() => writer.AddDocument($$"""{"j": {{deep}}}""")));
    }

    [Fact]
    public void An_empty_index_supports_every_operation()
    {
        var schema = new SchemaBuilder()
            .AddStringField("id", stored: true, fast: true)
            .AddTextField("body", stored: true)
            .AddU64Field("year", fast: true)
            .AddFacetField("cat")
            .Build();
        using var dir = new TempDirectory();
        using (var index = SearchIndex.Create(dir.Path, schema))
        {
            using var searcher = index.CreateSearcher(); // nothing committed yet
            var options = new SearchOptions
            {
                Limit = 10,
                OrderBy = [SortBy.Field("year", descending: true), SortBy.Field("id")],
                Snippets = new SnippetOptions("body"),
                Facets = [new FacetRequest("cat", "/", "/a") { Top = 3 }],
                Aggregations = """{"y": {"histogram": {"field": "year", "interval": 10}}, "s": {"stats": {"field": "year"}}}""",
            };
            var r = searcher.Search(Query.All, options);
            Assert.Empty(r);
            Assert.Equal(0, r.TotalCount);
            Assert.Empty(r.Facets["cat"]);
            Assert.NotNull(r.Aggregations);
            Assert.Equal(0, searcher.Count("anything"));
            Assert.Equal(0, searcher.DocumentCount);
            Assert.Equal(0, index.GetStats().SegmentCount);

            using var writer = index.CreateWriter();
            writer.ForceMerge();
            writer.GarbageCollectFiles();
            writer.DeleteAllDocuments();
            writer.Commit();
            using var manager = index.CreateSearcherManager();
            using var lease = manager.Acquire();
            Assert.Equal(0, lease.Searcher.DocumentCount);
        }
        using var reopened = SearchIndex.Open(dir.Path);
        using var again = reopened.CreateSearcher();
        Assert.Empty(again.Search("x"));
    }

    [Fact]
    public void Unicode_paths_field_names_and_highlights()
    {
        using var dir = new TempDirectory(" ünï 日本 🎉");
        var schema = new SchemaBuilder().AddTextField("タイトル", stored: true).AddTextField("ä b", stored: true).Build();
        using (var index = SearchIndex.Create(dir.Path, schema))
        using (var writer = index.CreateWriter())
        {
            writer.AddDocument("""{"タイトル": "🎉🎉 naïve café 𝄞 music", "ä b": "x"}""");
            writer.Commit();
        }
        using var reopened = SearchIndex.Open(dir.Path);
        using var searcher = reopened.CreateSearcher();
        var hit = Assert.Single(searcher.Search(Query.Term("タイトル", "café"), new SearchOptions { Snippets = new SnippetOptions("タイトル") }));
        var snippet = hit.Snippets["タイトル"];
        // Highlight ranges are UTF-16 offsets, after surrogate pairs (emoji) in the fragment.
        Assert.Equal("café", snippet.Fragment[Assert.Single(snippet.Highlights)]);
        Assert.Equal(1, searcher.Count(Query.Term("ä b", "x")));
        Assert.Equal(1, searcher.Count(Query.Parse("music")));
    }

    [Fact]
    public void Paths_longer_than_260_characters()
    {
        using var root = new TempDirectory();
        string path = root.Path;
        while (path.Length < 300) path = Path.Combine(path, new string('d', 40));
        using (var index = SearchIndex.Create(path, Books.Schema()))
        using (var writer = index.CreateWriter())
        {
            foreach (var json in Books.Json) writer.AddDocument(json);
            writer.Commit();
        }
        using var reopened = SearchIndex.Open(path);
        using var searcher = reopened.CreateSearcher();
        Assert.Equal(Books.Json.Length, searcher.DocumentCount);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Read_only_directories()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Read-only directories need ACLs on Windows.");
        using var dir = new TempDirectory();
        using (var index = SearchIndex.Create(dir.Path, Books.Schema()))
        using (var writer = index.CreateWriter())
        {
            foreach (var json in Books.Json) writer.AddDocument(json);
            writer.Commit();
        }
        string readOnly = Path.Combine(dir.Path, "..", Path.GetFileName(dir.Path) + "-empty");
        Directory.CreateDirectory(readOnly);
        var mode = File.GetUnixFileMode(dir.Path);
        File.SetUnixFileMode(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            // Running as root (some containers) ignores the permissions.
            bool enforced = true;
            try { File.WriteAllText(Path.Combine(readOnly, "probe"), ""); enforced = false; } catch (UnauthorizedAccessException) { }
            Assert.SkipUnless(enforced, "File permissions aren't enforced for this user.");

            // Reading works...
            using var index = SearchIndex.Open(dir.Path);
            using (var searcher = index.CreateSearcher())
                Assert.Equal(2, searcher.Count("mice"));
            // ...writing fails cleanly. The writer itself opens (its lock file already exists and is
            // writable), but committing new segment files can't.
            Assert.Equal(TantivyErrorCode.Io, ErrorOf(() => SearchIndex.Create(readOnly, Books.Schema())));
            using (var writer = index.CreateWriter())
            {
                writer.AddDocument(Books.Json[0]);
                var error = Assert.Throws<TantivyException>(writer.Commit);
                Assert.Equal(TantivyErrorCode.Io, error.Code);
                Assert.Contains("rolled back", error.Message);
                using (var searcher = index.CreateSearcher())
                    Assert.Equal(Books.Json.Length, searcher.DocumentCount);

                // The failed commit rolled the writer back: once the directory is writable again,
                // committing publishes nothing from before the failure, and the writer still works.
                File.SetUnixFileMode(dir.Path, mode);
                writer.Commit();
                using (var searcher = index.CreateSearcher())
                    Assert.Equal(Books.Json.Length, searcher.DocumentCount);
                writer.AddDocument(Books.Json[1]);
                writer.Commit();
                using (var searcher = index.CreateSearcher())
                    Assert.Equal(Books.Json.Length + 1, searcher.DocumentCount);
            }
        }
        finally
        {
            File.SetUnixFileMode(dir.Path, mode);
            File.SetUnixFileMode(readOnly, mode);
            Directory.Delete(readOnly, recursive: true);
        }
    }

    [Fact]
    public void Extreme_page_sizes_offsets_and_facet_tops()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        // Tantivy reserves memory for limit + offset hits up front; these are clamped to the index.
        Assert.Equal(4, searcher.Search(Query.All, limit: int.MaxValue).Count);
        Assert.Empty(searcher.Search(Query.All, limit: int.MaxValue, offset: int.MaxValue));
        var sorted = searcher.Search(Query.All, new SearchOptions { Limit = int.MaxValue, OrderBy = [SortBy.Field("year")] });
        Assert.Equal([1937UL, 1939, 1945, 1952], sorted.Select(h => h.Document.GetU64("year")!.Value));

        var schema = new SchemaBuilder().AddFacetField("c").Build();
        using var facets = SearchIndex.CreateInRam(schema);
        using (var writer = facets.CreateWriter())
        {
            writer.AddDocument("""{"c": "/a"}""");
            writer.Commit();
        }
        using var fs = facets.CreateSearcher();
        var r = fs.Search(Query.All, new SearchOptions { Facets = [new FacetRequest("c") { Top = int.MaxValue }] });
        Assert.Equal([new FacetCount("/a", 1)], r.Facets["c"]);
    }

    [Fact]
    public void Inputs_that_made_Tantivy_panic_are_errors()
    {
        // Each of these panicked inside Tantivy (found by fuzzing) and now fails with a clear error or works.
        Assert.Throws<ArgumentException>(() => new SchemaBuilder().AddTextField("-title"));

        var schema = new SchemaBuilder()
            .AddFacetField("cat")
            .AddDateField("d", fast: true)
            .AddJsonField("j", fast: true)
            .AddTextField("t")
            .Build();
        using var index = SearchIndex.CreateInRam(schema);
        using (var writer = index.CreateWriter())
        {
            Assert.Equal(TantivyErrorCode.InvalidArgument, ErrorOf(() => writer.AddDocument("""{"cat": "fiction"}""")));
            writer.AddDocument("""{"cat": "/fiction", "d": "2001-09-09T01:46:40Z", "j": {"n": 5}, "t": "x"}""");
            writer.AddDocument("""{"j": {"n": 7.5}, "t": "y"}""");
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();

        // Dates are stored as 64-bit nanoseconds: 1677..2262.
        Assert.Equal(TantivyErrorCode.InvalidArgument, ErrorOf(() => searcher.Count(Query.Term("d", new DateTime(1600, 1, 1, 0, 0, 0, DateTimeKind.Utc)))));
        Assert.Equal(1, searcher.Count(Query.Range("d", DateTimeOffset.MinValue, DateTimeOffset.MaxValue)));

        // A range in query text whose bounds look like different number types.
        Assert.Equal(2, searcher.Count("j.n:[0 TO 9.5]"));

        // Aggregations Tantivy can't run safely.
        var top = """{"h": {"top_hits": {"size": 2147483647, "sort": [{"d": "desc"}]}}}""";
        Assert.Equal(TantivyErrorCode.InvalidArgument, ErrorOf(() => searcher.Search(Query.All, new SearchOptions { Aggregations = top })));
        var terms = """{"t": {"terms": {"field": "j.n", "size": 2147483647}}}""";
        Assert.NotNull(searcher.Search(Query.All, new SearchOptions { Aggregations = terms }).Aggregations);
    }
}
