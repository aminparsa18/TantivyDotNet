//! The query JSON exchanged with .NET: our own small, versioned (with `ABI_VERSION`) description
//! of a query tree, built into a Tantivy `Box<dyn Query>` here. The managed `Query` classes
//! serialize to it.
//!
//! ```json
//! {"type":"boolean",
//!  "must":[{"type":"phrase","field":"body","text":"of mice","slop":1}],
//!  "should":[{"type":"fuzzy","field":"title","value":"mise","distance":1}],
//!  "must_not":[{"type":"term","field":"year","value":{"u64":1937}}]}
//! ```
//!
//! Term values are tagged with their kind (`str`, `u64`, `i64`, `f64`, `bool`, `date` as Unix
//! microseconds, `bytes` as an array of numbers, `facet` as a path) and converted to the field's type when that is
//! lossless, so `Term("year", 1937)` (an `i64` in C#) works on a `u64` field.

use std::collections::BTreeMap;
use std::ops::Bound;

use serde::Deserialize;
use tantivy::query::{
    AllQuery, BooleanQuery, BoostQuery, EmptyQuery, FuzzyTermQuery, Occur, PhraseQuery, Query,
    QueryParser, RangeQuery, TermQuery,
};
use tantivy::schema::{Field, FieldType, IndexRecordOption, Schema};
use tantivy::{DateTime, Index, Term};

use crate::ffi::{self, FfiError, FfiResult};

#[derive(Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum QuerySpec {
    All,
    Term {
        field: String,
        value: ValueSpec,
    },
    Fuzzy {
        field: String,
        value: String,
        distance: u8,
        #[serde(default = "default_true")]
        transpositions: bool,
        #[serde(default)]
        prefix: bool,
    },
    Phrase {
        field: String,
        text: String,
        #[serde(default)]
        slop: u32,
    },
    Boolean {
        #[serde(default)]
        must: Vec<QuerySpec>,
        #[serde(default)]
        should: Vec<QuerySpec>,
        #[serde(default)]
        must_not: Vec<QuerySpec>,
    },
    Range {
        field: String,
        lower: Option<BoundSpec>,
        upper: Option<BoundSpec>,
    },
    Boost {
        query: Box<QuerySpec>,
        factor: f32,
    },
    Parse {
        text: String,
        default_fields: Option<Vec<String>>,
        /// Combine unprefixed terms with AND instead of OR.
        #[serde(default)]
        conjunction: bool,
        #[serde(default)]
        boosts: BTreeMap<String, f32>,
        #[serde(default)]
        fuzzy: BTreeMap<String, FuzzySpec>,
        /// Drop unparsable parts instead of failing.
        #[serde(default)]
        lenient: bool,
    },
}

#[derive(Deserialize)]
pub struct FuzzySpec {
    distance: u8,
    #[serde(default = "default_true")]
    transpositions: bool,
    #[serde(default)]
    prefix: bool,
}

#[derive(Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ValueSpec {
    Str(String),
    U64(u64),
    I64(i64),
    F64(f64),
    Bool(bool),
    /// Unix timestamp in microseconds (UTC).
    Date(i64),
    Bytes(Vec<u8>),
    /// A facet path; only valid on facet fields (unlike `Str`, which also is).
    Facet(String),
}

#[derive(Deserialize)]
pub struct BoundSpec {
    value: ValueSpec,
    inclusive: bool,
}

fn default_true() -> bool {
    true
}

/// Fuzzy distances Tantivy builds Levenshtein automata for.
const MAX_FUZZY_DISTANCE: u8 = 2;

pub fn build(spec: &QuerySpec, index: &Index) -> FfiResult<Box<dyn Query>> {
    let schema = index.schema();
    Ok(match spec {
        QuerySpec::All => Box::new(AllQuery),

        QuerySpec::Term { field, value } => {
            let (f, ft) = indexed_field(&schema, field)?;
            let term = make_term(f, field, ft, value)?;
            Box::new(TermQuery::new(term, record_option(ft)))
        }

        QuerySpec::Fuzzy { field, value, distance, transpositions, prefix } => {
            let (f, ft) = indexed_field(&schema, field)?;
            require_text(field, ft, "fuzzy")?;
            if *distance > MAX_FUZZY_DISTANCE {
                return Err(FfiError::invalid(format!(
                    "fuzzy distance must be between 0 and {MAX_FUZZY_DISTANCE}, got {distance}"
                )));
            }
            let term = Term::from_field_text(f, value);
            Box::new(if *prefix {
                FuzzyTermQuery::new_prefix(term, *distance, *transpositions)
            } else {
                FuzzyTermQuery::new(term, *distance, *transpositions)
            })
        }

        QuerySpec::Phrase { field, text, slop } => {
            let (f, ft) = indexed_field(&schema, field)?;
            require_text(field, ft, "phrase")?;
            let has_positions = match ft {
                FieldType::Str(o) => o.get_indexing_options().is_some_and(|i| i.index_option().has_positions()),
                _ => false,
            };
            if !has_positions {
                return Err(FfiError::invalid(format!(
                    "field '{field}' is not indexed with positions, so it can't be used in a phrase query"
                )));
            }
            // Run the text through the field's own tokenizer, so "Of Mice" matches what was indexed.
            let mut analyzer = index.tokenizer_for_field(f)?;
            let mut stream = analyzer.token_stream(text);
            let mut terms = Vec::new();
            while stream.advance() {
                let token = stream.token();
                terms.push((token.position, Term::from_field_text(f, &token.text)));
            }
            match terms.len() {
                0 => Box::new(EmptyQuery),
                1 => Box::new(TermQuery::new(terms.pop().unwrap().1, IndexRecordOption::WithFreqs)),
                _ => Box::new(PhraseQuery::new_with_offset_and_slop(terms, *slop)),
            }
        }

        QuerySpec::Boolean { must, should, must_not } => {
            let mut clauses = Vec::with_capacity(must.len() + should.len() + must_not.len() + 1);
            for (occur, specs) in [(Occur::Must, must), (Occur::Should, should), (Occur::MustNot, must_not)] {
                for s in specs {
                    clauses.push((occur, build(s, index)?));
                }
            }
            // A purely negative query means "everything except": Tantivy would match nothing.
            if must.is_empty() && should.is_empty() && !must_not.is_empty() {
                clauses.push((Occur::Must, Box::new(AllQuery)));
            }
            Box::new(BooleanQuery::new(clauses))
        }

        QuerySpec::Range { field, lower, upper } => {
            let f = schema.get_field(field)?;
            let entry = schema.get_field_entry(f);
            if !entry.is_indexed() && !entry.is_fast() {
                return Err(FfiError::invalid(format!(
                    "field '{field}' is neither indexed nor fast, so it can't be used in a range query"
                )));
            }
            let ft = entry.field_type();
            if lower.is_none() && upper.is_none() {
                return Err(FfiError::invalid("a range query needs at least one bound"));
            }
            let bound = |b: &Option<BoundSpec>| -> FfiResult<Bound<Term>> {
                Ok(match b {
                    None => Bound::Unbounded,
                    Some(b) => {
                        let term = make_term(f, field, ft, &b.value)?;
                        if b.inclusive { Bound::Included(term) } else { Bound::Excluded(term) }
                    }
                })
            };
            Box::new(RangeQuery::new(bound(lower)?, bound(upper)?))
        }

        QuerySpec::Boost { query, factor } => {
            if !factor.is_finite() {
                return Err(FfiError::invalid(format!("boost factor must be finite, got {factor}")));
            }
            Box::new(BoostQuery::new(build(query, index)?, *factor))
        }

        QuerySpec::Parse { text, default_fields, conjunction, boosts, fuzzy, lenient } => {
            let fields = match default_fields {
                None => all_indexed_text_fields(&schema),
                Some(names) => names.iter().map(|n| schema.get_field(n)).collect::<Result<Vec<_>, _>>()?,
            };
            let mut parser = QueryParser::for_index(index, fields);
            if *conjunction {
                parser.set_conjunction_by_default();
            }
            for (name, boost) in boosts {
                if !boost.is_finite() {
                    return Err(FfiError::invalid(format!("boost for field '{name}' must be finite, got {boost}")));
                }
                parser.set_field_boost(schema.get_field(name)?, *boost);
            }
            for (name, f) in fuzzy {
                let (field, ft) = indexed_field(&schema, name)?;
                require_text(name, ft, "fuzzy")?;
                if f.distance > MAX_FUZZY_DISTANCE {
                    return Err(FfiError::invalid(format!(
                        "fuzzy distance must be between 0 and {MAX_FUZZY_DISTANCE}, got {}",
                        f.distance
                    )));
                }
                parser.set_field_fuzzy(field, f.prefix, f.distance, f.transpositions);
            }
            if *lenient {
                parser.parse_query_lenient(text).0
            } else {
                parser.parse_query(text).map_err(|e| FfiError::new(ffi::status::QUERY_PARSE, e.to_string()))?
            }
        }
    })
}

/// The term of a `{"type":"term",...}` spec, for the writer's delete/update-by-term operations.
pub fn term_of(spec: &QuerySpec, schema: &Schema) -> FfiResult<Term> {
    match spec {
        QuerySpec::Term { field, value } => {
            let (f, ft) = indexed_field(schema, field)?;
            make_term(f, field, ft, value)
        }
        _ => Err(FfiError::invalid("expected a term query")),
    }
}

pub fn parse_spec(json: &str) -> FfiResult<QuerySpec> {
    serde_json::from_str(json).map_err(|e| FfiError::invalid(format!("invalid query JSON: {e}")))
}

pub fn all_indexed_text_fields(schema: &Schema) -> Vec<Field> {
    schema
        .fields()
        .filter(|(_, e)| matches!(e.field_type(), FieldType::Str(_)) && e.is_indexed())
        .map(|(f, _)| f)
        .collect()
}

fn indexed_field<'a>(schema: &'a Schema, name: &str) -> FfiResult<(Field, &'a FieldType)> {
    let field = schema.get_field(name)?;
    let entry = schema.get_field_entry(field);
    if !entry.is_indexed() {
        return Err(FfiError::invalid(format!("field '{name}' is not indexed")));
    }
    Ok((field, entry.field_type()))
}

fn require_text(name: &str, ft: &FieldType, what: &str) -> FfiResult<()> {
    match ft {
        FieldType::Str(_) => Ok(()),
        _ => Err(FfiError::invalid(format!(
            "a {what} query needs a text field, but '{name}' is {}",
            type_name(ft)
        ))),
    }
}

/// Text fields are scored with term frequencies; other indexed fields only record presence.
fn record_option(ft: &FieldType) -> IndexRecordOption {
    match ft {
        FieldType::Str(_) => IndexRecordOption::WithFreqs,
        _ => IndexRecordOption::Basic,
    }
}

/// Builds a term for `field`, converting `value` to the field's type when that's lossless.
fn make_term(field: Field, name: &str, ft: &FieldType, value: &ValueSpec) -> FfiResult<Term> {
    use ValueSpec as V;
    let term = match (ft, value) {
        (FieldType::Str(_), V::Str(s)) => Term::from_field_text(field, s),
        (FieldType::U64(_), V::U64(n)) => Term::from_field_u64(field, *n),
        (FieldType::U64(_), V::I64(n)) if *n >= 0 => Term::from_field_u64(field, *n as u64),
        (FieldType::I64(_), V::I64(n)) => Term::from_field_i64(field, *n),
        (FieldType::I64(_), V::U64(n)) if *n <= i64::MAX as u64 => Term::from_field_i64(field, *n as i64),
        (FieldType::F64(_), V::F64(x)) => Term::from_field_f64(field, *x),
        (FieldType::F64(_), V::I64(n)) => Term::from_field_f64(field, *n as f64),
        (FieldType::F64(_), V::U64(n)) => Term::from_field_f64(field, *n as f64),
        (FieldType::Bool(_), V::Bool(b)) => Term::from_field_bool(field, *b),
        (FieldType::Date(_), V::Date(us)) => {
            Term::from_field_date_for_search(field, DateTime::from_timestamp_micros(*us))
        }
        (FieldType::Bytes(_), V::Bytes(b)) => Term::from_field_bytes(field, b),
        (FieldType::Facet(_), V::Str(s) | V::Facet(s)) => Term::from_facet(field, &crate::facets::parse_facet(s)?),
        _ => {
            return Err(FfiError::invalid(format!(
                "field '{name}' is {} and can't be queried with {}",
                type_name(ft),
                value_name(value)
            )))
        }
    };
    Ok(term)
}

fn type_name(ft: &FieldType) -> &'static str {
    match ft {
        FieldType::Str(_) => "a text field",
        FieldType::U64(_) => "a u64 field",
        FieldType::I64(_) => "an i64 field",
        FieldType::F64(_) => "an f64 field",
        FieldType::Bool(_) => "a bool field",
        FieldType::Date(_) => "a date field",
        FieldType::Bytes(_) => "a bytes field",
        FieldType::Facet(_) => "a facet field",
        _ => "a field type that is not supported here",
    }
}

fn value_name(v: &ValueSpec) -> String {
    match v {
        ValueSpec::Str(_) => "a string".into(),
        ValueSpec::U64(n) => format!("the unsigned integer {n}"),
        ValueSpec::I64(n) => format!("the integer {n}"),
        ValueSpec::F64(x) => format!("the number {x}"),
        ValueSpec::Bool(_) => "a bool".into(),
        ValueSpec::Date(_) => "a date".into(),
        ValueSpec::Bytes(_) => "bytes".into(),
        ValueSpec::Facet(p) => format!("the facet path '{p}'"),
    }
}
