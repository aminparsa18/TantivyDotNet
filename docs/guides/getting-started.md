# Getting started

## Install

```sh
dotnet add package TantivyDotNet --prerelease
```

The package targets .NET 10 and .NET 11 and contains the native Tantivy library for `win-x64`, `linux-x64`, `linux-arm64` (glibc 2.17 or newer), `osx-x64` and `osx-arm64`. You don't need Rust or any other native install. Alpine and other musl-based Linux, and Windows on Arm64, aren't supported yet. See [Deployment](deployment.md).

## Create an index, add documents, search

```csharp
using TantivyDotNet;

var schema = new SchemaBuilder()
    .AddStringField("id", stored: true)           // one exact term: IDs, tags, deletes
    .AddTextField("title", stored: true)          // tokenized full text
    .AddTextField("body")
    .AddU64Field("year", stored: true, fast: true)
    .Build();

using var index = SearchIndex.Create("books-index", schema);

using (var writer = index.CreateWriter())
{
    writer.AddDocument("""{"id": "b1", "title": "Of Mice and Men", "body": "Two migrant workers...", "year": 1937}""");
    writer.AddDocument("""{"id": "b2", "title": "The Pearl", "body": "A pearl diver finds...", "year": 1947}""");
    writer.Commit();
}

using var searcher = index.CreateSearcher();
var results = searcher.Search("body:pearl OR title:mice", limit: 10);
Console.WriteLine($"{results.TotalCount} matches");
foreach (var hit in results)
    Console.WriteLine($"{hit.Score:F2} {hit.Document.GetString("title")} ({hit.Document.GetU64("year")})");
```

What happens here:

1. The [schema](schema-design.md) fixes the fields and how each is indexed. It can't change after the index is created.
2. `SearchIndex.Create` makes an index in a directory. Later runs use `SearchIndex.Open("books-index")`. Use `SearchIndex.CreateInRam(schema)` for tests and throwaway indexes.
3. An [`IndexWriter`](xref:TantivyDotNet.IndexWriter) buffers changes. `Commit()` makes them durable and visible. Disposing a writer without committing discards its changes.
4. A [`Searcher`](xref:TantivyDotNet.Searcher) is a snapshot of the last commit. It's thread-safe and should be shared. Create a new one, or use a [`SearcherManager`](xref:TantivyDotNet.SearcherManager), to see later commits. See [Thread safety and lifetime](thread-safety.md).

## Documents

A document is a JSON object that maps field names to a value or an array of values. You can pass JSON text, UTF-8 bytes, or an object serialized with System.Text.Json:

```csharp
// AOT-safe: source-generated metadata
[JsonSerializable(typeof(Book))]
partial class BookJsonContext : JsonSerializerContext;

record Book(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("year")] ulong Year);

writer.AddDocument(new Book("b3", "Cannery Row", 1945), BookJsonContext.Default.Book);
writer.UpdateDocument(Query.Term("id", "b3"), new Book("b3", "Cannery Row", 1945), BookJsonContext.Default.Book);
writer.DeleteTerm("id", "b1");
```

Field names are case-sensitive. A property that isn't a schema field throws [`TantivyException`](xref:TantivyDotNet.TantivyException), so a typo can't be silently ignored. To drop unknown properties instead, create the writer with `new IndexWriterOptions { IgnoreUnknownFields = true }`.

Search hits contain only fields marked `stored`. Read them with the typed getters on [`Document`](xref:TantivyDotNet.Document) (`GetString`, `GetU64`, `GetDate`, ...) or as JSON with `ToJson()`.

## Next

- [Queries](queries.md): the query language, queries built in code, sorting, paging, snippets, facets and aggregations.
- [Analyzers](analyzers.md): stemming, stop words, n-grams and other text processing.
- [Deployment](deployment.md): containers, NativeAOT and trimming.
- The [API reference](xref:TantivyDotNet) documents every public type.
