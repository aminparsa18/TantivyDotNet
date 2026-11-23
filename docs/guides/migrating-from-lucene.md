# Migrating from Lucene.NET

Tantivy is modeled on Lucene, so most concepts carry over: segments, an inverted index, stored fields, doc values (called fast fields), a writer that commits, and searchers over point-in-time snapshots. This page maps Lucene.NET 4.8 code to TantivyDotNet and lists the differences that matter when you port an application.

Indexes aren't compatible between the two. Plan to reindex from your source data.

## Concepts

| Lucene.NET | TantivyDotNet |
| --- | --- |
| `FSDirectory.Open(path)` | `SearchIndex.Create(path, schema)`, then `SearchIndex.Open(path)` |
| `RAMDirectory` | `SearchIndex.CreateInRam(schema)` |
| Fields declared per document (`TextField`, `StringField`, ...) | A [schema](schema-design.md) declared once, when the index is created |
| `Field.Store.YES` | `stored: true` |
| Doc values (`NumericDocValuesField`, `SortedDocValuesField`) | `fast: true` |
| `TextField` | `AddTextField` |
| `StringField` | `AddStringField` |
| `Int64Field`, `DoubleField` (+ doc values for sorting) | `AddI64Field`, `AddF64Field` (with `fast: true` for sorting) |
| `StoredField` only | A field with `stored: true, indexed: false` (numbers), or a text field you don't query |
| Facet module with a taxonomy index | `AddFacetField` + `FacetRequest`; no separate taxonomy index |
| `Analyzer`, `PerFieldAnalyzerWrapper` | A tokenizer name per field in the schema; custom ones with [`Analyzer`](analyzers.md) |
| `IndexWriterConfig.RAMBufferSizeMB` | `IndexWriterOptions.HeapBytes` |
| `IndexWriterConfig.MergePolicy` | `IndexWriterOptions.MergePolicy` (`LogMergePolicy`, `MergePolicy.None`) |
| `DirectoryReader.Open` + `IndexSearcher` | `index.CreateSearcher()` |
| `SearcherManager` (`Acquire`/`Release`, `MaybeRefresh`) | `SearcherManager` (`Acquire` returns a lease you dispose; `Refresh`, or `RefreshOnCommit`) |
| `IndexSearcher.Doc(docId)` | Hits carry their stored `Document`; there's no separate load step |
| `TopDocs.TotalHits` | `SearchResults.TotalCount` |
| `IndexSearcher.Explain` | `Searcher.Explain(query, hit)` |

## Indexing

```csharp
// Lucene.NET
var doc = new Document
{
    new StringField("id", "b1", Field.Store.YES),
    new TextField("title", "Of Mice and Men", Field.Store.YES),
    new Int32Field("year", 1937, Field.Store.YES),
    new NumericDocValuesField("year_dv", 1937),
};
writer.UpdateDocument(new Term("id", "b1"), doc);
writer.Commit();
```

```csharp
// TantivyDotNet: the field options live in the schema; a document is JSON
writer.UpdateDocument(Query.Term("id", "b1"), """{"id": "b1", "title": "Of Mice and Men", "year": 1937}""");
writer.Commit();
```

One `year` field declared with `stored: true, fast: true` replaces Lucene's separate stored, indexed and doc-values fields.

| Lucene.NET | TantivyDotNet |
| --- | --- |
| `writer.AddDocument(doc)` | `writer.AddDocument(json)`, or a serialized object |
| `writer.UpdateDocument(term, doc)` | `writer.UpdateDocument(Query.Term(field, value), doc)` |
| `writer.DeleteDocuments(term)` | `writer.DeleteTerm(field, value)` |
| `writer.DeleteDocuments(query)` | `writer.DeleteDocuments(query)` |
| `writer.DeleteAll()` | `writer.DeleteAllDocuments()` |
| `writer.Commit()` | `writer.Commit()` |
| `writer.Rollback()` | `writer.Rollback()`; the writer stays open and usable |
| `writer.ForceMerge(n)` | `writer.ForceMerge(n)` |
| `writer.Dispose()` (commits by default in 4.8) | `writer.Dispose()` **discards** uncommitted changes, so call `Commit()` first |

## Queries

| Lucene.NET | TantivyDotNet |
| --- | --- |
| `QueryParser`, `MultiFieldQueryParser` | `Query.Parse(text, new QueryParserOptions { DefaultFields = [...] })` |
| `QueryParser.DefaultOperator = AND` | `QueryParserOptions.DefaultOperator = QueryOperator.And` |
| `TermQuery` | `Query.Term(field, value)` |
| `PhraseQuery` (with `Slop`) | `Query.Phrase(field, text, slop)`; the text is analyzed for you |
| `BooleanQuery` with `Occur.MUST` / `SHOULD` / `MUST_NOT` | `Query.Boolean(must, should, mustNot)`, or `&`, `\|`, `!` |
| `FuzzyQuery` | `Query.Fuzzy(field, value, distance)` |
| `PrefixQuery` | `Query.Fuzzy(field, text, distance: 0, prefix: true)` or `Query.Regex(field, "prefix.*")` |
| `WildcardQuery`, `RegexpQuery` | `Query.Regex(field, pattern)`, anchored, Rust regex syntax |
| `NumericRangeQuery`, `TermRangeQuery` | `Query.Range(field, lower, upper)` |
| `ConstantScoreQuery`, query boosts | `query.ConstantScore()`, `query.Boost(factor)` |
| `DisjunctionMaxQuery` | `Query.DisjunctionMax(queries, tieBreaker)` |
| `MoreLikeThis` | `Query.MoreLikeThis(document)` or `Query.MoreLikeThis(fieldTexts)` |
| `MatchAllDocsQuery` | `Query.All` |
| Filters (`QueryWrapperFilter`, `FilteredQuery`) | `query & filter.ConstantScore()` |

The query language is close to Lucene's but not the same. Tantivy has no `word~1` fuzzy syntax (use `QueryParserOptions.FuzzyFields`) and no wildcard terms like `mi*e` (use `Query.Regex`). See [Queries](queries.md#the-query-language).

## Searching

| Lucene.NET | TantivyDotNet |
| --- | --- |
| `searcher.Search(query, n)` | `searcher.Search(query, limit: n)` |
| `searcher.Search(query, n, sort)` with `SortField` | `SearchOptions.OrderBy = [SortBy.Field(...)]` |
| `searcher.SearchAfter(lastScoreDoc, ...)` | `SearchOptions.SearchAfter = lastHit.Cursor` |
| `TotalHitCountCollector` | `searcher.Count(query)` |
| `Highlighter`, `FastVectorHighlighter` | `SearchOptions.Snippets` |
| Facet counts (`FastTaxonomyFacetCounts`) | `SearchOptions.Facets` |
| Grouping and custom collectors | `SearchOptions.Aggregations` (Elasticsearch-style JSON) |

## Differences to know about

- **Scores differ.** Lucene.NET 4.8 scores with TF-IDF (`DefaultSimilarity`) unless you set `BM25Similarity`. Tantivy always uses BM25, and its scoring can't be customized. Tune ranking with boosts instead.
- **Tokenization differs.** Tantivy's `default` tokenizer splits on every non-alphanumeric character, so `science's` becomes `science` and `s`. Lucene's `StandardAnalyzer` keeps it as one token and removes English stop words. Check terms with `SearchIndex.Analyze`, and define an analyzer if you need Lucene's behavior.
- **No custom analysis code.** Analyzers are assembled from built-in tokenizers and filters. You can't write a tokenizer or filter in C#.
- **No near-real-time readers.** Lucene can open a reader on uncommitted changes (`DirectoryReader.Open(writer)`). In Tantivy, a document is searchable only after `Commit()`. Commits are cheaper than Lucene's, so commit more often if you need freshness, for example every second.
- **No document IDs.** Tantivy's internal document addresses change when segments merge and aren't exposed. Keep your own ID in a string field.
- **A schema is fixed.** You can't add a field to an existing index. A JSON field (`AddJsonField`) takes keys you didn't know at creation.
- **Unknown fields are errors.** Adding a document with a field the schema doesn't have throws, unless the writer ignores unknown fields.
- **`Dispose()` on a writer doesn't commit**, unlike Lucene.NET 4.8's default.
