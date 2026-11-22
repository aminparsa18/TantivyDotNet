//! The schema JSON exchanged with .NET. It is deliberately our own small format rather than
//! Tantivy's serde representation, so the managed side doesn't break when Tantivy's internal
//! option layout changes.
//!
//! ```json
//! [{"name":"title","type":"text","stored":true,"indexed":true,"fast":false,"tokenizer":"default"}]
//! ```

use serde::{Deserialize, Serialize};
use tantivy::schema::{
    BytesOptions, DateOptions, FacetOptions, FieldType, IndexRecordOption, NumericOptions, Schema,
    TextFieldIndexing, TextOptions,
};

use crate::ffi::{FfiError, FfiResult};

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

pub fn schema_from_json(json: &str) -> FfiResult<Schema> {
    let specs: Vec<FieldSpec> = serde_json::from_str(json)
        .map_err(|e| FfiError::invalid(format!("invalid schema JSON: {e}")))?;
    if specs.is_empty() {
        return Err(FfiError::invalid("schema has no fields"));
    }
    let mut builder = Schema::builder();
    for spec in &specs {
        if is_duplicate(&specs, &spec.name) {
            return Err(FfiError::invalid(format!("duplicate field name '{}'", spec.name)));
        }
        let name = spec.name.as_str();
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
            other => {
                return Err(FfiError::invalid(format!(
                    "field '{name}': unsupported field type '{other}'"
                )))
            }
        }
    }
    Ok(builder.build())
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

pub fn schema_to_json(schema: &Schema) -> String {
    let specs: Vec<FieldSpec> = schema
        .fields()
        .map(|(_, entry)| {
            let ft = entry.field_type();
            let (kind, tokenizer) = match ft {
                FieldType::Str(opts) => (
                    "text",
                    opts.get_indexing_options().map(|i| i.tokenizer().to_owned()),
                ),
                FieldType::U64(_) => ("u64", None),
                FieldType::I64(_) | FieldType::TieBreaker => ("i64", None),
                FieldType::F64(_) => ("f64", None),
                FieldType::Bool(_) => ("bool", None),
                FieldType::Date(_) => ("date", None),
                FieldType::Facet(_) => ("facet", None),
                FieldType::Bytes(_) => ("bytes", None),
                FieldType::JsonObject(_) => ("json", None),
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
    serde_json::to_string(&specs).expect("schema spec serialization cannot fail")
}
