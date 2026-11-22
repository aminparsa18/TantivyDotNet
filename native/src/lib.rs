use std::panic::catch_unwind;

use tantivy::collector::Count;
use tantivy::query::QueryParser;
use tantivy::schema::{Schema, STORED, TEXT};
use tantivy::{doc, Index, IndexWriter};

/// Bumped whenever the exported C ABI changes; the managed side checks it on load.
#[no_mangle]
pub extern "C" fn tdn_abi_version() -> u32 {
    1
}

/// End-to-end smoke test proving Tantivy links and runs: builds an in-RAM index with one
/// document, searches for "rust", and returns the hit count (1), or -1 on error/panic.
/// Replace with the real handle-based API in step 2.
#[no_mangle]
pub extern "C" fn tdn_smoke_test() -> i32 {
    catch_unwind(|| smoke().map(|n| n as i32).unwrap_or(-1)).unwrap_or(-1)
}

fn smoke() -> Result<usize, Box<dyn std::error::Error>> {
    let mut builder = Schema::builder();
    let body = builder.add_text_field("body", TEXT | STORED);
    let index = Index::create_in_ram(builder.build());

    let mut writer: IndexWriter = index.writer_with_num_threads(1, 15_000_000)?;
    writer.add_document(doc!(body => "hello from rust"))?;
    writer.commit()?;

    let searcher = index.reader()?.searcher();
    let query = QueryParser::for_index(&index, vec![body]).parse_query("rust")?;
    Ok(searcher.search(&query, &Count)?)
}
