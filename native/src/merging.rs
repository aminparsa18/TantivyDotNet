//! Merge policies, forced merges and garbage collection for a writer.
//!
//! Merge policy JSON (empty string = Tantivy's default `LogMergePolicy`):
//! `{"type": "none"}` or `{"type": "log", "min_segments"?, "max_docs_before_merge"?, "min_layer_size"?,
//! "level_log_size"?, "del_docs_ratio"?}`.

use std::time::Duration;

use serde::{Deserialize, Serialize};
use tantivy::directory::GarbageCollectionResult;
use tantivy::index::SegmentMeta;
use tantivy::indexer::{LogMergePolicy, MergePolicy, NoMergePolicy};
use tantivy::{Index, IndexWriter};

use crate::ffi::{FfiError, FfiResult};

#[derive(Deserialize, Clone, Default)]
#[serde(tag = "type", rename_all = "snake_case", deny_unknown_fields)]
pub enum MergePolicySpec {
    #[default]
    Default,
    None,
    Log {
        min_segments: Option<usize>,
        max_docs_before_merge: Option<usize>,
        min_layer_size: Option<u32>,
        level_log_size: Option<f64>,
        del_docs_ratio: Option<f32>,
    },
}

impl MergePolicySpec {
    pub fn from_json(json: &str) -> FfiResult<Self> {
        if json.is_empty() {
            return Ok(Self::Default);
        }
        let spec: Self =
            serde_json::from_str(json).map_err(|e| FfiError::invalid(format!("invalid merge policy JSON: {e}")))?;
        if let Self::Log { min_segments, level_log_size, del_docs_ratio, .. } = &spec {
            if matches!(min_segments, Some(n) if *n < 2) {
                return Err(FfiError::invalid("min_segments must be at least 2"));
            }
            if matches!(level_log_size, Some(x) if !(x.is_finite() && *x > 0.0)) {
                return Err(FfiError::invalid("level_log_size must be positive"));
            }
            // Tantivy panics outside (0, 1].
            if matches!(del_docs_ratio, Some(r) if !(*r > 0.0 && *r <= 1.0)) {
                return Err(FfiError::invalid("del_docs_ratio must be in (0, 1]"));
            }
        }
        Ok(spec)
    }

    pub fn build(&self) -> Box<dyn MergePolicy> {
        match self {
            Self::Default => Box::new(LogMergePolicy::default()),
            Self::None => Box::new(NoMergePolicy),
            Self::Log { min_segments, max_docs_before_merge, min_layer_size, level_log_size, del_docs_ratio } => {
                let mut p = LogMergePolicy::default();
                if let Some(v) = min_segments {
                    p.set_min_num_segments(*v);
                }
                if let Some(v) = max_docs_before_merge {
                    p.set_max_docs_before_merge(*v);
                }
                if let Some(v) = min_layer_size {
                    p.set_min_layer_size(*v);
                }
                if let Some(v) = level_log_size {
                    p.set_level_log_size(*v);
                }
                if let Some(v) = del_docs_ratio {
                    p.set_del_docs_ratio_before_merge(*v);
                }
                Box::new(p)
            }
        }
    }
}

/// Rounds of planning + merging before giving up. A round only fails when one of its merges
/// collides with a background merge of the same segments, so this is plenty.
const MAX_ROUNDS: usize = 8;

/// Merges the committed segments until at most `max_segments` remain and none has deleted
/// documents. Background merges are paused (NoMergePolicy) meanwhile and `policy` is restored
/// afterwards. Uncommitted segments are left alone.
pub fn force_merge(
    writer: &mut IndexWriter,
    index: &Index,
    policy: &MergePolicySpec,
    max_segments: usize,
) -> FfiResult<()> {
    if max_segments == 0 {
        return Err(FfiError::invalid("max_segments must be at least 1"));
    }
    writer.set_merge_policy(Box::new(NoMergePolicy));
    let result = merge_rounds(writer, index, max_segments);
    writer.set_merge_policy(policy.build());
    result
}

fn merge_rounds(writer: &mut IndexWriter, index: &Index, max_segments: usize) -> FfiResult<()> {
    let mut last_error = None;
    for round in 0..MAX_ROUNDS {
        let plan = plan(index.searchable_segment_metas()?, max_segments);
        if plan.is_empty() {
            return Ok(());
        }
        for ids in plan {
            // Fails when a background merge started before the pause owns one of these segments
            // and commits first; the next round plans again from the new segment set.
            if let Err(e) = writer.merge(&ids).wait() {
                last_error = Some(e);
                std::thread::sleep(Duration::from_millis(20 * (round as u64 + 1)));
                break;
            }
        }
    }
    Err(match last_error {
        Some(e) => e.into(),
        None => FfiError::new(crate::ffi::status::ERROR, "force merge did not converge"),
    })
}

/// The merges for one round: the smallest segments into one if there are too many, otherwise
/// each segment with deletes on its own (rewriting it drops the deleted documents).
fn plan(mut metas: Vec<SegmentMeta>, max_segments: usize) -> Vec<Vec<tantivy::index::SegmentId>> {
    if metas.len() > max_segments {
        metas.sort_by_key(|m| m.num_docs());
        let take = metas.len() - max_segments + 1;
        return vec![metas[..take].iter().map(|m| m.id()).collect()];
    }
    metas.iter().filter(|m| m.has_deletes()).map(|m| vec![m.id()]).collect()
}

#[derive(Serialize)]
struct GcJson {
    deleted: Vec<String>,
    failed: Vec<String>,
}

pub fn gc_to_json(result: GarbageCollectionResult) -> String {
    let names = |v: Vec<std::path::PathBuf>| v.into_iter().map(|p| p.to_string_lossy().into_owned()).collect();
    let json = GcJson { deleted: names(result.deleted_files), failed: names(result.failed_to_delete_files) };
    serde_json::to_string(&json).expect("gc result serialization cannot fail")
}
