# Analyzers

An analyzer turns text into the terms that get indexed. The same analyzer runs on query text for that field, so `"Running Cafés"` and `"running cafes"` can match the same documents. An analyzer is a **tokenizer**, which splits text into tokens, followed by **filters**, which change or drop tokens.

## Built-in tokenizers

Every index has these, and a text field uses one by name (`tokenizer:` in `AddTextField`):

| Name | Does |
| --- | --- |
| `default` | Splits on anything that isn't a letter or digit, lowercases, and drops tokens longer than 40 bytes |
| `en_stem` | `default`, plus English stemming (`running` → `run`) |
| `raw` | Keeps the whole value as one term (`AddStringField` uses it) |
| `whitespace` | Splits on whitespace only and keeps case and punctuation |

## Custom analyzers

Build one with [`Analyzer`](xref:TantivyDotNet.Analyzer), register it in the schema under a name, and use that name as a field's tokenizer:

```csharp
var schema = new SchemaBuilder()
    .AddAnalyzer("en_text", Analyzer.Simple()
        .LowerCase()
        .AsciiFolding()                          // café -> cafe
        .StopWords(AnalyzerLanguage.English)     // drop "the", "and", ...
        .Stem(AnalyzerLanguage.English))         // running -> run
    .AddTextField("body", stored: true, tokenizer: "en_text")
    .Build();
```

Tokenizers:

| Factory | Splits |
| --- | --- |
| `Analyzer.Simple()` | On every character that isn't a letter or digit (`default` without its filters) |
| `Analyzer.Whitespace()` | On whitespace |
| `Analyzer.Raw()` | Not at all: one token per value |
| `Analyzer.NGram(min, max, prefixOnly)` | Into every substring of `min` to `max` characters; with `prefixOnly`, only those at the start |
| `Analyzer.Regex(pattern)` | Into the matches of a Rust regex, for example `[A-Za-z0-9_-]+` |

Filters, in the order you add them:

| Filter | Does |
| --- | --- |
| `LowerCase()` | Lowercases |
| `AsciiFolding()` | Turns accented and other non-ASCII letters into their closest ASCII form |
| `StopWords(language)` / `StopWords(words)` | Drops common words: Tantivy's list for a language, or your own (exact match) |
| `Stem(language)` | Snowball stemming |
| `RemoveLong(maxBytes)` | Drops tokens longer than `maxBytes` UTF-8 bytes |

Order matters. `StopWords` and `Stem` expect lowercase input, so put `LowerCase()` first. Stemming is available for every [`AnalyzerLanguage`](xref:TantivyDotNet.AnalyzerLanguage). Stop word lists aren't available for Arabic, Greek, Romanian, Tamil and Turkish; using one fails when the index is created.

## Check what an analyzer produces

[`SearchIndex.Analyze`](xref:TantivyDotNet.SearchIndex.Analyze(System.String,System.String)) runs text through a tokenizer of the index:

```csharp
using var index = SearchIndex.CreateInRam(schema);
var terms = index.Analyze("en_text", "The cafés were running late");
// ["cafe", "were", "run", "late"]: "the" is a stop word, "were" isn't in Tantivy's English list
```

Use it when a query doesn't match what you expect. A [`Query.Term`](xref:TantivyDotNet.Query.Term(System.String,System.String)) isn't analyzed, so it has to equal one of these terms exactly.

## Common recipes

**Case-insensitive exact match**, for example email addresses or product codes:

```csharp
.AddAnalyzer("keyword_ci", Analyzer.Raw().LowerCase())
.AddTextField("email", tokenizer: "keyword_ci")
```

**Search as you type.** Index edge n-grams in a second field and search it with the user's partial input:

```csharp
.AddAnalyzer("prefix", Analyzer.NGram(1, 15, prefixOnly: true).LowerCase())
.AddTextField("title", stored: true)
.AddTextField("title_prefix", tokenizer: "prefix")
```

`prefixOnly` n-grams start at the beginning of the whole value, not of each word, so `"of mice"` produces `o`, `of`, `of `, `of m`, ... Put the text you want to complete from at the start of the value. For per-word prefixes, `FuzzyOptions(distance: 0, prefix: true)` in [`QueryParserOptions.FuzzyFields`](xref:TantivyDotNet.QueryParserOptions.FuzzyFields) matches terms that start with the typed word, without an n-gram field.

**Identifiers with punctuation.** The default tokenizer splits `ERR-4021` into `err` and `4021`. Keep the code together with a regex tokenizer:

```csharp
.AddAnalyzer("codes", Analyzer.Regex(@"[A-Za-z0-9_-]+").LowerCase())
```

## Persistence

An index created in a directory saves its analyzers in `tantivy-dotnet.json` next to Tantivy's files. `SearchIndex.Open` restores them, so you don't pass them again. They can't be changed afterwards: documents already indexed hold the old terms, and queries analyzed with a new analyzer wouldn't match them. To change an analyzer, create a new index and reindex.

Custom analyzers are built from the parts listed above. Tokenizers written in .NET aren't supported, because analysis runs inside the native library on its indexing threads.
