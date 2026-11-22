# Benchmarks

TantivyDotNet compared with Lucene.NET 4.8 on the same corpus. The results and what they measure are in the [main README](../README.md#performance).

## Corpus

English Wikipedia articles from the [CirrusSearch dump of 2025-12-29](https://dumps.wikimedia.org/other/cirrussearch/20251229/). Each article contributes its title and lead section (`opening_text`). The old `abstract.xml` dumps no longer exist, and the lead section is the same thing. The dump is 43 GB, so it's streamed, and reading stops after N articles or a download cap, whichever comes first. Only the extracted JSON lines are written to disk:

```sh
dotnet run -c Release --project benchmarks/TantivyDotNet.Benchmarks -- prepare            # 300,000 articles or 4,000 MB
dotnet run -c Release --project benchmarks/TantivyDotNet.Benchmarks -- prepare 50000 1000 # smaller
```

The published results used the default settings. The 4 GB cap was reached first, at 215,778 articles (184 MiB of JSON lines, about 40 minutes at 1.6 MB/s). Progress is printed every 10 seconds. The output goes to `artifacts/corpus/wiki-abstracts.jsonl`, or to `$TDN_BENCH_CORPUS` if set.

## Running

```sh
dotnet run -c Release --project benchmarks/TantivyDotNet.Benchmarks -- --filter '*'              # everything, ~20 min
dotnet run -c Release --project benchmarks/TantivyDotNet.Benchmarks -- --filter '*Query*'        # query latency only
dotnet run -c Release --project benchmarks/TantivyDotNet.Benchmarks -- size                      # index size
dotnet run -c Release --project benchmarks/TantivyDotNet.Benchmarks -- compare                   # where the engines' matches differ
```

- `IndexingBenchmarks` builds a fresh on-disk index per iteration under `artifacts/bench/`, with 1 and 4 threads.
- `QueryBenchmarks` and `size` share one index per engine, built once under `artifacts/bench/shared` and rebuilt when the corpus file changes. Each query benchmark first checks that both engines return the same number of matches and prints a warning if they don't. Small differences come from the tokenizers (see the main README).
- `compare` lists the articles only one engine matches, for each query.

## Where the time goes (native harnesses)

Two Rust programs in `native/examples` run the same work without .NET, to separate Tantivy's own cost from the binding's. They use the corpus and the shared index above (run the .NET benchmarks once first).

```sh
cd native
cargo run --release --example query_timing   # the 5 benchmark queries
cargo run --release --example index_timing   # indexing the corpus with 1 and 4 threads
```

`query_timing` prints, per query: Tantivy alone (count + top 10), plus loading the stored documents, the count and top 10 as two separate searches, the same in a `MultiCollector`, writing 10 documents as JSON, and our whole native search (`search::run`, request JSON in, result JSON out). The .NET benchmark minus `ours` is the cost of the FFI call and the managed side. It also checks that the documents in our result JSON are exactly Tantivy's `NamedFieldDocument` JSON.

`index_timing` parses every document with our parser and indexes it with Tantivy's writer, so its time compared with `IndexingBenchmarks` is what .NET adds (serializing each article and one FFI call per document).

## Publishing results

BenchmarkDotNet writes its reports, including a JSON export, to `artifacts/BenchmarkDotNet/results`. To update the published results and the chart in the main README after a full run:

```sh
dotnet run -c Release --project benchmarks/TantivyDotNet.Benchmarks -- export   # results.json + summary.svg
dotnet run -c Release --project benchmarks/TantivyDotNet.Benchmarks -- chart    # redraw summary.svg from results.json only
```

`export` reads the newest JSON report of each benchmark class, measures the shared indexes' size, and writes [results/results.json](results/results.json) and [results/summary.svg](results/summary.svg). The chart draws each measurement relative to Lucene.NET, with the absolute values at the bar ends. It's a self-contained SVG with a light and a dark variant (chosen by the viewer's color scheme). The README links it by absolute URL because the README is also the NuGet package's readme, so a changed chart shows up on nuget.org only after it's pushed to `main`.

Close other applications first. Indexing is CPU- and disk-bound, and the 4-thread runs use most of a laptop.
