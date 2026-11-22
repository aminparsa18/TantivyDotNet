//! The schema JSON exchanged with .NET. It is deliberately our own small format rather than
//! Tantivy's serde representation, so the managed side doesn't break when Tantivy's internal
//! option layout changes.
//!
//! ```json
//! {"fields": [{"name":"title","type":"text","stored":true,"indexed":true,"fast":false,"tokenizer":"default"}],
//!  "analyzers": {"name": {...}}}
//! ```
//! (`analyzers` is optional; see `analyzers`).

use serde::{Deserialize, Serialize};
use tantivy::schema::{
    BytesOptions, DateOptions, FacetOptions, FieldType, IndexRecordOption, IpAddrOptions, JsonObjectOptions,
    NumericOptions, Schema, TextFieldIndexing, TextOptions,
};

use crate::analyzers::{self, Analyzers};
use crate::ffi::{FfiError, FfiResult};

#[derive(Serialize, Deserialize)]
struct SchemaSpec {
    fields: Vec<FieldSpec>,
    #[serde(default)]
    analyzers: Analyzers,
}

#[derive(Serialize, Deserialize)]
pub struct FieldSpec {
    pub name: String,
    #[serde(rename = "type")]
    pub kind: String,
    #[serde(default)]
    pub stored: bool,
    #[serde(default = "default_true")]
    pub indexed: bool,
    #[serde(default)]
    pub fast: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tokenizer: Option<String>,
}

fn default_true() -> bool {
    true
}

/// Parses schema JSON into a Tantivy schema and the custom analyzers its text fields may use.
pub fn schema_from_json(json: &str) -> FfiResult<(Schema, Analyzers)> {
    let spec: SchemaSpec =
        serde_json::from_str(json).map_err(|e| FfiError::invalid(format!("invalid schema JSON: {e}")))?;
    let specs = &spec.fields;
    let used = specs
        .iter()
        .filter(|f| (f.kind == "text" || f.kind == "json") && f.indexed)
        .map(|f| (f.name.as_str(), f.tokenizer.as_deref().unwrap_or("default")));
    analyzers::validate(&spec.analyzers, used)?;
    if specs.is_empty() {
        return Err(FfiError::invalid("schema has no fields"));
    }
    let mut builder = Schema::builder();
    for spec in specs {
        if is_duplicate(specs, &spec.name) {
            return Err(FfiError::invalid(format!("duplicate field name '{}'", spec.name)));
        }
        let name = spec.name.as_str();
        // Tantivy asserts on these (a panic), so check first.
        if !tantivy::schema::is_valid_field_name(name) {
            return Err(FfiError::invalid(format!("invalid field name '{name}': it can't be empty or start with '-'")));
        }
        match spec.kind.as_str() {
            "text" => {
                let mut opts = TextOptions::default();
                if spec.indexed {
                    let tokenizer = spec.tokenizer.as_deref().unwrap_or("default");
                    opts = opts.set_indexing_options(
                        TextFieldIndexing::default()
                            .set_tokenizer(tokenizer)
                            .set_index_option(IndexRecordOption::WithFreqsAndPositions),
                    );
                }
                if spec.stored {
                    opts = opts.set_stored();
                }
                if spec.fast {
                    opts = opts.set_fast("raw");
                }
                builder.add_text_field(name, opts);
            }
            "u64" => {
                builder.add_u64_field(name, numeric(spec));
            }
            "i64" => {
                builder.add_i64_field(name, numeric(spec));
            }
            "f64" => {
                builder.add_f64_field(name, numeric(spec));
            }
            "bool" => {
                builder.add_bool_field(name, numeric(spec));
            }
            "date" => {
                let mut opts = DateOptions::default();
                if spec.stored {
                    opts = opts.set_stored();
                }
                if spec.indexed {
                    opts = opts.set_indexed();
                }
                if spec.fast {
                    opts = opts.set_fast();
                }
                builder.add_date_field(name, opts);
            }
            "facet" => {
                // Facets are always indexed and fast in Tantivy; only `stored` is optional.
                let mut opts = FacetOptions::default();
                if spec.stored {
                    opts = opts.set_stored();
                }
                builder.add_facet_field(name, opts);
            }
            "json" => {
                let mut opts = JsonObjectOptions::default();
                if spec.indexed {
                    let tokenizer = spec.tokenizer.as_deref().unwrap_or("default");
                    opts = opts.set_indexing_options(
                        TextFieldIndexing::default()
                            .set_tokenizer(tokenizer)
                            .set_index_option(IndexRecordOption::WithFreqsAndPositions),
                    );
                }
                if spec.stored {
                    opts = opts.set_stored();
                }
                if spec.fast {
                    opts = opts.set_fast("raw");
                }
                builder.add_json_field(name, opts);
            }
            "ip" => {
                let mut opts = IpAddrOptions::default();
                if spec.stored {
                    opts = opts.set_stored();
                }
                if spec.indexed {
                    opts = opts.set_indexed();
                }
                if spec.fast {
                    opts = opts.set_fast();
                }
                builder.add_ip_addr_field(name, opts);
            }
            "bytes" => {
                let mut opts = BytesOptions::default();
                if spec.stored {
                    opts = opts.set_stored();
                }
                if spec.indexed {
                    opts = opts.set_indexed();
                }
                if spec.fast {
                    opts = opts.set_fast();
                }
                builder.add_bytes_field(name, opts);
            }
            other => return Err(FfiError::invalid(format!("field '{name}': unsupported field type '{other}'"))),
        }
    }
    Ok((builder.build(), spec.analyzers))
}

/// True if `name` appears more than once in `specs` (checked once per field, so O(n^2) on
/// schemas that are at most a few hundred fields).
fn is_duplicate(specs: &[FieldSpec], name: &str) -> bool {
    specs.iter().filter(|s| s.name == name).count() > 1
}

fn numeric(spec: &FieldSpec) -> NumericOptions {
    let mut opts = NumericOptions::default();
    if spec.stored {
        opts = opts.set_stored();
    }
    if spec.indexed {
        opts = opts.set_indexed();
    }
    if spec.fast {
        opts = opts.set_fast();
    }
    opts
}

pub fn schema_to_json(schema: &Schema, analyzers: &Analyzers) -> String {
    let fields: Vec<FieldSpec> = schema
        .fields()
        .map(|(_, entry)| {
            let ft = entry.field_type();
            let (kind, tokenizer) = match ft {
                FieldType::Str(opts) => ("text", opts.get_indexing_options().map(|i| i.tokenizer().to_owned())),
                FieldType::U64(_) => ("u64", None),
                FieldType::I64(_) | FieldType::TieBreaker => ("i64", None),
                FieldType::F64(_) => ("f64", None),
                FieldType::Bool(_) => ("bool", None),
                FieldType::Date(_) => ("date", None),
                FieldType::Facet(_) => ("facet", None),
                FieldType::Bytes(_) => ("bytes", None),
                FieldType::JsonObject(opts) => {
                    ("json", opts.get_text_indexing_options().map(|i| i.tokenizer().to_owned()))
                }
                FieldType::IpAddr(_) => ("ip", None),
                FieldType::Custom(_) => ("custom", None),
            };
            FieldSpec {
                name: entry.name().to_owned(),
                kind: kind.to_owned(),
                stored: entry.is_stored(),
                indexed: entry.is_indexed(),
                fast: entry.is_fast(),
                tokenizer,
            }
        })
        .collect();
    serde_json::to_string(&SchemaSpec { fields, analyzers: analyzers.clone() })
        .expect("schema spec serialization cannot fail")
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi::status;
    use serde_json::{json, Value};

    fn round_trip(json: &str) -> Value {
        let (schema, analyzers) = schema_from_json(json).unwrap_or_else(|e| panic!("{json}: {}", e.message));
        serde_json::from_str(&schema_to_json(&schema, &analyzers)).unwrap()
    }

    fn error(json: &str) -> String {
        match schema_from_json(json) {
            Ok(_) => panic!("{json} was accepted"),
            Err(e) => {
                assert_eq!(e.code, status::INVALID_ARGUMENT, "{}", e.message);
                e.message
            }
        }
    }

    #[test]
    fn every_field_type_round_trips() {
        let input = json!({"fields": [
            {"name": "title", "type": "text", "stored": true, "indexed": true, "fast": false, "tokenizer": "en_stem"},
            {"name": "id", "type": "text", "stored": true, "indexed": true, "fast": true, "tokenizer": "raw"},
            {"name": "u", "type": "u64", "stored": true, "indexed": true, "fast": true},
            {"name": "i", "type": "i64", "stored": false, "indexed": true, "fast": false},
            {"name": "f", "type": "f64", "stored": true, "indexed": false, "fast": true},
            {"name": "b", "type": "bool", "stored": true, "indexed": true, "fast": false},
            {"name": "d", "type": "date", "stored": true, "indexed": true, "fast": true},
            {"name": "c", "type": "facet", "stored": true, "indexed": true, "fast": true},
            {"name": "j", "type": "json", "stored": true, "indexed": true, "fast": false, "tokenizer": "default"},
            {"name": "ip", "type": "ip", "stored": true, "indexed": false, "fast": true},
            {"name": "y", "type": "bytes", "stored": true, "indexed": true, "fast": false},
        ]});
        let output = round_trip(&input.to_string());
        assert_eq!(output["fields"], input["fields"]);
        // And the output is valid input describing the same schema.
        assert_eq!(round_trip(&output.to_string()), output);
    }

    #[test]
    fn defaults_are_indexed_not_stored_not_fast() {
        let out = round_trip(r#"{"fields":[{"name":"t","type":"text"},{"name":"n","type":"u64"}]}"#);
        assert_eq!(
            out["fields"],
            json!([
                {"name": "t", "type": "text", "stored": false, "indexed": true, "fast": false, "tokenizer": "default"},
                {"name": "n", "type": "u64", "stored": false, "indexed": true, "fast": false},
            ])
        );
    }

    #[test]
    fn unindexed_text_has_no_tokenizer() {
        let out =
            round_trip(r#"{"fields":[{"name":"t","type":"text","stored":true,"indexed":false,"tokenizer":"raw"}]}"#);
        assert_eq!(
            out["fields"][0],
            json!({"name": "t", "type": "text", "stored": true, "indexed": false, "fast": false})
        );
    }

    #[test]
    fn facets_are_always_indexed_and_fast() {
        let out = round_trip(r#"{"fields":[{"name":"c","type":"facet","indexed":false,"fast":false}]}"#);
        assert_eq!(out["fields"][0]["indexed"], true);
        assert_eq!(out["fields"][0]["fast"], true);
    }

    #[test]
    fn custom_analyzers_round_trip() {
        let input = json!({
            "fields": [{"name": "t", "type": "text", "stored": false, "indexed": true, "fast": false, "tokenizer": "folded"}],
            "analyzers": {"folded": {"tokenizer": {"type": "simple"}, "filters": [{"type": "lowercase"}, {"type": "ascii_folding"}]}},
        });
        assert_eq!(round_trip(&input.to_string()), input);
    }

    #[test]
    fn invalid_schemas_are_rejected() {
        assert!(error("").contains("invalid schema JSON"));
        assert!(error("[]").contains("invalid schema JSON"));
        assert!(error(r#"{"fields":[]}"#).contains("no fields"));
        assert!(error(r#"{"fields":[{"name":"a","type":"u64"},{"name":"a","type":"text"}]}"#).contains("duplicate"));
        assert!(error(r#"{"fields":[{"name":"a","type":"u32"}]}"#).contains("unsupported field type 'u32'"));
        assert!(error(r#"{"fields":[{"type":"u64"}]}"#).contains("missing field `name`"));
        assert!(error(r#"{"fields":[{"name":"a","type":"u64","stored":"yes"}]}"#).contains("invalid type"));
        assert!(error(r#"{"fields":[{"name":"t","type":"text","tokenizer":"nope"}]}"#).contains("nope"));
    }

    #[test]
    fn field_names_tantivy_rejects_are_errors_not_panics() {
        // Tantivy asserts on these in its schema builder.
        for name in ["", "-a"] {
            let json = json!({"fields": [{"name": name, "type": "u64"}]}).to_string();
            assert!(error(&json).contains("invalid field name"), "{name:?}");
        }
        for name in ["a-b", "$a", "シャボン玉", "a b"] {
            round_trip(&json!({"fields": [{"name": name, "type": "u64"}]}).to_string());
        }
    }
}
