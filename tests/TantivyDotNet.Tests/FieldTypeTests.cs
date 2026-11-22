using System.Net;

namespace TantivyDotNet.Tests;

public class FieldTypeTests
{
    private static SearchIndex Products()
    {
        var index = SearchIndex.CreateInRam(new SchemaBuilder()
            .AddStringField("id", stored: true)
            .AddJsonField("attrs", stored: true, fast: true)
            .Build());
        using var writer = index.CreateWriter();
        writer.AddDocument("""{"id": "p1", "attrs": {"color": "Dark Red", "size": 3, "price": 9.5, "tags": ["new", "sale"], "spec": {"wifi": true}}}""");
        writer.AddDocument("""{"id": "p2", "attrs": {"color": "blue", "size": 5, "price": 20, "released": "2024-05-01T00:00:00Z"}}""");
        writer.AddDocument("""{"id": "p3", "attrs": {"size": 12345678901, "spec": {"wifi": false}}}""");
        writer.Commit();
        return index;
    }

    [Fact]
    public void JsonField_TermQueriesByPathAndType()
    {
        using var index = Products();
        using var searcher = index.CreateSearcher();
        Assert.Equal(["p1"], Books.Ids(searcher.Search(Query.Term("attrs.color", "red"))));     // tokenized like text
        Assert.Equal(["p1"], Books.Ids(searcher.Search(Query.Term("attrs.size", 3L))));
        Assert.Equal(["p1"], Books.Ids(searcher.Search(Query.Term("attrs.size", 3UL))));        // normalized like indexing
        Assert.Equal(["p3"], Books.Ids(searcher.Search(Query.Term("attrs.size", 12345678901L))));
        Assert.Equal(["p2"], Books.Ids(searcher.Search(Query.Term("attrs.price", 20.0))));
        Assert.Equal(["p1"], Books.Ids(searcher.Search(Query.Term("attrs.price", 9.5))));
        Assert.Equal(["p1"], Books.Ids(searcher.Search(Query.Term("attrs.spec.wifi", true))));
        Assert.Equal(["p1"], Books.Ids(searcher.Search(Query.Term("attrs.tags", "sale"))));
        Assert.Equal(["p2"], Books.Ids(searcher.Search(Query.Term("attrs.released", new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero)))));
        Assert.Equal(["p1", "p2"], Books.Ids(searcher.Search(Query.TermSet("attrs.color", ["red", "blue"]))));
        Assert.Equal(["p2"], Books.Ids(searcher.Search("attrs.color:blue")));

        var whole = Assert.Throws<TantivyException>(() => searcher.Search(Query.Term("attrs", "red")));
        Assert.Contains("attrs.key", whole.Message);
    }

    [Fact]
    public void JsonField_RangesExistsAggregationsAndStoredValues()
    {
        using var index = Products();
        using var searcher = index.CreateSearcher();
        Assert.Equal(["p1", "p2"], Books.Ids(searcher.Search(Query.Range("attrs.size", 2L, 10L))));
        Assert.Equal(["p1", "p2"], Books.Ids(searcher.Search(Query.Exists("attrs.price"))));
        Assert.Equal(["p1", "p2", "p3"], Books.Ids(searcher.Search(Query.Exists("attrs"))));   // any subpath

        var aggs = searcher.Search(Query.All, new SearchOptions { Limit = 0, Aggregations = """{"avg_price": {"avg": {"field": "attrs.price"}}}""" });
        Assert.Equal(14.75, aggs.Aggregations!.Value.GetProperty("avg_price").GetProperty("value").GetDouble());

        var p1 = searcher.Search(Query.Term("id", "p1"))[0].Document;
        var attrs = Assert.Single(p1.GetValues("attrs"));
        Assert.Equal("Dark Red", attrs.GetProperty("color").GetString());
        Assert.True(attrs.GetProperty("spec").GetProperty("wifi").GetBoolean());

        Assert.Equal(new FieldDefinition("attrs", FieldType.Json, Stored: true, Indexed: true, Fast: true, "default"), index.Schema["attrs"]);
    }

    private static SearchIndex Hosts()
    {
        var index = SearchIndex.CreateInRam(new SchemaBuilder()
            .AddStringField("id", stored: true)
            .AddIpAddressField("ip", stored: true, fast: true)
            .Build());
        using var writer = index.CreateWriter();
        writer.AddDocument("""{"id": "h1", "ip": "192.168.0.1"}""");
        writer.AddDocument("""{"id": "h2", "ip": "10.0.0.5"}""");
        writer.Commit();
        writer.AddDocument("""{"id": "h3", "ip": "2001:db8::1"}""");
        writer.AddDocument("""{"id": "h4", "ip": "10.200.1.1"}""");
        writer.AddDocument("""{"id": "h5"}""");
        writer.Commit();
        return index;
    }

    [Fact]
    public void IpField_TermRangeTermSetAndStoredValues()
    {
        using var index = Hosts();
        using var searcher = index.CreateSearcher();
        Assert.Equal(["h2"], Books.Ids(searcher.Search(Query.Term("ip", IPAddress.Parse("10.0.0.5")))));
        Assert.Equal(["h2"], Books.Ids(searcher.Search(Query.Term("ip", IPAddress.Parse("::ffff:10.0.0.5")))));
        Assert.Equal(["h3"], Books.Ids(searcher.Search(Query.Term("ip", IPAddress.Parse("2001:db8::1")))));
        Assert.Equal(["h2", "h4"], Books.Ids(searcher.Search(Query.Range("ip", IPAddress.Parse("10.0.0.0"), IPAddress.Parse("10.255.255.255")))));
        Assert.Equal(["h1", "h3"], Books.Ids(searcher.Search(Query.TermSet("ip", [IPAddress.Parse("192.168.0.1"), IPAddress.Parse("2001:db8::1")]))));
        Assert.Equal(["h2"], Books.Ids(searcher.Search("ip:10.0.0.5")));

        var h1 = searcher.Search(Query.Term("id", "h1"))[0].Document;
        Assert.Equal(IPAddress.Parse("192.168.0.1"), h1.GetIpAddress("ip"));
        Assert.Throws<TantivyException>(() => searcher.Search(Query.Term("ip", "10.0.0.5")));
        Assert.Equal(new FieldDefinition("ip", FieldType.IpAddress, Stored: true, Indexed: true, Fast: true, null), index.Schema["ip"]);
    }

    [Fact]
    public void IpField_SortAndCursor()
    {
        using var index = Hosts();
        using var searcher = index.CreateSearcher();
        var options = new SearchOptions { Limit = 2, OrderBy = [SortBy.Field("ip")] };
        var page1 = searcher.Search(Query.All, options);
        Assert.Equal(["h2", "h4"], page1.Select(h => h.Document.GetString("id")));
        Assert.Equal(IPAddress.Parse("10.0.0.5"), page1[0].SortValues[0]);

        var page2 = searcher.Search(Query.All, new SearchOptions { Limit = 10, OrderBy = options.OrderBy, SearchAfter = page1[^1].Cursor });
        Assert.Equal(["h1", "h3", "h5"], page2.Select(h => h.Document.GetString("id")));   // IPv6 after mapped IPv4; missing last
        Assert.Null(page2[^1].SortValues[0]);
    }

    [Fact]
    public void IpField_UpdateDocumentByAddress()
    {
        using var index = Hosts();
        using (var writer = index.CreateWriter())
        {
            writer.UpdateDocument(Query.Term("ip", IPAddress.Parse("10.0.0.5")), """{"id": "h2b", "ip": "10.0.0.5"}""");
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();
        Assert.Equal(["h2b"], Books.Ids(searcher.Search(Query.Term("ip", IPAddress.Parse("10.0.0.5")))));
    }
}
