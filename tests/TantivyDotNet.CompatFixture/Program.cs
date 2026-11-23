using System.IO.Compression;
using System.Reflection;
using TantivyDotNet;
using TantivyDotNet.Tests.Compat;

// Writes tests/TantivyDotNet.Tests/Compat/Indexes/<version>.zip (or the path given as the argument).
var version = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
    .Single(a => a.Key == "TantivyCompatVersion").Value!;
var library = typeof(SearchIndex).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
if (library is null || !library.StartsWith(version, StringComparison.Ordinal))
    throw new InvalidOperationException($"loaded TantivyDotNet {library}, expected the {version} package");

var output = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(FindRepoRoot(), "tests", "TantivyDotNet.Tests", "Compat", "Indexes", $"{version}.zip");

var work = Path.Combine(Path.GetTempPath(), "tdn-compat-" + Guid.NewGuid().ToString("N"));
try
{
    CompatCorpus.Write(work);
    // Leftover lock files aren't part of the index.
    foreach (var lockFile in Directory.GetFiles(work, "*.lock")) File.Delete(lockFile);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.Delete(output);
    ZipFile.CreateFromDirectory(work, output, CompressionLevel.SmallestSize, includeBaseDirectory: false);
    Console.WriteLine($"TantivyDotNet {library}: wrote {output}");
    foreach (var file in Directory.GetFiles(work).Order(StringComparer.Ordinal))
        Console.WriteLine($"  {Path.GetFileName(file)} ({new FileInfo(file).Length} bytes)");
}
finally
{
    Directory.Delete(work, recursive: true);
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "TantivyDotNet.slnx"))) return dir.FullName;
    throw new InvalidOperationException("run from inside the repository, or pass the output path");
}
