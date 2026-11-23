---
_layout: landing
---

# TantivyDotNet

Fast embedded full-text search for .NET, powered by [Tantivy](https://github.com/quickwit-oss/tantivy), the Rust search engine library. Install one NuGet package and get Lucene-style indexing and search, with no Rust toolchain and no server to run.

```sh
dotnet add package TantivyDotNet --prerelease
```

```csharp
using TantivyDotNet;

var schema = new SchemaBuilder()
    .AddStringField("id", stored: true)
    .AddTextField("title", stored: true)
    .AddU64Field("year", stored: true, fast: true)
    .Build();

using var index = SearchIndex.CreateInRam(schema);
using (var writer = index.CreateWriter())
{
    writer.AddDocument("""{"id": "b1", "title": "Of Mice and Men", "year": 1937}""");
    writer.Commit();
}

using var searcher = index.CreateSearcher();
foreach (var hit in searcher.Search("title:mice"))
    Console.WriteLine(hit.Document.GetString("title"));
```

- **Features:** custom analyzers, a query language and queries built in code, snippets, facets, multi-key sorting with cursor paging, Elasticsearch-style aggregations, score explanations, merge control and shared searchers.
- **Platforms:** .NET 10 and 11 on Windows x64, Linux x64/arm64 (glibc) and macOS x64/arm64. Trimming- and NativeAOT-compatible.
- **Speed:** indexes 2-3× faster than Lucene.NET 4.8 and answers most queries 4-7× faster on the [benchmark](https://github.com/aminparsa18/TantivyDotNet#performance).

## Where to go next

- [Getting started](guides/getting-started.md)
- [Schema design](guides/schema-design.md), [analyzers](guides/analyzers.md) and [queries](guides/queries.md)
- [Thread safety and lifetime](guides/thread-safety.md) and [deployment](guides/deployment.md)
- [Migrating from Lucene.NET](guides/migrating-from-lucene.md)
- [Versioning and upgrades](guides/versioning.md)
- [API reference](api/index.md)

The project is in preview: the API may still change before 1.0. Source, issues and discussions are on [GitHub](https://github.com/aminparsa18/TantivyDotNet).
