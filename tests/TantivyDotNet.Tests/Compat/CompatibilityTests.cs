using System.IO.Compression;
using System.Net;
using System.Text.Json;

namespace TantivyDotNet.Tests.Compat;

/// <summary>
/// On-disk compatibility: every released version's fixture (Compat/Indexes/&lt;version&gt;.zip, written
/// by that version's package from <see cref="CompatCorpus"/>) must open, answer queries exactly like
/// an index this version writes from the same data, and accept writes and merges.
/// See README "Upgrades" for the policy and how to add a fixture after a release.
/// </summary>
public class CompatibilityTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Compat", "Indexes");

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var zip in Directory.GetFiles(FixtureDir, "*.zip").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileNameWithoutExtension(zip));
        return data;
    }

    [Fact]
    public void There_is_a_fixture() => Assert.NotEmpty(Directory.GetFiles(FixtureDir, "*.zip"));

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Old_index_matches_a_fresh_one(string version)
    {
        using var oldDir = Extract(version);
        using var freshDir = new TempDirectory();
        CompatCorpus.Write(freshDir.Path);

        using var old = SearchIndex.Open(oldDir.Path);
        using var fresh = SearchIndex.Open(freshDir.Path);

        Assert.Equal(fresh.Schema.Fields, old.Schema.Fields);
        Assert.Equal(fresh.Schema.Analyzers.OrderBy(a => a.Key), old.Schema.Analyzers.OrderBy(a => a.Key));
        Assert.Equal(DocStoreCompression.Zstd, old.Settings.DocStoreCompression);
        Assert.Equal(fresh.Settings.DocStoreBlockSize, old.Settings.DocStoreBlockSize);
        Assert.Equal(["cafe", "run"], old.Analyze("folded", "Café Running"));

        using var oldSearcher = old.CreateSearcher();
        using var freshSearcher = fresh.CreateSearcher();
        Assert.Equal(CompatCorpus.Live.Count(), oldSearcher.DocumentCount);
        Assert.Equal(CompatCorpus.DocumentCount / CompatCorpus.BatchSize, old.GetStats().Segments.Count);

        foreach (var (name, query, options) in Queries())
        {
            var freshResults = freshSearcher.Search(query, options);
            Assert.True(freshResults.TotalCount > 0, $"query '{name}' matches nothing, so it checks nothing");
            var expected = Describe(freshResults);
            var actual = Describe(oldSearcher.Search(query, options));
            Assert.True(expected == actual, $"{version}, query '{name}':\nexpected {expected}\nactual   {actual}");
        }

        // Stored values, field by field.
        foreach (var i in CompatCorpus.Live)
        {
            var doc = Assert.Single(oldSearcher.Search(Query.Term("id", CompatCorpus.Id(i)))).Document;
            Assert.Equal(CompatCorpus.Title(i), doc.GetString("title"));
            Assert.Equal(CompatCorpus.Year(i), doc.GetU64("year"));
            Assert.Equal(CompatCorpus.Delta(i), doc.GetI64("delta"));
            Assert.Equal(CompatCorpus.Rating(i), doc.GetF64("rating"));
            Assert.Equal(CompatCorpus.Available(i), doc.GetBool("available"));
            Assert.Equal(CompatCorpus.Published(i), doc.GetDate("published"));
            Assert.Equal(CompatCorpus.Hash(i), doc.GetBytes("hash"));
            Assert.Equal(CompatCorpus.Category(i), doc.GetString("category"));
            Assert.Equal(CompatCorpus.Color(i), doc.GetValues("attrs")[0].GetProperty("color").GetString());
            Assert.Equal(CompatCorpus.Ip(i), doc.GetIpAddress("ip"));
            Assert.False(doc.Contains("body"));
        }
        foreach (var i in Enumerable.Range(0, CompatCorpus.DocumentCount).Where(CompatCorpus.IsDeleted))
            Assert.Equal(0, oldSearcher.Count(Query.Term("id", CompatCorpus.Id(i))));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Old_index_accepts_writes_merges_and_reopens(string version)
    {
        using var dir = Extract(version);
        var live = CompatCorpus.Live.Count();
        using (var index = SearchIndex.Open(dir.Path))
        {
            using var writer = index.CreateWriter();
            writer.AddDocument(CompatCorpus.Document(CompatCorpus.DocumentCount));  // a new id
            writer.DeleteTerm("id", CompatCorpus.Id(0));
            writer.Commit();
            writer.ForceMerge(1);
            writer.GarbageCollectFiles();
            Assert.Single(index.GetStats().Segments);
        }

        using var reopened = SearchIndex.Open(dir.Path);
        using var searcher = reopened.CreateSearcher();
        Assert.Equal(live, searcher.DocumentCount);   // one added, one deleted
        Assert.Equal(1, searcher.Count(Query.Term("id", CompatCorpus.Id(CompatCorpus.DocumentCount))));
        Assert.Equal(0, searcher.Count(Query.Term("id", CompatCorpus.Id(0))));
        Assert.Equal(
            CompatCorpus.Live.Append(CompatCorpus.DocumentCount).Count(i => i != 0 && CompatCorpus.Title(i).Contains("café")),
            searcher.Count("title:cafe"));
        Assert.Equal(CompatCorpus.Category(1), Assert.Single(searcher.Search(Query.Term("id", CompatCorpus.Id(1)))).Document.GetString("category"));
    }

    /// <summary>Segments in a format Tantivy can't read (older than it supports after an upgrade, or
    /// from a newer release): the footer of every segment file is patched to another format version.</summary>
    [Theory]
    [InlineData(1)]   // too old
    [InlineData(9)]   // too new
    public void Unreadable_index_format_fails_with_IncompatibleIndex(int format)
    {
        using var dir = new TempDirectory();
        CompatCorpus.Write(dir.Path);
        foreach (var file in Directory.GetFiles(dir.Path).Where(f => Path.GetExtension(f) != ".json"))
        {
            var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(file));
            var patched = System.Text.RegularExpressions.Regex.Replace(text, "\"index_format_version\":(\\d+)", m =>
            {
                Assert.True(m.Groups[1].Length == 1, "the footer patch assumes a one-digit format version");
                return $"\"index_format_version\":{format}";
            });
            File.WriteAllBytes(file, System.Text.Encoding.Latin1.GetBytes(patched));
        }

        var e = Assert.Throws<TantivyException>(() =>
        {
            using var index = SearchIndex.Open(dir.Path);
            using var searcher = index.CreateSearcher();
        });
        Assert.Equal(TantivyErrorCode.IncompatibleIndex, e.Code);
        Assert.Contains($"index_format v{format}", e.Message);
        Assert.Contains("Reindex", e.Message);
    }

    private static TempDirectory Extract(string version)
    {
        var dir = new TempDirectory("-compat-" + version);
        ZipFile.ExtractToDirectory(Path.Combine(FixtureDir, version + ".zip"), dir.Path);
        return dir;
    }

    private static readonly SearchOptions Page = new() { Limit = 100 };

    /// <summary>Queries over every field type and search feature; each must give the same answer on
    /// an old index as on a fresh one.</summary>
    private static IEnumerable<(string Name, Query Query, SearchOptions Options)> Queries()
    {
        yield return ("all", Query.All, Page);
        yield return ("title folded", Query.Parse("title:CAFE"), Page);
        yield return ("title stemmed", Query.Parse("title:runs"), Page);
        yield return ("body en_stem", Query.Parse("body:running"), Page);
        yield return ("phrase", Query.Phrase("body", "tantivy running"), Page);
        yield return ("fuzzy", Query.Fuzzy("title", "fsst", 1), Page);
        yield return ("id", Query.Term("id", CompatCorpus.Id(7)), Page);
        yield return ("year term", Query.Term("year", CompatCorpus.Year(5)), Page);
        yield return ("year range", Query.Range("year", 1995UL, 2005UL), Page);
        yield return ("delta range", Query.Range("delta", -20_000_000_000L, 5_000_000_000L), Page);
        yield return ("rating range", Query.Range("rating", 1.0, 3.5), Page);
        yield return ("rating exists", Query.Exists("rating"), Page);
        yield return ("available", Query.Term("available", true), Page);
        yield return ("published range", Query.Range("published",
            new DateTimeOffset(2005, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero)), Page);
        yield return ("hash", Query.Term("hash", CompatCorpus.Hash(8)), Page);
        yield return ("facet", Query.Facet("category", "/genre/science"), Page);
        yield return ("json term", Query.Term("attrs.color", "blue"), Page);
        yield return ("json range", Query.Range("attrs.size", 10L, 30L), Page);
        yield return ("ip term", Query.Term("ip", CompatCorpus.Ip(4)!), Page);
        yield return ("ip range", Query.Range("ip", IPAddress.Parse("10.0.0.0"), IPAddress.Parse("10.0.2.255")), Page);
        yield return ("boolean", (Query.Parse("body:search") | Query.Term("attrs.color", "red")) & !Query.Term("available", false), Page);
        yield return ("sorted", Query.All, new SearchOptions
        {
            Limit = 100,
            OrderBy = [SortBy.Field("year", descending: true), SortBy.Field("id")],
        });
        yield return ("sorted f64 missing", Query.All, new SearchOptions { Limit = 100, OrderBy = [SortBy.Field("rating"), SortBy.Field("id")] });
        yield return ("snippets", Query.Parse("title:cafe"), new SearchOptions { Limit = 100, Snippets = new SnippetOptions("title") });
        yield return ("facets", Query.All, new SearchOptions { Limit = 0, Facets = [new FacetRequest("category", "/genre", "/genre/fiction")] });
        yield return ("aggregations", Query.All, new SearchOptions
        {
            Limit = 0,
            Aggregations = """
                {"years": {"histogram": {"field": "year", "interval": 10}},
                 "colors": {"terms": {"field": "attrs.color"}},
                 "rating": {"stats": {"field": "rating"}},
                 "ids": {"terms": {"field": "id", "size": 5, "order": {"_key": "asc"}}}}
                """,
        });
    }

    /// <summary>A canonical text form of a result: hits ordered by id (segment order differs between
    /// the indexes, so ties would come back in a different order) unless the query sorts.</summary>
    private static string Describe(SearchResults results)
    {
        var hits = results.Select(h => new
        {
            Id = h.Document.GetString("id"),
            Score = float.IsNaN(h.Score) ? "NaN" : h.Score.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture),
            Sort = string.Join(",", h.SortValues.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture))),
            Snippets = string.Join(";", h.Snippets.Select(kv => $"{kv.Key}={kv.Value.ToHtml()}")),
        });
        var ordered = results.Count > 0 && results[0].SortValues.Count > 0 ? hits : hits.OrderBy(h => h.Id, StringComparer.Ordinal);
        return JsonSerializer.Serialize(new
        {
            results.TotalCount,
            Hits = ordered.Select(h => $"{h.Id} {h.Score} [{h.Sort}] {h.Snippets}"),
            Facets = results.Facets.OrderBy(f => f.Key).Select(f => $"{f.Key}: {string.Join(",", f.Value.Select(c => $"{c.Path}={c.Count}"))}"),
            Aggregations = results.Aggregations?.GetRawText(),
        });
    }
}
