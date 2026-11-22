//! Aggregations: Tantivy's Elasticsearch-style aggregation JSON in, its result JSON out.
//!
//! Request part: `"aggregations": {"by_year": {"histogram": {"field": "year", "interval": 10}}}`,
//! passed through to Tantivy unchanged. Result part: `"aggregations": {"by_year": {"buckets": [...]}}`.

use tantivy::aggregation::agg_req::Aggregations;
use tantivy::aggregation::agg_result::AggregationResults;
use tantivy::aggregation::{AggContextParams, AggregationCollector, AggregationLimitsGuard};
use tantivy::collector::{FruitHandle, MultiCollector};
use tantivy::Index;

use crate::ffi::{FfiError, FfiResult};

/// Parses the aggregation request and adds its collector to `multi`.
pub fn add(
    request: &serde_json::Value,
    index: &Index,
    multi: &mut MultiCollector,
) -> FfiResult<FruitHandle<AggregationResults>> {
    let aggs: Aggregations = serde_json::from_value(request.clone())
        .map_err(|e| FfiError::invalid(format!("invalid aggregation request: {e}")))?;
    let context = AggContextParams::new(AggregationLimitsGuard::default(), index.tokenizers().clone());
    Ok(multi.add_collector(AggregationCollector::from_aggs(aggs, context)))
}
