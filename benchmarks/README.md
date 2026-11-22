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

Close other applications first. Indexing is CPU- and disk-bound, and the 4-thread runs use most of a laptop.
