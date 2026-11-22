//! Facet counts for a search.
//!
//! Request part: `"facets": [{"field": "genre", "paths": ["/", "/fiction"], "top": 10?}]`.
//! Result part: `"facets": {"genre": [{"path": "/fiction", "count": 3}, ...]}`: for each requested
//! path, its direct children with the number of matching documents under each. Without `top`,
//! children come in path order; with it, the `top` largest come first, highest count first.

use std::collections::{BTreeMap, HashSet};

use serde::{Deserialize, Serialize};
use tantivy::collector::{FacetCollector, FacetCounts, FruitHandle, MultiCollector, MultiFruit};
use tantivy::schema::{Facet, FieldType, Schema};

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
    path: Facet,
    top: Option<usize>,
    handle: FruitHandle<FacetCounts>,
}

/// Validates `requests` and adds one collector per requested path to `multi`. (One per path,
/// because a single `FacetCollector` panics when given a path and one of its ancestors.)
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
            let path = parse_facet(p)?;
            let mut collector = FacetCollector::for_field(&r.field);
            collector.add_facet(path.clone());
            let handle = multi.add_collector(collector);
            plans.push(FacetPlan { field: r.field.clone(), path, top: r.top, handle });
        }
    }
    Ok(plans)
}

pub fn collect(plans: Vec<FacetPlan>, fruits: &mut MultiFruit) -> FacetResultJson {
    let mut result = FacetResultJson::new();
    for plan in plans {
        let counts = plan.handle.extract(fruits);
        let children: Vec<(&Facet, u64)> = match plan.top {
            Some(k) => counts.top_k(plan.path.clone(), k),
            None => counts.get(plan.path.clone()).collect(),
        };
        result
            .entry(plan.field)
            .or_default()
            .extend(children.into_iter().map(|(f, count)| FacetCountJson { path: f.to_string(), count }));
    }
    result
}

pub fn parse_facet(path: &str) -> FfiResult<Facet> {
    Facet::from_text(path).map_err(|_| {
        FfiError::invalid(format!("invalid facet path '{path}': paths start with '/', like \"/genre/fiction\""))
    })
}
