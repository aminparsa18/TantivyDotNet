# Queries

There are two ways to write a query, and they mix freely:

- **The query language**, parsed from a string: `body:mice AND year:[1930 TO 1940]`. Good for text typed by users and for quick experiments.
- **Queries built in code** with [`Query`](xref:TantivyDotNet.Query): `Query.Term("id", "b1") & Query.Range("year", 1930, 1940)`. Good for filters and anything your program assembles. Nothing needs escaping, and the query tree can be inspected.

```csharp
var userPart = Query.Parse(userInput, new QueryParserOptions { DefaultFields = ["title", "body"], Lenient = true });
var query = userPart & Query.Term("language", "en") & Query.Range("year", 1900, null);
var results = searcher.Search(query, limit: 20);
```

## The query language

| Syntax | Meaning |
| --- | --- |
| `mice` | `mice` in the default fields |
| `title:mice` | `mice` in `title` |
| `"of mice and men"` | A phrase: the words in this order |
| `"of men"~2` | A phrase whose words may be up to 2 positions apart (slop) |
| `mice AND men`, `mice OR men` | Both, either |
| `+mice -cats` | `mice` required, `cats` excluded |
| `mice NOT cats` | `mice` but not `cats` |
| `(mice OR rats) AND title:men` | Grouping |
| `title:mice^3` | Weight a clause's score by 3 |
| `year:[1930 TO 1940]` | An inclusive range; `{1930 TO 1940}` excludes the bounds |
| `year:>=1930`, `year:<1940` | Open ranges |
| `published:[2020-01-01T00:00:00Z TO *]` | Dates are RFC 3339; `*` is an open bound |
| `genre:/fiction` | A facet category and the categories below it |
| `attrs.color:red` | A key of a JSON field |
| `*` | Every document |

Words without a field prefix search the default fields: every indexed text field, unless you pass `defaultFields` or [`QueryParserOptions.DefaultFields`](xref:TantivyDotNet.QueryParserOptions.DefaultFields). Words are analyzed with each field's tokenizer, and by default `a b` means `a OR b`.

A malformed query throws [`QueryParseException`](xref:TantivyDotNet.QueryParseException), and so does a field the schema doesn't have.

### Parser options

```csharp
var parser = new QueryParserOptions
{
    DefaultFields = ["title", "body"],
    DefaultOperator = QueryOperator.And,                               // "a b" means a AND b
    FieldBoosts = new Dictionary<string, float> { ["title"] = 3 },      // title matches count 3x
    FuzzyFields = new Dictionary<string, FuzzyOptions> { ["body"] = new(distance: 1) },  // typo tolerance
    Lenient = true,                                                    // never throw on user input
};
var results = searcher.Search(Query.Parse("mice men", parser));
```

**Use `Lenient = true` for end-user input.** Unbalanced quotes, stray parentheses and unknown fields are then dropped instead of throwing. There's no per-word `mice~1` fuzzy syntax (in Tantivy, `~` sets a phrase's slop); `FuzzyFields` makes every word searched in a field fuzzy instead. `FuzzyOptions(distance: 0, prefix: true)` gives prefix matching on each word for search-as-you-type.

## Queries built in code

| Factory | Matches |
| --- | --- |
| `Query.All` | Every document |
| `Query.Term(field, value)` | An exact term. **Not analyzed**: on a `default` text field, pass one lowercase word. Overloads for strings, integers, doubles, bools, dates, bytes and IP addresses |
| `Query.TermSet(field, values)` | Any of many exact terms; faster than an OR of terms; constant score |
| `Query.Phrase(field, text, slop)` | The words of `text` in order. Analyzed with the field's tokenizer |
| `Query.Fuzzy(field, value, distance)` | Terms within 0 to 2 edits; `prefix: true` matches terms starting with such a word |
| `Query.Regex(field, pattern)` | Terms matching a Rust regex as a whole |
| `Query.Range(field, lower, upper)` | Values between two bounds (inclusive by default; `null` is open). Numbers, dates, IP addresses and strings |
| `Query.Exists(field)` | Documents with a value in a fast field |
| `Query.Facet(field, path)` | A facet category and the categories below it |
| `Query.MoreLikeThis(document)` | Documents similar to a hit's document, or to some text per field |
| `Query.Parse(text, ...)` | Text in the query language |

Combine and adjust them:

```csharp
var q = Query.Phrase("body", "of mice") & !Query.Term("status", "draft");   // AND, NOT
var either = Query.Term("tag", "rust") | Query.Term("tag", "dotnet");      // OR
var weighted = Query.Boolean(
    must: [Query.Parse("mice", ["body"])],
    should: [Query.Term("tag", "classic").Boost(2)],                       // optional; raises the score
    mustNot: [Query.Term("status", "draft")]);
var filter = Query.Range("year", 1930, 1940).ConstantScore();              // matches, doesn't affect ranking
var bestField = Query.DisjunctionMax([Query.Parse("mice", ["title"]), Query.Parse("mice", ["body"])], tieBreaker: 0.1f);
```

`&` and `|` flatten chains into one [`BooleanQuery`](xref:TantivyDotNet.BooleanQuery). A Boolean with only `mustNot` clauses means "everything except", so `!Query.Term("status", "draft")` on its own works.

Range bounds are converted to the field's type when that's lossless, so `Query.Range("year", 1930, 1940)` works on a u64 field. A query on a field that doesn't exist, or that doesn't fit the field (a phrase on a number), throws [`TantivyException`](xref:TantivyDotNet.TantivyException) with code `InvalidArgument` when it runs.

## Search options

[`SearchOptions`](xref:TantivyDotNet.SearchOptions) holds everything besides the query. It's all computed in one pass over the matches.

```csharp
var results = searcher.Search(query, new SearchOptions
{
    Limit = 20,
    OrderBy = [SortBy.Field("year", descending: true), SortBy.Score()],
    Fields = ["id", "title"],
    Snippets = new SnippetOptions("body") { MaxLength = 200 },
    Facets = [new FacetRequest("category", "/") { Top = 10 }],
    Aggregations = """{"per_language": {"terms": {"field": "language"}}}""",
});
```

### Paging and sorting

`Limit` and `Offset` page through the hits. `TotalCount` counts every match. Large offsets are slow, because every skipped hit is still ranked. For deep paging, pass the last hit's [`Cursor`](xref:TantivyDotNet.SearchHit.Cursor) as `SearchAfter`, with the same `OrderBy`:

```csharp
var order = new[] { SortBy.Field("year", descending: true) };
var page = searcher.Search(query, new SearchOptions { Limit = 50, OrderBy = order });
while (page.Count > 0)
{
    // ... use page ...
    page = searcher.Search(query, new SearchOptions { Limit = 50, OrderBy = order, SearchAfter = page[^1].Cursor });
}
```

A cursor is a string, so you can hand it to a client and get it back with the next request. It stays valid after the index changes, so a server can read each page from whatever searcher its `SearcherManager` currently has:

- On the searcher that returned the cursor, the next page is exact.
- On a newer searcher (after a commit or merge), paging continues with the documents that sort after the cursor's sort values. Documents committed meanwhile show up if they sort after the cursor; deleted ones disappear; a document whose sort value changed can show up twice or not at all.
- Documents tied with the cursor on every sort key are skipped on a newer searcher. End `OrderBy` with a unique fast field, such as the document's id, so no two documents tie:

```csharp
var order = new[] { SortBy.Field("year", descending: true), SortBy.Field("id") };
```

- Relevance scores change as the index changes, so paging by score across commits is approximate.

To page through one unchanging snapshot instead, keep the same `Searcher` or `SearcherLease` for the whole listing. Start over (without `SearchAfter`) when the user changes the query or the order: a cursor only works with the `OrderBy` it came from.

`OrderBy` takes fast fields (of any type except facet and JSON) and `SortBy.Score()`. Documents without a value come last in both directions. Unless the score is a sort key, it isn't computed and `hit.Score` is `NaN`. `hit.SortValues` holds the values the hit was sorted by.

### Snippets

A snippet is the best-matching fragment of a stored text field, with the matched terms marked:

```csharp
foreach (var hit in results)
{
    Snippet s = hit.Snippets["body"];
    Console.WriteLine(s.IsEmpty ? hit.Document.GetString("title") : s.ToHtml());   // HTML-encoded, matches in <b>
}
```

`Highlights` are UTF-16 ranges into `Fragment`, for rendering without HTML. `MaxLength` is in UTF-8 bytes. Fuzzy, regex and range queries produce no highlights.

### Facets

```csharp
var results = searcher.Search(Query.All, new SearchOptions
{
    Limit = 0,                                             // counts only
    Facets = [new FacetRequest("category", "/", "/books") { Top = 5 }],
});
foreach (var (path, count) in results.Facets["category"])
    Console.WriteLine($"{path}: {count}");                 // direct children of "/" and "/books"
```

Counts cover all matches, not only the returned page. Filter by a category with `Query.Facet("category", "/books/fiction")`.

### Aggregations

`Aggregations` takes Tantivy's Elasticsearch-compatible JSON: `terms`, `range`, `histogram`, `date_histogram`, `avg`, `sum`, `min`, `max`, `stats`, `percentiles`, `cardinality`, `top_hits` and more, nested with `aggs`. Aggregated fields must be fast. The result is JSON, or your own type:

```csharp
JsonElement aggs = results.Aggregations!.Value;
var typed = results.GetAggregations(MyJsonContext.Default.LanguageAggs);   // AOT-safe
```

## Relevance

Scores are BM25, as in Lucene. To see how a hit's score was computed:

```csharp
Explanation e = searcher.Explain(query, results[0]);
Console.WriteLine(e);   // an indented tree of the score's parts
```

Tune ranking with `Boost`, `FieldBoosts`, `DisjunctionMax` (search several fields without rewarding repetition), and `ConstantScore` for filters that shouldn't change the order.
