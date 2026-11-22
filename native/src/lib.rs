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

mod facets;
mod ffi;
mod query_spec;
mod schema_spec;
mod snippets;
mod sorting;

use std::collections::BTreeMap;
use std::sync::Mutex;

use serde::{Deserialize, Serialize};
use tantivy::collector::{Count, MultiCollector};
use tantivy::schema::{Field, FieldType, NamedFieldDocument, Schema};
use tantivy::{
    Document, Index, IndexReader, IndexWriter, ReloadPolicy, Searcher, TantivyDocument, Term,
};

use facets::{FacetRequest, FacetResultJson};
use query_spec::QuerySpec;
use snippets::{SnippetJson, SnippetRequest, Snippets};
use sorting::SortRequest;
use ffi::{guard, handle, str_arg, write_out, FfiError, FfiResult};

/// Bumped whenever the exported C ABI changes; the managed side checks it on load.
pub const ABI_VERSION: u32 = 3;

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
}

/// `IndexWriter::commit`/`rollback` take `&mut self`. The mutex makes concurrent calls from .NET
/// serialize instead of being undefined behaviour; the public API still documents the writer as
/// not thread-safe.
pub struct TdnWriter {
    writer: Mutex<IndexWriter>,
    schema: Schema,
}

pub struct TdnSearcher {
    index: Index,
    searcher: Searcher,
}

impl TdnIndex {
    fn new(index: Index) -> FfiResult<Self> {
        // Manual reload: tdn_index_searcher reloads explicitly, so every new searcher sees the
        // latest commit, and no file-watcher thread is started.
        let reader = index.reader_builder().reload_policy(ReloadPolicy::Manual).try_into()?;
        Ok(Self { index, reader })
    }
}

impl TdnWriter {
    fn lock(&self) -> FfiResult<std::sync::MutexGuard<'_, IndexWriter>> {
        self.writer
            .lock()
            .map_err(|_| FfiError::new(ffi::status::ERROR, "writer is unusable after an earlier panic"))
    }

    fn field(&self, name: &str) -> FfiResult<(Field, &FieldType)> {
        let field = self.schema.get_field(name)?;
        Ok((field, self.schema.get_field_entry(field).field_type()))
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
        let schema = schema_spec::schema_from_json(str_arg(schema_ptr, schema_len, "schema")?)?;
        let index = TdnIndex::new(Index::create_in_ram(schema))?;
        write_out(out, Box::into_raw(Box::new(index)), "index")
    })
}

/// Creates a new index in an existing directory. Fails if the directory already holds an index.
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
        let schema = schema_spec::schema_from_json(str_arg(schema_ptr, schema_len, "schema")?)?;
        let index = TdnIndex::new(Index::create_in_dir(path, schema)?)?;
        write_out(out, Box::into_raw(Box::new(index)), "index")
    })
}

/// Opens an existing index directory.
#[no_mangle]
pub unsafe extern "C" fn tdn_index_open_in_dir(
    path_ptr: *const u8,
    path_len: usize,
    out: *mut *mut TdnIndex,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let path = str_arg(path_ptr, path_len, "path")?;
        let index = TdnIndex::new(Index::open_in_dir(path)?)?;
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
        let json = schema_spec::schema_to_json(&index.index.schema());
        write_out(out, TdnString::from_string(json), "schema")
    })
}

#[no_mangle]
pub unsafe extern "C" fn tdn_index_free(index: *mut TdnIndex) {
    ffi::free_box(index);
}

// ---------------------------------------------------------------------------------------------
// Writer
// ---------------------------------------------------------------------------------------------

/// Creates the index's writer. `num_threads == 0` lets Tantivy choose (up to 8, at least 15 MB
/// of heap per thread). Only one writer may exist per index at a time.
#[no_mangle]
pub unsafe extern "C" fn tdn_index_writer(
    index: *const TdnIndex,
    heap_bytes: usize,
    num_threads: usize,
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
        let writer = TdnWriter { writer: Mutex::new(writer), schema: index.schema() };
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
        let json = str_arg(json_ptr, json_len, "document")?;
        let doc = TantivyDocument::parse_json(&writer.schema, json)
            .map_err(|e| FfiError::invalid(format!("invalid document: {e}")))?;
        writer.lock()?.add_document(doc)?;
        Ok(())
    })
}

/// Deletes every document whose text field contains exactly this term. The value is not run
/// through the field's tokenizer.
#[no_mangle]
pub unsafe extern "C" fn tdn_writer_delete_text(
    writer: *const TdnWriter,
    field_ptr: *const u8,
    field_len: usize,
    value_ptr: *const u8,
    value_len: usize,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let writer = handle(writer, "writer")?;
        let name = str_arg(field_ptr, field_len, "field")?;
        let value = str_arg(value_ptr, value_len, "value")?;
        let (field, ty) = writer.field(name)?;
        expect_type(name, matches!(ty, FieldType::Str(_)), "text")?;
        writer.lock()?.delete_term(Term::from_field_text(field, value));
        Ok(())
    })
}

#[no_mangle]
pub unsafe extern "C" fn tdn_writer_delete_u64(
    writer: *const TdnWriter,
    field_ptr: *const u8,
    field_len: usize,
    value: u64,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let writer = handle(writer, "writer")?;
        let name = str_arg(field_ptr, field_len, "field")?;
        let (field, ty) = writer.field(name)?;
        expect_type(name, matches!(ty, FieldType::U64(_)), "u64")?;
        writer.lock()?.delete_term(Term::from_field_u64(field, value));
        Ok(())
    })
}

#[no_mangle]
pub unsafe extern "C" fn tdn_writer_delete_i64(
    writer: *const TdnWriter,
    field_ptr: *const u8,
    field_len: usize,
    value: i64,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let writer = handle(writer, "writer")?;
        let name = str_arg(field_ptr, field_len, "field")?;
        let (field, ty) = writer.field(name)?;
        expect_type(name, matches!(ty, FieldType::I64(_)), "i64")?;
        writer.lock()?.delete_term(Term::from_field_i64(field, value));
        Ok(())
    })
}

fn expect_type(name: &str, ok: bool, expected: &str) -> FfiResult<()> {
    if ok {
        Ok(())
    } else {
        Err(FfiError::invalid(format!("field '{name}' is not a {expected} field")))
    }
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

/// Drops the writer without committing; uncommitted operations are discarded.
#[no_mangle]
pub unsafe extern "C" fn tdn_writer_free(writer: *mut TdnWriter) {
    ffi::free_box(writer);
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
}

fn default_limit() -> usize {
    10
}

/// Runs a search described by request JSON (see `query_spec` for the query part):
/// `{"query": {...}, "limit": n, "offset": n, "sort": {...}?, "snippets": {...}?, "facets": [...]?}`,
/// and returns `{"count": <total matches>, "hits": [{"score": f|null, "sort": {...}?, "doc": {"field": [values...]},
/// "snippets": {...}?}], "facets": {...}?}` (see the `sorting`, `snippets` and `facets` modules).
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

        let mut fruits = s.searcher.search(&query, &collectors)?;
        let count = count_handle.extract(&mut fruits);
        let top = top_handle.map(|h| sorting::extract(h, &mut fruits)).unwrap_or_default();
        let facets = request.facets.is_some().then(|| facets::collect(facet_plans, &mut fruits));

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

        let json = serde_json::to_string(&SearchResultJson { count, hits, facets })
            .map_err(|e| FfiError::new(ffi::status::ERROR, format!("result serialization: {e}")))?;
        write_out(out, TdnString::from_string(json), "result")
    })
}

#[no_mangle]
pub unsafe extern "C" fn tdn_searcher_free(searcher: *mut TdnSearcher) {
    ffi::free_box(searcher);
}
