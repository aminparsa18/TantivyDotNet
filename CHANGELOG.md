# Changelog

All notable changes to TantivyDotNet are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org) as described in the [versioning policy](https://aminparsa18.github.io/TantivyDotNet/guides/versioning.html). Before 1.0, minor versions may contain breaking changes; they're marked **Breaking**.

## [Unreleased]

### Changed

- Query text that nests parentheses or `NOT` more than 32 levels deep is no longer parsed. With `QueryParserOptions.Lenient`, it matches nothing; otherwise it throws `QueryParseException`. Queries built in code (`Query.Boolean` and the other factories) aren't affected.

### Fixed

- A `SearchAfter` cursor used on a newer searcher, for example after a `SearcherManager` refresh, could return its own hit again and skip or repeat other documents tied with it on every sort key, because it compared document addresses that a merge had renumbered. A cursor now knows which snapshot it came from. On another snapshot, paging continues by sort value and skips the documents tied with the cursor; with a unique last sort key, such as an id, nothing is skipped or repeated. The `SearchAfter` documentation and the paging guide describe what happens to a cursor when the index changes. Cursors from 1.0.0-rc.1 still work and are treated as coming from another snapshot.

## [1.0.0-rc.1] - 2026-11-23

The first release candidate. The public API is frozen: it's listed in `PublicAPI.Shipped.txt`, and 1.0.0 will ship it unchanged unless feedback on this release candidate turns up a problem. Any such change will be listed here.

### Changed

- **Breaking**: `FieldDefinition`, `SegmentStats`, `FieldStats` and `GarbageCollectionResult` no longer have public constructors or `Deconstruct` methods, and their properties are get-only. They're results the library creates, and this lets later 1.x releases add properties to them. Read the properties instead of deconstructing.
- `SearchHit.Snippets` and `SearchHit.SortValues` no longer have public `init` accessors (they couldn't be used, since `SearchHit` has no public constructor).

### Added

- `TantivyErrorCode.IncompatibleIndex`: opening an index whose Tantivy format this release can't read throws `TantivyException` with this code and a message naming the formats involved.
- Symbol package (`.snupkg`) with SourceLink, so you can step into TantivyDotNet's source while debugging. Native debug symbols for each platform are attached to the GitHub Release.
- `THIRD-PARTY-NOTICES.txt` in the package, listing the licenses of the Rust crates compiled into the native library.
- Documentation site with an API reference and guides: schema design, analyzers, queries, thread safety and lifetime, deployment, migrating from Lucene.NET, and versioning.
- Versioning policy, contributing guide, security policy, and issue and pull request templates.

### Fixed

- On Windows, `Commit()` could fail with "Access is denied" while searchers or another `SearchIndex` on the same directory were reloading. Index metadata is now replaced with a rename that works while readers have the file open, and briefly retried if Windows still refuses.
- A failed `Commit()` could later be published partially by a background merge, so readers saw documents from a commit that had thrown. A failed commit now rolls the writer back (the writer stays usable), and `Commit()`'s documentation says so.
- `Rollback()` reset the writer's merge policy to Tantivy's default. The policy from `IndexWriterOptions` is now kept.

## [0.4.0-preview.1] - 2026-11-23

### Changed

- Faster searches: about 35% less time for term queries, 25% for OR queries and 40% for sorted queries, and about a third less managed memory per search. Counting a lone term query no longer visits the matches.

### Fixed

- Inputs that made the native library panic or allocate without bound, found by fuzzing: field names that are empty or start with `-` (now rejected by `SchemaBuilder`), dates outside the years 1677 to 2262, `limit + offset` above `uint.MaxValue`, huge `terms` sizes and histogram bucket counts in aggregations, `top_hits` with `from`, query-parser ranges with mixed number types, and facet values that aren't paths.
- A very large `limit` no longer reserves memory for that many hits up front.
- Facet counts at the root (`/`) were wrong for some category names (one-letter names, and names sharing a prefix).

## [0.3.0-preview.1] - 2026-11-22

### Added

- `IndexSettings` at creation: doc store compression (`Lz4`, `Zstd` with a level, `None`) and block size, saved with the index and readable as `SearchIndex.Settings`. `IndexSettings.SortBy` (experimental, `TDN0001`) stores documents ordered by a fast field.
- Merge control: `IndexWriterOptions.MergePolicy` (`MergePolicy.Default`, `MergePolicy.None`, `LogMergePolicy`), `IndexWriter.ForceMerge(maxSegments)` and `IndexWriter.GarbageCollectFiles()`.
- Index statistics: `SearchIndex.GetStats()` and `Searcher.GetStats()` with per-segment and per-field sizes.
- `SearcherManager`: one shared searcher for many requests, refreshed manually or on commit (`RefreshOnCommit`), with an optional warmer. Requests take a `SearcherLease`.

### Changed

- An `IndexWriter` that is never disposed no longer blocks the finalizer thread waiting for merges; its finalizer abandons them and releases the lock.

## [0.2.0-preview.1] - 2026-11-22

### Added

- Custom analyzers (`Analyzer`, `SchemaBuilder.AddAnalyzer`): tokenizers (simple, whitespace, raw, n-gram, regex) and filters (lowercase, ASCII folding, stop words, stemming, remove long). Saved with on-disk indexes and restored by `SearchIndex.Open`. `SearchIndex.Analyze` shows the terms a tokenizer produces.
- `IndexWriterOptions`, `IndexWriter.UpdateDocument` and `IndexWriter.DeleteDocuments(Query)`.
- `SearchOptions.Fields` (return only some stored fields) and `Searcher.Explain`.
- Sorting by several keys (`SortBy.Field`, `SortBy.Score`) and cursor paging (`SearchHit.Cursor`, `SearchOptions.SearchAfter`).
- `QueryParserOptions`: default fields and operator, per-field boosts, fuzzy fields, lenient parsing.
- Queries: `Regex`, `Exists`, `TermSet`, `DisjunctionMax`, `ConstantScore`, `MoreLikeThis`.
- JSON fields (`AddJsonField`) and IP address fields (`AddIpAddressField`).
- Lucene.NET benchmarks and the WikiSearch sample app.

### Changed

- **Breaking:** documents with a field the schema doesn't have are rejected with `TantivyException`. Set `IndexWriterOptions.IgnoreUnknownFields` to ignore such fields as before.
- **Breaking:** `SearchOptions.OrderBy` is a list of `SortBy` keys, and `SearchHit.SortValue` became `SearchHit.SortValues`. `SearchHit` is a class.
- `IndexWriter.Dispose()` waits for background merges to finish.

## [0.1.0-preview.1] - 2026-11-22

First preview.

### Added

- Schemas (`SchemaBuilder`) with text, string, u64, i64, f64, bool, date, bytes and facet fields.
- In-memory and on-disk indexes (`SearchIndex.CreateInRam`, `Create`, `Open`), an `IndexWriter` with add, delete, commit and rollback, and thread-safe `Searcher` snapshots.
- Query strings and queries built in code (`Query.Term`, `Fuzzy`, `Phrase`, `Boolean`, `Range`, `Facet`, `Parse`, boosts and `&`, `|`, `!` operators).
- `SearchOptions` with snippets and highlighting, facet counts, sorting by a fast field, and Elasticsearch-style aggregations.
- One NuGet package with native libraries for `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`, trimming- and NativeAOT-compatible.

[Unreleased]: https://github.com/aminparsa18/TantivyDotNet/compare/v1.0.0-rc.1...HEAD
[1.0.0-rc.1]: https://github.com/aminparsa18/TantivyDotNet/compare/v0.4.0-preview.1...v1.0.0-rc.1
[0.4.0-preview.1]: https://github.com/aminparsa18/TantivyDotNet/compare/v0.3.0-preview.1...v0.4.0-preview.1
[0.3.0-preview.1]: https://github.com/aminparsa18/TantivyDotNet/compare/v0.2.0-preview.1...v0.3.0-preview.1
[0.2.0-preview.1]: https://github.com/aminparsa18/TantivyDotNet/compare/v0.1.0-preview.1...v0.2.0-preview.1
[0.1.0-preview.1]: https://github.com/aminparsa18/TantivyDotNet/releases/tag/v0.1.0-preview.1
