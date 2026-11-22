//! Times indexing the benchmark corpus (benchmarks/TantivyDotNet.Benchmarks/IndexingBenchmarks.cs)
//! inside Rust, to see how much of a TantivyDotNet indexing run is .NET + FFI:
//!
//! - `parse only`: our document parsing (`documents::parse`) of every line, nothing indexed
//! - `index`:      parse + `add_document` per line, commit, wait for merges (what .NET does per call,
//!   minus serializing the article to JSON and the FFI call)
//!
//! ```sh
//! cargo run --release --example index_timing -- ../artifacts/corpus/wiki-abstracts.jsonl 1 4
//! ```

use std::time::Instant;

use tantivy::Index;
use tantivy_dotnet::timing;

/// The benchmark schema (Engines.TantivySchema), as the managed SchemaBuilder writes it.
const SCHEMA: &str = r#"{"fields":[
  {"name":"id","type":"text","stored":true,"indexed":true,"fast":false,"tokenizer":"raw"},
  {"name":"title","type":"text","stored":true,"indexed":true,"fast":false,"tokenizer":"default"},
  {"name":"body","type":"text","stored":true,"indexed":true,"fast":false,"tokenizer":"default"},
  {"name":"links","type":"u64","stored":false,"indexed":false,"fast":true}]}"#;

fn main() {
    let mut args = std::env::args().skip(1);
    let corpus = args.next().unwrap_or_else(|| "../artifacts/corpus/wiki-abstracts.jsonl".into());
    let threads: Vec<usize> = args.map(|a| a.parse().unwrap()).collect();
    let threads = if threads.is_empty() { vec![1, 4] } else { threads };

    let text = std::fs::read_to_string(&corpus).expect("corpus (run the benchmarks' `prepare` first)");
    let lines: Vec<&str> = text.lines().collect();
    let schema = timing::schema(SCHEMA).unwrap();
    println!("{} documents, {:.0} MiB of JSON", lines.len(), text.len() as f64 / 1048576.0);

    let t = Instant::now();
    for line in &lines {
        std::hint::black_box(timing::parse_document(&schema, line).unwrap());
    }
    println!("parse only           {:>6.2} s", t.elapsed().as_secs_f64());

    for &n in &threads {
        let mut runs = Vec::new();
        for _ in 0..3 {
            let dir = std::env::temp_dir().join(format!("tdn-index-timing-{}", std::process::id()));
            let _ = std::fs::remove_dir_all(&dir);
            std::fs::create_dir_all(&dir).unwrap();
            let t = Instant::now();
            {
                let index = Index::create_in_dir(&dir, schema.clone()).unwrap();
                let mut writer = index.writer_with_num_threads(n, 256 * 1024 * 1024).unwrap();
                for line in &lines {
                    writer.add_document(timing::parse_document(&schema, line).unwrap()).unwrap();
                }
                writer.commit().unwrap();
                writer.wait_merging_threads().unwrap();
            }
            runs.push(t.elapsed().as_secs_f64());
            std::fs::remove_dir_all(&dir).unwrap();
        }
        runs.sort_by(f64::total_cmp);
        println!("index, {n} thread(s)   {:>6.2} s  (median of 3: {:.2?})", runs[1], runs);
    }
}
