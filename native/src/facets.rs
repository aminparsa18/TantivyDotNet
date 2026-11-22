//! Facet counts for a search.
//!
//! Request part: `"facets": [{"field": "genre", "paths": ["/", "/fiction"], "top": 10?}]`.
//! Result part: `"facets": {"genre": [{"path": "/fiction", "count": 3}, ...]}`: for each requested
//! path, its direct children with the number of matching documents under each. Without `top`,
//! children come in path order; with it, the `top` largest come first, highest count first (ties
//! in path order).
//!
//! Counted by our own collector rather than Tantivy's `FacetCollector`, which (0.27) gets the root
//! path wrong: it groups the root's children by their name minus its first byte, so one-letter
//! top-level facets (`/a/x`, `/a/y`) are counted per leaf and overwrite each other, and names that
//! differ only in their first byte (`/bat`, `/cat`) are merged. It also panics when one collector
//! is given a path and one of its ancestors, and reserves `top` entries up front.

use std::collections::{BTreeMap, HashSet};

use serde::{Deserialize, Serialize};
use tantivy::collector::{Collector, FruitHandle, MultiCollector, MultiFruit, SegmentCollector};
use tantivy::schema::{Facet, FieldType, Schema};
use tantivy::{DocId, Score, SegmentOrdinal, SegmentReader};

use crate::ffi::{FfiError, FfiResult};

#[derive(Deserialize)]
pub struct FacetRequest {
    field: String,
    paths: Vec<String>,
    #[serde(default)]
    top: Option<usize>,
}

#[derive(Serialize)]
pub struct FacetCountJson {
    path: String,
    count: u64,
}

pub type FacetResultJson = BTreeMap<String, Vec<FacetCountJson>>;

pub struct FacetPlan {
    field: String,
    top: Option<usize>,
    handle: FruitHandle<ChildCounts>,
}

/// Validates `requests` and adds one collector per requested path to `multi`.
pub fn add(requests: &[FacetRequest], schema: &Schema, multi: &mut MultiCollector) -> FfiResult<Vec<FacetPlan>> {
    let mut seen = HashSet::new();
    let mut plans = Vec::new();
    for r in requests {
        if !seen.insert(r.field.as_str()) {
            return Err(FfiError::invalid(format!(
                "facet field '{}' was requested twice; pass several paths in one request instead",
                r.field
            )));
        }
        let field = schema.get_field(&r.field)?;
        if !matches!(schema.get_field_entry(field).field_type(), FieldType::Facet(_)) {
            return Err(FfiError::invalid(format!("field '{}' is not a facet field", r.field)));
        }
        if r.paths.is_empty() {
            return Err(FfiError::invalid(format!("facet request for '{}' has no paths", r.field)));
        }
        if r.top == Some(0) {
            return Err(FfiError::invalid("facet top must be positive"));
        }
        for p in &r.paths {
            let collector = ChildCounter { field: r.field.clone(), path: parse_facet(p)? };
            let handle = multi.add_collector(collector);
            plans.push(FacetPlan { field: r.field.clone(), top: r.top, handle });
        }
    }
    Ok(plans)
}

pub fn collect(plans: Vec<FacetPlan>, fruits: &mut MultiFruit) -> FacetResultJson {
    let mut result = FacetResultJson::new();
    for plan in plans {
        let counts = plan.handle.extract(fruits);
        let mut children: Vec<(Vec<u8>, u64)> = counts.into_iter().collect(); // path order
        if let Some(k) = plan.top {
            // Stable: equal counts stay in path order.
            children.sort_by_key(|&(_, n)| std::cmp::Reverse(n));
            children.truncate(k);
        }
        let entries = result.entry(plan.field).or_default();
        for (encoded, count) in children {
            let path = Facet::from_encoded(encoded).map(|f| f.to_string()).unwrap_or_default();
            entries.push(FacetCountJson { path, count });
        }
    }
    result
}

pub fn parse_facet(path: &str) -> FfiResult<Facet> {
    Facet::from_text(path).map_err(|_| {
        FfiError::invalid(format!("invalid facet path '{path}': paths start with '/', like \"/genre/fiction\""))
    })
}

/// Separates the components of an encoded facet (Tantivy's encoding: components joined by 0).
const SEP: u8 = 0;

/// Matching documents per direct child of a path, keyed by the child's encoded facet (so the map
/// is in path order).
pub type ChildCounts = BTreeMap<Vec<u8>, u64>;

/// Counts matching documents under each direct child of `path` (each document once per child).
struct ChildCounter {
    field: String,
    path: Facet,
}

impl Collector for ChildCounter {
    type Fruit = ChildCounts;
    type Child = ChildCounterSegment;

    fn for_segment(&self, _: SegmentOrdinal, reader: &SegmentReader) -> tantivy::Result<ChildCounterSegment> {
        let facets = reader.facet_reader(&self.field)?;
        let dict = facets.facet_dict();
        let prefix = self.path.encoded_str().as_bytes();
        // Descendants of the path sort right after it (the root's are every term), so one pass
        // over that range maps each term to its child. `child_of` is indexed by term ordinal.
        let mut child_of = vec![NO_CHILD; dict.num_terms()];
        let mut children: Vec<Vec<u8>> = Vec::new();
        let mut terms = dict.range().ge(prefix).into_stream()?;
        while terms.advance() {
            let key = terms.key();
            let rest = if prefix.is_empty() {
                key
            } else if key == prefix {
                continue; // the path itself, not a child
            } else if key.len() > prefix.len() && key.starts_with(prefix) && key[prefix.len()] == SEP {
                &key[prefix.len() + 1..]
            } else {
                break; // past the descendants
            };
            let name_len = rest.iter().position(|&b| b == SEP).unwrap_or(rest.len());
            let child_len = key.len() - rest.len() + name_len;
            if children.last().is_none_or(|last| last[..] != key[..child_len]) {
                children.push(key[..child_len].to_vec());
            }
            child_of[terms.term_ord() as usize] = (children.len() - 1) as u32;
        }
        Ok(ChildCounterSegment {
            counts: vec![0; children.len()],
            last_doc: vec![DocId::MAX; children.len()],
            children,
            child_of,
            reader: facets,
        })
    }

    fn requires_scoring(&self) -> bool {
        false
    }

    fn merge_fruits(&self, segments: Vec<ChildCounts>) -> tantivy::Result<ChildCounts> {
        let mut merged = ChildCounts::new();
        for counts in segments {
            for (child, n) in counts {
                *merged.entry(child).or_default() += n;
            }
        }
        Ok(merged)
    }
}

const NO_CHILD: u32 = u32::MAX;

struct ChildCounterSegment {
    reader: tantivy::fastfield::FacetReader,
    /// Child index per term ordinal, or `NO_CHILD`.
    child_of: Vec<u32>,
    /// Encoded facet of each child.
    children: Vec<Vec<u8>>,
    counts: Vec<u64>,
    /// The last document counted for each child, so a document with several values under one
    /// child counts once.
    last_doc: Vec<DocId>,
}

impl SegmentCollector for ChildCounterSegment {
    type Fruit = ChildCounts;

    fn collect(&mut self, doc: DocId, _: Score) {
        for ord in self.reader.facet_ords(doc) {
            let child = self.child_of[ord as usize];
            if child != NO_CHILD && self.last_doc[child as usize] != doc {
                self.last_doc[child as usize] = doc;
                self.counts[child as usize] += 1;
            }
        }
    }

    fn harvest(self) -> ChildCounts {
        self.children.into_iter().zip(self.counts).filter(|&(_, n)| n > 0).collect()
    }
}

#[cfg(test)]
mod tests {
    use serde_json::{json, Value};

    use crate::testing::Fixture;

    const SCHEMA: &str = r#"{"fields":[{"name":"c","type":"facet"},{"name":"t","type":"text"}]}"#;

    fn facets(f: &Fixture, paths: &str, extra: &str) -> Value {
        let request =
            format!(r#"{{"query":{{"type":"all"}},"limit":0,"facets":[{{"field":"c","paths":{paths}{extra}}}]}}"#);
        f.search(&request).unwrap()["facets"]["c"].clone()
    }

    #[test]
    fn root_children_with_short_or_similar_names() {
        // Tantivy's FacetCollector miscounts both of these at the root.
        let f = Fixture::new(
            SCHEMA,
            &[
                &[r#"{"c":"/a/x"}"#, r#"{"c":"/a/y"}"#, r#"{"c":"/bat/1"}"#],
                &[r#"{"c":"/a/y"}"#, r#"{"c":"/b"}"#, r#"{"c":"/cat/2"}"#, r#"{"c":"/cat"}"#],
            ],
        );
        assert_eq!(
            facets(&f, r#"["/"]"#, ""),
            json!([
                {"path": "/a", "count": 3}, {"path": "/b", "count": 1},
                {"path": "/bat", "count": 1}, {"path": "/cat", "count": 2},
            ])
        );
        assert_eq!(
            facets(&f, r#"["/a", "/cat", "/b", "/nope"]"#, ""),
            json!([{"path": "/a/x", "count": 1}, {"path": "/a/y", "count": 2}, {"path": "/cat/2", "count": 1}])
        );
    }

    #[test]
    fn a_document_counts_once_per_child() {
        let f = Fixture::new(SCHEMA, &[&[r#"{"c":["/g/x","/g/y","/h"]}"#, r#"{"c":["/g/x/deep","/g/x"]}"#]]);
        assert_eq!(facets(&f, r#"["/"]"#, ""), json!([{"path": "/g", "count": 2}, {"path": "/h", "count": 1}]));
        assert_eq!(facets(&f, r#"["/g"]"#, ""), json!([{"path": "/g/x", "count": 2}, {"path": "/g/y", "count": 1}]));
    }

    #[test]
    fn children_of_a_path_exclude_siblings_sharing_its_prefix() {
        let f =
            Fixture::new(SCHEMA, &[&[r#"{"c":"/fiction/novel"}"#, r#"{"c":"/fictional/x"}"#, r#"{"c":"/fiction"}"#]]);
        assert_eq!(facets(&f, r#"["/fiction"]"#, ""), json!([{"path": "/fiction/novel", "count": 1}]));
    }

    #[test]
    fn only_matching_documents_are_counted() {
        let f = Fixture::new(
            SCHEMA,
            &[&[r#"{"c":"/a/x","t":"yes"}"#, r#"{"c":"/a/y","t":"no"}"#], &[r#"{"c":"/a/y","t":"yes"}"#]],
        );
        let r = f
            .search(r#"{"query":{"type":"parse","text":"yes"},"limit":0,"facets":[{"field":"c","paths":["/a"]}]}"#)
            .unwrap();
        assert_eq!(r["facets"]["c"], json!([{"path": "/a/x", "count": 1}, {"path": "/a/y", "count": 1}]));
    }

    #[test]
    fn top_orders_by_count_then_path() {
        let docs = [r#"{"c":"/p/b"}"#, r#"{"c":"/p/c"}"#, r#"{"c":"/p/c"}"#, r#"{"c":"/p/a"}"#, r#"{"c":"/p/d"}"#];
        let f = Fixture::new(SCHEMA, &[&docs]);
        assert_eq!(
            facets(&f, r#"["/p"]"#, r#","top":2"#),
            json!([{"path": "/p/c", "count": 2}, {"path": "/p/a", "count": 1}])
        );
        // A huge top neither reserves memory nor drops anything.
        let all = facets(&f, r#"["/p"]"#, r#","top":18446744073709551615"#);
        assert_eq!(all.as_array().unwrap().len(), 4);
    }

    #[test]
    fn escaped_slashes_and_unicode() {
        let f = Fixture::new(SCHEMA, &[&[r#"{"c":"/size/1\\/2 inch"}"#, r#"{"c":"/größe/ä"}"#]]);
        assert_eq!(
            facets(&f, r#"["/", "/size"]"#, ""),
            json!([{"path": "/größe", "count": 1}, {"path": "/size", "count": 1}, {"path": "/size/1\\/2 inch", "count": 1}])
        );
    }

    #[test]
    fn empty_index_and_missing_segments() {
        let f = Fixture::new(SCHEMA, &[&[r#"{"t":"no facet here"}"#]]);
        assert_eq!(facets(&f, r#"["/"]"#, ""), json!([]));
        let empty = Fixture::new(SCHEMA, &[]);
        assert_eq!(facets(&empty, r#"["/", "/a"]"#, ""), json!([]));
    }
}
