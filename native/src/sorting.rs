//! Ordering search hits by relevance or by one fast field.
//!
//! Request part: `"sort": {"field": "year", "order": "asc" | "desc"}` (absent = by score).
//! Per hit, when sorted by a field: `"sort": {"u64": 1937}` (tagged like query values in
//! `query_spec`; dates as Unix microseconds) or `null` when the document has no value. Documents
//! without a value come last in both orders.

use serde::Deserialize;
use serde_json::{json, Value};
use tantivy::collector::sort_key::SortByErasedType;
use tantivy::collector::{FruitHandle, MultiCollector, TopDocs};
use tantivy::schema::{FieldType, OwnedValue, Schema};
use tantivy::{DocAddress, Order, Score};

use crate::ffi::{FfiError, FfiResult};

#[derive(Deserialize)]
pub struct SortRequest {
    field: String,
    order: SortOrder,
}

#[derive(Deserialize, Clone, Copy)]
#[serde(rename_all = "snake_case")]
enum SortOrder {
    Asc,
    Desc,
}

pub enum TopHandle {
    Score(FruitHandle<Vec<(Score, DocAddress)>>),
    Field(FruitHandle<Vec<(OwnedValue, DocAddress)>>),
}

/// A top hit: its score (NaN when sorted by a field, because scoring is skipped), its sort value
/// (when sorted by a field), and its address.
pub type TopHit = (Score, Option<Value>, DocAddress);

/// Adds the collector for the requested page of hits to `multi`.
pub fn add(
    sort: Option<&SortRequest>,
    limit: usize,
    offset: usize,
    schema: &Schema,
    multi: &mut MultiCollector,
) -> FfiResult<TopHandle> {
    let top = TopDocs::with_limit(limit).and_offset(offset);
    let Some(sort) = sort else {
        return Ok(TopHandle::Score(multi.add_collector(top.order_by_score())));
    };
    let field = schema.get_field(&sort.field)?;
    let entry = schema.get_field_entry(field);
    let sortable = matches!(
        entry.field_type(),
        FieldType::U64(_)
            | FieldType::I64(_)
            | FieldType::F64(_)
            | FieldType::Bool(_)
            | FieldType::Date(_)
            | FieldType::Str(_)
            | FieldType::Bytes(_)
    );
    if !sortable {
        return Err(FfiError::invalid(format!("field '{}' can't be sorted by: its type isn't supported", sort.field)));
    }
    if !entry.is_fast() {
        return Err(FfiError::invalid(format!(
            "field '{}' can't be sorted by: it isn't a fast field (create it with fast: true)",
            sort.field
        )));
    }
    let order = match sort.order {
        SortOrder::Asc => Order::Asc,
        SortOrder::Desc => Order::Desc,
    };
    let collector = top.order_by((SortByErasedType::for_field(&sort.field), order));
    Ok(TopHandle::Field(multi.add_collector(collector)))
}

pub fn extract(handle: TopHandle, fruits: &mut tantivy::collector::MultiFruit) -> Vec<TopHit> {
    match handle {
        TopHandle::Score(h) => h.extract(fruits).into_iter().map(|(score, addr)| (score, None, addr)).collect(),
        TopHandle::Field(h) => h
            .extract(fruits)
            .into_iter()
            .map(|(value, addr)| (Score::NAN, Some(tagged(value)), addr))
            .collect(),
    }
}

fn tagged(value: OwnedValue) -> Value {
    match value {
        OwnedValue::Str(s) => json!({ "str": s }),
        OwnedValue::U64(n) => json!({ "u64": n }),
        OwnedValue::I64(n) => json!({ "i64": n }),
        OwnedValue::F64(x) => json!({ "f64": x }),
        OwnedValue::Bool(b) => json!({ "bool": b }),
        OwnedValue::Date(d) => json!({ "date": d.into_timestamp_micros() }),
        OwnedValue::Bytes(b) => json!({ "bytes": b }),
        _ => Value::Null,
    }
}
