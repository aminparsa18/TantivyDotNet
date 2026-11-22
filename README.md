# TantivyDotNet

An idiomatic .NET binding for [Tantivy](https://github.com/quickwit-oss/tantivy), the fast full-text search engine library written in Rust (think Lucene). The goal is a NuGet package that gives you embedded full-text search in .NET without needing to know that Rust is involved.

## Status

**Early development.** Schemas, in-memory and on-disk indexes, adding and deleting documents, commit and rollback, query strings and queries built in code, snippets, facets, sorting and aggregations all work. CI builds a NuGet package for every supported platform and tests it, including from a NativeAOT app. Nothing is published on NuGet yet, and the API may still change.

## Installation

Not on nuget.org yet. Once it is:

```sh
dotnet add package TantivyDotNet
```

One package covers every supported platform. It targets .NET 10 and .NET 11, and it carries the native Tantivy library for each runtime under `runtimes/{rid}/native/`:

| RID | Notes |
| --- | --- |
| `win-x64` | C runtime linked statically, so no Visual C++ Redistributable is needed |
| `linux-x64`, `linux-arm64` | glibc 2.17 or newer (RHEL 7, Ubuntu 16.04 and Debian 9 onward); not Alpine/musl |
| `osx-x64`, `osx-arm64` | |

The library is trimming- and NativeAOT-compatible (`IsAotCompatible`). Avoid the few overloads marked `[RequiresUnreferencedCode]`; each has an AOT-safe alternative.

## Usage

```csharp
using TantivyDotNet;

var schema = new SchemaBuilder()
    .AddStringField("id", stored: true)          // exact-match: good for IDs and deletes
    .AddTextField("title", stored: true)         // tokenized full text
    .AddTextField("body")
    .AddU64Field("year", stored: true, fast: true)
    .Build();

using var index = SearchIndex.Create("books-index", schema);   // or SearchIndex.CreateInRam(schema) / SearchIndex.Open(dir)

using (var writer = index.CreateWriter(heapBytes: 50_000_000))
{
    writer.AddDocument(new { id = "b1", title = "Of Mice and Men", body = "...", year = 1937 });
    writer.AddDocument("""{"id": "b2", "title": "The Pearl", "year": 1947}""");   // raw JSON works too
    writer.DeleteTerm("id", "old-id");
    writer.Commit();                             // Dispose without Commit discards changes
}

using var searcher = index.CreateSearcher();     // a snapshot of the last commit
var hits = searcher.Search("body:mice AND year:[1930 TO 1940]", limit: 10, defaultFields: ["title", "body"]);
Console.WriteLine($"{hits.TotalCount} matches");
foreach (var hit in hits)
    Console.WriteLine($"{hit.Score} {hit.Document.GetString("title")}");
```

Queries can also be built in code and mixed with parsed ones:

```csharp
var query = Query.Phrase("body", "of mice", slop: 1)            // analyzed with the field's tokenizer
          & Query.Range("year", 1930, 1940)                     // inclusive bounds, null = open
          & !Query.Term("title", "draft")                       // exact term, not analyzed
          | Query.Fuzzy("title", "mise", distance: 1).Boost(2); // also Query.Parse("..."), Query.All, Query.Boolean(...)
var results = searcher.Search(query);
```

Beyond a limit and offset, a search takes `SearchOptions` for ordering, snippets and facet counts:

```csharp
var results = searcher.Search(query, new SearchOptions
{
    Limit = 20,
    Offset = 40,
    OrderBy = SortBy.Field("year", descending: true),       // a fast field; null = by relevance
    Snippets = new SnippetOptions("body") { MaxLength = 200 },
    Facets = [new FacetRequest("genre", "/fiction") { Top = 5 }],
    Aggregations = """{"by_decade": {"histogram": {"field": "year", "interval": 10}}}""",
});
```

- **Sorting** works on one fast field (`fast: true`) of any type except facets. Documents without a value come last, and `hit.SortValue` holds the value sorted by. Relevance isn't computed, so `hit.Score` is `NaN`.
- **Snippets** highlight matches in stored text fields:
  ```csharp
  Snippet s = results[0].Snippets["body"];
  s.ToHtml();                                            // "...a farm with <b>rabbits</b> and mice", HTML-encoded
  foreach (Range r in s.Highlights) Console.WriteLine(s.Fragment[r]);
  ```
- **Facets** are hierarchical categories (`.AddFacetField("genre")`, documents hold paths like `"/fiction/novel"`). Filter with `Query.Facet("genre", "/fiction")` and count with `FacetRequest`:
  ```csharp
  foreach (var (path, count) in results.Facets["genre"])  // counts over all matches, not just this page
      Console.WriteLine($"{path}: {count}");              // /fiction/novel: 12 ...
  ```
- **Aggregations** use Tantivy's Elasticsearch-compatible JSON (`terms`, `range`, `histogram`, `date_histogram`, `avg`, `stats`, `percentiles`, `cardinality`, `top_hits` and more, with nested `aggs`) over fast fields. They run in the same pass as the hits. Read the result as JSON or as your own types:
  ```csharp
  JsonElement aggs = results.Aggregations!.Value;          // {"by_decade": {"buckets": [{"key": 1930.0, "doc_count": 2}, ...]}}
  var typed = results.GetAggregations(MyJsonContext.Default.MyAggs);   // AOT-safe; GetAggregations<T>() uses reflection
  ```

Some behavior to know about:

- **Thread safety.** `SearchIndex` and `Searcher` are thread-safe, so share one searcher across threads. `IndexWriter` is not thread-safe; use it from one thread at a time (it already indexes on several threads internally). Only one writer can be open per index at a time.
- **Searchers are snapshots.** A searcher doesn't see commits made after it was created. Create a new one to see them; creating one is cheap.
- **Trimming and NativeAOT.** `AddDocument(object)` uses reflection-based System.Text.Json and is annotated with `[RequiresUnreferencedCode]`. In trimmed or AOT apps, use `AddDocument(doc, MyJsonContext.Default.MyType)` or pass JSON strings instead.
- **Errors.** Native errors throw `TantivyException`, whose `Code` property tells you the category. Query syntax errors throw `QueryParseException`. Using an object after it has been disposed throws `ObjectDisposedException`.
- **Documents.** Field values are matched to schema fields by name, and unknown fields are ignored. Dates must be RFC 3339 strings with an offset, so use `DateTimeOffset` or UTC `DateTime`.

## How it works

```
native/                     Rust cdylib wrapping Tantivy behind a small C ABI (exports prefixed tdn_)
  build.rs                  runs csbindgen, which generates src/TantivyDotNet/Native/NativeMethods.g.cs
src/TantivyDotNet/          the .NET library: internal generated P/Invokes plus a hand-written public API
tests/TantivyDotNet.Tests/  xUnit v3 tests
tests/TantivyDotNet.AotSmoke/  NativeAOT console app that exercises the package end to end
.github/workflows/ci.yml    native build per RID -> one package -> tests + AOT app on every platform
```

- Each Rust object is an opaque handle wrapped in a `SafeHandle`. No raw pointers are exposed publicly.
- Every export catches panics. Errors come back as status codes and turn into `TantivyException`.
- Complex shapes cross the boundary as JSON: documents, schemas, and a single search request (query tree, paging, sort, snippets, facets, aggregations) that runs in one collector pass.
- Every P/Invoke takes only blittable types, and the assembly sets `[DisableRuntimeMarshalling]`, so the calls need no marshalling stubs under NativeAOT.

## Building

Prerequisites:

- [.NET SDK 11](https://dotnet.microsoft.com/download) (pinned in `global.json`), plus the .NET 10 runtime to run the net10.0 tests
- [Rust](https://rustup.rs) stable toolchain, with `cargo` on your `PATH`

```sh
dotnet test --solution TantivyDotNet.slnx            # Debug, on net10.0 and net11.0
dotnet test --solution TantivyDotNet.slnx -c Release # Release (cargo --release, with LTO)
```

The library project calls `cargo build` before compiling C#. That also regenerates `NativeMethods.g.cs` and copies the native library into the output folder. MSBuild skips the cargo step when no file under `native/` has changed, so incremental builds stay fast. The first Release build takes a few minutes because of LTO.

### Packaging

A local `dotnet pack src/TantivyDotNet -c Release` produces a package with only your machine's native library. CI builds the full package like this:

1. Each RID's library is built on its own runner. Linux uses [cargo-zigbuild](https://github.com/rust-cross/cargo-zigbuild) to target glibc 2.17, and the job fails if a newer glibc symbol slips in.
2. One job packs the libraries with `-p:SkipNativeBuild=true -p:NativeAssetsDir=<dir with one sub-folder per RID> -p:RequireAllNativeAssets=true`.
3. On each of the five platforms, the tests and the NativeAOT smoke app are built against the packed `.nupkg` (`-p:TantivyPackageVersion=<version> -p:TantivyPackageSource=<folder>`) and run.

The package is uploaded as a workflow artifact. To try a local package the same way:

```sh
dotnet pack src/TantivyDotNet -c Release -o artifacts/packages -p:Version=0.1.0-local.1
dotnet publish tests/TantivyDotNet.AotSmoke -c Release -r osx-arm64 -o artifacts/aot \
  -p:TantivyPackageVersion=0.1.0-local.1 -p:TantivyPackageSource=$PWD/artifacts/packages
./artifacts/aot/TantivyDotNet.AotSmoke
```

## License

[MIT](LICENSE), the same license as Tantivy.
