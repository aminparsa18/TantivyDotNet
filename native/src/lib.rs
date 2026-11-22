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

mod ffi;
mod schema_spec;

use std::sync::Mutex;

use serde::Serialize;
use tantivy::collector::{Count, TopDocs};
use tantivy::query::QueryParser;
use tantivy::schema::{Field, FieldType, NamedFieldDocument, Schema};
use tantivy::{
    Document, Index, IndexReader, IndexWriter, ReloadPolicy, Searcher, TantivyDocument, Term,
};

use ffi::{guard, handle, str_arg, write_out, FfiError, FfiResult};

/// Bumped whenever the exported C ABI changes; the managed side checks it on load.
pub const ABI_VERSION: u32 = 2;

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
}

#[derive(Serialize)]
struct HitJson {
    score: f32,
    doc: NamedFieldDocument,
}

/// Parses `query` with Tantivy's query language and returns
/// `{"count": <total matches>, "hits": [{"score": f, "doc": {"field": [values...]}}]}`.
///
/// `default_fields_json` is a JSON array of field names used for terms without a `field:` prefix;
/// empty means every indexed text field. `limit == 0` only counts.
#[no_mangle]
pub unsafe extern "C" fn tdn_searcher_search(
    searcher: *const TdnSearcher,
    query_ptr: *const u8,
    query_len: usize,
    default_fields_ptr: *const u8,
    default_fields_len: usize,
    limit: usize,
    offset: usize,
    out: *mut TdnString,
    err: *mut TdnString,
) -> i32 {
    guard(err, || {
        let s = handle(searcher, "searcher")?;
        let query_text = str_arg(query_ptr, query_len, "query")?;
        let default_fields_json = str_arg(default_fields_ptr, default_fields_len, "default fields")?;
        let schema = s.index.schema();

        let default_fields = if default_fields_json.is_empty() {
            all_indexed_text_fields(&schema)
        } else {
            let names: Vec<String> = serde_json::from_str(default_fields_json)
                .map_err(|e| FfiError::invalid(format!("invalid default fields JSON: {e}")))?;
            names.iter().map(|n| schema.get_field(n)).collect::<Result<Vec<_>, _>>()?
        };

        let query = QueryParser::for_index(&s.index, default_fields)
            .parse_query(query_text)
            .map_err(|e| FfiError::new(ffi::status::QUERY_PARSE, e.to_string()))?;

        let (top, count) = if limit == 0 {
            (Vec::new(), s.searcher.search(&query, &Count)?)
        } else {
            let top_docs = TopDocs::with_limit(limit).and_offset(offset).order_by_score();
            s.searcher.search(&query, &(top_docs, Count))?
        };

        let hits = top
            .into_iter()
            .map(|(score, address)| {
                let doc: TantivyDocument = s.searcher.doc(address)?;
                Ok(HitJson { score, doc: doc.to_named_doc(&schema) })
            })
            .collect::<FfiResult<Vec<_>>>()?;

        let json = serde_json::to_string(&SearchResultJson { count, hits })
            .map_err(|e| FfiError::new(ffi::status::ERROR, format!("result serialization: {e}")))?;
        write_out(out, TdnString::from_string(json), "result")
    })
}

fn all_indexed_text_fields(schema: &Schema) -> Vec<Field> {
    schema
        .fields()
        .filter(|(_, e)| matches!(e.field_type(), FieldType::Str(_)) && e.is_indexed())
        .map(|(f, _)| f)
        .collect()
}

#[no_mangle]
pub unsafe extern "C" fn tdn_searcher_free(searcher: *mut TdnSearcher) {
    ffi::free_box(searcher);
}
