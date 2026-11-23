//! Fixtures shared by the unit tests: a RAM index built from our schema JSON, documents committed
//! in batches (one segment per batch, merges off), and helpers that run requests through the same
//! code as the exports. Also the corpus of the fuzz targets (`fuzzing`).
#![cfg_attr(not(test), allow(dead_code))]

use serde_json::Value;
use tantivy::indexer::NoMergePolicy;
use tantivy::{Index, IndexWriter, Searcher};

use crate::ffi::{FfiError, FfiResult};
use crate::{analyzers, documents, query_spec, schema_spec, search};

pub struct Fixture {
    pub index: Index,
    pub searcher: Searcher,
}

impl Fixture {
    /// An index with `schema_json`, holding one segment per non-empty batch of document JSON.
    pub fn new(schema_json: &str, batches: &[&[&str]]) -> Self {
        let (schema, custom) = schema_spec::schema_from_json(schema_json).unwrap();
        let index = Index::create_in_ram(schema.clone());
        analyzers::register(index.tokenizers(), &custom).unwrap();
        let mut writer: IndexWriter = index.writer_with_num_threads(1, 15_000_000).unwrap();
        writer.set_merge_policy(Box::new(NoMergePolicy));
        for batch in batches {
            for doc in *batch {
                writer.add_document(documents::parse(&schema, doc, false).unwrap()).unwrap();
            }
            writer.commit().unwrap();
        }
        writer.wait_merging_threads().unwrap();
        let searcher = index.reader().unwrap().searcher();
        Self { index, searcher }
    }

    /// Like [`Fixture::new`] with owned documents.
    pub fn from_owned(schema_json: &str, batches: &[Vec<String>]) -> Self {
        let borrowed: Vec<Vec<&str>> = batches.iter().map(|b| b.iter().map(String::as_str).collect()).collect();
        let slices: Vec<&[&str]> = borrowed.iter().map(Vec::as_slice).collect();
        Self::new(schema_json, &slices)
    }

    /// Runs a search request JSON, as `tdn_searcher_search` does.
    pub fn search(&self, request: &str) -> FfiResult<Value> {
        let json = search::run(&self.searcher, &self.index, request)?;
        Ok(serde_json::from_str(&json).expect("search result is JSON"))
    }

    /// The number of documents matching a query JSON.
    pub fn count(&self, query: &str) -> usize {
        match self.try_count(query) {
            Ok(n) => n,
            Err(e) => panic!("query {query} failed: {}", e.message),
        }
    }

    pub fn try_count(&self, query: &str) -> FfiResult<usize> {
        let spec = query_spec::parse_spec(query)?;
        let q = query_spec::build(&spec, &self.index)?;
        Ok(self.searcher.search(&q, &tantivy::collector::Count)?)
    }

    /// The error a query JSON fails with (panics if it succeeds).
    pub fn query_error(&self, query: &str) -> FfiError {
        match self.try_count(query) {
            Ok(n) => panic!("query {query} unexpectedly matched {n} documents"),
            Err(e) => e,
        }
    }
}

/// A RAM directory whose `meta.json` writes fail while `fail_meta` is set, like a commit hitting
/// an IO error (on Windows: "Access is denied" replacing a file another handle has open).
#[derive(Clone, Debug, Default)]
pub struct FailingDirectory {
    inner: tantivy::directory::RamDirectory,
    pub fail_meta: std::sync::Arc<std::sync::atomic::AtomicBool>,
}

impl tantivy::Directory for FailingDirectory {
    fn get_file_handle(
        &self,
        path: &std::path::Path,
    ) -> Result<std::sync::Arc<dyn tantivy::directory::FileHandle>, tantivy::directory::error::OpenReadError> {
        self.inner.get_file_handle(path)
    }
    fn delete(&self, path: &std::path::Path) -> Result<(), tantivy::directory::error::DeleteError> {
        self.inner.delete(path)
    }
    fn exists(&self, path: &std::path::Path) -> Result<bool, tantivy::directory::error::OpenReadError> {
        self.inner.exists(path)
    }
    fn open_write(
        &self,
        path: &std::path::Path,
    ) -> Result<tantivy::directory::WritePtr, tantivy::directory::error::OpenWriteError> {
        self.inner.open_write(path)
    }
    fn atomic_read(&self, path: &std::path::Path) -> Result<Vec<u8>, tantivy::directory::error::OpenReadError> {
        self.inner.atomic_read(path)
    }
    fn atomic_write(&self, path: &std::path::Path, data: &[u8]) -> std::io::Result<()> {
        if path == std::path::Path::new("meta.json") && self.fail_meta.load(std::sync::atomic::Ordering::SeqCst) {
            return Err(std::io::Error::new(std::io::ErrorKind::PermissionDenied, "Access is denied (test)"));
        }
        self.inner.atomic_write(path, data)
    }
    fn sync_directory(&self) -> std::io::Result<()> {
        self.inner.sync_directory()
    }
    fn watch(&self, callback: tantivy::directory::WatchCallback) -> tantivy::Result<tantivy::directory::WatchHandle> {
        self.inner.watch(callback)
    }
}

/// A term query JSON with a tagged value, e.g. `term("year", r#"{"u64":1937}"#)`.
pub fn term(field: &str, tagged_value: &str) -> String {
    format!(r#"{{"type":"term","field":"{field}","value":{tagged_value}}}"#)
}

/// A tiny deterministic PRNG (xorshift64*), so the tests need no extra crates and are repeatable.
pub struct Rng(u64);

impl Rng {
    pub fn new(seed: u64) -> Self {
        Self(seed.max(1))
    }

    pub fn next(&mut self) -> u64 {
        self.0 ^= self.0 >> 12;
        self.0 ^= self.0 << 25;
        self.0 ^= self.0 >> 27;
        self.0.wrapping_mul(0x2545_f491_4f6c_dd1d)
    }

    /// Uniform in `0..n`.
    pub fn below(&mut self, n: u64) -> u64 {
        self.next() % n
    }

    /// True with probability `percent` / 100.
    pub fn chance(&mut self, percent: u64) -> bool {
        self.below(100) < percent
    }
}
