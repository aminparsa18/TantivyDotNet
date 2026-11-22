//! Statistics of a searcher's snapshot, from Tantivy's space usage report.
//!
//! ```json
//! {"segments": [{"id": "uuid", "max_doc": n, "deleted": n, "bytes": n, "store": n}],
//!  "fields": [{"name": "title", "termdict": n, "postings": n, "positions": n, "fast": n, "fieldnorms": n}]}
//! ```
//! `fields` sums every segment; it doesn't include the doc store, which isn't split per field.

use std::collections::BTreeMap;

use serde::Serialize;
use tantivy::space_usage::PerFieldSpaceUsage;
use tantivy::Searcher;

use crate::ffi::{status, FfiError, FfiResult};

#[derive(Serialize)]
struct StatsJson {
    segments: Vec<SegmentJson>,
    fields: Vec<FieldJson>,
}

#[derive(Serialize)]
struct SegmentJson {
    id: String,
    max_doc: u32,
    deleted: u32,
    bytes: u64,
    store: u64,
}

#[derive(Serialize, Default)]
struct FieldJson {
    name: String,
    termdict: u64,
    postings: u64,
    positions: u64,
    fast: u64,
    fieldnorms: u64,
}

pub fn to_json(searcher: &Searcher) -> FfiResult<String> {
    let mut segments = Vec::new();
    let mut fields: BTreeMap<String, FieldJson> = BTreeMap::new();
    for reader in searcher.segment_readers() {
        // Space usage reopens the segment's files by name. A stale snapshot can still search
        // (it holds its files open), but once a merge replaced them and they were deleted, this fails.
        let usage = reader.space_usage().map_err(|e| {
            FfiError::new(
                status::IO,
                format!(
                    "can't read the size of segment {}: {e}. Its files were probably replaced by a merge and deleted; \
                     take stats from a newer searcher",
                    reader.segment_id().uuid_string()
                ),
            )
        })?;
        segments.push(SegmentJson {
            id: reader.segment_id().uuid_string(),
            max_doc: reader.max_doc(),
            deleted: reader.num_deleted_docs(),
            bytes: usage.total().get_bytes(),
            store: usage.store().total().get_bytes(),
        });
        let mut add = |per_field: &PerFieldSpaceUsage, slot: fn(&mut FieldJson) -> &mut u64| {
            for f in per_field.fields() {
                let entry = fields
                    .entry(f.field_name().to_owned())
                    .or_insert_with(|| FieldJson { name: f.field_name().to_owned(), ..Default::default() });
                *slot(entry) += f.total().get_bytes();
            }
        };
        add(usage.termdict(), |f| &mut f.termdict);
        add(usage.postings(), |f| &mut f.postings);
        add(usage.positions(), |f| &mut f.positions);
        add(usage.fast_fields(), |f| &mut f.fast);
        add(usage.fieldnorms(), |f| &mut f.fieldnorms);
    }
    let json = StatsJson { segments, fields: fields.into_values().collect() };
    Ok(serde_json::to_string(&json).expect("stats serialization cannot fail"))
}
