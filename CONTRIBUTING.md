# Contributing to TantivyDotNet

Thanks for helping. Bug reports, documentation fixes and pull requests are all welcome. For a larger change, such as a new feature or a change to public API, please open an issue first so we can agree on the design before you write the code.

By contributing, you agree that your contribution is licensed under the [MIT license](LICENSE). Security problems go through [SECURITY.md](SECURITY.md), not public issues.

## Prerequisites

- [.NET SDK 11](https://dotnet.microsoft.com/download) (the version is pinned in `global.json`), plus the .NET 10 runtime to run the `net10.0` tests.
- [Rust](https://rustup.rs) via rustup, with `cargo` on your `PATH`. `native/rust-toolchain.toml` pins the Rust version, and rustup installs it on first use, with rustfmt and clippy.

That's all you need to build and test. Some tasks need extra tools, listed in their sections below.

## Build and test

```sh
dotnet test --solution TantivyDotNet.slnx              # Debug, on net10.0 and net11.0
dotnet test --solution TantivyDotNet.slnx -c Release   # Release: cargo --release with LTO
```

Building the library project runs `cargo build` in `native/` first. That compiles the Rust library, regenerates `src/TantivyDotNet/Native/NativeMethods.g.cs`, and copies the native library into the output folder. MSBuild skips cargo when nothing under `native/` changed, so incremental builds take a second or two. A first Release build takes a few minutes because of LTO.

Rust checks, which CI also runs:

```sh
cd native
cargo fmt --check
cargo clippy --all-targets --all-features --locked -- -D warnings
cargo test --all-features --locked
```

Before you open a pull request, make sure both test commands above pass and the Rust checks are clean. The build treats warnings in the library as errors, including missing XML documentation on public API.

## Repository layout

```
native/                     Rust cdylib wrapping Tantivy behind a C ABI (exports prefixed tdn_)
  src/lib.rs                every extern "C" export and #[repr(C)] struct (csbindgen reads only this file)
  src/*.rs                  the logic, one module per area: schema_spec, query_spec, search, sorting, ...
  build.rs                  runs csbindgen, which writes src/TantivyDotNet/Native/NativeMethods.g.cs
  fuzz/                     cargo-fuzz targets for the JSON entry points
src/TantivyDotNet/          the .NET library: internal generated P/Invokes plus the hand-written public API
  Interop/                  SafeHandles, status codes to exceptions, UTF-8 helpers
tests/TantivyDotNet.Tests/  xUnit v3 tests, including stress, leak, crash and compatibility tests
tests/TantivyDotNet.AotSmoke/  NativeAOT console app that exercises every feature through AOT-safe overloads
tests/TantivyDotNet.CompatFixture/  writes the index a released version produces, for the compatibility tests
benchmarks/                 BenchmarkDotNet comparison with Lucene.NET
samples/WikiSearch/         sample web app
docs/                       documentation site (docfx): guides and the API reference
```

## How the binding works

- Each Rust object (`Index`, `IndexWriter`, searcher, reader) is an opaque pointer, wrapped on the .NET side in a `SafeHandle` with a matching `tdn_*_free`. No raw pointers appear in the public API.
- Every export catches panics (`guard` in `ffi.rs`). No panic may cross the boundary. Exports return a status code, and the error message comes back through an out-parameter as a Rust-allocated UTF-8 buffer.
- Strings cross as a UTF-8 pointer plus length in both directions, never null-terminated.
- Complex shapes cross as JSON: schemas, documents, query trees, and one search request per search (query, paging, sort, snippets, facets, aggregations), answered in one native call.
- Every P/Invoke signature is blittable, and the assembly sets `[DisableRuntimeMarshalling]`, so NativeAOT needs no marshalling stubs.

## Common changes

### Adding a native export

1. Write the `extern "C"` function in `native/src/lib.rs`, named `tdn_<object>_<action>`, wrapped in `guard(err, || ...)`. Keep `lib.rs` to exports and put the logic in a module.
2. Rebuild. `build.rs` regenerates `NativeMethods.g.cs`. Commit the regenerated file: CI checks it's up to date.
3. Wrap it in C# in the type it belongs to. Take a lease on the handle for the duration of the call, and turn the status into an exception:

   ```csharp
   using var lease = _handle.Lease();
   TdnString err;
   Ffi.Check(NativeMethods.tdn_writer_something((TdnWriter*)lease.Pointer, ..., &err), err);
   ```

4. If you changed the signature of an existing export, bump `ABI_VERSION` in `lib.rs` and `Ffi.ExpectedAbiVersion` together.
5. Add a case to the ABI tests in `lib.rs` if the export takes pointers that may be null.

### Adding a search option

Search options don't need a new export. Add a field to the native `SearchRequest` and handle it in a module, add a property to `SearchOptions` and write it in `ToRequestJson`, and parse any result in `SearchResults.Parse`.

### Changing public API

The public API is tracked in `src/TantivyDotNet/PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`. Adding public API without listing it, or removing listed API, fails the build (RS0016, RS0017). New API goes into `PublicAPI.Unshipped.txt`. Your IDE's code fix for RS0016 adds the line for you.

Every public member needs XML documentation: the summary, what it throws, and anything surprising about it. The API reference site is generated from these comments.

Before 1.0, breaking changes are possible but need a reason, and the changelog must mark them. From 1.0 on, see the [versioning policy](docs/guides/versioning.md).

### Tests

Add tests for every change in `tests/TantivyDotNet.Tests`. If the feature can be used from an AOT app, exercise it in `tests/TantivyDotNet.AotSmoke` too. CI publishes that app with NativeAOT, with warnings as errors, on every platform. Rust logic that's easier to test in Rust gets unit tests in its module (see `src/testing.rs` for fixtures).

Longer-running tests:

- Stress and leak tests run for a few seconds by default. Set `TDN_STRESS_SECONDS=120` to run them longer.
- Fuzzing needs a nightly toolchain and `cargo install cargo-fuzz`. In `native/fuzz`: `cargo +nightly fuzz build -O`, then `./run_all.sh 300` to run every target for 5 minutes. CI fuzzes each target on every change under `native/`.

### Changelog

Add a line to the `[Unreleased]` section of [CHANGELOG.md](CHANGELOG.md) for every change a user would notice: added, changed, fixed, removed. Mark breaking changes with **Breaking**. Internal refactoring and test-only changes don't need an entry.

### Documentation

The site in `docs/` is built with [docfx](https://dotnet.github.io/docfx/), installed as a local tool:

```sh
dotnet tool restore
dotnet docfx docs/docfx.json --serve      # http://localhost:8080
```

Guides are Markdown files in `docs/guides`. Link to API pages with `xref:`, for example `[SearchOptions](xref:TantivyDotNet.SearchOptions)`. CI builds the site with warnings as errors, so broken links fail the build.

### Rust dependencies

After changing `native/Cargo.toml` or `Cargo.lock`:

- `cargo deny --locked check` (`cargo install cargo-deny`) checks licenses, advisories and sources against `native/deny.toml`.
- Regenerate the third-party notices, which CI checks are current: `cd native && cargo about generate --locked --offline about.hbs -o ../THIRD-PARTY-NOTICES.txt` (`cargo install cargo-about --features cli`).

To upgrade Tantivy, also read its changelog for index format changes, and run the full test suite, which opens an index written by every released version. See [Versioning and upgrades](docs/guides/versioning.md#index-format-and-tantivy-upgrades).

## Pull requests

- Keep a pull request to one change, and describe what it changes and why.
- CI runs the Rust checks, builds the native library for every platform, packs the NuGet package, and runs the tests and the NativeAOT app against that package on Linux x64 and arm64, macOS x64 and arm64, and Windows. All of it must pass.
- Commit messages: a short summary line in the imperative or descriptive form, followed by a blank line and details if needed.

## Packaging and releases

These are for maintainers.

**Packaging.** A local `dotnet pack src/TantivyDotNet -c Release` produces a package with only your machine's native library. CI builds the full package in three stages:

1. Each RID's library is built on its own runner. Linux uses [cargo-zigbuild](https://github.com/rust-cross/cargo-zigbuild) to target glibc 2.17, and the job fails if a newer glibc symbol slips in. Debug info is split off into symbol files.
2. One job packs the libraries with `-p:SkipNativeBuild=true -p:NativeAssetsDir=<dir with one sub-folder per RID> -p:RequireAllNativeAssets=true`.
3. On each of the five platforms, the tests and the NativeAOT app are built against the packed `.nupkg` (`-p:TantivyPackageVersion=<version> -p:TantivyPackageSource=<folder>`) and run.

To test a local package the same way:

```sh
dotnet pack src/TantivyDotNet -c Release -o artifacts/packages -p:Version=0.1.0-local.1
dotnet publish tests/TantivyDotNet.AotSmoke -c Release -r osx-arm64 -o artifacts/aot \
  -p:TantivyPackageVersion=0.1.0-local.1 -p:TantivyPackageSource=$PWD/artifacts/packages
./artifacts/aot/TantivyDotNet.AotSmoke
```

NuGet caches packages by version, so use a new version for each local package.

**Releasing.**

1. Move the `[Unreleased]` entries in `CHANGELOG.md` under the new version, with today's date, and update the comparison links at the bottom.
2. For a stable release, move the lines of `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt`. From 1.0 on, set `PackageValidationBaselineVersion` to the previous release.
3. Commit, then tag and push: `git tag -a v1.2.0 -m "TantivyDotNet 1.2.0" && git push origin v1.2.0`. The tag without its `v` becomes the package version.
4. `publish.yml` runs the whole pipeline for the tag, pushes the package and its symbols to nuget.org with [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing), and creates the GitHub Release with the native symbols. Tags containing `-` are marked as prereleases.
5. Add the release's compatibility fixture and commit it:

   ```sh
   dotnet run --project tests/TantivyDotNet.CompatFixture -p:TantivyCompatVersion=<released version>
   ```

A published version can't be deleted or reused on nuget.org. If a release is broken, publish a new version.
