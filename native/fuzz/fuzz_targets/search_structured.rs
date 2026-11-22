//! Builds search requests from the fuzzer's bytes with `arbitrary`, so it reaches deep query trees,
//! sorts with cursors, snippets, facets and aggregations far more often than mutated raw JSON does.
//! Field names come from the corpus schema (plus the odd unknown one); values are mostly of a
//! plausible kind, sometimes not, with edge values (0, MAX, NaN-ish, empty, huge) favored.
#![no_main]

use arbitrary::{Result, Unstructured};
use libfuzzer_sys::fuzz_target;
use serde_json::{json, Map, Value};
use tantivy_dotnet::fuzzing::{self, FIELDS};

fuzz_target!(|data: &[u8]| {
    let mut u = Unstructured::new(data);
    if let Ok(request) = request(&mut u) {
        // TDN_FUZZ_PRINT=1 shows the request, to turn a crash artifact into a test.
        if std::env::var_os("TDN_FUZZ_PRINT").is_some() {
            eprintln!("request: {request}");
        }
        fuzzing::search(&request.to_string());
    }
});

fn field(u: &mut Unstructured) -> Result<String> {
    Ok(match u.int_in_range(0..=19)? {
        0 => String::arbitrary_take(u)?,
        1 => format!("attrs.{}", *u.choose(&["color", "n", "tags", "nested.k", "missing"])?),
        2 => format!("jf.{}", *u.choose(&["n", "s", "x"])?),
        _ => u.choose(&FIELDS)?.to_string(),
    })
}

trait TakeString {
    fn arbitrary_take(u: &mut Unstructured) -> Result<String>;
}

impl TakeString for String {
    /// A short string, favoring words that occur in the corpus.
    fn arbitrary_take(u: &mut Unstructured) -> Result<String> {
        const WORDS: [&str; 14] =
            ["mice", "two", "california", "grapes", "a", "the", "", "crème", "日本語", "/fiction", "/", "x", "Eden", "*"];
        if u.ratio(3, 4)? {
            Ok(u.choose(&WORDS)?.to_string())
        } else {
            let s: &str = u.arbitrary()?;
            Ok(s.chars().take(40).collect())
        }
    }
}

fn number(u: &mut Unstructured) -> Result<Value> {
    Ok(match u.int_in_range(0..=9)? {
        0 => json!(u64::MAX),
        1 => json!(i64::MIN),
        2 => json!(0),
        3 => json!(1e300),
        4 => json!(-0.5),
        5 => json!(u.arbitrary::<f64>()?.clamp(-1e308, 1e308)),
        6 => json!(u.arbitrary::<i64>()?),
        _ => json!(u.int_in_range(0..=2000u64)?),
    })
}

fn tagged_value(u: &mut Unstructured) -> Result<Value> {
    Ok(match u.int_in_range(0..=9)? {
        0 => json!({ "u64": u.arbitrary::<u64>()? }),
        1 => json!({ "i64": u.arbitrary::<i64>()? }),
        2 => json!({ "f64": u.arbitrary::<f64>()?.clamp(-1e308, 1e308) }),
        3 => json!({ "bool": u.arbitrary::<bool>()? }),
        4 => json!({ "date": u.arbitrary::<i64>()? }),
        5 => json!({ "bytes": u.arbitrary::<Vec<u8>>()?.into_iter().take(16).collect::<Vec<_>>() }),
        6 => json!({ "facet": String::arbitrary_take(u)? }),
        7 => json!({ "ip": *u.choose(&["10.0.0.1", "::1", "nope", "", "2001:db8::1"])? }),
        8 => json!({ "u64": u.int_in_range(1930..=1960u64)? }),
        _ => json!({ "str": String::arbitrary_take(u)? }),
    })
}

fn bound(u: &mut Unstructured) -> Result<Value> {
    Ok(if u.ratio(1, 4)? { Value::Null } else { json!({"value": tagged_value(u)?, "inclusive": u.arbitrary::<bool>()?}) })
}

fn queries(u: &mut Unstructured, depth: u32) -> Result<Vec<Value>> {
    let n = u.int_in_range(0..=3)?;
    (0..n).map(|_| query(u, depth + 1)).collect()
}

fn query(u: &mut Unstructured, depth: u32) -> Result<Value> {
    let leaf_only = depth >= 6;
    let kind = if leaf_only { u.int_in_range(0..=9)? } else { u.int_in_range(0..=15)? };
    Ok(match kind {
        0 => json!({"type": "all"}),
        1 | 2 => json!({"type": "term", "field": field(u)?, "value": tagged_value(u)?}),
        3 => json!({"type": "fuzzy", "field": field(u)?, "value": String::arbitrary_take(u)?,
                    "distance": u.int_in_range(0..=3u8)?, "transpositions": u.arbitrary::<bool>()?, "prefix": u.arbitrary::<bool>()?}),
        4 => json!({"type": "phrase", "field": field(u)?, "text": String::arbitrary_take(u)? + " " + &String::arbitrary_take(u)?,
                    "slop": u.int_in_range(0..=u32::MAX)? % 5}),
        5 => json!({"type": "range", "field": field(u)?, "lower": bound(u)?, "upper": bound(u)?}),
        6 => json!({"type": "regex", "field": field(u)?, "pattern": *u.choose(&[".*", "gr.*", "(", "[a-z]{1,3}", "a{1000}", ""])?}),
        7 => json!({"type": "exists", "field": field(u)?}),
        8 => json!({"type": "term_set", "field": field(u)?, "values": (0..u.int_in_range(0..=4)?).map(|_| tagged_value(u)).collect::<Result<Vec<_>>>()?}),
        9 => {
            let text = ["mice", "title:(", "year:[1930 TO 1940]", "two AND -eden", "\"of mice\"~2", "attrs.color:red",
                        "cat:/fiction", "*", "", "title:mice^2 OR body:two", "price:>5", "id:IN [a b]"][u.int_in_range(0..=11)?];
            let mut q = json!({"type": "parse", "text": text, "lenient": u.arbitrary::<bool>()?, "conjunction": u.arbitrary::<bool>()?});
            if u.arbitrary()? {
                q["default_fields"] = json!([field(u)?]);
            }
            if u.arbitrary()? {
                q["fuzzy"] = json!({ field(u)?: {"distance": u.int_in_range(0..=3u8)?, "prefix": u.arbitrary::<bool>()?} });
            }
            if u.arbitrary()? {
                q["boosts"] = json!({ field(u)?: number(u)? });
            }
            q
        }
        10 | 11 => json!({"type": "boolean", "must": queries(u, depth)?, "should": queries(u, depth)?, "must_not": queries(u, depth)?}),
        12 => json!({"type": "boost", "query": query(u, depth + 1)?, "factor": number(u)?}),
        13 => json!({"type": "const_score", "query": query(u, depth + 1)?, "score": number(u)?}),
        14 => json!({"type": "disjunction_max", "queries": queries(u, depth)?, "tie_breaker": u.int_in_range(0..=12u8)? as f64 / 10.0}),
        _ => {
            let mut doc = Map::new();
            for _ in 0..u.int_in_range(0..=2)? {
                doc.insert(field(u)?, if u.arbitrary()? { json!([String::arbitrary_take(u)?]) } else { number(u)? });
            }
            json!({"type": "more_like_this", "document": doc, "options": {
                "min_doc_frequency": u.int_in_range(0..=2u64)?, "min_term_frequency": u.int_in_range(0..=2u64)?,
                "max_query_terms": u.int_in_range(0..=30u64)?, "min_word_length": u.int_in_range(0..=3u64)?,
                "max_word_length": u.int_in_range(0..=50u64)?}})
        }
    })
}

fn window(u: &mut Unstructured) -> Result<u64> {
    Ok(match u.int_in_range(0..=7)? {
        0 => 0,
        1 => u64::MAX,
        2 => i32::MAX as u64,
        3 => u32::MAX as u64,
        _ => u.int_in_range(0..=12)?,
    })
}

fn request(u: &mut Unstructured) -> Result<Value> {
    let mut r = Map::new();
    r.insert("query".into(), query(u, 0)?);
    r.insert("limit".into(), json!(window(u)?));
    r.insert("offset".into(), json!(window(u)?));
    if u.ratio(1, 2)? {
        let keys: Vec<Value> = (0..u.int_in_range(1..=3)?)
            .map(|_| -> Result<Value> {
                Ok(if u.ratio(1, 4)? {
                    json!({"type": "score"})
                } else {
                    json!({"type": "field", "field": field(u)?, "order": if u.arbitrary()? { "desc" } else { "asc" }})
                })
            })
            .collect::<Result<_>>()?;
        if u.ratio(1, 3)? {
            let after: Vec<Value> = keys
                .iter()
                .map(|_| -> Result<Value> {
                    Ok(match u.int_in_range(0..=3)? {
                        0 => Value::Null,
                        1 => json!({ "score": number(u)? }),
                        _ => tagged_value(u)?,
                    })
                })
                .collect::<Result<_>>()?;
            let addr = [u.int_in_range(0..=4u32)?, *u.choose(&[0u32, 1, 2, u32::MAX])?];
            r.insert("search_after".into(), json!({"keys": after, "addr": addr}));
        }
        r.insert("sort".into(), Value::Array(keys));
    }
    if u.ratio(1, 3)? {
        r.insert("fields".into(), json!((0..u.int_in_range(0..=3)?).map(|_| field(u)).collect::<Result<Vec<_>>>()?));
    }
    if u.ratio(1, 3)? {
        let fields = (0..u.int_in_range(0..=3)?).map(|_| field(u)).collect::<Result<Vec<_>>>()?;
        r.insert("snippets".into(), json!({"fields": fields, "max_length": *u.choose(&[0u64, 1, 5, 150, u64::MAX])?}));
    }
    if u.ratio(1, 4)? {
        let paths: Vec<&str> = (0..u.int_in_range(0..=3)?)
            .map(|_| u.choose(&["/", "/fiction", "/a", "/b/c", "fiction", "/award/x", ""]).copied())
            .collect::<Result<_>>()?;
        let mut facet = json!({"field": if u.ratio(4, 5)? { "cat".to_owned() } else { field(u)? }, "paths": paths});
        if u.arbitrary()? {
            facet["top"] = json!(*u.choose(&[0u64, 1, 3, u64::MAX])?);
        }
        r.insert("facets".into(), json!([facet]));
    }
    if u.ratio(1, 4)? {
        r.insert("aggregations".into(), json!({ "agg": aggregation(u, 0)? }));
    }
    Ok(Value::Object(r))
}

fn aggregation(u: &mut Unstructured, depth: u32) -> Result<Value> {
    let f = field(u)?;
    let mut agg = match u.int_in_range(0..=9)? {
        0 => json!({"terms": {"field": f, "size": window(u)?, "min_doc_count": u.int_in_range(0..=2u64)?}}),
        1 => json!({"histogram": {"field": f, "interval": number(u)?, "min_doc_count": u.int_in_range(0..=1u64)?}}),
        2 => json!({"date_histogram": {"field": f, "fixed_interval": *u.choose(&["1d", "30d", "1ms", "0d", "x"])?}}),
        3 => json!({"range": {"field": f, "ranges": [{"to": number(u)?}, {"from": number(u)?, "to": number(u)?}, {"from": number(u)?}]}}),
        4 => json!({"avg": {"field": f}}),
        5 => json!({"stats": {"field": f, "missing": number(u)?}}),
        6 => json!({"percentiles": {"field": f, "percents": [number(u)?, 50.0]}}),
        7 => json!({"cardinality": {"field": f}}),
        8 => json!({"top_hits": {"size": window(u)?, "from": window(u)?, "sort": [{ f.clone(): "desc" }], "docvalue_fields": [f]}}),
        _ => json!({"extended_stats": {"field": f, "sigma": number(u)?}}),
    };
    if depth < 2 && u.ratio(1, 3)? {
        agg["aggs"] = json!({ "sub": aggregation(u, depth + 1)? });
    }
    Ok(agg)
}
