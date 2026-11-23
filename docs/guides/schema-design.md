# Schema design

A [`Schema`](xref:TantivyDotNet.Schema) lists an index's fields. Each field has a type and up to three options, which decide what you can do with it:

| Option | What it enables | Cost |
| --- | --- | --- |
| **indexed** | Searching: term, phrase, range and fuzzy queries | An inverted index (term dictionary and postings) |
| **stored** | Returning the original value in search hits | Compressed row storage (the doc store) |
| **fast** | Sorting, aggregations, `Query.Exists`, fast range queries | A column per field, memory-mapped |

The schema is fixed when the index is created and is saved with it. To change it, create a new index and reindex your data. Design it from your queries: for each thing you'll search, sort, filter or display, decide which field serves it.

## Field types

| Builder method | Type | Defaults | Typical use |
| --- | --- | --- | --- |
| `AddTextField` | Text | indexed, tokenized with `default` | Titles, bodies, anything searched by words |
| `AddStringField` | Text | indexed, one term (`raw` tokenizer) | IDs, tags, enum values, keys you delete or update by |
| `AddU64Field`, `AddI64Field`, `AddF64Field` | Numbers | indexed | Years, prices, counts, popularity |
| `AddBoolField` | Bool | indexed | Flags |
| `AddDateField` | Date | indexed | Timestamps (stored in UTC, second precision) |
| `AddBytesField` | Bytes | indexed | Hashes, binary keys (base64 in JSON) |
| `AddIpAddressField` | IP address | indexed | IPv4 and IPv6 addresses |
| `AddFacetField` | Facet | always indexed and fast | Hierarchical categories like `/books/fiction` |
| `AddJsonField` | JSON | indexed | Attributes whose keys you don't know in advance |

Numeric, date, bool, bytes and IP fields are indexed by default (`indexed: true`). Turn it off for a field you only sort or aggregate on. Nothing is stored or fast unless you ask for it.

```csharp
var schema = new SchemaBuilder()
    .AddStringField("id", stored: true)                 // update and delete by ID
    .AddTextField("title", stored: true)                // searched and displayed
    .AddTextField("body", stored: true)                 // stored so snippets can be built from it
    .AddStringField("language", fast: true)             // filter, and aggregate with "terms"
    .AddFacetField("category", stored: true)            // /books/fiction/classic
    .AddU64Field("popularity", indexed: false, fast: true)   // sort only
    .AddDateField("published", stored: true, fast: true)     // display, range filter, sort
    .AddJsonField("attrs", stored: true, fast: true)     // {"color": "red", "size": 42}
    .Build();
```

## Choosing options

**Give every document an ID field.** Use `AddStringField("id")`. `UpdateDocument(Query.Term("id", ...), doc)` and `DeleteTerm("id", ...)` need an exact term, and a tokenized text field doesn't keep the value as one term.

**Store only what you display.** Stored fields are compressed in blocks, and a hit decompresses its block. Large stored fields make every hit slower to load. If you keep the full document in a database, store just the ID and load the rest from there. `SearchOptions.Fields` limits which stored fields a search returns.

**Snippets need stored text.** [`SnippetOptions`](xref:TantivyDotNet.SnippetOptions) works only on fields that are both indexed and stored.

**Sorting and aggregations need fast fields.** `SortBy.Field("year")` and `{"terms": {"field": "language"}}` read columns, not the inverted index. A fast text field holds the whole value untokenized, so make it a string field, or add a second string field next to the text field:

```csharp
.AddTextField("author", stored: true)              // search "steinbeck"
.AddStringField("author_exact", fast: true)        // sort by author, count per author
```

Each document then carries the value under both names.

**Range queries work on indexed or fast fields.** A fast field is usually quicker for wide ranges on numbers and dates.

**Text matching depends on the tokenizer.** `default` splits on punctuation and whitespace and lowercases. `en_stem` also stems English words (`running` matches `run`). `raw` keeps the whole value. `whitespace` splits on whitespace only and keeps case. For anything else, define an analyzer: see [Analyzers](analyzers.md).

## Values in documents

- A field can hold several values: `{"tag": ["a", "b"]}`. Search matches any of them, and `Document.GetStrings("tag")` returns all of them.
- Dates are RFC 3339 strings with an offset (`"2024-05-01T12:00:00Z"`). Serialize `DateTimeOffset` or UTC `DateTime` values; a `DateTime` without a time zone is rejected. Dates outside the years 1677 to 2262 are rejected.
- Bytes are base64 strings. IP addresses are strings like `"10.0.0.1"` or `"2001:db8::1"`.
- Facet values are paths that start with `/`. A document can have several. A `/` inside a segment is written `\/`.
- A JSON field takes any object. Query its keys by path: `Query.Term("attrs.color", "red")`, `Query.Range("attrs.size", 40L, 50L)`, or `attrs.color:red` in the query language. Strings are tokenized; numbers, booleans and dates keep their types.
- Missing fields are fine. Sorting puts documents without a value last.

## Index settings

[`IndexSettings`](xref:TantivyDotNet.IndexSettings) are chosen at creation and saved with the index:

```csharp
using var index = SearchIndex.Create("books-index", schema, new IndexSettings
{
    DocStoreCompression = DocStoreCompression.Zstd,   // smaller stored fields, slower to load; default Lz4
    ZstdLevel = 9,
    DocStoreBlockSize = 64 * 1024,                    // larger blocks compress better, load slower
});
```

The default (LZ4, 16 KB blocks) suits most indexes. Zstd pays off when stored fields are large and searches return few hits.
