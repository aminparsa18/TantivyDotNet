//! C ABI over Tantivy, consumed by TantivyDotNet via csbindgen-generated P/Invokes.
//!
//! Conventions (every export follows them):
//! - Names are prefixed `tdn_`.
//! - Fallible exports return a status code (see `ffi::status`; 0 = OK). On failure, a
//!   Rust-allocated UTF-8 message is written to the `err` out-parameter and must be released with
//!   `tdn_string_free`. No panic crosses the boundary.
//! - Strings are UTF-8 pointer + length in both directions; nothing is null-terminated.
//! - Objects are opaque handles (`Box::into_raw`) released with the matching `tdn_*_free`.
//!   Free functions accept null.
//!
//! # Safety (applies to every `unsafe extern "C"` export)
//! Pointer + length pairs must describe readable memory for the duration of the call; handles
//! must be null or live values from this library (not yet freed); out-parameters must be null or
//! writable. The .NET layer upholds this via `SafeHandle` leases and `fixed` buffers.
#![allow(clippy::missing_safety_doc)] // covered once by the crate-level section above

mod aggregations;
mod analyzers;
mod documents;
mod facets;
mod ffi;
mod query_spec;
mod schema_spec;
mod snippets;
mod sorting;

use std::collections::BTreeMap;
use std::sync::Mutex;

use serde::{Deserialize, Serialize};
use tantivy::aggregation::agg_result::AggregationResults;
use tantivy::collector::{Count, MultiCollector};
use tantivy::schema::{NamedFieldDocument, Schema};
use tantivy::{
    Document, Index, IndexReader, IndexWriter, ReloadPolicy, Searcher, TantivyDocument, Term,
};

use facets::{FacetRequest, FacetResultJson};
use query_spec::QuerySpec;
use snippets::{SnippetJson, SnippetRequest, Snippets};
use sorting::SortRequest;
use ffi::{guard, handle, str_arg, write_out, FfiError, FfiResult};

/// Bumped whenever the exported C ABI changes; the managed side checks it on load.
pub const ABI_VERSION: u32 = 4;

/// A Rust-owned UTF-8 buffer handed to the caller. Release with `tdn_string_free`.
#[repr(C)]
pub struct TdnString {
    pub ptr: *mut u8,
    pub len: usize,
}

impl TdnString {
    fn from_string(s: String) -> Self {
        let boxed: Box<[u8]> = s.into_bytes().into_boxed_slice();
        let len = boxed.len();
        let ptr = Box::into_raw(boxed) as *mut u8;
        Self { ptr, len }
    }
}

pub struct TdnIndex {
    index: Index,
    reader: IndexReader,
    analyzers: analyzers::Analyzers,
}

/// `IndexWriter::commit`/`rollback` take `&mut self`. The mutex makes concurrent calls from .NET
/// serialize instead of being undefined behaviour; the public API still documents the writer as
/// not thread-safe.
pub struct TdnWriter {
    writer: Mutex<IndexWriter>,
    index: Index,
    schema: Schema,
    ignore_unknown_fields: bool,
}

pub struct TdnSearcher {
    index: Index,
    searcher: Searcher,
}

impl TdnIndex {
    fn new(index: Index, analyzers: analyzers::Analyzers) -> FfiResult<Self> {
        analyzers::register(index.tokenizers(), &analyzers)?;
        // Manual reload: tdn_index_searcher reloads explicitly, so every new searcher sees the
        // latest commit, and no file-watcher thread is started.
        let reader = index.reader_builder().reload_policy(ReloadPolicy::Manual).try_into()?;
        Ok(Self { index, reader, analyzers })
    }
}

impl TdnWriter {
    fn lock(&self) -> FfiResult<std::sync::MutexGuard<'_, IndexWriter>> {
        self.writer
            .lock()
            .map_err(|_| FfiError::new(ffi::status::ERROR, "writer is unusable after an earlier panic"))
    }

    fn parse_doc(&self, json: &str) -> FfiResult<TantivyDocument> {
        documents::parse(&self.schema, json, self.ignore_unknown_fields)
    }

    fn term(&self, term_json: &str) -> FfiResult<Term> {
        query_spec::term_of(&query_spec::parse_spec(term_json)?, &self.schema)
    }
}

#[no_mangle]
pub extern "C" fn tdn_abi_version() -> u32 {
    ABI_VERSION
}

/// Releases a buffer returned through a `TdnString` out-parameter. Accepts an empty/null buffer.
#[no_mangle]
pub unsafe extern "C" fn tdn_string_free(s: TdnString) {
    if !s.ptr.is_null() {
        drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(s.ptr, s.len)));
    }
}

// ---------------------------------------------------------------------------------------------
// Index
// ---------------------------------------------------------------------------------------------

/// Creates an index in anonymous memory from schema JSON (see `schema_spec`).
#[no_mangle]
pub unsafe extern "C" fn tdn_index_create_in_ram(
    schema_ptr: *const u8,
    schema_len: usize,
    out: *mut *mut TdnIndex,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let (schema, analyzers) = schema_spec::schema_from_json(str_arg(schema_ptr, schema_len, "schema")?)?;
        let index = TdnIndex::new(Index::create_in_ram(schema), analyzers)?;
        write_out(out, Box::into_raw(Box::new(index)), "index")
    })
}

/// Creates a new index in an existing directory. Fails if the directory already holds an index.
/// Custom analyzers are saved next to it (`analyzers::ANALYZERS_FILE`).
#[no_mangle]
pub unsafe extern "C" fn tdn_index_create_in_dir(
    path_ptr: *const u8,
    path_len: usize,
    schema_ptr: *const u8,
    schema_len: usize,
    out: *mut *mut TdnIndex,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let path = str_arg(path_ptr, path_len, "path")?;
        let (schema, analyzers) = schema_spec::schema_from_json(str_arg(schema_ptr, schema_len, "schema")?)?;
        let created = Index::create_in_dir(path, schema)?;
        if !analyzers.is_empty() {
            analyzers::save(std::path::Path::new(path), &analyzers)?;
        }
        let index = TdnIndex::new(created, analyzers)?;
        write_out(out, Box::into_raw(Box::new(index)), "index")
    })
}

/// Opens an existing index directory, registering the custom analyzers saved with it.
#[no_mangle]
pub unsafe extern "C" fn tdn_index_open_in_dir(
    path_ptr: *const u8,
    path_len: usize,
    out: *mut *mut TdnIndex,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let path = str_arg(path_ptr, path_len, "path")?;
        let opened = Index::open_in_dir(path)?;
        let analyzers = analyzers::load(std::path::Path::new(path))?;
        let index = TdnIndex::new(opened, analyzers)?;
        write_out(out, Box::into_raw(Box::new(index)), "index")
    })
}

/// Returns the index's schema as schema JSON.
#[no_mangle]
pub unsafe extern "C" fn tdn_index_schema(
    index: *const TdnIndex,
    out: *mut TdnString,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let index = handle(index, "index")?;
        let json = schema_spec::schema_to_json(&index.index.schema(), &index.analyzers);
        write_out(out, TdnString::from_string(json), "schema")
    })
}

/// Runs `text` through the index's tokenizer `name` (built-in or custom) and returns the token
/// texts as a JSON array of strings.
#[no_mangle]
pub unsafe extern "C" fn tdn_index_analyze(
    index: *const TdnIndex,
    name_ptr: *const u8,
    name_len: usize,
    text_ptr: *const u8,
    text_len: usize,
    out: *mut TdnString,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let index = handle(index, "index")?;
        let name = str_arg(name_ptr, name_len, "tokenizer name")?;
        let text = str_arg(text_ptr, text_len, "text")?;
        let tokens = analyzers::analyze(&index.index, name, text)?;
        let json = serde_json::to_string(&tokens).expect("string list serialization cannot fail");
        write_out(out, TdnString::from_string(json), "tokens")
    })
}

#[no_mangle]
pub unsafe extern "C" fn tdn_index_free(index: *mut TdnIndex) {
    ffi::free_box(index);
}

// ---------------------------------------------------------------------------------------------
// Writer
// ---------------------------------------------------------------------------------------------

/// Writer option flags for `tdn_index_writer`.
pub const WRITER_IGNORE_UNKNOWN_FIELDS: u32 = 1;

/// Creates the index's writer. `num_threads == 0` lets Tantivy choose (up to 8, at least 15 MB
/// of heap per thread). `flags` is a combination of `WRITER_*` bits. Only one writer may exist
/// per index at a time.
#[no_mangle]
pub unsafe extern "C" fn tdn_index_writer(
    index: *const TdnIndex,
    heap_bytes: usize,
    num_threads: usize,
    flags: u32,
    out: *mut *mut TdnWriter,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let index = &handle(index, "index")?.index;
        let writer: IndexWriter = if num_threads == 0 {
            index.writer(heap_bytes)?
        } else {
            index.writer_with_num_threads(num_threads, heap_bytes)?
        };
        let writer = TdnWriter {
            writer: Mutex::new(writer),
            index: index.clone(),
            schema: index.schema(),
            ignore_unknown_fields: flags & WRITER_IGNORE_UNKNOWN_FIELDS != 0,
        };
        write_out(out, Box::into_raw(Box::new(writer)), "writer")
    })
}

/// Adds one document given as a JSON object (field name -> value or array of values).
#[no_mangle]
pub unsafe extern "C" fn tdn_writer_add_json(
    writer: *const TdnWriter,
    json_ptr: *const u8,
    json_len: usize,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let writer = handle(writer, "writer")?;
        let doc = writer.parse_doc(str_arg(json_ptr, json_len, "document")?)?;
        writer.lock()?.add_document(doc)?;
        Ok(())
    })
}

/// Deletes every document containing the term of a term-query JSON
/// (`{"type":"term","field":...,"value":{...}}`, see `query_spec`), then adds the document.
/// Both happen under one lock, so they land in the same commit; the delete doesn't affect the
/// new document.
#[no_mangle]
pub unsafe extern "C" fn tdn_writer_update_json(
    writer: *const TdnWriter,
    term_ptr: *const u8,
    term_len: usize,
    json_ptr: *const u8,
    json_len: usize,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let writer = handle(writer, "writer")?;
        let term = writer.term(str_arg(term_ptr, term_len, "term")?)?;
        let doc = writer.parse_doc(str_arg(json_ptr, json_len, "document")?)?;
        let w = writer.lock()?;
        w.delete_term(term);
        w.add_document(doc)?;
        Ok(())
    })
}

/// Deletes every document containing the term of a term-query JSON (see `tdn_writer_update_json`).
/// The value is not run through the field's tokenizer.
#[no_mangle]
pub unsafe extern "C" fn tdn_writer_delete_term(
    writer: *const TdnWriter,
    term_ptr: *const u8,
    term_len: usize,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let writer = handle(writer, "writer")?;
        let term = writer.term(str_arg(term_ptr, term_len, "term")?)?;
        writer.lock()?.delete_term(term);
        Ok(())
    })
}

/// Deletes every document matching a query JSON (see `query_spec`), as of this point in the
/// operation order.
#[no_mangle]
pub unsafe extern "C" fn tdn_writer_delete_query(
    writer: *const TdnWriter,
    query_ptr: *const u8,
    query_len: usize,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let writer = handle(writer, "writer")?;
        let spec = query_spec::parse_spec(str_arg(query_ptr, query_len, "query")?)?;
        let query = query_spec::build(&spec, &writer.index)?;
        writer.lock()?.delete_query(query)?;
        Ok(())
    })
}

#[no_mangle]
pub unsafe extern "C" fn tdn_writer_delete_all(writer: *const TdnWriter, err: *mut TdnString) -> i32 {
    guard(err, || {
        handle(writer, "writer")?.lock()?.delete_all_documents()?;
        Ok(())
    })
}

#[no_mangle]
pub unsafe extern "C" fn tdn_writer_commit(writer: *const TdnWriter, err: *mut TdnString) -> i32 {
    guard(err, || {
        handle(writer, "writer")?.lock()?.commit()?;
        Ok(())
    })
}

/// Discards every add/delete since the last commit.
#[no_mangle]
pub unsafe extern "C" fn tdn_writer_rollback(writer: *const TdnWriter, err: *mut TdnString) -> i32 {
    guard(err, || {
        handle(writer, "writer")?.lock()?.rollback()?;
        Ok(())
    })
}

/// Frees the writer without committing; uncommitted operations are discarded. Blocks until
/// background merges finish (`wait_merging_threads`), like Lucene's `IndexWriter.Dispose()`; a plain
/// drop would kill them, wasting their work and leaving orphan files until the next commit.
/// Errors from the merge threads are ignored: what they would have merged is still committed.
#[no_mangle]
pub unsafe extern "C" fn tdn_writer_free(writer: *mut TdnWriter) {
    if writer.is_null() {
        return;
    }
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let writer = Box::from_raw(writer);
        let inner = writer.writer.into_inner().unwrap_or_else(|poisoned| poisoned.into_inner());
        let _ = inner.wait_merging_threads();
    }));
}

// ---------------------------------------------------------------------------------------------
// Searcher
// ---------------------------------------------------------------------------------------------

/// Creates a searcher over a snapshot of the latest commit. Searchers are thread-safe.
#[no_mangle]
pub unsafe extern "C" fn tdn_index_searcher(
    index: *const TdnIndex,
    out: *mut *mut TdnSearcher,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let index = handle(index, "index")?;
        index.reader.reload()?;
        let searcher = TdnSearcher { index: index.index.clone(), searcher: index.reader.searcher() };
        write_out(out, Box::into_raw(Box::new(searcher)), "searcher")
    })
}

/// Number of live (non-deleted) documents in the snapshot. Returns 0 for a null handle.
#[no_mangle]
pub unsafe extern "C" fn tdn_searcher_num_docs(searcher: *const TdnSearcher) -> u64 {
    searcher.as_ref().map_or(0, |s| s.searcher.num_docs())
}

#[derive(Serialize)]
struct SearchResultJson {
    count: usize,
    hits: Vec<HitJson>,
    #[serde(skip_serializing_if = "Option::is_none")]
    facets: Option<FacetResultJson>,
    #[serde(skip_serializing_if = "Option::is_none")]
    aggregations: Option<AggregationResults>,
}

#[derive(Serialize)]
struct HitJson {
    /// NaN when sorted by a field; serde_json writes non-finite floats as `null`.
    score: f32,
    #[serde(skip_serializing_if = "Option::is_none")]
    sort: Option<serde_json::Value>,
    doc: NamedFieldDocument,
    #[serde(skip_serializing_if = "Option::is_none")]
    snippets: Option<BTreeMap<String, SnippetJson>>,
}

#[derive(Deserialize)]
struct SearchRequest {
    query: QuerySpec,
    #[serde(default = "default_limit")]
    limit: usize,
    #[serde(default)]
    offset: usize,
    #[serde(default)]
    snippets: Option<SnippetRequest>,
    #[serde(default)]
    facets: Option<Vec<FacetRequest>>,
    #[serde(default)]
    sort: Option<SortRequest>,
    #[serde(default)]
    aggregations: Option<serde_json::Value>,
}

fn default_limit() -> usize {
    10
}

/// Runs a search described by request JSON (see `query_spec` for the query part):
/// `{"query": {...}, "limit": n, "offset": n, "sort": {...}?, "snippets": {...}?, "facets": [...]?, "aggregations": {...}?}`,
/// and returns `{"count": <total matches>, "hits": [{"score": f|null, "sort": {...}?, "doc": {"field": [values...]},
/// "snippets": {...}?}], "facets": {...}?, "aggregations": {...}?}`
/// (see the `sorting`, `snippets`, `facets` and `aggregations` modules).
/// `limit == 0` only counts.
#[no_mangle]
pub unsafe extern "C" fn tdn_searcher_search(
    searcher: *const TdnSearcher,
    request_ptr: *const u8,
    request_len: usize,
    out: *mut TdnString,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let s = handle(searcher, "searcher")?;
        let request_json = str_arg(request_ptr, request_len, "search request")?;
        let request: SearchRequest = serde_json::from_str(request_json)
            .map_err(|e| FfiError::invalid(format!("invalid search request JSON: {e}")))?;
        let schema = s.index.schema();
        let query = query_spec::build(&request.query, &s.index)?;

        let mut collectors = MultiCollector::new();
        let count_handle = collectors.add_collector(Count);
        let top_handle = match request.limit {
            0 => None,
            limit => Some(sorting::add(request.sort.as_ref(), limit, request.offset, &schema, &mut collectors)?),
        };
        let facet_plans = match &request.facets {
            Some(f) => facets::add(f, &schema, &mut collectors)?,
            None => Vec::new(),
        };

        let agg_handle = match &request.aggregations {
            Some(a) => Some(aggregations::add(a, &s.index, &mut collectors)?),
            None => None,
        };

        let mut fruits = s.searcher.search(&query, &collectors)?;
        let count = count_handle.extract(&mut fruits);
        let top = top_handle.map(|h| sorting::extract(h, &mut fruits)).unwrap_or_default();
        let facets = request.facets.is_some().then(|| facets::collect(facet_plans, &mut fruits));
        let aggregations = agg_handle.map(|h| h.extract(&mut fruits));

        // Built even with no hits, so an invalid snippet field fails consistently.
        let snippets = request
            .snippets
            .as_ref()
            .map(|r| Snippets::new(r, &s.searcher, query.as_ref()))
            .transpose()?;
        let hits = top
            .into_iter()
            .map(|(score, sort, address)| {
                let doc: TantivyDocument = s.searcher.doc(address)?;
                let snippets = snippets.as_ref().map(|g| g.for_doc(&doc));
                Ok(HitJson { score, sort, doc: doc.to_named_doc(&schema), snippets })
            })
            .collect::<FfiResult<Vec<_>>>()?;

        let json = serde_json::to_string(&SearchResultJson { count, hits, facets, aggregations })
            .map_err(|e| FfiError::new(ffi::status::ERROR, format!("result serialization: {e}")))?;
        write_out(out, TdnString::from_string(json), "result")
    })
}

#[no_mangle]
pub unsafe extern "C" fn tdn_searcher_free(searcher: *mut TdnSearcher) {
    ffi::free_box(searcher);
}
