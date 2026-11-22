//! Entry points for the fuzz targets in `native/fuzz` (cargo-fuzz), built only with the `fuzzing`
//! feature. Each runs one of the JSON inputs the exports take, but without `guard`, so a panic
//! reaches the fuzzer as a crash. Errors are expected: only panics, aborts (stack overflow, failed
//! allocations), hangs and broken invariants are findings.

use std::sync::OnceLock;

use tantivy::collector::Count;
use tantivy::{DocAddress, Index};

use crate::testing::Fixture;
use crate::{analyzers, documents, merging, query_spec, schema_spec, search, settings};

/// Every field type, custom analyzers, fast JSON, missing and multi-valued fields, three segments.
pub const SCHEMA: &str = r#"{"fields":[
    {"name":"title","type":"text","stored":true},
    {"name":"body","type":"text","stored":true,"tokenizer":"en_stem"},
    {"name":"folded","type":"text","stored":true,"tokenizer":"folded"},
    {"name":"grams","type":"text","tokenizer":"grams"},
    {"name":"id","type":"text","tokenizer":"raw","fast":true,"stored":true},
    {"name":"year","type":"u64","fast":true,"stored":true},
    {"name":"delta","type":"i64","fast":true,"stored":true},
    {"name":"price","type":"f64","fast":true,"stored":true},
    {"name":"flag","type":"bool","fast":true,"stored":true},
    {"name":"when","type":"date","fast":true,"stored":true},
    {"name":"cat","type":"facet","stored":true},
    {"name":"attrs","type":"json","stored":true},
    {"name":"jf","type":"json","fast":true},
    {"name":"ip","type":"ip","fast":true,"stored":true},
    {"name":"blob","type":"bytes","fast":true,"stored":true},
    {"name":"note","type":"text","stored":true,"indexed":false}],
  "analyzers":{
    "folded":{"tokenizer":{"type":"simple"},"filters":[{"type":"lowercase"},{"type":"ascii_folding"},
              {"type":"stop_words","language":"english"},{"type":"remove_long","limit":20}]},
    "grams":{"tokenizer":{"type":"ngram","min_gram":2,"max_gram":3}}}}"#;

/// Field names of [`SCHEMA`], for generators.
pub const FIELDS: [&str; 16] = [
    "title", "body", "folded", "grams", "id", "year", "delta", "price", "flag", "when", "cat", "attrs", "jf", "ip",
    "blob", "note",
];

const DOCS: [&[&str]; 3] = [
    &[
        r#"{"title":"Of Mice and Men","body":"Two drifters in California","folded":"Crème brûlée","grams":"mice",
            "id":"a","year":1937,"delta":-5,"price":9.5,"flag":true,"when":"1937-02-06T00:00:00Z",
            "cat":["/fiction/novella","/award/x"],"attrs":{"color":"red","n":5,"tags":["a","b"]},"jf":{"n":5,"s":"x"},
            "ip":"10.0.0.1","blob":"AQI=","note":"n"}"#,
        r#"{"title":["The Grapes of Wrath","Grapes"],"body":"A family in California during the Dust Bowl","id":"b",
            "year":1939,"delta":3,"price":-12.25,"flag":false,"cat":"/fiction/novel","attrs":{"n":5.5,"nested":{"k":true}},
            "jf":{"n":-1},"ip":"::1"}"#,
    ],
    &[
        r#"{"title":"East of Eden","body":"Two families in the Salinas Valley","id":"c","year":1952,"cat":"/a",
            "attrs":{"n":7},"jf":{"s":"y"},"when":"2001-09-09T01:46:40Z","blob":""}"#,
        r#"{"title":"","id":"d"}"#,
    ],
    &[r#"{"title":"日本語のタイトル 🎉","body":"ünïcödé text","id":"e","year":0,"delta":-9223372036854775808,
          "price":1e300,"cat":"/b/c/d","ip":"2001:db8::1","blob":"/w=="}"#],
];

fn corpus() -> &'static Fixture {
    static CORPUS: OnceLock<Fixture> = OnceLock::new();
    CORPUS.get_or_init(|| Fixture::new(SCHEMA, &DOCS))
}

/// Schema JSON (also tried as index settings and merge policy JSON). A schema that parses must
/// round-trip: its output is valid input and stable.
pub fn schema(json: &str) {
    if let Ok((schema, custom)) = schema_spec::schema_from_json(json) {
        let index = Index::create_in_ram(schema.clone());
        analyzers::register(index.tokenizers(), &custom).expect("validated analyzers register");
        let out = schema_spec::schema_to_json(&schema, &custom);
        let (again, again_custom) = schema_spec::schema_from_json(&out).expect("schema output is valid input");
        assert_eq!(schema_spec::schema_to_json(&again, &again_custom), out, "schema round trip is stable");
        for name in custom.keys() {
            let _ = analyzers::analyze(&index, name, "Hello Wörld, the QUICK brown foxes jumped 123 times — ¿qué?");
        }
    }
    if let Ok(s) = settings::from_json(json) {
        // Index sorting is checked by Tantivy against the schema when the index is created.
        let _ = Index::builder().schema(corpus().index.schema()).settings(s.clone()).create_in_ram();
        let _ = settings::to_json(&s);
    }
    if let Ok(policy) = merging::MergePolicySpec::from_json(json) {
        let _ = policy.build();
    }
}

/// Query JSON, built against the corpus and counted, explained and used as a writer term.
pub fn query(json: &str) {
    let c = corpus();
    let Ok(spec) = query_spec::parse_spec(json) else { return };
    let _ = query_spec::term_of(&spec, &c.index.schema());
    let Ok(q) = query_spec::build(&spec, &c.index) else { return };
    let _ = c.searcher.search(&q, &Count);
    // Tantivy's `explain` seeks an `EmptyScorer` against a `debug_assert!` (harmless in release
    // builds, which the fuzzers use), so it's skipped where debug assertions are on.
    if !cfg!(debug_assertions) {
        let _ = search::explain(&c.searcher, &c.index, json, DocAddress::new(0, 1));
    }
}

/// Search request JSON against the corpus. A result must be valid JSON.
pub fn search(json: &str) {
    let c = corpus();
    if let Ok(result) = search::run(&c.searcher, &c.index, json) {
        serde_json::from_str::<serde_json::Value>(&result).expect("search result is JSON");
    }
}

/// Document JSON for the corpus schema, parsed both ways; a valid document is indexed in a fresh
/// index, committed, and searched for with snippets, so its stored form is read back too.
pub fn document(json: &str) {
    let schema = corpus().index.schema();
    let _ = documents::parse(&schema, json, true);
    let Ok(doc) = documents::parse(&schema, json, false) else { return };
    let (spec_schema, custom) = schema_spec::schema_from_json(SCHEMA).unwrap();
    let index = Index::create_in_ram(spec_schema);
    analyzers::register(index.tokenizers(), &custom).unwrap();
    let mut writer: tantivy::IndexWriter = index.writer_with_num_threads(1, 15_000_000).unwrap();
    writer.add_document(doc).expect("a parsed document can be added");
    writer.commit().expect("commit");
    let searcher = index.reader().unwrap().searcher();
    let request = r#"{"query":{"type":"parse","text":"the a 1 x","lenient":true},"limit":5,
                      "snippets":{"fields":["title","body","folded"],"max_length":20}}"#;
    let all = r#"{"query":{"type":"all"},"limit":5,"sort":[{"type":"field","field":"id"}]}"#;
    for r in [request, all] {
        let result = search::run(&searcher, &index, r).unwrap_or_else(|e| panic!("search failed: {}", e.message));
        serde_json::from_str::<serde_json::Value>(&result).expect("search result is JSON");
    }
}

#[cfg(test)]
mod tests {
    /// Every seed input runs through its target's entry point (catches a stale harness or seed).
    #[test]
    fn seeds_run_cleanly() {
        let seeds = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("fuzz/seeds");
        type Target = (&'static str, fn(&str));
        let targets: [Target; 4] = [
            ("schema", super::schema),
            ("query", super::query),
            ("search", super::search),
            ("document", super::document),
        ];
        for (name, run) in targets {
            let mut n = 0;
            for entry in std::fs::read_dir(seeds.join(name)).unwrap() {
                let path = entry.unwrap().path();
                run(&std::fs::read_to_string(&path).unwrap());
                n += 1;
            }
            assert!(n > 0, "no seeds for {name}");
        }
    }
}
