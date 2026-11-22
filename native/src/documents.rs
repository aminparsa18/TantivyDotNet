//! Parsing documents given as JSON objects (field name -> value or array of values).

use serde_json::{Map, Value};
use tantivy::schema::{Facet, FieldType, Schema};
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
    for (key, value) in &object {
        if let Ok(field) = schema.get_field(key) {
            check_facets(key, schema.get_field_entry(field).field_type(), value)?;
        }
    }
    TantivyDocument::from_json_object(schema, object).map_err(|e| FfiError::invalid(format!("invalid document: {e}")))
}

/// Tantivy converts a facet field's JSON strings with `Facet::from`, which panics on a string that
/// isn't a facet path; this turns that into an error first. `value` is one value or an array.
pub fn check_facets(name: &str, ft: &FieldType, value: &Value) -> FfiResult<()> {
    if !matches!(ft, FieldType::Facet(_)) {
        return Ok(());
    }
    let values = match value {
        Value::Array(items) => items.as_slice(),
        single => std::slice::from_ref(single),
    };
    for v in values {
        if let Value::String(text) = v {
            if Facet::from_text(text).is_err() {
                return Err(FfiError::invalid(format!(
                    "field '{name}': '{text}' is not a facet path (paths start with '/', like \"/genre/fiction\")"
                )));
            }
        }
    }
    Ok(())
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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi::status;

    fn schema() -> Schema {
        crate::schema_spec::schema_from_json(
            r#"{"fields":[{"name":"Title","type":"text"},{"name":"c","type":"facet"},{"name":"n","type":"u64"}]}"#,
        )
        .unwrap()
        .0
    }

    fn error(json: &str) -> String {
        let e = parse(&schema(), json, false).err().unwrap_or_else(|| panic!("{json} was accepted"));
        assert_eq!(e.code, status::INVALID_ARGUMENT);
        e.message
    }

    #[test]
    fn facet_values_that_are_not_paths_are_errors() {
        // Tantivy's conversion panics on these.
        assert!(error(r#"{"c":"fiction"}"#).contains("'fiction' is not a facet path"));
        assert!(error(r#"{"c":["/ok",""]}"#).contains("'' is not a facet path"));
        assert!(parse(&schema(), r#"{"c":["/a/b","/"]}"#, false).is_ok());
    }

    #[test]
    fn unknown_fields_are_rejected_with_a_hint() {
        assert!(error(r#"{"title":"x"}"#).contains("did you mean 'Title'?"));
        assert!(error(r#"{"zzz":1}"#).contains("the schema's fields are: Title, c, n"));
        assert!(parse(&schema(), r#"{"zzz":1,"n":2}"#, true).is_ok());
    }

    #[test]
    fn malformed_documents_are_errors() {
        for json in ["", "[]", "1", "null", r#"{"n":"x"}"#, r#"{"n":-1}"#, r#"{"n":1.5}"#, r#"{"Title":{"a":1}}"#, "{"]
        {
            assert!(error(json).contains("invalid document"), "{json}");
        }
    }
}
