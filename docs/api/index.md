# API reference

Everything public is in the [`TantivyDotNet`](xref:TantivyDotNet) namespace. The main entry points:

| Type | Role |
| --- | --- |
| <xref:TantivyDotNet.SchemaBuilder> | Declares fields and analyzers; builds a <xref:TantivyDotNet.Schema> |
| <xref:TantivyDotNet.SearchIndex> | Creates or opens an index; makes writers, searchers and searcher managers |
| <xref:TantivyDotNet.IndexWriter> | Adds, updates and deletes documents; commits |
| <xref:TantivyDotNet.Searcher> | Runs queries against a snapshot |
| <xref:TantivyDotNet.SearcherManager> | Shares one searcher across requests and refreshes it |
| <xref:TantivyDotNet.Query> | Builds queries in code or parses query text |
| <xref:TantivyDotNet.SearchOptions> | Paging, sorting, returned fields, snippets, facets and aggregations |
| <xref:TantivyDotNet.SearchResults> | Hits, total count, facet counts and aggregation results |
| <xref:TantivyDotNet.TantivyException> | Errors from the native engine, with a <xref:TantivyDotNet.TantivyErrorCode> |
