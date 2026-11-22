# WikiSearch

A small search site over Wikipedia abstracts, built on TantivyDotNet in about 200 lines of C# plus one HTML page.

It shows:

- **Indexing** a JSON-lines file by passing each line straight to `AddDocument(string)`.
- **Query syntax** from a search box: words, `"phrases"`, `"sloppy phrases"~2`, `AND`/`OR`/`-excluded`, `title:word`. A syntax error becomes an HTTP 400 with the parser's message (`QueryParseException`).
- **Queries built in code**: a popularity filter (`Query.Range`), and a typo-tolerant retry (`Query.Fuzzy`) when a single word finds nothing.
- **Snippets** with highlighted matches (`SnippetOptions`, `Snippet.ToHtml`).
- **Sorting** by a fast field (most-linked articles first) and **paging**.
- **Aggregations**: a `range` aggregation over incoming links, computed in the same pass as the hits, used as filter chips.
- **NativeAOT**: the app is trimming- and AOT-safe, so it publishes as a single native binary.

## Run it

You need a corpus first. The benchmarks' `prepare` command streams a few GB of the Wikipedia dump and writes `artifacts/corpus/wiki-abstracts.jsonl`:

```sh
dotnet run -c Release --project benchmarks/TantivyDotNet.Benchmarks -- prepare
dotnet run -c Release --project samples/WikiSearch
```

Then open the URL it prints (http://localhost:5000 by default). On the first start it indexes the corpus into `artifacts/wiki-index`, and later starts reuse that index. Options:

```sh
dotnet run -c Release --project samples/WikiSearch -- --corpus my.jsonl --index my-index --urls http://localhost:8080
```

Any JSON-lines file with `id`, `title`, `body` and `links` (a number) fields works as a corpus.

As a native binary:

```sh
dotnet publish samples/WikiSearch -c Release -r osx-arm64 -o artifacts/wikisearch
./artifacts/wikisearch/WikiSearch --index artifacts/wiki-index
```

The API behind the page is `GET /api/search?q=...&page=1&sort=relevance|links&minLinks=&maxLinks=`.
