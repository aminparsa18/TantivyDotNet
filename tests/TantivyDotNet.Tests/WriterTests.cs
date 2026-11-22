namespace TantivyDotNet.Tests;

public class WriterTests
{
    [Fact]
    public void UnknownField_IsRejectedByDefault_WithCasingHint()
    {
        using var index = SearchIndex.CreateInRam(Books.Schema());
        using var writer = index.CreateWriter();

        var e = Assert.Throws<TantivyException>(() => writer.AddDocument("""{"id": "x", "Title": "Wrong case"}"""));
        Assert.Equal(TantivyErrorCode.InvalidArgument, e.Code);
        Assert.Contains("'Title'", e.Message);
        Assert.Contains("did you mean 'title'", e.Message);

        var other = Assert.Throws<TantivyException>(() => writer.AddDocument("""{"id": "x", "author": "Steinbeck"}"""));
        Assert.Contains("id, title, body, year", other.Message);

        writer.Commit();
        using var searcher = index.CreateSearcher();
        Assert.Equal(0, searcher.DocumentCount);
    }

    [Fact]
    public void UnknownField_IsDropped_WhenIgnoreUnknownFields()
    {
        using var index = SearchIndex.CreateInRam(Books.Schema());
        using (var writer = index.CreateWriter(new IndexWriterOptions { IgnoreUnknownFields = true }))
        {
            writer.AddDocument("""{"id": "x", "title": "Kept", "author": "Dropped"}""");
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();
        var hit = Assert.Single(searcher.Search(Query.All));
        Assert.Equal("Kept", hit.Document.GetString("title"));
        Assert.DoesNotContain("author", hit.Document.ToJson());
    }

    [Fact]
    public void IndexWriterOptions_ValidateValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IndexWriterOptions { HeapBytes = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new IndexWriterOptions { ThreadCount = 0 });
        using var index = SearchIndex.CreateInRam(Books.Schema());
        using var writer = index.CreateWriter(new IndexWriterOptions { HeapBytes = 20_000_000, ThreadCount = 1 });
    }

    [Fact]
    public void UpdateDocument_ReplacesByTerm()
    {
        using var index = Books.InRam();
        using (var writer = index.CreateWriter())
        {
            writer.UpdateDocument(Query.Term("id", "b1"), """{"id": "b1", "title": "Of Mice and Men (2nd ed.)", "body": "revised", "year": 1938}""");
            writer.UpdateDocument(Query.Term("id", "b9"), """{"id": "b9", "title": "New", "body": "added because no b9 existed", "year": 2000}""");
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();
        Assert.Equal(5, searcher.DocumentCount);
        var b1 = Assert.Single(searcher.Search(Query.Term("id", "b1")));
        Assert.Equal("Of Mice and Men (2nd ed.)", b1.Document.GetString("title"));
        Assert.Equal(1938UL, b1.Document.GetU64("year"));
    }

    [Fact]
    public void UpdateDocument_ByNumericTerm_AndTypedDocument()
    {
        using var index = Books.InRam();
        using (var writer = index.CreateWriter())
        {
            // Every book from 1939 is replaced by one document.
            writer.UpdateDocument(Query.Term("year", 1939UL), new BookDto("b2", "Grapes", 1939), TestJsonContext.Default.BookDto);
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();
        var hit = Assert.Single(searcher.Search(Query.Term("year", 1939L)));
        Assert.Equal("Grapes", hit.Document.GetString("title"));
    }

    [Fact]
    public void UpdateDocument_WithInvalidDocument_DeletesNothing()
    {
        using var index = Books.InRam();
        using (var writer = index.CreateWriter())
        {
            Assert.Throws<TantivyException>(() => writer.UpdateDocument(Query.Term("id", "b1"), """{"id": "b1", "year": "not a number"}"""));
            var badTerm = Assert.Throws<TantivyException>(() => writer.UpdateDocument(Query.Term("year", "text"), """{"id": "b1"}"""));
            Assert.Equal(TantivyErrorCode.InvalidArgument, badTerm.Code);
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();
        Assert.Equal(4, searcher.DocumentCount);
        Assert.Single(searcher.Search(Query.Term("id", "b1")));
    }

    [Fact]
    public void DeleteDocuments_ByQuery_OnlyAffectsEarlierDocuments()
    {
        using var index = Books.InRam();
        using (var writer = index.CreateWriter())
        {
            writer.DeleteDocuments(Query.Range("year", null, 1940L));                 // b1, b2
            writer.AddDocument("""{"id": "b5", "title": "Tortilla Flat", "body": "", "year": 1935}""");  // added after: kept
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();
        Assert.Equal(["b3", "b4", "b5"], Books.Ids(searcher.Search(Query.All)));
    }

    [Fact]
    public void DeleteDocuments_WithParsedQuery()
    {
        using var index = Books.InRam();
        using (var writer = index.CreateWriter())
        {
            writer.DeleteDocuments(Query.Parse("body:mice"));
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();
        Assert.Equal(["b2", "b3"], Books.Ids(searcher.Search(Query.All)));
    }
}
