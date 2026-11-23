# TantivyDotNet

An idiomatic .NET binding for [Tantivy](https://github.com/quickwit-oss/tantivy), the fast full-text search engine library written in Rust (think Lucene). The goal is a NuGet package that gives you embedded full-text search in .NET without needing to know that Rust is involved.

![Benchmark results: TantivyDotNet indexes 2-3× faster than Lucene.NET and answers term, AND, OR and sorted queries 4-7× faster; phrase queries are 4% slower, and the index is 4% larger.](https://raw.githubusercontent.com/aminparsa18/TantivyDotNet/main/benchmarks/results/summary.svg)

What was measured, and how, is under [Performance](#performance).

## Status

The project is in early development. Schemas (including JSON and IP address fields), custom analyzers, in-memory and on-disk indexes, adding, updating and deleting documents, commit and rollback, query strings and queries built in code, snippets, facets, multi-key sorting with cursor paging, aggregations, score explanations, merge control, index settings, index statistics and shared searchers all work. CI builds a NuGet package for every supported platform and tests it, including from a NativeAOT app. Preview releases are [on nuget.org](https://www.nuget.org/packages/TantivyDotNet); the API may still change before 1.0.

## Installation

```sh
dotnet add package TantivyDotNet --prerelease
```

One package covers every supported platform. It targets .NET 10 and .NET 11, and it carries the native Tantivy library for each runtime under `runtimes/{rid}/native/`:

| RID | Notes |
| --- | --- |
| `win-x64` | C runtime linked statically, so no Visual C++ Redistributable is needed |
| `linux-x64`, `linux-arm64` | glibc 2.17 or newer (RHEL 7, Ubuntu 16.04 and Debian 9 onward); not Alpine/musl |
| `osx-x64`, `osx-arm64` | |

Not supported yet: Alpine and other musl-based Linux (`linux-musl-*`), and Windows on Arm64 (`win-arm64`). On Windows on Arm, an x64 process (running under emulation) works.

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
    writer.UpdateDocument(Query.Term("id", "b2"), """{"id": "b2", "title": "The Pearl (2nd ed.)", "year": 1947}""");
    writer.DeleteTerm("id", "old-id");           // or DeleteDocuments(anyQuery)
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

Other query types: `Query.Regex`, `Query.Exists` (fast fields), `Query.TermSet`, `Query.DisjunctionMax`, `query.ConstantScore()` and `Query.MoreLikeThis(hit.Document)`. For text typed by users, parse with options:

```csharp
var parser = new QueryParserOptions
{
    DefaultFields = ["title", "body"],
    DefaultOperator = QueryOperator.And,                         // "a b" means a AND b
    FieldBoosts = new Dictionary<string, float> { ["title"] = 3 },
    FuzzyFields = new Dictionary<string, FuzzyOptions> { ["body"] = new(distance: 1) },
    Lenient = true,                                              // drop what doesn't parse instead of throwing
};
var userResults = searcher.Search(Query.Parse(userInput, parser));
Console.WriteLine(searcher.Explain(query, results[0]));          // how the score was computed
```

Beyond a limit and offset, a search takes `SearchOptions` for ordering, returned fields, snippets, facet counts and aggregations:

```csharp
var results = searcher.Search(query, new SearchOptions
{
    Limit = 20,
    Offset = 40,
    OrderBy = [SortBy.Field("year", descending: true), SortBy.Score()],   // fast fields and/or score; null = by relevance
    Fields = ["title"],                                      // stored fields to return; null = all
    Snippets = new SnippetOptions("body") { MaxLength = 200 },
    Facets = [new FacetRequest("genre", "/fiction") { Top = 5 }],
    Aggregations = """{"by_decade": {"histogram": {"field": "year", "interval": 10}}}""",
});
```

- Sorting takes one or more keys: fast fields (`fast: true`) of any type except facets and JSON, and `SortBy.Score()`. Documents without a value come last, and `hit.SortValues` holds the values sorted by. Unless the score is a key, relevance isn't computed, so `hit.Score` is `NaN`.
- For deep paging, don't use `Offset`, which gets slower as it grows. Pass the last hit's `Cursor` as `SearchAfter` (with the same `OrderBy`) to get the next page:
  ```csharp
  var next = searcher.Search(query, new SearchOptions { Limit = 20, OrderBy = order, SearchAfter = results[^1].Cursor });
  ```
- Snippets highlight matches in stored text fields:
  ```csharp
  Snippet s = results[0].Snippets["body"];
  s.ToHtml();                                            // "...a farm with <b>rabbits</b> and mice", HTML-encoded
  foreach (Range r in s.Highlights) Console.WriteLine(s.Fragment[r]);
  ```
- Facets are hierarchical categories (`.AddFacetField("genre")`, documents hold paths like `"/fiction/novel"`). Filter with `Query.Facet("genre", "/fiction")` and count with `FacetRequest`:
  ```csharp
  foreach (var (path, count) in results.Facets["genre"])  // counts over all matches, not just this page
      Console.WriteLine($"{path}: {count}");              // /fiction/novel: 12 ...
  ```
- Aggregations use Tantivy's Elasticsearch-compatible JSON (`terms`, `range`, `histogram`, `date_histogram`, `avg`, `stats`, `percentiles`, `cardinality`, `top_hits` and more, with nested `aggs`) over fast fields. They run in the same pass as the hits. Read the result as JSON or as your own types:
  ```csharp
  JsonElement aggs = results.Aggregations!.Value;          // {"by_decade": {"buckets": [{"key": 1930.0, "doc_count": 2}, ...]}}
  var typed = results.GetAggregations(MyJsonContext.Default.MyAggs);   // AOT-safe; GetAggregations<T>() uses reflection
  ```

### Analyzers

Text fields use a built-in tokenizer (`default`, `raw`, `en_stem`, `whitespace`) or a custom analyzer defined in the schema. An index created in a directory saves its analyzers, and `SearchIndex.Open` restores them. `index.Analyze("en_text", "Running cafés")` shows the terms produced.

```csharp
var schema = new SchemaBuilder()
    .AddAnalyzer("en_text", Analyzer.Simple().LowerCase().AsciiFolding()
        .StopWords(AnalyzerLanguage.English).Stem(AnalyzerLanguage.English))
    .AddAnalyzer("prefix", Analyzer.NGram(1, 10, prefixOnly: true).LowerCase())   // search-as-you-type
    .AddTextField("body", tokenizer: "en_text")
    .AddJsonField("attrs", stored: true, fast: true)   // schemaless: Query.Term("attrs.color", "red")
    .AddIpAddressField("ip", fast: true)               // Query.Range("ip", IPAddress.Parse("10.0.0.0"), ...)
    .Build();
```

### Index lifecycle

Settings fixed at creation are saved with the index. Merge behavior is chosen per writer:

```csharp
using var index = SearchIndex.Create("books-index", schema, new IndexSettings
{
    DocStoreCompression = DocStoreCompression.Zstd, ZstdLevel = 9,   // stored fields; default Lz4
    DocStoreBlockSize = 64 * 1024,
});
using (var writer = index.CreateWriter(new IndexWriterOptions { MergePolicy = new LogMergePolicy { MinSegments = 4 } }))  // or MergePolicy.None
{
    // ... bulk load, Commit() ...
    writer.ForceMerge();               // for a read-mostly index: merge down to 1 segment, dropping deleted documents
    writer.GarbageCollectFiles();      // delete files no longer used (also done after commits and merges)
}
IndexStats stats = index.GetStats();   // or searcher.GetStats() for that snapshot
Console.WriteLine(stats);              // "1 segments, 1000 docs (0 deleted), 81920 bytes"
foreach (var f in stats.Fields) Console.WriteLine($"{f.Name}: {f.TotalBytes} bytes ({f.PostingsBytes} postings)");
```

`IndexSettings.SortBy` (store documents ordered by a fast field) is experimental, because Tantivy has removed this feature once before; using it raises warning `TDN0001`.

### Sharing a searcher

In a server, a `SearcherManager` keeps one searcher for all requests and replaces it when the index changes. A lease keeps its searcher valid until disposed, even after it's replaced:

```csharp
using var manager = index.CreateSearcherManager(new SearcherManagerOptions
{
    RefreshOnCommit = true,                           // pick up commits (also from other processes) automatically
    Warmer = s => s.Search("common query", limit: 1), // runs on each new searcher before requests see it
});
using (var lease = manager.Acquire())                 // per request
    lease.Searcher.Search(query);
manager.Refresh();                                    // or reload explicitly, e.g. right after your own Commit()
```

With `RefreshOnCommit`, a commit shows up shortly after `Commit()` returns (Tantivy reloads in the background), not immediately.

### Behavior to know about

- A commit is atomic: if the process dies (even during `Commit()`), reopening the index gives the last completed commit, and uncommitted documents are lost. The writer lock is an OS file lock, so it's released when the process dies and the next process can open a writer right away. Files of an interrupted commit or merge are deleted by a later commit or `GarbageCollectFiles()`. The tests check this by killing a child process in the middle of commits.
- `IndexWriter.Dispose()` waits for background merges. A writer that's never disposed is cleaned up by its finalizer, which doesn't wait: it abandons running merges and releases the lock.
- `SearchIndex` and `Searcher` are thread-safe, so share one searcher across threads. `IndexWriter` is not thread-safe; use it from one thread at a time (it already indexes on several threads internally). Only one writer can be open per index at a time.
- A searcher is a snapshot: it doesn't see commits made after it was created. Create a new one to see them (creating one is cheap), or use a `SearcherManager`. An old snapshot keeps searching after a merge replaced its segments, but `GetStats()` on it can then fail, because sizes are read by reopening the deleted files.
- `AddDocument(object)` uses reflection-based System.Text.Json and is annotated with `[RequiresUnreferencedCode]`. In trimmed or AOT apps, use `AddDocument(doc, MyJsonContext.Default.MyType)` or pass JSON strings instead.
- Native errors throw `TantivyException`, whose `Code` property tells you the category. Query syntax errors throw `QueryParseException`. Using an object after it has been disposed throws `ObjectDisposedException`.
- Field values in documents are matched to schema fields by name, case-sensitively. A property that isn't a schema field throws `TantivyException` (so `new { Title = ... }` doesn't silently miss a `title` field); create the writer with `new IndexWriterOptions { IgnoreUnknownFields = true }` to drop such properties instead. Dates must be RFC 3339 strings with an offset, so use `DateTimeOffset` or UTC `DateTime`.

## Performance

Compared with [Lucene.NET](https://lucenenet.apache.org/) 4.8.0-beta00018 on 215,778 English Wikipedia articles: title plus lead section, 171 MiB of text. Both engines index the same fields (id, title and body stored, plus a numeric column for sorting) and give each writer the same 256 MB indexing buffer. Neither uses stemming or stop words. The results are in the chart at the top of this page.

Measured with BenchmarkDotNet 0.15.8 on .NET 10, macOS 26, Intel Core i5-1038NG7 (4 cores / 8 threads), from an SSD. The exact figures are in [benchmarks/results/results.json](benchmarks/results/results.json). Some notes on what this measures:

- Indexing starts from articles already in memory as .NET objects, so building each engine's documents is included: serializing to JSON for TantivyDotNet, filling reused `Field` objects for Lucene. With N threads, Tantivy gets N indexing threads fed from one caller thread, while Lucene gets N threads calling `AddDocument`. Closing the writer waits for background merges in both engines.
- Queries run single-threaded against an open searcher, using queries built in code. TantivyDotNet's figure includes the cost of crossing the native boundary (the request and results travel as JSON). Allocation figures (17 KB vs 62 KB per term query, for example) count managed memory only; Tantivy's native allocations aren't included.
- Match counts differ slightly because the tokenizers differ. Lucene keeps `science's` and `States.The` as single tokens; Tantivy's default tokenizer splits them at the punctuation. Tantivy's matches are a superset of Lucene's in every query: 1 extra for each term query, 22 extra for the phrase.
- Lucene is a little faster only on phrase queries. The time is spent in Tantivy's phrase matching: called directly from Rust, Tantivy takes about as long for this query, and the binding adds almost nothing (see [where the time goes](benchmarks/README.md#where-the-time-goes-native-harnesses)).

To run it yourself, see [benchmarks/README.md](benchmarks/README.md).

## Sample app

[samples/WikiSearch](samples/WikiSearch) is a small search site over the same corpus, about 200 lines of C# plus one HTML page. It has highlighted snippets, sorting, paging, a popularity filter built from an aggregation, and a typo-tolerant fallback, and it publishes as a single NativeAOT binary.

## How it works

```
native/                     Rust cdylib wrapping Tantivy behind a small C ABI (exports prefixed tdn_)
  build.rs                  runs csbindgen, which generates src/TantivyDotNet/Native/NativeMethods.g.cs
src/TantivyDotNet/          the .NET library: internal generated P/Invokes plus a hand-written public API
tests/TantivyDotNet.Tests/  xUnit v3 tests
tests/TantivyDotNet.AotSmoke/  NativeAOT console app that exercises the package end to end
tests/TantivyDotNet.CompatFixture/  writes the index a released version produces, for the compatibility tests
benchmarks/                 BenchmarkDotNet comparison with Lucene.NET
samples/WikiSearch/         sample web app
.github/workflows/ci.yml    native build per RID -> one package -> tests + AOT app on every platform
```

- Each Rust object is an opaque handle wrapped in a `SafeHandle`. No raw pointers are exposed publicly.
- Every export catches panics. Errors come back as status codes and turn into `TantivyException`.
- Complex shapes cross the boundary as JSON: documents, schemas, and a search request (query tree, paging, sort, snippets, facets, aggregations) that is answered in one native call.
- Every P/Invoke takes only blittable types, and the assembly sets `[DisableRuntimeMarshalling]`, so the calls need no marshalling stubs under NativeAOT.

## Upgrades

An index is a directory in [Tantivy's](https://github.com/quickwit-oss/tantivy) on-disk format. Each TantivyDotNet release is built against one Tantivy version (currently 0.27). That version reads a range of index format versions (currently 4 to 7) and writes the newest. Our own analyzer file (`tantivy-dotnet.json`) has its own version number.

- **Upgrading TantivyDotNet.** A new release opens indexes written by every earlier release whose index format its Tantivy still reads. New segments, from commits and merges, are written in the current format. Old segments are rewritten when they are merged, for example with `ForceMerge`.
- **Downgrading isn't supported.** Once a newer release has written to an index, older releases may not be able to read it.
- **When a reindex is needed.** If a Tantivy upgrade drops a format your index still uses, `SearchIndex.Open` (or creating a searcher) throws `TantivyException` with `Code == TantivyErrorCode.IncompatibleIndex`. The message names the format the index uses and the formats this release reads. Rebuild the index from your source data. Running `ForceMerge(1)` with the old release does not help, because it writes the format that release already uses. Release notes call out any release that drops a format. From 1.0 on, dropping read support for indexes written by an earlier 1.x release counts as a breaking change, which means a new major version.

The compatibility tests (`tests/TantivyDotNet.Tests/Compat`) enforce this. `Compat/Indexes/<version>.zip` holds an index written by each released version: every field type, a custom analyzer, a zstd doc store, three segments and deletes. The tests open each one and check that it gives the same results as an index written by the current code from the same data, and that it accepts writes and merges. After each release, add its fixture:

```sh
dotnet run --project tests/TantivyDotNet.CompatFixture -p:TantivyCompatVersion=<released version>
```

To upgrade Tantivy: bump it in `native/Cargo.toml`, and read its changelog for changes to the index format. Run the full test suite, which includes the fixtures of every release. Regenerate `THIRD-PARTY-NOTICES.txt`. If a fixture no longer opens, the new Tantivy dropped that format. Remove the fixture only as part of a release that announces the reindex. Before 1.0 that's a minor version; from 1.0 on, a major one.

## Building

Prerequisites:

- [.NET SDK 11](https://dotnet.microsoft.com/download) (pinned in `global.json`), plus the .NET 10 runtime to run the net10.0 tests
- [Rust](https://rustup.rs) via rustup, with `cargo` on your `PATH`. The Rust version is pinned in `native/rust-toolchain.toml`, and rustup installs it on first use.

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

The package is uploaded as a workflow artifact, with its symbol package.

What the package carries besides the code:

- **Symbols.** A `.snupkg` holds the portable PDBs. [SourceLink](https://github.com/dotnet/sourcelink) points them at the exact commit on GitHub, so you can step into TantivyDotNet's source while debugging. The native library is built with line tables. CI splits them off (a `.pdb` on Windows, a `.dSYM` on macOS, a `.debug` file on Linux), so the packed library stays small. Each release attaches them to its [GitHub Release](https://github.com/aminparsa18/TantivyDotNet/releases) as `tantivy_dotnet-<version>-<rid>-symbols.zip` for symbolicating native crash dumps.
- **Third-party notices.** `THIRD-PARTY-NOTICES.txt` lists the license of every Rust crate compiled into the native library. [cargo-about](https://github.com/EmbarkStudios/cargo-about) generates it from `native/Cargo.lock` (configured in `native/about.toml`), and CI fails if the committed copy is out of date. After changing Rust dependencies, regenerate it with `cd native && cargo about generate --locked --offline about.hbs -o ../THIRD-PARTY-NOTICES.txt` (requires `cargo install cargo-about --features cli`).
- **Public API tracking.** `src/TantivyDotNet/PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` list every public symbol ([PublicApiAnalyzers](https://github.com/dotnet/roslyn-analyzers/blob/main/src/PublicApiAnalyzers/PublicApiAnalyzers.Help.md)). Adding API without listing it, or removing listed API, fails the build. New API goes into Unshipped. At a stable release, Unshipped moves into Shipped. Package validation (`EnablePackageValidation`) checks the package's target frameworks against each other; from 1.0 on it also checks against the previous release (`PackageValidationBaselineVersion`).

Supply chain checks in CI: [cargo-deny](https://github.com/EmbarkStudios/cargo-deny) (`native/deny.toml`) fails on non-permissive licenses, RustSec advisories, yanked crates and unknown sources. Dependabot (`.github/dependabot.yml`) opens weekly updates for NuGet packages, crates, the .NET SDK and GitHub Actions. Every action is pinned to a commit SHA. The Rust toolchain and the tools installed inside workflows are bumped by hand.

To release, push a version tag (`git tag v0.1.0-preview.1 && git push origin v0.1.0-preview.1`). `publish.yml` runs the whole pipeline above for that tag. It then pushes the tested package and its symbols to nuget.org with [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing), so no API key is stored, and creates the GitHub Release with the native symbols. The tag without its `v` becomes the package version. After a release, add its compatibility fixture (see [Upgrades](#upgrades)).

To try a local package the same way:

```sh
dotnet pack src/TantivyDotNet -c Release -o artifacts/packages -p:Version=0.1.0-local.1
dotnet publish tests/TantivyDotNet.AotSmoke -c Release -r osx-arm64 -o artifacts/aot \
  -p:TantivyPackageVersion=0.1.0-local.1 -p:TantivyPackageSource=$PWD/artifacts/packages
./artifacts/aot/TantivyDotNet.AotSmoke
```

## License

[MIT](LICENSE), the same license as Tantivy.
