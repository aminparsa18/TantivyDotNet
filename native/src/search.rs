//! Running a search request: one `MultiCollector` pass for the count, the page of hits, facets
//! and aggregations, then loading the hits' stored fields and snippets.
//!
//! Request: `{"query": {...}, "limit": n, "offset": n, "sort": [...]?, "search_after": {...}?, "fields": [...]?,
//! "snippets": {...}?, "facets": [...]?, "aggregations": {...}?}`.
//! Result: `{"count": <total matches>, "hits": [{"score": f|null, "sort": [...], "addr": [segment, doc],
//! "doc": {"field": [values...]}, "snippets": {...}?}], "facets": {...}?, "aggregations": {...}?}`
//! (see the `sorting`, `snippets`, `facets` and `aggregations` modules). `limit == 0` only counts.

use std::collections::{BTreeMap, HashSet};

use serde::ser::{SerializeMap, SerializeSeq};
use serde::{Deserialize, Serialize, Serializer};
use tantivy::aggregation::agg_result::AggregationResults;
use tantivy::collector::{Count, MultiCollector};
use tantivy::query::Query;
use tantivy::query::Explanation;
use tantivy::schema::{Field, OwnedValue, Schema, Value};
use tantivy::{DocAddress, Index, Searcher, TantivyDocument};

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
struct SearchResultJson<'a> {
    count: usize,
    hits: Vec<HitJson<'a>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    facets: Option<FacetResultJson>,
    #[serde(skip_serializing_if = "Option::is_none")]
    aggregations: Option<AggregationResults>,
}

#[derive(Serialize)]
struct HitJson<'a> {
    /// NaN when sorted by a field; serde_json writes non-finite floats as `null`.
    score: f32,
    /// One tagged value per sort key (`[{"score": f}]` when sorted by relevance); see `sorting`.
    sort: serde_json::Value,
    /// The hit's `DocAddress` as `[segment_ord, doc_id]`, for `explain`.
    addr: [u32; 2],
    doc: DocJson<'a>,
    #[serde(skip_serializing_if = "Option::is_none")]
    snippets: Option<BTreeMap<String, SnippetJson>>,
}

pub fn run(searcher: &Searcher, index: &Index, request_json: &str) -> FfiResult<String> {
    let request: SearchRequest = serde_json::from_str(request_json)
        .map_err(|e| FfiError::invalid(format!("invalid search request JSON: {e}")))?;
    let schema = index.schema();
    let query = query_spec::build(&request.query, index)?;
    let only_fields = request.fields.as_deref().map(|names| stored_fields(&schema, names)).transpose()?;

    let top_plan = match request.limit {
        0 => None,
        limit => Some(sorting::plan(&request.sort, request.search_after.as_ref(), limit, request.offset, &schema)?),
    };

    let (count, top, facets, aggregations) = if request.facets.is_none() && request.aggregations.is_none() {
        // The common case: just the count and a page of hits, with static collectors.
        let (count, top) = count_and_top(searcher, &request.query, query.as_ref(), top_plan)?;
        (count, top, None, None)
    } else {
        let mut collectors = MultiCollector::new();
        let count_handle = collectors.add_collector(Count);
        let top_handle = top_plan.map(|p| p.add_to(&mut collectors));
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
        let top = top_handle.map(|h| sorting::extract(h, &mut fruits)).unwrap_or_default();
        let facets = request.facets.is_some().then(|| facets::collect(facet_plans, &mut fruits));
        let aggregations = agg_handle.map(|h| h.extract(&mut fruits));
        (count, top, facets, aggregations)
    };

    // Built even with no hits, so an invalid snippet field fails consistently.
    let snippets = request.snippets.as_ref().map(|r| Snippets::new(r, searcher, query.as_ref())).transpose()?;
    // With no fields and no snippets requested, the document store isn't read at all.
    let skip_docs = snippets.is_none() && only_fields.as_ref().is_some_and(|f| f.is_empty());
    let hits = top
        .into_iter()
        .map(|(score, sort, address)| {
            let addr = [address.segment_ord, address.doc_id];
            let only = only_fields.as_ref();
            if skip_docs {
                return Ok(HitJson { score, sort, addr, doc: DocJson { doc: None, schema: &schema, only }, snippets: None });
            }
            let doc: TantivyDocument = searcher.doc(address)?;
            let snippets = snippets.as_ref().map(|g| g.for_doc(&doc));
            Ok(HitJson { score, sort, addr, doc: DocJson { doc: Some(doc), schema: &schema, only }, snippets })
        })
        .collect::<FfiResult<Vec<_>>>()?;

    serde_json::to_string(&SearchResultJson { count, hits, facets, aggregations })
        .map_err(|e| FfiError::new(status::ERROR, format!("result serialization: {e}")))
}

/// The total count and the page of hits. A term query (or all documents) is counted in a search
/// of its own: Tantivy answers that from the posting list's length, and the hits can then be
/// collected with Block-WAND. Other queries are counted in the same pass as the hits.
fn count_and_top(
    searcher: &Searcher,
    spec: &QuerySpec,
    query: &dyn Query,
    plan: Option<sorting::TopPlan>,
) -> FfiResult<(usize, Vec<sorting::TopHit>)> {
    match plan {
        None => Ok((searcher.search(query, &Count)?, Vec::new())),
        Some(plan) => {
            let separate_count = matches!(spec, QuerySpec::Term { .. } | QuerySpec::All);
            Ok(plan.search_with_count(searcher, query, separate_count)?)
        }
    }
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

/// A stored document written as `{"field": [values...]}`, straight from the document's values:
/// the same JSON as Tantivy's `NamedFieldDocument` (fields by name, values in stored order,
/// `OwnedValue`'s encoding), without first copying every value and field name.
struct DocJson<'a> {
    doc: Option<TantivyDocument>,
    schema: &'a Schema,
    /// Only these fields (`SearchOptions.Fields`); `None` = all.
    only: Option<&'a HashSet<Field>>,
}

impl Serialize for DocJson<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let Some(doc) = &self.doc else {
            return serializer.serialize_map(Some(0))?.end();
        };
        let mut values: Vec<_> = doc
            .field_values()
            .filter(|(field, _)| self.only.is_none_or(|only| only.contains(field)))
            .collect();
        // Group by field name (a stable sort keeps each field's values in stored order).
        values.sort_by(|a, b| self.schema.get_field_name(a.0).cmp(self.schema.get_field_name(b.0)));
        let fields = values.chunk_by(|a, b| a.0 == b.0);
        let mut map = serializer.serialize_map(None)?;
        for group in fields {
            map.serialize_entry(self.schema.get_field_name(group[0].0), &FieldValues(group))?;
        }
        map.end()
    }
}

struct FieldValues<'a, 'd>(&'a [(Field, tantivy::schema::document::CompactDocValue<'d>)]);

impl Serialize for FieldValues<'_, '_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut seq = serializer.serialize_seq(Some(self.0.len()))?;
        for (_, value) in self.0 {
            match value.as_str() {
                Some(text) => seq.serialize_element(text)?,
                None => seq.serialize_element(&OwnedValue::from(value.as_value()))?,
            }
        }
        seq.end()
    }
}
