using System.Runtime.CompilerServices;

namespace TantivyDotNet.Tests;

public class IndexSettingsTests
{
    [Fact]
    public void Defaults_are_reported_for_a_new_index()
    {
        using var index = Books.InRam();
        Assert.Equal(DocStoreCompression.Lz4, index.Settings.DocStoreCompression);
        Assert.Null(index.Settings.ZstdLevel);
        Assert.Equal(16_384, index.Settings.DocStoreBlockSize);
#pragma warning disable TDN0001
        Assert.Null(index.Settings.SortBy);
#pragma warning restore TDN0001
    }

    [Fact]
    public void Settings_are_saved_with_an_on_disk_index()
    {
        using var dir = new TempDirectory();
        var settings = new IndexSettings { DocStoreCompression = DocStoreCompression.Zstd, ZstdLevel = 9, DocStoreBlockSize = 65_536 };
        using (var index = SearchIndex.Create(dir.Path, Books.Schema(), settings))
        {
            using var writer = index.CreateWriter();
            foreach (var json in Books.Json) writer.AddDocument(json);
            writer.Commit();
        }
        using var reopened = SearchIndex.Open(dir.Path);
        Assert.Equal(DocStoreCompression.Zstd, reopened.Settings.DocStoreCompression);
        Assert.Equal(9, reopened.Settings.ZstdLevel);
        Assert.Equal(65_536, reopened.Settings.DocStoreBlockSize);
        using var searcher = reopened.CreateSearcher();
        Assert.Equal("East of Eden", searcher.Search("title:eden")[0].Document.GetString("title"));
    }

    [Theory]
    [InlineData(DocStoreCompression.Lz4)]
    [InlineData(DocStoreCompression.Zstd)]
    [InlineData(DocStoreCompression.None)]
    public void Stored_fields_round_trip_with_every_compression(DocStoreCompression compression)
    {
        using var index = SearchIndex.CreateInRam(Books.Schema(), new IndexSettings { DocStoreCompression = compression });
        using (var writer = index.CreateWriter())
        {
            foreach (var json in Books.Json) writer.AddDocument(json);
            writer.Commit();
        }
        using var searcher = index.CreateSearcher();
        Assert.Equal(["b1", "b2", "b3", "b4"], Books.Ids(searcher.Search(Query.All, limit: 10)));
        Assert.Equal(compression, index.Settings.DocStoreCompression);
    }

    [Fact]
    public void Compression_shrinks_the_doc_store()
    {
        long StoreBytes(DocStoreCompression compression)
        {
            using var index = SearchIndex.CreateInRam(Books.Schema(), new IndexSettings { DocStoreCompression = compression });
            using (var writer = index.CreateWriter())
            {
                for (int i = 0; i < 500; i++)
                    writer.AddDocument($$"""{"id": "x{{i}}", "title": "the same title repeated over and over again {{i % 3}}"}""");
                writer.Commit();
            }
            return index.GetStats().Segments.Sum(s => s.StoreBytes);
        }

        long none = StoreBytes(DocStoreCompression.None), lz4 = StoreBytes(DocStoreCompression.Lz4), zstd = StoreBytes(DocStoreCompression.Zstd);
        Assert.True(lz4 < none, $"lz4 {lz4} vs none {none}");
        Assert.True(zstd < none, $"zstd {zstd} vs none {none}");
    }

    [Fact]
    public void Invalid_settings_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IndexSettings { ZstdLevel = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new IndexSettings { ZstdLevel = 23 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new IndexSettings { DocStoreBlockSize = 0 });
        Assert.Throws<ArgumentException>(() => SearchIndex.CreateInRam(Books.Schema(), new IndexSettings { ZstdLevel = 5 }));
#pragma warning disable TDN0001
        Assert.Throws<ArgumentException>(() => new IndexSettings { SortBy = SortBy.Score() });
        // Sorting needs a fast field.
        var e = Assert.Throws<TantivyException>(() => SearchIndex.CreateInRam(Books.Schema(), new IndexSettings { SortBy = SortBy.Field("title") }));
#pragma warning restore TDN0001
        Assert.Equal(TantivyErrorCode.InvalidArgument, e.Code);
    }

#pragma warning disable TDN0001
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_sorted_index_stores_documents_in_field_order(bool descending)
    {
        using var dir = new TempDirectory();
        var settings = new IndexSettings { SortBy = SortBy.Field("year", descending) };
        using (var index = SearchIndex.Create(dir.Path, Books.Schema(), settings))
        {
            using var writer = index.CreateWriter(new IndexWriterOptions { MergePolicy = MergePolicy.None });
            // Two commits, so the merge also has to keep the order.
            writer.AddDocument(Books.Json[2]);
            writer.AddDocument(Books.Json[0]);
            writer.Commit();
            writer.AddDocument(Books.Json[3]);
            writer.AddDocument(Books.Json[1]);
            writer.Commit();
            writer.ForceMerge();
        }
        using var reopened = SearchIndex.Open(dir.Path);
        Assert.Equal("year", reopened.Settings.SortBy!.FieldName);
        Assert.Equal(descending, reopened.Settings.SortBy.Descending);
        using var searcher = reopened.CreateSearcher();
        // All documents score the same, so hits come in document order, which is the sort order.
        var years = searcher.Search(Query.All, limit: 10).Select(h => h.Document.GetU64("year")).ToList();
        var expected = descending ? new ulong?[] { 1952, 1945, 1939, 1937 } : [1937, 1939, 1945, 1952];
        Assert.Equal(expected, years);
    }
#pragma warning restore TDN0001
}

public class MergeTests
{
    private static SearchIndex IndexWithSegments(int segments, out IndexWriter writer, MergePolicy? policy = null)
    {
        var index = SearchIndex.CreateInRam(Books.Schema());
        // One indexing thread, so each commit makes exactly one segment.
        writer = index.CreateWriter(new IndexWriterOptions { MergePolicy = policy ?? MergePolicy.None, ThreadCount = 1 });
        for (int s = 0; s < segments; s++)
        {
            for (int i = 0; i < 3; i++) writer.AddDocument($$"""{"id": "s{{s}}-{{i}}", "title": "segment {{s}} doc {{i}}", "year": {{1900 + s}}}""");
            writer.Commit();
        }
        return index;
    }

    [Fact]
    public void Without_a_merge_policy_each_commit_adds_a_segment()
    {
        using var index = IndexWithSegments(4, out var writer);
        using (writer)
            Assert.Equal(4, index.GetStats().SegmentCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void ForceMerge_merges_down_to_the_requested_segment_count(int maxSegments)
    {
        using var index = IndexWithSegments(4, out var writer);
        using (writer) writer.ForceMerge(maxSegments);
        var stats = index.GetStats();
        Assert.Equal(maxSegments, stats.SegmentCount);
        Assert.Equal(12, stats.DocumentCount);
        using var searcher = index.CreateSearcher();
        Assert.Equal(3, searcher.Count("title:segment AND year:1902"));
    }

    [Fact]
    public void ForceMerge_drops_deleted_documents()
    {
        using var index = IndexWithSegments(1, out var writer);
        using (writer)
        {
            writer.DeleteTerm("id", "s0-1");
            writer.Commit();
            Assert.Equal(1, index.GetStats().DeletedDocumentCount);
            writer.ForceMerge();
        }
        var stats = index.GetStats();
        Assert.Equal(0, stats.DeletedDocumentCount);
        Assert.Equal(2, stats.DocumentCount);
        Assert.Equal(1, stats.SegmentCount);
    }

    [Fact]
    public void ForceMerge_leaves_uncommitted_changes_pending()
    {
        using var index = IndexWithSegments(2, out var writer);
        using (writer)
        {
            writer.AddDocument("""{"id": "pending", "title": "pending"}""");
            writer.ForceMerge();
            Assert.Equal(6, index.GetStats().DocumentCount);
            writer.Commit();
        }
        Assert.Equal(7, index.GetStats().DocumentCount);
    }

    [Fact]
    public void An_old_searcher_keeps_its_snapshot_across_a_merge()
    {
        using var index = IndexWithSegments(3, out var writer);
        using var old = index.CreateSearcher();
        Assert.Equal(3, old.GetStats().SegmentCount);
        using (writer) writer.ForceMerge();
        // The old snapshot keeps its (now replaced) segments open and still searches them.
        Assert.Equal(9, old.Search(Query.All, limit: 20).Count);
        Assert.Equal(1, index.GetStats().SegmentCount);
        using var latest = index.CreateSearcher();
        Assert.Equal(9, latest.Search(Query.All, limit: 20).Count);
    }

    [Fact]
    public void ForceMerge_on_an_empty_index_does_nothing()
    {
        using var index = SearchIndex.CreateInRam(Books.Schema());
        using var writer = index.CreateWriter();
        writer.ForceMerge();
        Assert.Equal(0, index.GetStats().SegmentCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.ForceMerge(0));
    }

    [Fact]
    public void The_log_merge_policy_merges_in_the_background()
    {
        using var dir = new TempDirectory();
        using (var index = SearchIndex.Create(dir.Path, Books.Schema()))
        using (var writer = index.CreateWriter(new IndexWriterOptions { MergePolicy = new LogMergePolicy { MinSegments = 2, MinLayerSize = 0 } }))
        {
            for (int s = 0; s < 6; s++)
            {
                writer.AddDocument($$"""{"id": "d{{s}}", "title": "doc {{s}}"}""");
                writer.Commit();
            }
        } // Dispose waits for the merges.
        using var reopened = SearchIndex.Open(dir.Path);
        var stats = reopened.GetStats();
        Assert.True(stats.SegmentCount < 6, $"{stats.SegmentCount} segments");
        Assert.Equal(6, stats.DocumentCount);
    }

    [Fact]
    public void Merge_policy_settings_are_validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogMergePolicy { MinSegments = 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogMergePolicy { DeletedDocsRatio = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogMergePolicy { DeletedDocsRatio = 1.5 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogMergePolicy { LevelLogSize = double.NaN });
        Assert.Throws<ArgumentNullException>(() => new IndexWriterOptions { MergePolicy = null! });
        Assert.StartsWith("Log(MinSegments = 8", MergePolicy.Default.ToString());
    }

    [Fact]
    public void GarbageCollectFiles_removes_files_of_merged_segments()
    {
        using var dir = new TempDirectory();
        using var index = SearchIndex.Create(dir.Path, Books.Schema());
        using var writer = index.CreateWriter(new IndexWriterOptions { MergePolicy = MergePolicy.None, ThreadCount = 1 });
        for (int s = 0; s < 3; s++)
        {
            writer.AddDocument($$"""{"id": "d{{s}}", "title": "doc {{s}}"}""");
            writer.Commit();
        }
        writer.ForceMerge();
        var result = writer.GarbageCollectFiles();
        if (!OperatingSystem.IsWindows()) Assert.Empty(result.FailedFiles);

        var segmentId = Assert.Single(index.GetStats().Segments).Id;
        // Segment files are named <segment id>.<component>; only the merged segment's remain.
        var segmentPrefixes = Directory.GetFiles(dir.Path)
            .Select(Path.GetFileName)
            .Where(n => n!.Contains('.') && !n.StartsWith('.') && n != "meta.json" && !n.EndsWith(".json"))
            .Select(n => n![..n.IndexOf('.')])
            .Distinct();
        Assert.Equal([segmentId], segmentPrefixes);
    }
}

public class IndexStatsTests
{
    // Books.InRam spreads the documents over several indexing threads, hence segments.
    private static SearchIndex SingleSegmentBooks()
    {
        var index = SearchIndex.CreateInRam(Books.Schema());
        using var writer = index.CreateWriter(new IndexWriterOptions { ThreadCount = 1 });
        foreach (var json in Books.Json) writer.AddDocument(json);
        writer.Commit();
        return index;
    }

    [Fact]
    public void Stats_describe_segments_and_fields()
    {
        using var index = SingleSegmentBooks();
        var stats = index.GetStats();
        Assert.Equal(1, stats.SegmentCount);
        Assert.Equal(4, stats.DocumentCount);
        Assert.Equal(0, stats.DeletedDocumentCount);
        Assert.True(stats.SizeBytes > 0);
        var segment = Assert.Single(stats.Segments);
        Assert.Equal(32, segment.Id.Length);
        Assert.Equal(stats.SizeBytes, segment.SizeBytes);
        Assert.True(segment.StoreBytes > 0 && segment.StoreBytes < segment.SizeBytes);

        var title = stats.Fields.Single(f => f.Name == "title");
        Assert.True(title.TermDictionaryBytes > 0 && title.PostingsBytes > 0 && title.PositionsBytes > 0);
        Assert.Equal(title.TermDictionaryBytes + title.PostingsBytes + title.PositionsBytes + title.FastFieldBytes + title.FieldNormBytes, title.TotalBytes);
        Assert.True(stats.Fields.Single(f => f.Name == "year").FastFieldBytes > 0);
        Assert.Equal(stats.Fields.Select(f => f.Name).Order(StringComparer.Ordinal), stats.Fields.Select(f => f.Name));
        Assert.Contains("1 segments, 4 docs (0 deleted)", stats.ToString());
    }

    [Fact]
    public void Stats_of_an_empty_index()
    {
        using var index = SearchIndex.CreateInRam(Books.Schema());
        var stats = index.GetStats();
        Assert.Equal(0, stats.SegmentCount);
        Assert.Equal(0, stats.SizeBytes);
        Assert.Empty(stats.Fields);
    }

    [Fact]
    public void A_searchers_stats_describe_its_own_snapshot()
    {
        using var index = SingleSegmentBooks();
        using var old = index.CreateSearcher();
        using (var writer = index.CreateWriter(new IndexWriterOptions { ThreadCount = 1 }))
        {
            writer.DeleteTerm("id", "b1");
            writer.AddDocument("""{"id": "b5", "title": "Tortilla Flat", "year": 1935}""");
            writer.Commit();
        }
        Assert.Equal(4, old.GetStats().DocumentCount);
        Assert.Equal(1, old.GetStats().SegmentCount);
        var latest = index.GetStats();
        Assert.Equal(4, latest.DocumentCount);
        Assert.Equal(1, latest.DeletedDocumentCount);
        Assert.Equal(2, latest.SegmentCount);
    }
}

public class WriterFinalizerTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonWriter(SearchIndex index)
    {
        var writer = index.CreateWriter();
        writer.AddDocument("""{"id": "x", "title": "never committed"}""");
    }

    [Fact]
    public void An_abandoned_writer_is_finalized_and_releases_the_lock()
    {
        using var dir = new TempDirectory();
        using var index = SearchIndex.Create(dir.Path, Books.Schema());
        AbandonWriter(index);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try
            {
                using var writer = index.CreateWriter();
                break;
            }
            catch (TantivyException e) when (e.Code == TantivyErrorCode.LockBusy && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
        using var searcher = index.CreateSearcher();
        Assert.Equal(0, searcher.DocumentCount);
    }
}
