# Deployment

## Platforms

The package carries a native library for each supported runtime identifier (RID). The .NET host picks the right one at run time, or at publish time when you pass `-r <rid>`.

| RID | Requirement |
| --- | --- |
| `win-x64` | Windows 10 or later. The C runtime is linked in, so no Visual C++ Redistributable is needed |
| `linux-x64`, `linux-arm64` | glibc 2.17 or newer: RHEL/CentOS 7, Debian 9, Ubuntu 16.04 and later |
| `osx-x64`, `osx-arm64` | Versions supported by .NET 10 |

**Not supported yet:** Alpine and other musl-based Linux (`linux-musl-x64`, `linux-musl-arm64`), and Windows on Arm64 (`win-arm64`). On those, the first call into the library throws `DllNotFoundException`. On Windows on Arm, an x64 process running under emulation works.

## Containers

Use a glibc-based image. The official .NET images based on Debian and Ubuntu work, including the chiseled Ubuntu images, because .NET itself needs the same system libraries.

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish MyApp/MyApp.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
VOLUME /data
ENTRYPOINT ["dotnet", "MyApp.dll"]
```

The `-alpine` tags don't work, because they use musl.

Things to plan for:

- **Keep the index on a volume.** An index is a directory. Mount it from a volume so it survives container restarts, and open it with `SearchIndex.Open` when it exists:

  ```csharp
  var index = File.Exists(Path.Combine(dir, "meta.json"))
      ? SearchIndex.Open(dir)
      : SearchIndex.Create(dir, schema);
  ```

- **One writer per index.** Only one process can write an index at a time, and the lock is a file lock in the index directory. If several replicas share a volume, let one of them write (or run indexing as a separate job), and let the others open the index read-only with a `SearcherManager` and `RefreshOnCommit = true`. Use a local disk or a block volume. File locks and memory-mapped files aren't reliable on network file systems such as NFS or SMB.
- **Memory.** Indexes are memory-mapped, so the OS page cache holds the hot parts of the index. That memory counts against a container's limit as cache and is reclaimed under pressure, but give the container room for it, or searches read from disk. The writer's `HeapBytes` (50 MB by default) is allocated on top.
- **File handles.** Each segment keeps several files open. Many segments, or many old searchers kept alive, can hit a low `ulimit -n`. `ForceMerge` after a bulk load reduces the segment count.

## NativeAOT and trimming

The library is trimming- and NativeAOT-compatible: it sets `IsAotCompatible`, and CI publishes a NativeAOT test app on every supported platform. All calls into the native library are blittable P/Invokes, so they need no marshalling code at run time.

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
</PropertyGroup>
```

```sh
dotnet publish -c Release -r linux-x64 -o out
```

The output folder holds your executable **and** the native library (`libtantivy_dotnet.so`, `libtantivy_dotnet.dylib` or `tantivy_dotnet.dll`). The library isn't linked into the executable, so deploy both files together.

A few overloads use reflection-based JSON serialization and are marked `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`. The compiler warns when an AOT or trimmed app calls them. Each has an AOT-safe alternative:

| Reflection overload | AOT-safe alternative |
| --- | --- |
| `writer.AddDocument(object)` | `writer.AddDocument(doc, MyJsonContext.Default.MyType)` or a JSON string |
| `writer.UpdateDocument(term, object)` | `writer.UpdateDocument(term, doc, MyJsonContext.Default.MyType)` |
| `results.GetAggregations<T>()` | `results.GetAggregations(MyJsonContext.Default.MyAggs)` or `results.Aggregations` |

```csharp
[JsonSerializable(typeof(Book))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
partial class MyJsonContext : JsonSerializerContext;
```

The naming policy matters: schema field names are case-sensitive, and an unknown property is an error, so `Title` doesn't fill a field named `title`.

## Single-file apps

`PublishSingleFile` works. The native library is extracted next to the executable unless you set `IncludeNativeLibrariesForSelfExtract`, in which case it's extracted to a temporary directory at startup.

## Debugging native crashes

Native crashes should not happen: every call into Rust catches panics and turns them into a `TantivyException` with code `Panic`. Please [report](https://github.com/aminparsa18/TantivyDotNet/issues) any you see. To symbolicate a native crash dump, download `tantivy_dotnet-<version>-<rid>-symbols.zip` from the version's [GitHub Release](https://github.com/aminparsa18/TantivyDotNet/releases). Managed symbols are on the NuGet symbol server, with SourceLink.
