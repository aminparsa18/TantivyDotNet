// Usage: TantivyDotNet.CrashHelper <mode> <index directory>
//   commit-loop  creates the index if needed, then commits batches of BatchSize documents forever,
//                printing "committed <total>" after each commit. The test kills it at some point.
//   hold-writer  opens the index, takes the writer, adds uncommitted documents, prints "ready" and
//                waits to be killed.
using TantivyDotNet;

const int BatchSize = 2000;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: TantivyDotNet.CrashHelper commit-loop|hold-writer <dir>");
    return 2;
}

string mode = args[0], dir = args[1];
using var index = Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()
    ? SearchIndex.Open(dir)
    : SearchIndex.Create(dir, new SchemaBuilder().AddStringField("id", stored: true).AddTextField("body", stored: true).Build());
var writer = index.CreateWriter(new IndexWriterOptions { HeapBytes = 30_000_000, ThreadCount = 1 });

switch (mode)
{
    case "commit-loop":
        long total = index.CreateSearcher().DocumentCount;
        for (int batch = 0; ; batch++)
        {
            for (int i = 0; i < BatchSize; i++)
            {
                long n = total + i;
                writer.AddDocument($$"""{"id": "d{{n}}", "body": "document {{n}} of batch {{batch}} with some text to make the commit take a while: lorem ipsum dolor sit amet {{n % 97}} {{n % 89}}"}""");
            }
            writer.Commit();
            total += BatchSize;
            Console.WriteLine($"committed {total}");
        }

    case "hold-writer":
        for (int i = 0; i < 100; i++) writer.AddDocument($$"""{"id": "u{{i}}", "body": "uncommitted"}""");
        Console.WriteLine("ready");
        Thread.Sleep(Timeout.Infinite);
        return 0;

    default:
        Console.Error.WriteLine($"unknown mode {mode}");
        return 2;
}
