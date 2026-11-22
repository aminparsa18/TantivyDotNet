//! Times the benchmark queries (benchmarks/TantivyDotNet.Benchmarks/QueryBenchmarks.cs) inside Rust,
//! to split a TantivyDotNet search into its parts:
//!
//! - `tantivy`:      Count + top 10 with Tantivy's own collectors (no stored fields)
//! - `tantivy+docs`: the same, plus loading each hit's stored document
//! - `2-pass`:       Count and top 10 as two separate searches (each takes its own fast path:
//!                   `Weight::count`, and Block-WAND for the top hits)
//! - `multi`:        Count + top 10 in a `MultiCollector` (one pass, dynamic dispatch)
//! - `docs->json`:   loading the 10 documents and writing them as named-field JSON (alone)
//! - `ours`:         our whole native search (`search::run`): request JSON in, result JSON out
//!
//! The difference between `ours` and the .NET benchmark is the FFI call plus the managed side.
//!
//! ```sh
//! cargo run --release --example query_timing -- ../artifacts/bench/shared/tantivy
//! ```

use std::hint::black_box;
use std::time::{Duration, Instant};

use tantivy::collector::{Count, MultiCollector, TopDocs};
use tantivy::schema::Value;
use tantivy::{Document, Index, Order, TantivyDocument};
use tantivy_dotnet::timing;

const SORT: &str = r#"[{"type":"field","field":"links","order":"desc"}]"#;

fn main() {
    let dir = std::env::args().nth(1).unwrap_or_else(|| "../artifacts/bench/shared/tantivy".into());
    let index = Index::open_in_dir(&dir).expect("open the shared benchmark index (run the .NET benchmarks once)");
    let reader = index.reader().unwrap();
    let searcher = reader.searcher();
    let title = index.schema().get_field("title").unwrap();

    let term = |t: &str| format!(r#"{{"type":"term","field":"body","value":{{"str":"{t}"}}}}"#);
    let queries: Vec<(&str, String, Option<&str>)> = vec![
        ("Term", term("science"), None),
        ("And", format!(r#"{{"type":"boolean","must":[{},{}]}}"#, term("american"), term("football")), None),
        ("Or", format!(r#"{{"type":"boolean","should":[{},{}]}}"#, term("american"), term("football")), None),
        ("Phrase", r#"{"type":"phrase","field":"body","text":"united states"}"#.into(), None),
        ("SortedByField", term("science"), Some(SORT)),
    ];

    println!(
        "{:<14} {:>9} {:>13} {:>9} {:>9} {:>11} {:>9} {:>12}",
        "query", "tantivy", "tantivy+docs", "2-pass", "multi", "docs->json", "ours", "result JSON"
    );
    for (name, query_json, sort) in &queries {
        let query = timing::build_query(query_json, &index).unwrap();
        let top = |with_docs: bool| {
            let hits: Vec<tantivy::DocAddress> = match sort {
                None => {
                    let (n, top) = searcher.search(&query, &(Count, TopDocs::with_limit(10).order_by_score())).unwrap();
                    black_box(n);
                    top.into_iter().map(|(_, a)| a).collect()
                }
                Some(_) => {
                    let c = TopDocs::with_limit(10).order_by_fast_field::<u64>("links", Order::Desc);
                    let (n, top) = searcher.search(&query, &(Count, c)).unwrap();
                    black_box(n);
                    top.into_iter().map(|(_, a)| a).collect()
                }
            };
            if with_docs {
                for a in hits {
                    let doc: TantivyDocument = searcher.doc(a).unwrap();
                    black_box(doc.get_first(title).and_then(|v| v.as_str().map(str::len)));
                }
            }
        };
        let request = match sort {
            None => format!(r#"{{"query":{query_json},"limit":10}}"#),
            Some(s) => format!(r#"{{"query":{query_json},"limit":10,"sort":{s}}}"#),
        };
        let ours = || timing::search(&searcher, &index, &request).unwrap();

        let addrs: Vec<tantivy::DocAddress> = searcher
            .search(&query, &TopDocs::with_limit(10).order_by_score())
            .unwrap()
            .into_iter()
            .map(|(_, a)| a)
            .collect();
        let schema = index.schema();
        let t_two = time(|| {
            black_box(searcher.search(&query, &Count).unwrap());
            match sort {
                None => drop(black_box(searcher.search(&query, &TopDocs::with_limit(10).order_by_score()).unwrap())),
                Some(_) => drop(black_box(
                    searcher
                        .search(&query, &TopDocs::with_limit(10).order_by_fast_field::<u64>("links", Order::Desc))
                        .unwrap(),
                )),
            }
        });
        let t_multi = time(|| {
            let mut multi = MultiCollector::new();
            let count = multi.add_collector(Count);
            let top = multi.add_collector(TopDocs::with_limit(10).order_by_score());
            let mut fruits = searcher.search(&query, &multi).unwrap();
            black_box((count.extract(&mut fruits), top.extract(&mut fruits)));
        });
        let t_json = time(|| {
            let docs: Vec<_> = addrs
                .iter()
                .map(|&a| searcher.doc::<TantivyDocument>(a).unwrap().to_named_doc(&schema))
                .collect();
            black_box(serde_json::to_string(&docs).unwrap());
        });
        // Our documents must be exactly Tantivy's NamedFieldDocument JSON.
        let result: serde_json::Value = serde_json::from_str(&ours()).unwrap();
        for hit in result["hits"].as_array().unwrap() {
            let addr = tantivy::DocAddress::new(hit["addr"][0].as_u64().unwrap() as u32, hit["addr"][1].as_u64().unwrap() as u32);
            let named = searcher.doc::<TantivyDocument>(addr).unwrap().to_named_doc(&schema);
            assert_eq!(serde_json::to_string(&hit["doc"]).unwrap(), serde_json::to_string(&serde_json::to_value(named).unwrap()).unwrap());
        }
        let t_core = time(|| top(false));
        let t_docs = time(|| top(true));
        let t_ours = time(|| {
            black_box(ours());
        });
        println!(
            "{:<14} {:>7.0}µs {:>11.0}µs {:>7.0}µs {:>7.0}µs {:>9.0}µs {:>7.0}µs {:>10} B",
            name,
            us(t_core),
            us(t_docs),
            us(t_two),
            us(t_multi),
            us(t_json),
            us(t_ours),
            ours().len()
        );
    }
}

/// Median of 15 rounds, each averaging as many calls as fit in ~100 ms (after a warm-up).
fn time(mut f: impl FnMut()) -> Duration {
    let warm = Instant::now();
    let mut n = 0u32;
    while warm.elapsed() < Duration::from_millis(300) {
        f();
        n += 1;
    }
    let per_round = (n / 3).max(1);
    let mut rounds: Vec<Duration> = (0..15)
        .map(|_| {
            let t = Instant::now();
            for _ in 0..per_round {
                f();
            }
            t.elapsed() / per_round
        })
        .collect();
    rounds.sort();
    rounds[rounds.len() / 2]
}

fn us(d: Duration) -> f64 {
    d.as_secs_f64() * 1e6
}
