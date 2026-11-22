//! Parsing documents given as JSON objects (field name -> value or array of values).

use serde_json::{Map, Value};
use tantivy::schema::Schema;
use tantivy::TantivyDocument;

use crate::ffi::{FfiError, FfiResult};

/// Parses one document. Unless `ignore_unknown_fields`, a key that isn't a schema field is an
/// error (Tantivy itself silently drops it, which hides typos and casing mistakes).
pub fn parse(schema: &Schema, json: &str, ignore_unknown_fields: bool) -> FfiResult<TantivyDocument> {
    let object: Map<String, Value> =
        serde_json::from_str(json).map_err(|e| FfiError::invalid(format!("invalid document: {e}")))?;
    if !ignore_unknown_fields {
        if let Some(key) = object.keys().find(|k| schema.get_field(k).is_err()) {
            return Err(unknown_field(schema, key));
        }
    }
    TantivyDocument::from_json_object(schema, object)
        .map_err(|e| FfiError::invalid(format!("invalid document: {e}")))
}

fn unknown_field(schema: &Schema, key: &str) -> FfiError {
    let names: Vec<&str> = schema.fields().map(|(_, e)| e.name()).collect();
    let hint = match names.iter().find(|n| n.eq_ignore_ascii_case(key)) {
        Some(similar) => format!("did you mean '{similar}'? Field names are case-sensitive"),
        None => {
            const SHOWN: usize = 20;
            let mut list = names.iter().take(SHOWN).copied().collect::<Vec<_>>().join(", ");
            if names.len() > SHOWN {
                list.push_str(", ...");
            }
            format!("the schema's fields are: {list}")
        }
    };
    FfiError::invalid(format!(
        "document has unknown field '{key}': {hint}. To drop unknown fields instead, create the writer with IgnoreUnknownFields"
    ))
}
