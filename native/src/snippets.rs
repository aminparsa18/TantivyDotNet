//! Snippets (highlighted excerpts) for search hits.
//!
//! Request part: `"snippets": {"fields": ["body"], "max_length": 150}`.
//! Per hit: `"snippets": {"body": {"fragment": "...", "highlights": [[start, end], ...]}}`, with
//! highlight offsets in UTF-16 code units into `fragment`, sorted and non-overlapping, so .NET can
//! index the string directly.

use std::collections::BTreeMap;
use std::ops::Range;

use serde::{Deserialize, Serialize};
use tantivy::query::Query;
use tantivy::schema::FieldType;
use tantivy::snippet::{Snippet, SnippetGenerator};
use tantivy::{Searcher, TantivyDocument};

use crate::ffi::{FfiError, FfiResult};

#[derive(Deserialize)]
pub struct SnippetRequest {
    fields: Vec<String>,
    /// Tantivy measures this in UTF-8 bytes of the source text.
    #[serde(default = "default_max_length")]
    max_length: usize,
}

fn default_max_length() -> usize {
    150
}

#[derive(Serialize)]
pub struct SnippetJson {
    fragment: String,
    highlights: Vec<[usize; 2]>,
}

/// One generator per requested field, built once per search.
pub struct Snippets {
    generators: Vec<(String, SnippetGenerator)>,
}

impl Snippets {
    pub fn new(request: &SnippetRequest, searcher: &Searcher, query: &dyn Query) -> FfiResult<Self> {
        if request.max_length == 0 {
            return Err(FfiError::invalid("snippet max length must be positive"));
        }
        let schema = searcher.schema();
        let mut generators = Vec::with_capacity(request.fields.len());
        for name in &request.fields {
            let field = schema.get_field(name)?;
            let entry = schema.get_field_entry(field);
            if !matches!(entry.field_type(), FieldType::Str(_)) || !entry.is_stored() {
                return Err(FfiError::invalid(format!(
                    "snippets need a stored text field, but '{name}' is not one"
                )));
            }
            let mut generator = SnippetGenerator::create(searcher, query, field)?;
            generator.set_max_num_chars(request.max_length);
            generators.push((name.clone(), generator));
        }
        Ok(Self { generators })
    }

    pub fn for_doc(&self, doc: &TantivyDocument) -> BTreeMap<String, SnippetJson> {
        self.generators
            .iter()
            .map(|(name, g)| (name.clone(), to_json(&g.snippet_from_doc(doc))))
            .collect()
    }
}

fn to_json(snippet: &Snippet) -> SnippetJson {
    let fragment = snippet.fragment();
    let highlights = merge(snippet.highlighted())
        .into_iter()
        .map(|r| [utf16_len(&fragment[..r.start]), utf16_len(&fragment[..r.end])])
        .collect();
    SnippetJson { fragment: fragment.to_owned(), highlights }
}

/// Sorts byte ranges and merges overlapping or touching ones.
fn merge(ranges: &[Range<usize>]) -> Vec<Range<usize>> {
    let mut sorted = ranges.to_vec();
    sorted.sort_by_key(|r| (r.start, r.end));
    let mut merged: Vec<Range<usize>> = Vec::with_capacity(sorted.len());
    for r in sorted {
        match merged.last_mut() {
            Some(last) if r.start <= last.end => last.end = last.end.max(r.end),
            _ => merged.push(r),
        }
    }
    merged
}

fn utf16_len(s: &str) -> usize {
    s.chars().map(char::len_utf16).sum()
}
