//! Running a search request: one `MultiCollector` pass for the count, the page of hits, facets
//! and aggregations, then loading the hits' stored fields and snippets.
//!
//! Request: `{"query": {...}, "limit": n, "offset": n, "sort": [...]?, "search_after": {...}?, "fields": [...]?,
//! "snippets": {...}?, "facets": [...]?, "aggregations": {...}?}`.
//! Result: `{"count": <total matches>, "hits": [{"score": f|null, "sort": [...], "addr": [segment, doc],
//! "doc": {"field": [values...]}, "snippets": {...}?}], "facets": {...}?, "aggregations": {...}?}`
//! (see the `sorting`, `snippets`, `facets` and `aggregations` modules). `limit == 0` only counts.

use std::collections::{BTreeMap, HashSet};

use serde::{Deserialize, Serialize};
use tantivy::aggregation::agg_result::AggregationResults;
use tantivy::collector::{Count, MultiCollector};
use tantivy::query::Explanation;
use tantivy::schema::{Field, NamedFieldDocument, OwnedValue, Schema, Value};
use tantivy::{DocAddress, Document, Index, Searcher, TantivyDocument};

use crate::facets::{self, FacetRequest, FacetResultJson};
use crate::ffi::{status, FfiError, FfiResult};
use crate::query_spec::{self, QuerySpec};
use crate::snippets::{SnippetJson, SnippetRequest, Snippets};
use crate::sorting::{self, SearchAfter, SortKeyRequest};
use crate::aggregations;

#[derive(Deserialize)]
struct SearchRequest {
    query: QuerySpec,
    #[serde(default = "default_limit")]
    limit: usize,
    #[serde(default)]
    offset: usize,
    #[serde(default)]
    fields: Option<Vec<String>>,
    #[serde(default)]
    snippets: Option<SnippetRequest>,
    #[serde(default)]
    facets: Option<Vec<FacetRequest>>,
    #[serde(default)]
    sort: Vec<SortKeyRequest>,
    #[serde(default)]
    search_after: Option<SearchAfter>,
    #[serde(default)]
    aggregations: Option<serde_json::Value>,
}

fn default_limit() -> usize {
    10
}

#[derive(Serialize)]
struct SearchResultJson {
    count: usize,
    hits: Vec<HitJson>,
    #[serde(skip_serializing_if = "Option::is_none")]
    facets: Option<FacetResultJson>,
    #[serde(skip_serializing_if = "Option::is_none")]
    aggregations: Option<AggregationResults>,
}

#[derive(Serialize)]
struct HitJson {
    /// NaN when sorted by a field; serde_json writes non-finite floats as `null`.
    score: f32,
    /// One tagged value per sort key (`[{"score": f}]` when sorted by relevance); see `sorting`.
    sort: serde_json::Value,
    /// The hit's `DocAddress` as `[segment_ord, doc_id]`, for `explain`.
    addr: [u32; 2],
    doc: NamedFieldDocument,
    #[serde(skip_serializing_if = "Option::is_none")]
    snippets: Option<BTreeMap<String, SnippetJson>>,
}

pub fn run(searcher: &Searcher, index: &Index, request_json: &str) -> FfiResult<String> {
    let request: SearchRequest = serde_json::from_str(request_json)
        .map_err(|e| FfiError::invalid(format!("invalid search request JSON: {e}")))?;
    let schema = index.schema();
    let query = query_spec::build(&request.query, index)?;
    let only_fields = request.fields.as_deref().map(|names| stored_fields(&schema, names)).transpose()?;

    let mut collectors = MultiCollector::new();
    let count_handle = collectors.add_collector(Count);
    let top_handle = match request.limit {
        0 => None,
        limit => Some(sorting::add(
            &request.sort,
            request.search_after.as_ref(),
            limit,
            request.offset,
            &schema,
            &mut collectors,
        )?),
    };
    let facet_plans = match &request.facets {
        Some(f) => facets::add(f, &schema, &mut collectors)?,
        None => Vec::new(),
    };
    let agg_handle = match &request.aggregations {
        Some(a) => Some(aggregations::add(a, index, &mut collectors)?),
        None => None,
    };

    let mut fruits = searcher.search(&query, &collectors)?;
    let count = count_handle.extract(&mut fruits);
    let top = top_handle.map(|h| sorting::extract(h, &mut fruits)).transpose()?.unwrap_or_default();
    let facets = request.facets.is_some().then(|| facets::collect(facet_plans, &mut fruits));
    let aggregations = agg_handle.map(|h| h.extract(&mut fruits));

    // Built even with no hits, so an invalid snippet field fails consistently.
    let snippets = request.snippets.as_ref().map(|r| Snippets::new(r, searcher, query.as_ref())).transpose()?;
    // With no fields and no snippets requested, the document store isn't read at all.
    let skip_docs = snippets.is_none() && only_fields.as_ref().is_some_and(|f| f.is_empty());
    let hits = top
        .into_iter()
        .map(|(score, sort, address)| {
            let addr = [address.segment_ord, address.doc_id];
            if skip_docs {
                return Ok(HitJson { score, sort, addr, doc: NamedFieldDocument(BTreeMap::new()), snippets: None });
            }
            let doc: TantivyDocument = searcher.doc(address)?;
            let snippets = snippets.as_ref().map(|g| g.for_doc(&doc));
            Ok(HitJson { score, sort, addr, doc: named_doc(&doc, &schema, only_fields.as_ref()), snippets })
        })
        .collect::<FfiResult<Vec<_>>>()?;

    serde_json::to_string(&SearchResultJson { count, hits, facets, aggregations })
        .map_err(|e| FfiError::new(status::ERROR, format!("result serialization: {e}")))
}

/// Explains how `query_json` scores the document at `address`, as Tantivy's explanation JSON:
/// `{"value": f, "description": "...", "details": [...]?, "context": [...]?}`.
pub fn explain(searcher: &Searcher, index: &Index, query_json: &str, address: DocAddress) -> FfiResult<String> {
    let spec = query_spec::parse_spec(query_json)?;
    let query = query_spec::build(&spec, index)?;
    if address.segment_ord as usize >= searcher.segment_readers().len()
        || address.doc_id >= searcher.segment_reader(address.segment_ord).max_doc()
    {
        return Err(FfiError::invalid("the hit's document isn't in this searcher's snapshot"));
    }
    let explanation: Explanation = query.explain(searcher, address)?;
    serde_json::to_string(&explanation).map_err(|e| FfiError::new(status::ERROR, format!("explanation serialization: {e}")))
}

/// Resolves the requested field names; each must be a stored field.
fn stored_fields(schema: &Schema, names: &[String]) -> FfiResult<HashSet<Field>> {
    names
        .iter()
        .map(|name| {
            let field = schema.get_field(name)?;
            if !schema.get_field_entry(field).is_stored() {
                return Err(FfiError::invalid(format!(
                    "field '{name}' isn't stored, so it can't be returned (create it with stored: true)"
                )));
            }
            Ok(field)
        })
        .collect()
}

fn named_doc(doc: &TantivyDocument, schema: &Schema, only: Option<&HashSet<Field>>) -> NamedFieldDocument {
    let Some(only) = only else {
        return doc.to_named_doc(schema);
    };
    let mut map: BTreeMap<String, Vec<OwnedValue>> = BTreeMap::new();
    for (field, value) in doc.field_values() {
        if only.contains(&field) {
            map.entry(schema.get_field_name(field).to_owned())
                .or_default()
                .push(OwnedValue::from(value.as_value()));
        }
    }
    NamedFieldDocument(map)
}
