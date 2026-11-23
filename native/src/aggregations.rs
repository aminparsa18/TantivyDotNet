//! Aggregations: Tantivy's Elasticsearch-style aggregation JSON in, its result JSON out.
//!
//! Request part: `"aggregations": {"by_year": {"histogram": {"field": "year", "interval": 10}}}`,
//! passed to Tantivy after [`check`]. Result part: `"aggregations": {"by_year": {"buckets": [...]}}`.

use serde_json::{Map, Value};
use tantivy::aggregation::agg_req::Aggregations;
use tantivy::aggregation::agg_result::AggregationResults;
use tantivy::aggregation::{AggContextParams, AggregationCollector, AggregationLimitsGuard};
use tantivy::collector::{FruitHandle, MultiCollector};
use tantivy::columnar::{ColumnType, MonotonicallyMappableToU64};
use tantivy::{DateTime, Index, Searcher};

use crate::ffi::{FfiError, FfiResult};

/// Terms aggregations return at most this many buckets (Tantivy's default bucket limit), so a
/// larger `size` is clamped to it. Tantivy computes `size * 10` in a `u32`, which overflows.
const MAX_TERMS_SIZE: u64 = 65_000;

/// `top_hits` reserves `2 * (from + size)` entries per segment up front, so the window is bounded
/// (Elasticsearch's default `max_result_window`).
const MAX_TOP_HITS: u64 = 10_000;

/// A histogram that would fill more empty buckets than this is rejected. Far beyond that, Tantivy's
/// own memory check overflows and lets it through, and the buckets exhaust memory.
const MAX_HISTOGRAM_BUCKETS: f64 = 10_000_000.0;

/// Parses the aggregation request and adds its collector to `multi`.
pub fn add(
    request: &serde_json::Value,
    searcher: &Searcher,
    index: &Index,
    multi: &mut MultiCollector,
) -> FfiResult<FruitHandle<AggregationResults>> {
    let mut request = request.clone();
    if let Value::Object(aggs) = &mut request {
        check(aggs, searcher)?;
    }
    let aggs: Aggregations =
        serde_json::from_value(request).map_err(|e| FfiError::invalid(format!("invalid aggregation request: {e}")))?;
    let context = AggContextParams::new(AggregationLimitsGuard::default(), index.tokenizers().clone());
    Ok(multi.add_collector(AggregationCollector::from_aggs(aggs, context)))
}

/// Rejects or clamps the parts of a request that make Tantivy panic, overflow or reserve huge
/// amounts of memory (all found by fuzzing). Anything malformed is left for Tantivy to report.
fn check(aggs: &mut Map<String, Value>, searcher: &Searcher) -> FfiResult<()> {
    for (name, agg) in aggs.iter_mut() {
        let Value::Object(agg) = agg else { continue };
        for (kind, params) in agg.iter_mut() {
            match (kind.as_str(), params) {
                ("aggs" | "aggregations", Value::Object(sub)) => check(sub, searcher)?,
                ("terms", Value::Object(p)) => {
                    for key in ["size", "segment_size"] {
                        if p.get(key).and_then(Value::as_u64).is_some_and(|n| n > MAX_TERMS_SIZE) {
                            p.insert(key.into(), MAX_TERMS_SIZE.into());
                        }
                    }
                }
                ("top_hits", Value::Object(p)) => check_top_hits(name, p, searcher)?,
                ("histogram", Value::Object(p)) => check_histogram(name, p, searcher)?,
                _ => {}
            }
        }
    }
    Ok(())
}

fn check_top_hits(name: &str, p: &Map<String, Value>, searcher: &Searcher) -> FfiResult<()> {
    let number = |key: &str| p.get(key).and_then(Value::as_u64).unwrap_or(0);
    if number("size").saturating_add(number("from")) > MAX_TOP_HITS {
        return Err(FfiError::invalid(format!(
            "top_hits aggregation '{name}': size + from must be at most {MAX_TOP_HITS}"
        )));
    }
    // Tantivy reads a docvalue field that isn't a column of a segment as a glob, and asserts that
    // the glob matches a column there: so every field must be a column in every segment.
    if let Some(Value::Array(fields)) = p.get("docvalue_fields") {
        for field in fields.iter().filter_map(Value::as_str) {
            if field.contains('*') {
                return Err(FfiError::invalid(format!(
                    "top_hits aggregation '{name}': wildcards in docvalue_fields aren't supported; list the fields"
                )));
            }
            for segment in searcher.segment_readers() {
                let found = !segment.fast_fields().dynamic_column_handles(field)?.is_empty();
                if !found {
                    return Err(FfiError::invalid(format!(
                        "top_hits aggregation '{name}': docvalue field '{field}' must be a fast field with a value \
                         in every segment (Tantivy 0.27 fails otherwise)"
                    )));
                }
            }
        }
    }
    Ok(())
}

/// With `min_doc_count` 0 (the default), Tantivy fills every bucket between the smallest and the
/// largest value (or the extended bounds), so the bucket count is checked against the field's range.
fn check_histogram(name: &str, p: &Map<String, Value>, searcher: &Searcher) -> FfiResult<()> {
    let (Some(field), Some(interval)) =
        (p.get("field").and_then(Value::as_str), p.get("interval").and_then(Value::as_f64))
    else {
        return Ok(());
    };
    if !(interval.is_finite() && interval > 0.0) {
        return Err(FfiError::invalid(format!("histogram aggregation '{name}': interval must be a positive number")));
    }
    if p.get("min_doc_count").and_then(Value::as_u64).unwrap_or(0) > 0 {
        return Ok(());
    }
    let (mut lo, mut hi) = (f64::INFINITY, f64::NEG_INFINITY);
    for segment in searcher.segment_readers() {
        for (column, ty) in segment.fast_fields().u64_lenient_for_type_all(None, field)? {
            let to_f64 = |v: u64| match ty {
                ColumnType::I64 => i64::from_u64(v) as f64,
                ColumnType::U64 => v as f64,
                ColumnType::F64 => f64::from_u64(v),
                ColumnType::DateTime => DateTime::from_u64(v).into_timestamp_nanos() as f64,
                _ => f64::NAN,
            };
            let (min, max) = (to_f64(column.min_value()), to_f64(column.max_value()));
            if min.is_finite() && max.is_finite() {
                lo = lo.min(min);
                hi = hi.max(max);
            }
        }
    }
    if let Some(Value::Object(bounds)) = p.get("extended_bounds") {
        lo = lo.min(bounds.get("min").and_then(Value::as_f64).unwrap_or(f64::INFINITY));
        hi = hi.max(bounds.get("max").and_then(Value::as_f64).unwrap_or(f64::NEG_INFINITY));
    }
    if lo <= hi && (hi - lo) / interval > MAX_HISTOGRAM_BUCKETS {
        return Err(FfiError::invalid(format!(
            "histogram aggregation '{name}': interval {interval} gives more than {MAX_HISTOGRAM_BUCKETS} buckets \
             over the values of '{field}'; use a larger interval or min_doc_count 1"
        )));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use crate::testing::Fixture;

    fn fixture() -> Fixture {
        Fixture::new(
            r#"{"fields":[{"name":"id","type":"text","tokenizer":"raw","fast":true},{"name":"x","type":"f64","fast":true},
                {"name":"j","type":"json","fast":true}]}"#,
            &[
                &[r#"{"id":"a","x":1.5,"j":{"n":1}}"#, r#"{"id":"b","x":1000.0}"#],
                &[r#"{"id":"c","x":-20.0,"j":{"n":5}}"#],
            ],
        )
    }

    fn aggs(f: &Fixture, aggs: &str) -> Result<serde_json::Value, String> {
        f.search(&format!(r#"{{"query":{{"type":"all"}},"limit":0,"aggregations":{aggs}}}"#))
            .map(|r| r["aggregations"].clone())
            .map_err(|e| e.message)
    }

    #[test]
    fn huge_terms_sizes_are_clamped() {
        // Tantivy computes size * 10 in a u32.
        let f = fixture();
        let r = aggs(&f, r#"{"t":{"terms":{"field":"id","size":4294967295,"segment_size":4294967295}}}"#).unwrap();
        assert_eq!(r["t"]["buckets"].as_array().unwrap().len(), 3);
        let nested = r#"{"o":{"terms":{"field":"id"},"aggs":{"t":{"terms":{"field":"id","size":4294967295}}}}}"#;
        assert!(aggs(&f, nested).is_ok());
    }

    #[test]
    fn top_hits_windows_and_wildcards() {
        let f = fixture();
        let top = |size: u64, fields: &str| {
            format!(r#"{{"h":{{"top_hits":{{"size":{size},"sort":[{{"x":"desc"}}],"docvalue_fields":{fields}}}}}}}"#)
        };
        assert_eq!(aggs(&f, &top(2, r#"["x"]"#)).unwrap()["h"]["hits"].as_array().unwrap().len(), 2);
        // Tantivy reserves the whole window up front (2^32 entries: an abort).
        assert!(aggs(&f, &top(u32::MAX as u64, r#"["x"]"#)).unwrap_err().contains("size + from must be at most"));
        assert!(aggs(&f, &top(u64::MAX, r#"["x"]"#)).unwrap_err().contains("size + from must be at most"));
        // A glob matching no column in some segment hits an assertion in Tantivy.
        assert!(aggs(&f, &top(2, r#"["j.*"]"#)).unwrap_err().contains("wildcards"));
        // Unknown, not fast, or without values in a segment: Tantivy treats those as globs too.
        for field in ["nope", "j.missing", "j"] {
            let e = aggs(&f, &top(2, &format!(r#"["{field}"]"#))).unwrap_err();
            assert!(e.contains("must be a fast field with a value in every segment"), "{field}: {e}");
        }
        let sub = r#"{"t":{"terms":{"field":"id"},"aggs":{"h":{"top_hits":{"size":20000}}}}}"#;
        assert!(aggs(&f, sub).unwrap_err().contains("size + from"));
    }

    #[test]
    fn histograms_with_too_many_buckets_are_rejected() {
        let f = fixture();
        let hist = |interval: &str, extra: &str| {
            format!(r#"{{"h":{{"histogram":{{"field":"x","interval":{interval}{extra}}}}}}}"#)
        };
        assert_eq!(aggs(&f, &hist("100", "")).unwrap()["h"]["buckets"].as_array().unwrap().len(), 12); // -100 .. 1000
                                                                                                       // ~1e303 buckets: Tantivy's memory check overflows and lets that through.
        assert!(aggs(&f, &hist("1e-300", "")).unwrap_err().contains("buckets"));
        assert!(aggs(&f, &hist("1", r#","extended_bounds":{"min":-1e300,"max":1e300}"#))
            .unwrap_err()
            .contains("buckets"));
        assert!(aggs(&f, &hist("0", "")).unwrap_err().contains("positive"));
        // Only filled buckets are created with min_doc_count > 0.
        assert!(aggs(&f, &hist("1e-300", r#","min_doc_count":1"#)).is_ok());
        let json = r#"{"h":{"histogram":{"field":"j.n","interval":1e-300}}}"#;
        assert!(aggs(&f, json).unwrap_err().contains("buckets"));
    }
}
