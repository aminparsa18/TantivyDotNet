using System.Text.Json.Serialization;
using TantivyDotNet.Interop;
using TantivyDotNet.Native;

namespace TantivyDotNet.Tests;

public class CoreApiTests
{
    [Fact]
    public void NativeAbiVersion_MatchesManagedExpectation() =>
        Assert.Equal(Ffi.ExpectedAbiVersion, NativeMethods.tdn_abi_version());

    [Fact]
    public void ReadmeExample_Works()
    {
        var schema = new SchemaBuilder()
            .AddTextField("title", stored: true)
            .AddTextField("body")
            .AddU64Field("year", indexed: true, fast: true)
            .Build();
        using var index = SearchIndex.CreateInRam(schema);
        using (var writer = index.CreateWriter(heapBytes: 50_000_000))
        {
            writer.AddDocument(new { title = "Of Mice and Men", body = "A story about mice and men", year = 1937 });
            writer.AddDocument("""{"title": "The Pearl", "body": "A pearl diver", "year": 1947}""");
            writer.AddDocument(new { title = "old", body = "to be deleted", year = 1935 });
            writer.DeleteTerm("title", "old");
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();
        var hits = searcher.Search("body:mice AND year:[1930 TO 1940]", limit: 10, defaultFields: ["title", "body"]);

        var hit = Assert.Single(hits);
        Assert.Equal("Of Mice and Men", hit.Document.GetString("title"));
        Assert.True(hit.Score > 0);
        Assert.Equal(2, searcher.DocumentCount);
    }

    [Fact]
    public void Schema_RoundTripsThroughIndex()
    {
        using var index = SearchIndex.CreateInRam(new SchemaBuilder()
            .AddTextField("title", stored: true, tokenizer: "en_stem")
            .AddStringField("id", fast: true)
            .AddI64Field("delta", indexed: false, fast: true)
            .AddF64Field("price", stored: true)
            .AddBoolField("active")
            .AddDateField("published", stored: true)
            .AddBytesField("blob", stored: true, indexed: false)
            .Build());

        Assert.Equal(
            [
                new FieldDefinition("title", FieldType.Text, Stored: true, Indexed: true, Fast: false, "en_stem"),
                new FieldDefinition("id", FieldType.Text, Stored: false, Indexed: true, Fast: true, "raw"),
                new FieldDefinition("delta", FieldType.I64, Stored: false, Indexed: false, Fast: true, null),
                new FieldDefinition("price", FieldType.F64, Stored: true, Indexed: true, Fast: false, null),
                new FieldDefinition("active", FieldType.Bool, Stored: false, Indexed: true, Fast: false, null),
                new FieldDefinition("published", FieldType.Date, Stored: true, Indexed: true, Fast: false, null),
                new FieldDefinition("blob", FieldType.Bytes, Stored: true, Indexed: false, Fast: false, null),
            ],
            index.Schema.Fields);
        Assert.Equal(FieldType.Date, index.Schema["published"].Type);
        Assert.False(index.Schema.TryGetField("nope", out _));
    }

    [Fact]
    public void SchemaBuilder_RejectsDuplicatesAndEmpty()
    {
        Assert.Throws<ArgumentException>(() => new SchemaBuilder().AddTextField("a").AddU64Field("a"));
        Assert.Throws<InvalidOperationException>(() => new SchemaBuilder().Build());
    }

    [Fact]
    public void StoredValues_OfEveryType_RoundTrip()
    {
        using var index = SearchIndex.CreateInRam(new SchemaBuilder()
            .AddStringField("id", stored: true)
            .AddTextField("tags", stored: true)
            .AddU64Field("u", stored: true)
            .AddI64Field("i", stored: true)
            .AddF64Field("f", stored: true)
            .AddBoolField("b", stored: true)
            .AddDateField("d", stored: true)
            .AddBytesField("raw", stored: true)
            .Build());
        using (var writer = index.CreateWriter())
        {
            writer.AddDocument(new
            {
                id = "x",
                tags = new[] { "red", "green" },
                u = ulong.MaxValue,
                i = -42L,
                f = 2.5,
                b = true,
                d = new DateTimeOffset(2024, 5, 1, 12, 30, 0, TimeSpan.Zero),
                raw = new byte[] { 1, 2, 3 },
            });
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();
        var doc = Assert.Single(searcher.Search("id:x")).Document;

        Assert.Equal(["red", "green"], doc.GetStrings("tags"));
        Assert.Equal(ulong.MaxValue, doc.GetU64("u"));
        Assert.Equal(-42L, doc.GetI64("i"));
        Assert.Equal(2.5, doc.GetF64("f"));
        Assert.True(doc.GetBool("b"));
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 12, 30, 0, TimeSpan.Zero), doc.GetDate("d"));
        Assert.Equal([1, 2, 3], doc.GetBytes("raw"));
        Assert.Null(doc.GetString("missing"));
        Assert.Throws<InvalidOperationException>(() => doc.GetString("u"));
    }

    [Fact]
    public void OnDisk_CreateCloseReopenSearch()
    {
        using var dir = new TempDirectory();
        using (var index = SearchIndex.Create(dir.Path, Books.Schema()))
        using (var writer = index.CreateWriter())
        {
            foreach (var json in Books.Json) writer.AddDocument(json);
            writer.Commit();
        }

        using (var reopened = SearchIndex.Open(dir.Path))
        using (var searcher = reopened.CreateSearcher())
        {
            Assert.Equal(Books.Schema().Fields, reopened.Schema.Fields);
            Assert.Equal(4, searcher.DocumentCount);
            Assert.Equal(["b1", "b4"], Books.Ids(searcher.Search("mice")));
        }

        var ex = Assert.Throws<TantivyException>(() => SearchIndex.Create(dir.Path, Books.Schema()));
        Assert.Equal(TantivyErrorCode.IndexAlreadyExists, ex.Code);
    }

    [Fact]
    public void Open_MissingDirectory_Throws()
    {
        using var dir = new TempDirectory();
        var ex = Assert.Throws<TantivyException>(() => SearchIndex.Open(dir.Path));
        Assert.Equal(TantivyErrorCode.Io, ex.Code);
    }

    [Fact]
    public void Deletes_ByStringU64AndI64Term()
    {
        using var index = SearchIndex.CreateInRam(new SchemaBuilder()
            .AddStringField("id", stored: true)
            .AddU64Field("u")
            .AddI64Field("i")
            .Build());
        using var writer = index.CreateWriter();
        writer.AddDocument("""{"id": "a", "u": 1, "i": -1}""");
        writer.AddDocument("""{"id": "b", "u": 2, "i": -2}""");
        writer.AddDocument("""{"id": "c", "u": 3, "i": -3}""");
        writer.AddDocument("""{"id": "d", "u": 4, "i": -4}""");
        writer.Commit();

        writer.DeleteTerm("id", "a");
        writer.DeleteTerm("u", 2UL);
        writer.DeleteTerm("i", -3L);
        writer.Commit();

        using var searcher = index.CreateSearcher();
        Assert.Equal(1, searcher.DocumentCount);
        Assert.Equal("d", Assert.Single(searcher.Search("*")).Document.GetString("id"));

        var wrongType = Assert.Throws<TantivyException>(() => writer.DeleteTerm("id", 1UL));
        Assert.Equal(TantivyErrorCode.InvalidArgument, wrongType.Code);
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => writer.DeleteTerm("nope", "x")).Code);
    }

    [Fact]
    public void DeleteAllDocuments_ClearsIndexOnCommit()
    {
        using var index = Books.InRam();
        using (var writer = index.CreateWriter())
        {
            writer.DeleteAllDocuments();
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();
        Assert.Equal(0, searcher.DocumentCount);
    }

    [Fact]
    public void Rollback_DiscardsUncommittedAddsAndDeletes()
    {
        using var index = Books.InRam();
        using (var writer = index.CreateWriter())
        {
            writer.AddDocument("""{"id": "new", "title": "Uncommitted"}""");
            writer.DeleteTerm("id", "b1");
            writer.Rollback();
            writer.AddDocument("""{"id": "after", "title": "Added after rollback"}""");
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();
        Assert.Equal(5, searcher.DocumentCount);
        Assert.Equal(0, searcher.Count("uncommitted"));
        Assert.Equal(1, searcher.Count("id:b1"));
        Assert.Equal(1, searcher.Count("id:after"));
    }

    [Fact]
    public void DisposingWriter_WithoutCommit_DiscardsChanges()
    {
        using var index = Books.InRam();
        using (var writer = index.CreateWriter())
            writer.AddDocument("""{"id": "lost"}""");
        using var searcher = index.CreateSearcher();
        Assert.Equal(4, searcher.DocumentCount);
    }

    [Fact]
    public void SecondWriter_FailsWithLockBusy_UntilFirstIsDisposed()
    {
        using var index = Books.InRam();
        var first = index.CreateWriter();
        var ex = Assert.Throws<TantivyException>(() => index.CreateWriter());
        Assert.Equal(TantivyErrorCode.LockBusy, ex.Code);
        first.Dispose();
        using var second = index.CreateWriter();
    }

    [Fact]
    public void InvalidDocument_Throws()
    {
        using var index = Books.InRam();
        using var writer = index.CreateWriter();
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => writer.AddDocument("not json")).Code);
        Assert.Equal(TantivyErrorCode.InvalidArgument, Assert.Throws<TantivyException>(() => writer.AddDocument("""{"year": "nineteen"}""")).Code);
    }

    [Fact]
    public void AddDocument_WithSourceGeneratedTypeInfo()
    {
        using var index = SearchIndex.CreateInRam(Books.Schema());
        using (var writer = index.CreateWriter())
        {
            writer.AddDocument(new BookDto("g1", "Tortilla Flat", 1935), TestJsonContext.Default.BookDto);
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();
        var doc = Assert.Single(searcher.Search("tortilla")).Document;
        Assert.Equal("g1", doc.GetString("id"));
        Assert.Equal(1935UL, doc.GetU64("year"));
    }

    [Fact]
    public void QuerySyntaxErrors_SurfaceAsQueryParseException()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        Assert.Throws<QueryParseException>(() => searcher.Search("title:(unclosed"));
        Assert.Throws<QueryParseException>(() => searcher.Search("nosuchfield:x"));
        var asBase = Assert.ThrowsAny<TantivyException>(() => searcher.Search("year:[1 TO"));
        Assert.Equal(TantivyErrorCode.QueryParse, asBase.Code);

        // An unknown *default* field is an argument error, not a query syntax error.
        var ex = Assert.Throws<TantivyException>(() => searcher.Search("mice", defaultFields: ["nope"]));
        Assert.Equal(TantivyErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void Search_DefaultFieldsLimitOffsetAndCount()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();

        Assert.Equal(["b1", "b4"], Books.Ids(searcher.Search("mice")));     // all text fields
        Assert.Single(searcher.Search("rabbits"));                          // body match
        Assert.Empty(searcher.Search("rabbits", defaultFields: ["title"])); // title only
        var page1 = searcher.Search("mice", limit: 1, offset: 0);
        var page2 = searcher.Search("mice", limit: 1, offset: 1);
        Assert.Single(page1);
        Assert.Single(page2);
        Assert.Equal(2, page1.TotalCount);
        Assert.Equal(["b1", "b4"], Books.Ids(page1).Concat(Books.Ids(page2)).Order());
        Assert.True(page1[0].Score >= page2[0].Score);
        Assert.Empty(searcher.Search("mice", limit: 10, offset: 2));
        Assert.Empty(searcher.Search("mice", limit: 0));
        Assert.Equal(2, searcher.Count("mice"));
        Assert.Equal(3, searcher.Count("year:[1930 TO 1949]"));
        Assert.Equal(4, searcher.Count("*"));
    }

    [Fact]
    public void Searcher_IsASnapshot()
    {
        using var index = Books.InRam();
        using var before = index.CreateSearcher();
        using (var writer = index.CreateWriter())
        {
            writer.AddDocument("""{"id": "b5", "title": "Sweet Thursday"}""");
            writer.Commit();
        }
        using var after = index.CreateSearcher();
        Assert.Equal(4, before.DocumentCount);
        Assert.Equal(5, after.DocumentCount);
    }

    [Fact]
    public void UnicodeText_IndexesSearchesAndRoundTrips()
    {
        using var dir = new TempDirectory(suffix: "-índice-日本-🔍");
        using var index = SearchIndex.Create(dir.Path, Books.Schema());
        string title = "Café Müller — Преступление и наказание 🦀 東京";
        using (var writer = index.CreateWriter())
        {
            writer.AddDocument(new { id = "ü-1", title });
            writer.AddDocument(new { id = "ü-2", title = "Ελληνικά κείμενα" });
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();

        Assert.Equal(title, Assert.Single(searcher.Search("преступление")).Document.GetString("title"));
        Assert.Equal("ü-1", Assert.Single(searcher.Search("café")).Document.GetString("id"));
        Assert.Equal("ü-2", Assert.Single(searcher.Search("ελληνικά")).Document.GetString("id"));
        Assert.Equal(1, searcher.Count("東京"));
        Assert.Equal(1, searcher.Count("id:ü-2"));
    }

    [Fact]
    public void DisposingTwice_IsHarmless()
    {
        var index = Books.InRam();
        var writer = index.CreateWriter();
        var searcher = index.CreateSearcher();
        writer.Dispose(); writer.Dispose();
        searcher.Dispose(); searcher.Dispose();
        index.Dispose(); index.Dispose();
    }

    [Fact]
    public void UsingDisposedObjects_ThrowsObjectDisposedException()
    {
        var index = Books.InRam();
        var writer = index.CreateWriter();
        var searcher = index.CreateSearcher();
        writer.Dispose();
        searcher.Dispose();
        index.Dispose();

        Assert.Equal(nameof(IndexWriter), Assert.Throws<ObjectDisposedException>(() => writer.AddDocument("{}")).ObjectName);
        Assert.Throws<ObjectDisposedException>(() => writer.Commit());
        Assert.Throws<ObjectDisposedException>(() => writer.DeleteTerm("id", "x"));
        Assert.Equal(nameof(Searcher), Assert.Throws<ObjectDisposedException>(() => searcher.Search("mice")).ObjectName);
        Assert.Throws<ObjectDisposedException>(() => searcher.DocumentCount);
        Assert.Equal(nameof(SearchIndex), Assert.Throws<ObjectDisposedException>(() => index.CreateSearcher()).ObjectName);
        Assert.Throws<ObjectDisposedException>(() => index.CreateWriter());
        Assert.NotNull(index.Schema); // cached; still readable
    }

    [Fact]
    public void WritersAndSearchers_OutliveTheirIndex()
    {
        var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        using var writer = index.CreateWriter();
        index.Dispose();

        writer.AddDocument("""{"id": "late", "title": "Still writable"}""");
        writer.Commit();
        Assert.Equal(2, searcher.Count("mice"));
    }

    [Fact]
    public async Task ConcurrentSearches_OnOneSearcher_AreConsistent()
    {
        using var index = Books.InRam();
        using var searcher = index.CreateSearcher();
        string[] queries = ["mice", "family", "year:[1930 TO 1940]", "title:eden", "*"];
        long[] expected = [.. queries.Select(q => searcher.Count(q))];

        var tasks = Enumerable.Range(0, Environment.ProcessorCount * 2).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 300; i++)
            {
                int q = (t + i) % queries.Length;
                var results = searcher.Search(queries[q], limit: 10);
                Assert.Equal(expected[q], results.TotalCount);
                Assert.Equal((int)Math.Min(expected[q], 10), results.Count);
            }
        }, TestContext.Current.CancellationToken));
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task DisposingSearcher_DuringConcurrentSearches_NeverCrashes()
    {
        using var index = Books.InRam();
        var searcher = index.CreateSearcher();
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 2_000; i++)
            {
                try { searcher.Search("mice"); }
                catch (ObjectDisposedException) { return; }
            }
        }, TestContext.Current.CancellationToken)).ToList();
        await Task.Delay(5, TestContext.Current.CancellationToken);
        searcher.Dispose();
        await Task.WhenAll(tasks);
    }
}

public sealed record BookDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("year")] int Year);

[JsonSerializable(typeof(BookDto))]
internal partial class TestJsonContext : JsonSerializerContext;
