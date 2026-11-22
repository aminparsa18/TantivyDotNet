# TantivyDotNet

An idiomatic .NET binding for [Tantivy](https://github.com/quickwit-oss/tantivy), the fast full-text search engine library written in Rust (think Lucene). The goal is a NuGet package that gives you embedded full-text search in .NET without needing to know that Rust is involved.

## Status

**Early development. Not usable yet.** The native library builds, links and runs a smoke test from .NET. The public C# API (schema, index, writer, searcher) comes next. Nothing is published on NuGet yet.

## How it works

```
native/                     Rust cdylib wrapping Tantivy behind a small C ABI (exports prefixed tdn_)
  build.rs                  runs csbindgen, which generates src/TantivyDotNet/Native/NativeMethods.g.cs
src/TantivyDotNet/          the .NET library: internal generated P/Invokes plus a hand-written public API
tests/TantivyDotNet.Tests/  xUnit v3 tests
```

- Each Rust object is an opaque handle wrapped in a `SafeHandle`. No raw pointers are exposed publicly.
- Every export catches panics. Errors come back as status codes and turn into `TantivyException`.
- Complex shapes (documents, schemas, aggregations) cross the boundary as JSON.

## Building

Prerequisites:

- [.NET SDK 11](https://dotnet.microsoft.com/download) (pinned in `global.json`)
- [Rust](https://rustup.rs) stable toolchain, with `cargo` on your `PATH`

```sh
dotnet test --solution TantivyDotNet.slnx            # Debug
dotnet test --solution TantivyDotNet.slnx -c Release # Release (cargo --release, with LTO)
```

The library project calls `cargo build` before compiling C#. That also regenerates `NativeMethods.g.cs` and copies the native library into the output folder. MSBuild skips the cargo step when no file under `native/` has changed, so incremental builds stay fast. The first Release build takes a few minutes because of LTO.

## License

[MIT](LICENSE), the same license as Tantivy.
