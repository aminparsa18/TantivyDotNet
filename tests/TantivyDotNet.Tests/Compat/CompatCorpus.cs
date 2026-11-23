using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace TantivyDotNet.Tests.Compat;

/// <summary>
/// The data of the compatibility fixtures: an index with every field type, a custom analyzer,
/// zstd doc store, three segments and deletes. CompatibilityTests opens the fixture each release
/// wrote (Compat/Indexes/&lt;version&gt;.zip) and checks it answers queries like an index this
/// version writes from the same data.
/// </summary>
/// <remarks>
/// Also compiled into tests/TantivyDotNet.CompatFixture against an older released package, which
/// writes the fixture: use only API that every fixture's release has, and never change what an
/// existing fixture's data looks like (extend with new fields guarded by a version check instead).
/// </remarks>
internal static class CompatCorpus
{
    public const int DocumentCount = 60;
    public const int BatchSize = 20;

    public static readonly string[] Words =
        ["café", "naïve", "running", "runner", "search", "index", "lucene", "tantivy", "rust", "dotnet", "fast", "engine"];
    public static readonly string[] Colors = ["red", "green", "blue", "black"];
    public static readonly string[] Genres = ["fiction", "science", "history"];

    public static Schema Schema() => new SchemaBuilder()
        .AddAnalyzer("folded", Analyzer.Simple().LowerCase().AsciiFolding().Stem(AnalyzerLanguage.English))
        .AddStringField("id", stored: true, fast: true)
        .AddTextField("title", stored: true, tokenizer: "folded")
        .AddTextField("body", tokenizer: "en_stem")
        .AddU64Field("year", stored: true, fast: true)
        .AddI64Field("delta", stored: true, fast: true)
        .AddF64Field("rating", stored: true, fast: true)
        .AddBoolField("available", stored: true, fast: true)
        .AddDateField("published", stored: true, fast: true)
        .AddBytesField("hash", stored: true)
        .AddFacetField("category", stored: true)
        .AddJsonField("attrs", stored: true, fast: true)
        .AddIpAddressField("ip", stored: true, fast: true)
        .Build();

    public static IndexSettings Settings() => new()
    {
        DocStoreCompression = DocStoreCompression.Zstd,
        ZstdLevel = 3,
        DocStoreBlockSize = 4096,
    };

    /// <summary>Documents deleted (by id) after the last batch.</summary>
    public static bool IsDeleted(int i) => i % 7 == 3;

    public static IEnumerable<int> Live => Enumerable.Range(0, DocumentCount).Where(i => !IsDeleted(i));

    public static string Id(int i) => $"d{i:D3}";
    public static string Title(int i) => $"{Words[i % Words.Length]} {Words[(i * 5 + 1) % Words.Length]}";
    public static string Body(int i) =>
        string.Join(' ', Enumerable.Range(0, 3 + i % 4).Select(k => Words[(i + k * 7) % Words.Length]));
    public static ulong Year(int i) => 1990UL + (ulong)(i % 35);
    public static long Delta(int i) => (i % 2 == 0 ? -1L : 1L) * i * 1_000_000_007L;
    /// <summary>Missing for every 11th document (missing values in fast fields).</summary>
    public static double? Rating(int i) => i % 11 == 0 ? null : Math.Round(i * 0.37 % 5, 2);
    public static bool Available(int i) => i % 3 != 0;
    public static DateTimeOffset Published(int i) =>
        new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i * 97).AddSeconds(i * 13);
    public static byte[] Hash(int i) => [(byte)i, (byte)(i * 3), (byte)(255 - i), 0];
    public static string Category(int i) => $"/genre/{Genres[i % Genres.Length]}/{(i % 2 == 0 ? "a" : "b")}";
    public static string Color(int i) => Colors[i % Colors.Length];
    public static IPAddress? Ip(int i) => i % 11 == 0 ? null
        : i % 3 == 0 ? IPAddress.Parse($"2001:db8::{i:x}")
        : IPAddress.Parse($"10.0.{i / 10}.{i}");

    public static string Document(int i)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("id", Id(i));
            json.WriteString("title", Title(i));
            json.WriteString("body", Body(i));
            json.WriteNumber("year", Year(i));
            json.WriteNumber("delta", Delta(i));
            if (Rating(i) is { } rating) json.WriteNumber("rating", rating);
            json.WriteBoolean("available", Available(i));
            json.WriteString("published", Published(i).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            json.WriteString("hash", Convert.ToBase64String(Hash(i)));
            json.WriteString("category", Category(i));
            json.WriteStartObject("attrs");
            json.WriteString("color", Color(i));
            json.WriteNumber("size", i);
            json.WriteStartArray("tags");
            json.WriteStringValue($"t{i % 5}");
            json.WriteStringValue("all");
            json.WriteEndArray();
            json.WriteEndObject();
            if (Ip(i) is { } ip) json.WriteString("ip", ip.ToString());
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Writes the corpus to a new index: three commits of <see cref="BatchSize"/> documents
    /// (one segment each, no merges), then a commit deleting <see cref="IsDeleted"/> documents.</summary>
    public static void Write(string directory)
    {
        using var index = SearchIndex.Create(directory, Schema(), Settings());
        using var writer = index.CreateWriter(new IndexWriterOptions { ThreadCount = 1, MergePolicy = MergePolicy.None });
        for (var i = 0; i < DocumentCount; i++)
        {
            writer.AddDocument(Document(i));
            if ((i + 1) % BatchSize == 0) writer.Commit();
        }
        foreach (var i in Enumerable.Range(0, DocumentCount).Where(IsDeleted)) writer.DeleteTerm("id", Id(i));
        writer.Commit();
    }
}
