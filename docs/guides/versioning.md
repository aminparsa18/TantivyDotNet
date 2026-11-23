# Versioning and upgrades

TantivyDotNet follows [Semantic Versioning 2.0](https://semver.org). This page defines what a breaking change is for this package, and how indexes behave across upgrades.

## Before 1.0

Versions `0.x.y-preview.N` are previews. A minor version (`0.4` to `0.5`) may contain breaking changes, which the [changelog](../changelog.md) lists. Indexes written by earlier previews keep working unless a release notes otherwise.

Release candidates (`1.0.0-rc.N`) freeze the API: `PublicAPI.Shipped.txt` holds what 1.0.0 will ship. An RC can still change it, but only to fix a problem that feedback turns up, and the changelog marks such a change **Breaking**. 1.0.0 follows once a release candidate has gone a few weeks without API changes.

## What a major version protects

From 1.0 on, the following change only in a new major version.

**Public API.** Every public type and member of the `TantivyDotNet` namespace, as listed in `PublicAPI.Shipped.txt`. Code that compiles against 1.x compiles and runs against any later 1.x without changes. The build enforces this with the public API analyzers and NuGet package validation against the previous release. Breaking includes:

- removing or renaming a type or member, or changing its signature
- changing a default value (for example the default of `IndexWriterOptions.IgnoreUnknownFields` or of a field's `indexed` flag)
- changing documented behavior that correct code relies on, such as which exception a method throws for a given error

**Supported platforms.** The runtime identifiers (`win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`), the oldest supported .NET version (.NET 10 for 1.x), and the oldest supported OS baseline (glibc 2.17 on Linux).

**On-disk compatibility.** An index written by a 1.x release opens in every later 1.x release. That includes the Tantivy index format, the analyzers saved with the index, and the settings in `meta.json`. Text analysis is part of this: a built-in tokenizer or filter doesn't change which terms it produces, because an index and its queries must agree on terms.

## What isn't breaking

These can change in a minor or patch release:

- **New API**: types, members, overloads, optional `SearchOptions` and other options properties, and new enum values. Treat `TantivyErrorCode` and `FieldType` as open: handle values you don't know.
- **New platforms and frameworks**: a new RID or target framework. Dropping a target framework while an older one still covers it isn't breaking either: a .NET 11 app falls back to the .NET 10 build.
- **The native interface.** The native library ships inside the package, and the `tdn_*` exports are internal. They change freely between versions, and a package's managed and native parts are always used together.
- **Text that's meant for people**: exception messages, `ToString()` output (of `Query`, `Explanation`, `IndexSettings`, ...), and stats details.
- **Scores and tie order.** Relevance scores can change slightly between versions, for example after a Tantivy upgrade. So can the order of hits with equal scores or sort values.
- **Cursors.** `SearchHit.Cursor` is opaque and meant for paging through one result set. A cursor from another version may be rejected.
- **Experimental API**: members marked `[Experimental]` (diagnostic IDs starting with `TDN`), such as `IndexSettings.SortBy`. They can change or go away in any release.
- **Performance, memory use and file layout** within the index format, and bug fixes, even when a program depended on the bug.

## Version numbers

- **Major** (`2.0.0`): breaking changes as defined above. The changelog explains how to migrate.
- **Minor** (`1.3.0`): new features, Tantivy upgrades, new platforms. Indexes written by the previous minor version open, but an index written by `1.3` may not open in `1.2`.
- **Patch** (`1.3.1`): bug fixes and security fixes only, with no API or index format changes.
- **Prereleases**: `-preview.N` before 1.0, `-rc.N` for release candidates.

Fixes go into the latest minor version. See [SECURITY.md](https://github.com/aminparsa18/TantivyDotNet/blob/main/SECURITY.md) for how long security fixes are provided.

## Index format and Tantivy upgrades

An index is a directory in [Tantivy's](https://github.com/quickwit-oss/tantivy) on-disk format. Each TantivyDotNet release is built against one Tantivy version (currently 0.27). That version reads a range of index format versions (currently 4 to 7) and writes the newest. The analyzer file (`tantivy-dotnet.json`) has its own version number.

- **Upgrading TantivyDotNet.** A new release opens indexes written by every earlier release whose index format its Tantivy still reads. New segments, from commits and merges, are written in the current format. Old segments are rewritten when they're merged, for example with `ForceMerge`.
- **Downgrading isn't supported.** Once a newer release has written to an index, older releases may not be able to read it. Keep a copy of the index directory if you might roll back, or plan to reindex.
- **When a reindex is needed.** If a Tantivy upgrade drops a format your index still uses, `SearchIndex.Open` (or creating a searcher) throws [`TantivyException`](xref:TantivyDotNet.TantivyException) with `Code == TantivyErrorCode.IncompatibleIndex`. The message names the format the index uses and the formats this release reads. Rebuild the index from your source data. Running `ForceMerge` with the old release doesn't help, because it writes the format that release already uses. Before 1.0, a release that drops a format is a minor version; from 1.0 on, it's a major version. The changelog always calls it out.

A typical reindex runs without downtime: build a new index in a new directory from your source data while the old one keeps serving, then switch the application to the new directory and delete the old one.

```csharp
try
{
    index = SearchIndex.Open(dir);
}
catch (TantivyException e) when (e.Code == TantivyErrorCode.IncompatibleIndex)
{
    index = Rebuild(dir + ".new", schema);   // your code: create, add every document, commit
}
```

### How compatibility is tested

The repository keeps an index written by each released version (`tests/TantivyDotNet.Tests/Compat/Indexes/<version>.zip`), covering every field type, a custom analyzer, a zstd doc store, several segments and deletes. The tests open each one with the current code, check that every test query returns the same results as an index freshly written from the same data, and check that the old index accepts writes and merges.
