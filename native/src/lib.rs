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
mod merging;
mod query_spec;
mod schema_spec;
mod search;
mod settings;
mod snippets;
mod sorting;
mod stats;
#[cfg(test)]
mod testing;

use std::sync::Mutex;

use tantivy::schema::Schema;
use tantivy::{DocAddress, Index, IndexReader, IndexWriter, ReloadPolicy, Searcher, TantivyDocument, Term};

use ffi::{guard, handle, str_arg, write_out, FfiError, FfiResult};

/// Bumped whenever the exported C ABI changes; the managed side checks it on load.
pub const ABI_VERSION: u32 = 5;

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
    /// Restored after `tdn_writer_force_merge`, which pauses background merges.
    merge_policy: merging::MergePolicySpec,
}

pub struct TdnSearcher {
    index: Index,
    searcher: Searcher,
}

/// A reader of its own, separate from `TdnIndex::reader`, backing a .NET `SearcherManager`.
pub struct TdnReader {
    index: Index,
    reader: IndexReader,
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
        self.writer.lock().map_err(|_| FfiError::new(ffi::status::ERROR, "writer is unusable after an earlier panic"))
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

/// Creates an index in anonymous memory from schema JSON (see `schema_spec`) and settings JSON
/// (see `settings`; empty = defaults).
#[no_mangle]
pub unsafe extern "C" fn tdn_index_create_in_ram(
    schema_ptr: *const u8,
    schema_len: usize,
    settings_ptr: *const u8,
    settings_len: usize,
    out: *mut *mut TdnIndex,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let (schema, analyzers) = schema_spec::schema_from_json(str_arg(schema_ptr, schema_len, "schema")?)?;
        let settings = settings::from_json(str_arg(settings_ptr, settings_len, "settings")?)?;
        let created = Index::builder().schema(schema).settings(settings).create_in_ram()?;
        let index = TdnIndex::new(created, analyzers)?;
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
    settings_ptr: *const u8,
    settings_len: usize,
    out: *mut *mut TdnIndex,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let path = str_arg(path_ptr, path_len, "path")?;
        let (schema, analyzers) = schema_spec::schema_from_json(str_arg(schema_ptr, schema_len, "schema")?)?;
        let settings = settings::from_json(str_arg(settings_ptr, settings_len, "settings")?)?;
        let created = Index::builder().schema(schema).settings(settings).create_in_dir(path)?;
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
pub unsafe extern "C" fn tdn_index_schema(index: *const TdnIndex, out: *mut TdnString, err: *mut TdnString) -> i32 {
    guard(err, || {
        let index = handle(index, "index")?;
        let json = schema_spec::schema_to_json(&index.index.schema(), &index.analyzers);
        write_out(out, TdnString::from_string(json), "schema")
    })
}

/// Returns the settings the index was created with, as settings JSON (see `settings`).
#[no_mangle]
pub unsafe extern "C" fn tdn_index_settings(index: *const TdnIndex, out: *mut TdnString, err: *mut TdnString) -> i32 {
    guard(err, || {
        let index = handle(index, "index")?;
        let json = settings::to_json(index.index.settings());
        write_out(out, TdnString::from_string(json), "settings")
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
/// of heap per thread). `flags` is a combination of `WRITER_*` bits; the merge policy is merge
/// policy JSON (see `merging`; empty = Tantivy's default). Only one writer may exist per index at
/// a time.
#[no_mangle]
pub unsafe extern "C" fn tdn_index_writer(
    index: *const TdnIndex,
    heap_bytes: usize,
    num_threads: usize,
    flags: u32,
    merge_policy_ptr: *const u8,
    merge_policy_len: usize,
    out: *mut *mut TdnWriter,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let index = &handle(index, "index")?.index;
        let merge_policy =
            merging::MergePolicySpec::from_json(str_arg(merge_policy_ptr, merge_policy_len, "merge policy")?)?;
        let writer: IndexWriter = if num_threads == 0 {
            index.writer(heap_bytes)?
        } else {
            index.writer_with_num_threads(num_threads, heap_bytes)?
        };
        if !matches!(merge_policy, merging::MergePolicySpec::Default) {
            writer.set_merge_policy(merge_policy.build());
        }
        let writer = TdnWriter {
            writer: Mutex::new(writer),
            index: index.clone(),
            schema: index.schema(),
            ignore_unknown_fields: flags & WRITER_IGNORE_UNKNOWN_FIELDS != 0,
            merge_policy,
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

/// Merges the committed segments down to at most `max_segments`, also rewriting segments with
/// deleted documents (see `merging::force_merge`). Blocks until done.
#[no_mangle]
pub unsafe extern "C" fn tdn_writer_force_merge(
    writer: *const TdnWriter,
    max_segments: u32,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let w = handle(writer, "writer")?;
        merging::force_merge(&mut *w.lock()?, &w.index, &w.merge_policy, max_segments as usize)
    })
}

/// Deletes index files no longer used by the index or an open searcher; returns
/// `{"deleted": [names], "failed": [names]}`.
#[no_mangle]
pub unsafe extern "C" fn tdn_writer_garbage_collect(
    writer: *const TdnWriter,
    out: *mut TdnString,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let result = handle(writer, "writer")?.lock()?.garbage_collect_files().wait()?;
        write_out(out, TdnString::from_string(merging::gc_to_json(result)), "result")
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

/// Like `tdn_writer_free`, but doesn't wait for background merges: they're abandoned, and their
/// files are cleaned up by a later garbage collection. Used from the .NET finalizer, which must
/// not block.
#[no_mangle]
pub unsafe extern "C" fn tdn_writer_free_no_wait(writer: *mut TdnWriter) {
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

/// Runs a search described by request JSON and returns result JSON (see `search`).
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
        let request = str_arg(request_ptr, request_len, "search request")?;
        let json = search::run(&s.searcher, &s.index, request)?;
        write_out(out, TdnString::from_string(json), "result")
    })
}

/// Explains the score of the document at `[segment_ord, doc_id]` (a hit's `addr`) for a query
/// JSON; returns Tantivy's explanation JSON (see `search::explain`).
#[no_mangle]
pub unsafe extern "C" fn tdn_searcher_explain(
    searcher: *const TdnSearcher,
    query_ptr: *const u8,
    query_len: usize,
    segment_ord: u32,
    doc_id: u32,
    out: *mut TdnString,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let s = handle(searcher, "searcher")?;
        let query = str_arg(query_ptr, query_len, "query")?;
        let json = search::explain(&s.searcher, &s.index, query, DocAddress::new(segment_ord, doc_id))?;
        write_out(out, TdnString::from_string(json), "explanation")
    })
}

/// Returns statistics of the searcher's snapshot as stats JSON (see `stats`).
#[no_mangle]
pub unsafe extern "C" fn tdn_searcher_stats(
    searcher: *const TdnSearcher,
    out: *mut TdnString,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let s = handle(searcher, "searcher")?;
        write_out(out, TdnString::from_string(stats::to_json(&s.searcher)?), "stats")
    })
}

#[no_mangle]
pub unsafe extern "C" fn tdn_searcher_free(searcher: *mut TdnSearcher) {
    ffi::free_box(searcher);
}

// ---------------------------------------------------------------------------------------------
// Reader (for SearcherManager)
// ---------------------------------------------------------------------------------------------

/// Reader flag for `tdn_index_reader`: reload in the background shortly after each commit
/// (Tantivy's `OnCommitWithDelay`, which watches meta.json). Otherwise reloads are manual.
pub const READER_RELOAD_ON_COMMIT: u32 = 1;

/// Creates a reader over the latest commit.
#[no_mangle]
pub unsafe extern "C" fn tdn_index_reader(
    index: *const TdnIndex,
    flags: u32,
    out: *mut *mut TdnReader,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let index = &handle(index, "index")?.index;
        let policy =
            if flags & READER_RELOAD_ON_COMMIT != 0 { ReloadPolicy::OnCommitWithDelay } else { ReloadPolicy::Manual };
        let reader = index.reader_builder().reload_policy(policy).try_into()?;
        write_out(out, Box::into_raw(Box::new(TdnReader { index: index.clone(), reader })), "reader")
    })
}

/// Returns a searcher over the reader's current snapshot, after reloading it first if `reload`
/// is non-zero. If `current` (nullable) already covers the same segments and deletes, writes
/// null instead, so callers can keep (and keep warm) the searcher they have.
#[no_mangle]
pub unsafe extern "C" fn tdn_reader_searcher(
    reader: *const TdnReader,
    reload: u32,
    current: *const TdnSearcher,
    out: *mut *mut TdnSearcher,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let r = handle(reader, "reader")?;
        if reload != 0 {
            r.reader.reload()?;
        }
        let searcher = r.reader.searcher();
        if let Some(current) = current.as_ref() {
            let (a, b) = (current.searcher.generation(), searcher.generation());
            if a.generation_id() == b.generation_id() || a.segments() == b.segments() {
                return write_out(out, std::ptr::null_mut(), "searcher");
            }
        }
        let searcher = TdnSearcher { index: r.index.clone(), searcher };
        write_out(out, Box::into_raw(Box::new(searcher)), "searcher")
    })
}

#[no_mangle]
pub unsafe extern "C" fn tdn_reader_free(reader: *mut TdnReader) {
    ffi::free_box(reader);
}

/// Entry points for `examples/query_timing.rs`, which times the search path without .NET. Not part of
/// the C ABI (csbindgen only reads `extern "C"` functions).
#[doc(hidden)]
pub mod timing {
    use tantivy::query::Query;
    use tantivy::{Index, Searcher};

    /// Builds a query from our query JSON.
    pub fn build_query(query_json: &str, index: &Index) -> Result<Box<dyn Query>, String> {
        let spec = crate::query_spec::parse_spec(query_json).map_err(|e| e.message)?;
        crate::query_spec::build(&spec, index).map_err(|e| e.message)
    }

    /// Builds a schema from our schema JSON (custom analyzers aren't supported here).
    pub fn schema(schema_json: &str) -> Result<tantivy::schema::Schema, String> {
        crate::schema_spec::schema_from_json(schema_json).map(|(s, _)| s).map_err(|e| e.message)
    }

    /// Parses a document exactly as `tdn_writer_add_json` does (unknown fields rejected).
    pub fn parse_document(schema: &tantivy::schema::Schema, json: &str) -> Result<tantivy::TantivyDocument, String> {
        crate::documents::parse(schema, json, false).map_err(|e| e.message)
    }

    /// Runs a search request exactly as `tdn_searcher_search` does and returns the result JSON.
    pub fn search(searcher: &Searcher, index: &Index, request_json: &str) -> Result<String, String> {
        crate::search::run(searcher, index, request_json).map_err(|e| e.message)
    }
}

/// The C ABI itself: status codes, error strings and handles, called as .NET calls them.
#[cfg(test)]
mod abi_tests {
    use super::*;
    use crate::ffi::status;

    fn empty() -> TdnString {
        TdnString { ptr: std::ptr::null_mut(), len: 0 }
    }

    /// Takes (and frees) a returned string.
    unsafe fn take(s: TdnString) -> String {
        let text = String::from_utf8(std::slice::from_raw_parts(s.ptr, s.len).to_vec()).unwrap();
        tdn_string_free(s);
        text
    }

    const SCHEMA: &str = r#"{"fields":[{"name":"t","type":"text","stored":true}]}"#;

    unsafe fn ram_index() -> *mut TdnIndex {
        let (mut index, mut err) = (std::ptr::null_mut(), empty());
        let code = tdn_index_create_in_ram(SCHEMA.as_ptr(), SCHEMA.len(), std::ptr::null(), 0, &mut index, &mut err);
        assert_eq!(code, status::OK);
        index
    }

    #[test]
    fn round_trip_through_the_exports() {
        unsafe {
            let index = ram_index();
            let (mut writer, mut err) = (std::ptr::null_mut(), empty());
            assert_eq!(tdn_index_writer(index, 15_000_000, 1, 0, std::ptr::null(), 0, &mut writer, &mut err), 0);
            let doc = r#"{"t":"hello wörld"}"#;
            assert_eq!(tdn_writer_add_json(writer, doc.as_ptr(), doc.len(), &mut err), 0);
            assert_eq!(tdn_writer_commit(writer, &mut err), 0);
            let mut searcher = std::ptr::null_mut();
            assert_eq!(tdn_index_searcher(index, &mut searcher, &mut err), 0);
            assert_eq!(tdn_searcher_num_docs(searcher), 1);
            let request = r#"{"query":{"type":"parse","text":"wörld"}}"#;
            let mut out = empty();
            assert_eq!(tdn_searcher_search(searcher, request.as_ptr(), request.len(), &mut out, &mut err), 0);
            let result: serde_json::Value = serde_json::from_str(&take(out)).unwrap();
            assert_eq!(result["count"], 1);
            assert_eq!(result["hits"][0]["doc"]["t"][0], "hello wörld");
            tdn_searcher_free(searcher);
            tdn_writer_free(writer);
            tdn_index_free(index);
        }
    }

    #[test]
    fn errors_come_back_as_status_and_message() {
        unsafe {
            let mut err = empty();
            let mut index = std::ptr::null_mut();
            let bad = "{";
            assert_eq!(
                tdn_index_create_in_ram(bad.as_ptr(), 1, std::ptr::null(), 0, &mut index, &mut err),
                status::INVALID_ARGUMENT
            );
            assert!(take(err).contains("invalid schema JSON"));
            assert!(index.is_null(), "no handle on failure");

            // Invalid UTF-8, and a null pointer with a length.
            let mut err = empty();
            let bytes = [0xff, 0xfe];
            assert_eq!(
                tdn_index_create_in_ram(bytes.as_ptr(), 2, std::ptr::null(), 0, &mut index, &mut err),
                status::INVALID_ARGUMENT
            );
            assert!(take(err).contains("invalid UTF-8"));
            let mut err = empty();
            assert_eq!(
                tdn_index_create_in_ram(std::ptr::null(), 5, std::ptr::null(), 0, &mut index, &mut err),
                status::INVALID_ARGUMENT
            );
            assert!(take(err).contains("null pointer"));

            // Null handles and out-parameters.
            let mut err = empty();
            let mut out = empty();
            assert_eq!(
                tdn_searcher_search(std::ptr::null(), std::ptr::null(), 0, &mut out, &mut err),
                status::INVALID_ARGUMENT
            );
            assert!(take(err).contains("searcher handle is null"));
            let index = ram_index();
            let mut err = empty();
            assert_eq!(tdn_index_schema(index, std::ptr::null_mut(), &mut err), status::INVALID_ARGUMENT);
            assert!(take(err).contains("out-parameter is null"));
            // A null error out-parameter is allowed: the status still comes back.
            assert_eq!(tdn_index_schema(index, std::ptr::null_mut(), std::ptr::null_mut()), status::INVALID_ARGUMENT);
            assert_eq!(tdn_searcher_num_docs(std::ptr::null()), 0);
            tdn_index_free(index);

            // Freeing null is a no-op.
            tdn_index_free(std::ptr::null_mut());
            tdn_writer_free(std::ptr::null_mut());
            tdn_writer_free_no_wait(std::ptr::null_mut());
            tdn_searcher_free(std::ptr::null_mut());
            tdn_reader_free(std::ptr::null_mut());
            tdn_string_free(empty());
        }
    }

    #[test]
    fn panics_become_a_status() {
        let mut err = empty();
        let code = ffi::guard(&mut err, || panic!("boom {}", 42));
        assert_eq!(code, status::PANIC);
        assert_eq!(unsafe { take(err) }, "native panic: boom 42");
        let mut err = empty();
        assert_eq!(ffi::guard(&mut err, || std::panic::panic_any(7u8)), status::PANIC);
        assert_eq!(unsafe { take(err) }, "native panic: unknown panic payload");
    }

    #[test]
    fn only_one_writer_at_a_time() {
        unsafe {
            let index = ram_index();
            let (mut first, mut second, mut err) = (std::ptr::null_mut(), std::ptr::null_mut(), empty());
            assert_eq!(tdn_index_writer(index, 15_000_000, 1, 0, std::ptr::null(), 0, &mut first, &mut err), 0);
            assert_eq!(
                tdn_index_writer(index, 15_000_000, 1, 0, std::ptr::null(), 0, &mut second, &mut err),
                status::LOCK_BUSY
            );
            tdn_string_free(err);
            tdn_writer_free(first);
            let mut err = empty();
            assert_eq!(
                tdn_index_writer(index, 15_000_000, 1, 0, std::ptr::null(), 0, &mut second, &mut err),
                0,
                "lock released"
            );
            tdn_writer_free_no_wait(second);
            tdn_index_free(index);
        }
    }
}
