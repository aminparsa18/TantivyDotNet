namespace TantivyDotNet.Tests;

/// <summary>A unique temporary directory, deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory(string? suffix = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tdn-tests", Guid.NewGuid().ToString("N") + suffix);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (DirectoryNotFoundException) { }
    }
}

internal static class Books
{
    public static Schema Schema() => new SchemaBuilder()
        .AddStringField("id", stored: true)
        .AddTextField("title", stored: true)
        .AddTextField("body")
        .AddU64Field("year", stored: true, fast: true)
        .Build();

    public static readonly string[] Json =
    [
        """{"id": "b1", "title": "Of Mice and Men", "body": "George and Lennie dream of a farm with rabbits and mice", "year": 1937}""",
        """{"id": "b2", "title": "The Grapes of Wrath", "body": "The Joad family leaves Oklahoma for California", "year": 1939}""",
        """{"id": "b3", "title": "East of Eden", "body": "Two families in the Salinas Valley", "year": 1952}""",
        """{"id": "b4", "title": "Cannery Row", "body": "Mack and the boys throw a party; frogs and mice everywhere", "year": 1945}""",
    ];

    public static SearchIndex InRam()
    {
        var index = SearchIndex.CreateInRam(Schema());
        using var writer = index.CreateWriter();
        foreach (var json in Json) writer.AddDocument(json);
        writer.Commit();
        return index;
    }

    public static IEnumerable<string?> Ids(SearchResults results) => results.Select(h => h.Document.GetString("id")).Order();
}
