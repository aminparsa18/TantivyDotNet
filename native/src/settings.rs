//! Index settings JSON exchanged with .NET, chosen when an index is created. Tantivy stores them in
//! `meta.json`, so an opened index reports the settings it was created with.
//!
//! ```json
//! {"compression": "lz4"|"zstd"|"none", "zstd_level": 3?, "block_size": 16384,
//!  "sort_by": {"field": "year", "descending": true}?}
//! ```
//! Every member is optional on input (Tantivy's defaults apply); output always has all of them.

use serde::{Deserialize, Serialize};
use tantivy::store::{Compressor, ZstdCompressor};
use tantivy::{IndexSettings, IndexSortByField, Order};

use crate::ffi::{FfiError, FfiResult};

#[derive(Serialize, Deserialize, Default)]
struct SettingsSpec {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    compression: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    zstd_level: Option<i32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    block_size: Option<usize>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    sort_by: Option<SortBySpec>,
}

#[derive(Serialize, Deserialize)]
struct SortBySpec {
    field: String,
    #[serde(default)]
    descending: bool,
}

/// Parses settings JSON; an empty string means Tantivy's defaults. Whether `sort_by` names a
/// suitable fast field is checked by Tantivy when the index is created.
pub fn from_json(json: &str) -> FfiResult<IndexSettings> {
    let mut settings = IndexSettings::default();
    if json.is_empty() {
        return Ok(settings);
    }
    let spec: SettingsSpec =
        serde_json::from_str(json).map_err(|e| FfiError::invalid(format!("invalid index settings JSON: {e}")))?;
    if spec.zstd_level.is_some() && spec.compression.as_deref() != Some("zstd") {
        return Err(FfiError::invalid("zstd_level requires zstd compression"));
    }
    if let Some(compression) = &spec.compression {
        settings.docstore_compression = match compression.as_str() {
            "none" => Compressor::None,
            "lz4" => Compressor::Lz4,
            "zstd" => {
                if let Some(level) = spec.zstd_level {
                    if !(1..=22).contains(&level) {
                        return Err(FfiError::invalid(format!("zstd level must be between 1 and 22, got {level}")));
                    }
                }
                Compressor::Zstd(ZstdCompressor { compression_level: spec.zstd_level })
            }
            other => return Err(FfiError::invalid(format!("unknown doc store compression '{other}'"))),
        };
    }
    if let Some(block_size) = spec.block_size {
        if block_size == 0 {
            return Err(FfiError::invalid("doc store block size must be positive"));
        }
        settings.docstore_blocksize = block_size;
    }
    settings.sort_by_field = spec
        .sort_by
        .map(|s| IndexSortByField { field: s.field, order: if s.descending { Order::Desc } else { Order::Asc } });
    Ok(settings)
}

pub fn to_json(settings: &IndexSettings) -> String {
    let (compression, zstd_level) = match settings.docstore_compression {
        Compressor::None => ("none", None),
        Compressor::Lz4 => ("lz4", None),
        Compressor::Zstd(z) => ("zstd", z.compression_level),
    };
    let spec = SettingsSpec {
        compression: Some(compression.to_owned()),
        zstd_level,
        block_size: Some(settings.docstore_blocksize),
        sort_by: settings
            .sort_by_field
            .as_ref()
            .map(|s| SortBySpec { field: s.field.clone(), descending: s.order.is_desc() }),
    };
    serde_json::to_string(&spec).expect("settings serialization cannot fail")
}
