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
use tantivy::columnar::NumericalValue;
use tantivy::query::{
    AllQuery, BooleanQuery, BoostQuery, ConstScoreQuery, DisjunctionMaxQuery, EmptyQuery, ExistsQuery, FuzzyTermQuery,
    MoreLikeThisQuery, Occur, PhraseQuery, Query, QueryParser, RangeQuery, RegexQuery, TermQuery, TermSetQuery,
};
use tantivy::schema::{Field, FieldType, IndexRecordOption, OwnedValue, Schema};
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
    Regex {
        field: String,
        pattern: String,
    },
    Exists {
        field: String,
    },
    TermSet {
        field: String,
        values: Vec<ValueSpec>,
    },
    DisjunctionMax {
        queries: Vec<QuerySpec>,
        #[serde(default)]
        tie_breaker: f32,
    },
    ConstScore {
        query: Box<QuerySpec>,
        score: f32,
    },
    MoreLikeThis {
        /// Field name -> JSON values, like a document (`{"body": ["text"], "year": [1937]}`).
        document: serde_json::Map<String, serde_json::Value>,
        #[serde(default)]
        options: MoreLikeThisSpec,
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

#[derive(Deserialize, Default)]
pub struct MoreLikeThisSpec {
    min_doc_frequency: Option<u64>,
    max_doc_frequency: Option<u64>,
    min_term_frequency: Option<usize>,
    max_query_terms: Option<usize>,
    min_word_length: Option<usize>,
    max_word_length: Option<usize>,
    boost_factor: Option<f32>,
    stop_words: Option<Vec<String>>,
}

#[derive(Deserialize)]
pub struct FuzzySpec {
    distance: u8,
    #[serde(default = "default_true")]
    transpositions: bool,
    #[serde(default)]
    prefix: bool,
}

#[derive(Deserialize, Clone)]
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
    /// An IPv4 or IPv6 address in text form.
    Ip(String),
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
            let target = term_target(&schema, field)?;
            Box::new(TermQuery::new(target.term(field, value)?, target.record_option()))
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
            let (f, path) =
                schema.find_field(field).ok_or_else(|| FfiError::invalid(format!("unknown field '{field}'")))?;
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
            let (lower_value, upper_value) = (lower.as_ref().map(|b| &b.value), upper.as_ref().map(|b| &b.value));
            let terms = match ft {
                FieldType::JsonObject(o) => {
                    if path.is_empty() {
                        return Err(FfiError::invalid(format!(
                            "'{field}' is a JSON field: query a path inside it, like '{field}.key'"
                        )));
                    }
                    let target =
                        Target::Json { field: f, path: path.to_owned(), expand_dots: o.is_expand_dots_enabled() };
                    let (lower_value, upper_value, as_f64) = unify_json_bounds(field, lower_value, upper_value)?;
                    let term = |v: Option<ValueSpec>| v.map(|v| target.json_term(field, &v, !as_f64)).transpose();
                    (term(lower_value)?, term(upper_value)?)
                }
                _ => {
                    let term = |v: Option<&ValueSpec>| v.map(|v| make_term(f, field, ft, v)).transpose();
                    (term(lower_value)?, term(upper_value)?)
                }
            };
            let bound = |b: &Option<BoundSpec>, term: Option<Term>| match (b, term) {
                (Some(b), Some(t)) if b.inclusive => Bound::Included(t),
                (Some(_), Some(t)) => Bound::Excluded(t),
                _ => Bound::Unbounded,
            };
            Box::new(RangeQuery::new(bound(lower, terms.0), bound(upper, terms.1)))
        }

        QuerySpec::Boost { query, factor } => {
            if !factor.is_finite() {
                return Err(FfiError::invalid(format!("boost factor must be finite, got {factor}")));
            }
            Box::new(BoostQuery::new(build(query, index)?, *factor))
        }

        QuerySpec::Regex { field, pattern } => {
            let (f, ft) = indexed_field(&schema, field)?;
            require_text(field, ft, "regex")?;
            Box::new(
                RegexQuery::from_pattern(pattern, f).map_err(|e| FfiError::invalid(format!("invalid regex: {e}")))?,
            )
        }

        QuerySpec::Exists { field } => {
            let (f, _) =
                schema.find_field(field).ok_or_else(|| FfiError::invalid(format!("unknown field '{field}'")))?;
            if !schema.get_field_entry(f).is_fast() {
                return Err(FfiError::invalid(format!(
                    "field '{field}' isn't a fast field, so an exists query can't check it (create it with fast: true)"
                )));
            }
            Box::new(ExistsQuery::new(field.clone(), true))
        }

        QuerySpec::TermSet { field, values } => {
            let target = term_target(&schema, field)?;
            let terms = values.iter().map(|v| target.term(field, v)).collect::<FfiResult<Vec<_>>>()?;
            Box::new(TermSetQuery::new(terms))
        }

        QuerySpec::DisjunctionMax { queries, tie_breaker } => {
            if !tie_breaker.is_finite() || *tie_breaker < 0.0 || *tie_breaker > 1.0 {
                return Err(FfiError::invalid(format!("tie breaker must be between 0 and 1, got {tie_breaker}")));
            }
            let disjuncts = queries.iter().map(|q| build(q, index)).collect::<FfiResult<Vec<_>>>()?;
            Box::new(DisjunctionMaxQuery::with_tie_breaker(disjuncts, *tie_breaker))
        }

        QuerySpec::ConstScore { query, score } => {
            if !score.is_finite() {
                return Err(FfiError::invalid(format!("constant score must be finite, got {score}")));
            }
            Box::new(ConstScoreQuery::new(build(query, index)?, *score))
        }

        QuerySpec::MoreLikeThis { document, options } => {
            let mut fields = Vec::with_capacity(document.len());
            for (name, json) in document {
                let field = schema.get_field(name)?;
                let ft = schema.get_field_entry(field).field_type();
                crate::documents::check_facets(name, ft, json)?;
                let items = match json {
                    serde_json::Value::Array(items) => items.clone(),
                    other => vec![other.clone()],
                };
                let values = items
                    .into_iter()
                    .map(|v| ft.value_from_json(v).map_err(|e| FfiError::invalid(format!("field '{name}': {e}"))))
                    .collect::<FfiResult<Vec<OwnedValue>>>()?;
                fields.push((field, values));
            }
            let o = options;
            let mut b = MoreLikeThisQuery::builder();
            if let Some(v) = o.min_doc_frequency {
                b = b.with_min_doc_frequency(v);
            }
            if let Some(v) = o.max_doc_frequency {
                b = b.with_max_doc_frequency(v);
            }
            if let Some(v) = o.min_term_frequency {
                b = b.with_min_term_frequency(v);
            }
            if let Some(v) = o.max_query_terms {
                b = b.with_max_query_terms(v);
            }
            if let Some(v) = o.min_word_length {
                b = b.with_min_word_length(v);
            }
            if let Some(v) = o.max_word_length {
                b = b.with_max_word_length(v);
            }
            if let Some(v) = o.boost_factor {
                b = b.with_boost_factor(v);
            }
            if let Some(v) = &o.stop_words {
                b = b.with_stop_words(v.clone());
            }
            Box::new(b.with_document_fields(fields))
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
        QuerySpec::Term { field, value } => term_target(schema, field)?.term(field, value),
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
        _ => Err(FfiError::invalid(format!("a {what} query needs a text field, but '{name}' is {}", type_name(ft)))),
    }
}

/// What a term query's field name refers to: a whole field, or a path inside a JSON field
/// (`attrs.color`, found with `Schema::find_field`).
enum Target<'a> {
    Plain(Field, &'a FieldType),
    Json { field: Field, path: String, expand_dots: bool },
}

fn term_target<'a>(schema: &'a Schema, name: &str) -> FfiResult<Target<'a>> {
    let (field, path) = schema.find_field(name).ok_or_else(|| FfiError::invalid(format!("unknown field '{name}'")))?;
    let entry = schema.get_field_entry(field);
    if !entry.is_indexed() {
        return Err(FfiError::invalid(format!("field '{}' is not indexed", entry.name())));
    }
    match entry.field_type() {
        FieldType::JsonObject(o) => {
            if path.is_empty() {
                return Err(FfiError::invalid(format!(
                    "'{name}' is a JSON field: query a path inside it, like '{name}.key'"
                )));
            }
            Ok(Target::Json { field, path: path.to_owned(), expand_dots: o.is_expand_dots_enabled() })
        }
        ft => Ok(Target::Plain(field, ft)),
    }
}

impl Target<'_> {
    fn term(&self, name: &str, value: &ValueSpec) -> FfiResult<Term> {
        match self {
            Target::Plain(f, ft) => make_term(*f, name, ft, value),
            Target::Json { .. } => self.json_term(name, value, true),
        }
    }

    /// A term for a JSON path. With `normalize`, numbers are normalized as Tantivy does when
    /// indexing JSON (i64 when it fits), which a term must match; a range can skip it.
    fn json_term(&self, name: &str, value: &ValueSpec, normalize: bool) -> FfiResult<Term> {
        match self {
            Target::Plain(..) => unreachable!("json_term on a plain field"),
            Target::Json { field, path, expand_dots } => {
                let mut term = Term::from_field_json_path(*field, path, *expand_dots);
                let number = number_of(value).map(|n| if normalize { n.normalize() } else { n });
                match (value, number) {
                    (_, Some(NumericalValue::I64(v))) => term.append_type_and_fast_value(v),
                    (_, Some(NumericalValue::U64(v))) => term.append_type_and_fast_value(v),
                    (_, Some(NumericalValue::F64(v))) => term.append_type_and_fast_value(v),
                    (ValueSpec::Str(s), _) => term.append_type_and_str(s),
                    (ValueSpec::Bool(b), _) => term.append_type_and_fast_value(*b),
                    (ValueSpec::Date(us), _) => term.append_type_and_fast_value(
                        DateTime::from_timestamp_micros(*us).truncate(tantivy::schema::DATE_TIME_PRECISION_INDEXED),
                    ),
                    (other, _) => {
                        return Err(FfiError::invalid(format!(
                            "a path in JSON field '{name}' can't be queried with {}",
                            value_name(other)
                        )))
                    }
                }
                Ok(term)
            }
        }
    }

    fn record_option(&self) -> IndexRecordOption {
        match self {
            Target::Plain(_, ft) => record_option(ft),
            Target::Json { .. } => IndexRecordOption::WithFreqs,
        }
    }
}

fn number_of(value: &ValueSpec) -> Option<NumericalValue> {
    match value {
        ValueSpec::U64(n) => Some(NumericalValue::U64(*n)),
        ValueSpec::I64(n) => Some(NumericalValue::I64(*n)),
        ValueSpec::F64(x) => Some(NumericalValue::F64(*x)),
        _ => None,
    }
}

/// Tantivy reads both bounds of a range on a JSON path as the type of the first (and panics when
/// the other differs), so they're brought to one type: numbers that normalize to different kinds
/// both become f64 (the returned flag: build them without normalizing again); other kinds must
/// already match.
fn unify_json_bounds(
    name: &str,
    lower: Option<&ValueSpec>,
    upper: Option<&ValueSpec>,
) -> FfiResult<(Option<ValueSpec>, Option<ValueSpec>, bool)> {
    use std::mem::discriminant;
    let (Some(a), Some(b)) = (lower, upper) else { return Ok((lower.cloned(), upper.cloned(), false)) };
    let as_f64 = |n: NumericalValue| match n {
        NumericalValue::U64(v) => v as f64,
        NumericalValue::I64(v) => v as f64,
        NumericalValue::F64(v) => v,
    };
    match (number_of(a).map(NumericalValue::normalize), number_of(b).map(NumericalValue::normalize)) {
        (Some(x), Some(y)) if discriminant(&x) != discriminant(&y) => {
            Ok((Some(ValueSpec::F64(as_f64(x))), Some(ValueSpec::F64(as_f64(y))), true))
        }
        (Some(_), Some(_)) => Ok((Some(a.clone()), Some(b.clone()), false)),
        (None, None) if discriminant(a) == discriminant(b) => Ok((Some(a.clone()), Some(b.clone()), false)),
        _ => Err(FfiError::invalid(format!(
            "the bounds of a range on JSON path '{name}' must have the same type, got {} and {}",
            value_name(a),
            value_name(b)
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
        (FieldType::IpAddr(_), V::Ip(s)) => {
            let ip: std::net::IpAddr =
                s.parse().map_err(|_| FfiError::invalid(format!("'{s}' is not an IP address")))?;
            let v6 = match ip {
                std::net::IpAddr::V4(v4) => v4.to_ipv6_mapped(),
                std::net::IpAddr::V6(v6) => v6,
            };
            Term::from_field_ip_addr(field, v6)
        }
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
        FieldType::IpAddr(_) => "an IP address field",
        FieldType::JsonObject(_) => "a JSON field",
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
        ValueSpec::Ip(ip) => format!("the IP address {ip}"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi::status;
    use crate::testing::{term, Fixture};

    const SCHEMA: &str = r#"{"fields":[
        {"name":"title","type":"text","stored":true},
        {"name":"body","type":"text"},
        {"name":"id","type":"text","tokenizer":"raw","fast":true,"stored":true},
        {"name":"year","type":"u64","fast":true,"stored":true},
        {"name":"delta","type":"i64","fast":true},
        {"name":"price","type":"f64","fast":true},
        {"name":"flag","type":"bool"},
        {"name":"when","type":"date","fast":true},
        {"name":"cat","type":"facet"},
        {"name":"attrs","type":"json"},
        {"name":"ip","type":"ip","fast":true},
        {"name":"blob","type":"bytes"},
        {"name":"note","type":"text","stored":true,"indexed":false}]}"#;

    /// Three books over two segments.
    fn books() -> Fixture {
        Fixture::new(
            SCHEMA,
            &[
                &[
                    r#"{"title":"Of Mice and Men","body":"two drifters in california","id":"a","year":1937,"delta":-5,
                        "price":9.5,"flag":true,"when":"2001-09-09T01:46:40Z","cat":"/fiction/classic",
                        "attrs":{"color":"red","n":5},"ip":"10.0.0.1","blob":"AQI=","note":"x"}"#,
                    r#"{"title":"The Grapes of Wrath","body":"a family in california during the dust bowl","id":"b",
                        "year":1939,"delta":3,"price":12.0,"flag":false,"cat":"/fiction/classic",
                        "attrs":{"color":"blue","n":5.5},"ip":"::1"}"#,
                ],
                &[r#"{"title":"East of Eden","body":"two families in the salinas valley","id":"c","year":1952,
                      "cat":"/fiction","attrs":{"n":7}}"#],
            ],
        )
    }

    fn assert_invalid(f: &Fixture, query: &str, fragment: &str) {
        let e = f.query_error(query);
        assert_eq!(e.code, status::INVALID_ARGUMENT, "{query}: {}", e.message);
        assert!(e.message.contains(fragment), "{query}: expected '{fragment}' in '{}'", e.message);
    }

    #[test]
    fn term_values_are_not_analyzed() {
        let f = books();
        assert_eq!(f.count(&term("title", r#"{"str":"mice"}"#)), 1);
        assert_eq!(f.count(&term("title", r#"{"str":"Mice"}"#)), 0);
        assert_eq!(f.count(&term("id", r#"{"str":"a"}"#)), 1);
        assert_eq!(f.count(r#"{"type":"all"}"#), 3);
    }

    #[test]
    fn numbers_convert_to_the_field_type_when_lossless() {
        let f = books();
        assert_eq!(f.count(&term("year", r#"{"u64":1937}"#)), 1);
        assert_eq!(f.count(&term("year", r#"{"i64":1937}"#)), 1);
        assert_eq!(f.count(&term("delta", r#"{"u64":3}"#)), 1);
        assert_eq!(f.count(&term("delta", r#"{"i64":-5}"#)), 1);
        assert_eq!(f.count(&term("price", r#"{"i64":12}"#)), 1);
        assert_eq!(f.count(&term("price", r#"{"u64":12}"#)), 1);
        assert_eq!(f.count(&term("price", r#"{"f64":9.5}"#)), 1);
        assert_invalid(&f, &term("year", r#"{"i64":-1}"#), "can't be queried with the integer -1");
        assert_invalid(&f, &term("delta", r#"{"u64":18446744073709551615}"#), "unsigned integer");
        assert_invalid(&f, &term("year", r#"{"f64":1937.0}"#), "the number 1937");
        assert_invalid(&f, &term("year", r#"{"str":"1937"}"#), "a u64 field and can't be queried with a string");
    }

    #[test]
    fn other_value_kinds() {
        let f = books();
        assert_eq!(f.count(&term("flag", r#"{"bool":true}"#)), 1);
        assert_eq!(f.count(&term("when", r#"{"date":1000000000000000}"#)), 1);
        assert_eq!(f.count(&term("blob", r#"{"bytes":[1,2]}"#)), 1);
        assert_eq!(f.count(&term("ip", r#"{"ip":"10.0.0.1"}"#)), 1);
        assert_eq!(f.count(&term("ip", r#"{"ip":"::ffff:10.0.0.1"}"#)), 1);
        assert_eq!(f.count(&term("ip", r#"{"ip":"::1"}"#)), 1);
        assert_invalid(&f, &term("ip", r#"{"ip":"10.0.0"}"#), "not an IP address");
        assert_invalid(&f, &term("ip", r#"{"str":"10.0.0.1"}"#), "an IP address field");
        assert_invalid(&f, &term("blob", r#"{"bytes":[256]}"#), "invalid query JSON");
    }

    #[test]
    fn facet_terms_match_the_path_and_its_descendants() {
        let f = books();
        assert_eq!(f.count(&term("cat", r#"{"facet":"/fiction/classic"}"#)), 2);
        assert_eq!(f.count(&term("cat", r#"{"str":"/fiction"}"#)), 3);
        assert_invalid(&f, &term("title", r#"{"facet":"/fiction"}"#), "can't be queried with the facet path");
        assert_invalid(&f, &term("cat", r#"{"facet":"fiction"}"#), "fiction");
    }

    #[test]
    fn unknown_and_unindexed_fields() {
        let f = books();
        assert_invalid(&f, &term("nope", r#"{"str":"a"}"#), "unknown field 'nope'");
        assert_invalid(&f, &term("note", r#"{"str":"x"}"#), "not indexed");
        assert_invalid(&f, r#"{"type":"fuzzy","field":"note","value":"x","distance":1}"#, "not indexed");
    }

    #[test]
    fn json_paths_normalize_numbers_like_indexing() {
        let f = books();
        assert_eq!(f.count(&term("attrs.color", r#"{"str":"red"}"#)), 1);
        assert_eq!(f.count(&term("attrs.n", r#"{"u64":5}"#)), 1);
        assert_eq!(f.count(&term("attrs.n", r#"{"i64":5}"#)), 1);
        assert_eq!(f.count(&term("attrs.n", r#"{"f64":5.0}"#)), 1);
        assert_eq!(f.count(&term("attrs.n", r#"{"f64":5.5}"#)), 1);
        assert_eq!(f.count(&term("attrs.missing", r#"{"str":"red"}"#)), 0);
        assert_invalid(&f, &term("attrs", r#"{"str":"red"}"#), "query a path inside it");
        assert_invalid(&f, &term("attrs.n", r#"{"bytes":[1]}"#), "can't be queried with bytes");
        let range = r#"{"type":"range","field":"attrs.n","lower":{"value":{"i64":6},"inclusive":true},"upper":null}"#;
        // Tantivy only runs ranges on JSON paths of fast JSON fields.
        assert_invalid(&f, range, "only supported for fast fields");
    }

    #[test]
    fn ranges_on_fast_json_paths_with_mixed_bound_types() {
        // Tantivy panics when the two bounds of a JSON range have different types.
        let f = Fixture::new(
            r#"{"fields":[{"name":"j","type":"json","fast":true},{"name":"s","type":"json"}]}"#,
            &[&[r#"{"j":{"n":5}}"#, r#"{"j":{"n":7.5}}"#, r#"{"j":{"n":-3,"s":"m"}}"#], &[r#"{"j":{"n":10,"s":"z"}}"#]],
        );
        let range = |lower: &str, upper: &str| {
            format!(
                r#"{{"type":"range","field":"j.n","lower":{{"value":{lower},"inclusive":true}},"upper":{{"value":{upper},"inclusive":true}}}}"#
            )
        };
        assert_eq!(f.count(&range(r#"{"i64":0}"#, r#"{"f64":9.5}"#)), 2);
        assert_eq!(f.count(&range(r#"{"f64":-3.5}"#, r#"{"u64":5}"#)), 2);
        assert_eq!(f.count(&range(r#"{"u64":5}"#, r#"{"i64":10}"#)), 3);
        assert_eq!(f.count(&range(r#"{"f64":5.0}"#, r#"{"u64":18446744073709551615}"#)), 3);
        assert_invalid(&f, &range(r#"{"i64":0}"#, r#"{"str":"x"}"#), "must have the same type");
        assert_invalid(&f, &range(r#"{"bool":true}"#, r#"{"date":0}"#), "must have the same type");
        let strings = range(r#"{"str":"a"}"#, r#"{"str":"n"}"#).replace("j.n", "j.s");
        assert_eq!(f.count(&strings), 1);
        let whole = r#"{"type":"range","field":"j","lower":{"value":{"i64":0},"inclusive":true},"upper":null}"#;
        assert_invalid(&f, whole, "query a path inside it");
    }

    #[test]
    fn fuzzy() {
        let f = books();
        assert_eq!(f.count(r#"{"type":"fuzzy","field":"title","value":"mise","distance":1}"#), 1);
        assert_eq!(f.count(r#"{"type":"fuzzy","field":"title","value":"mise","distance":0}"#), 0);
        assert_eq!(f.count(r#"{"type":"fuzzy","field":"title","value":"grap","distance":0,"prefix":true}"#), 1);
        assert_eq!(f.count(r#"{"type":"fuzzy","field":"title","value":"mcie","distance":1}"#), 1);
        let no_transpositions =
            r#"{"type":"fuzzy","field":"title","value":"mcie","distance":1,"transpositions":false}"#;
        assert_eq!(f.count(no_transpositions), 0);
        assert_invalid(&f, r#"{"type":"fuzzy","field":"title","value":"x","distance":3}"#, "between 0 and 2");
        assert_invalid(&f, r#"{"type":"fuzzy","field":"year","value":"x","distance":1}"#, "needs a text field");
        assert_invalid(&f, r#"{"type":"fuzzy","field":"title","value":"x","distance":256}"#, "invalid query JSON");
    }

    #[test]
    fn phrases_use_the_field_tokenizer() {
        let f = books();
        let phrase =
            |text: &str, slop: u32| format!(r#"{{"type":"phrase","field":"body","text":"{text}","slop":{slop}}}"#);
        assert_eq!(f.count(&phrase("two drifters", 0)), 1);
        assert_eq!(f.count(&phrase("Two DRIFTERS!", 0)), 1);
        assert_eq!(f.count(&phrase("drifters two", 0)), 0);
        assert_eq!(f.count(&phrase("two california", 0)), 0);
        assert_eq!(f.count(&phrase("two california", 2)), 1);
        assert_eq!(f.count(&phrase("california", 0)), 2); // one token: a term query
        assert_eq!(f.count(&phrase(" , ! ", 0)), 0); // no tokens: matches nothing
        assert_eq!(f.count(&phrase("", 0)), 0);
        assert_invalid(&f, r#"{"type":"phrase","field":"year","text":"1937"}"#, "needs a text field");
    }

    #[test]
    fn booleans() {
        let f = books();
        let a = term("id", r#"{"str":"a"}"#);
        let b = term("id", r#"{"str":"b"}"#);
        let cal = term("body", r#"{"str":"california"}"#);
        assert_eq!(f.count(&format!(r#"{{"type":"boolean","must_not":[{a}]}}"#)), 2, "only must_not = all except");
        assert_eq!(f.count(&format!(r#"{{"type":"boolean","should":[{a},{b}]}}"#)), 2);
        assert_eq!(f.count(&format!(r#"{{"type":"boolean","must":[{cal}],"must_not":[{a}]}}"#)), 1);
        assert_eq!(f.count(&format!(r#"{{"type":"boolean","must":[{a},{b}]}}"#)), 0);
        assert_eq!(f.count(r#"{"type":"boolean"}"#), 0);
        // An error anywhere in the tree fails the whole query.
        assert_invalid(&f, &format!(r#"{{"type":"boolean","should":[{a},{}]}}"#, term("x", r#"{"str":"a"}"#)), "'x'");
    }

    #[test]
    fn ranges() {
        let f = books();
        let range = |field: &str, lower: &str, upper: &str| {
            format!(r#"{{"type":"range","field":"{field}","lower":{lower},"upper":{upper}}}"#)
        };
        let incl = |v: &str| format!(r#"{{"value":{v},"inclusive":true}}"#);
        let excl = |v: &str| format!(r#"{{"value":{v},"inclusive":false}}"#);
        assert_eq!(f.count(&range("year", &incl(r#"{"u64":1937}"#), &incl(r#"{"u64":1939}"#))), 2);
        assert_eq!(f.count(&range("year", &excl(r#"{"u64":1937}"#), &incl(r#"{"u64":1939}"#))), 1);
        assert_eq!(f.count(&range("year", &excl(r#"{"i64":1937}"#), "null")), 2);
        assert_eq!(f.count(&range("year", "null", &excl(r#"{"u64":1937}"#))), 0);
        assert_eq!(f.count(&range("delta", &incl(r#"{"i64":-10}"#), &incl(r#"{"i64":0}"#))), 1);
        assert_eq!(f.count(&range("price", &incl(r#"{"i64":10}"#), "null")), 1);
        assert_eq!(f.count(&range("when", &incl(r#"{"date":0}"#), "null")), 1);
        assert_eq!(f.count(&range("id", &incl(r#"{"str":"a"}"#), &incl(r#"{"str":"b"}"#))), 2);
        assert_invalid(&f, &range("year", "null", "null"), "at least one bound");
        assert_invalid(&f, &range("note", &incl(r#"{"str":"a"}"#), "null"), "neither indexed nor fast");
        assert_invalid(&f, &range("year", &incl(r#"{"str":"a"}"#), "null"), "can't be queried with a string");
        assert_invalid(&f, &range("nope", &incl(r#"{"u64":1}"#), "null"), "unknown field");
    }

    #[test]
    fn scoring_wrappers_reject_non_finite_numbers() {
        let f = books();
        let a = term("id", r#"{"str":"a"}"#);
        assert_eq!(f.count(&format!(r#"{{"type":"boost","query":{a},"factor":2.5}}"#)), 1);
        assert_eq!(f.count(&format!(r#"{{"type":"const_score","query":{a},"score":1}}"#)), 1);
        // 1e39 is finite JSON but infinite as an f32.
        assert_invalid(&f, &format!(r#"{{"type":"boost","query":{a},"factor":1e39}}"#), "finite");
        assert_invalid(&f, &format!(r#"{{"type":"const_score","query":{a},"score":-1e39}}"#), "finite");
        assert_eq!(f.count(&format!(r#"{{"type":"disjunction_max","queries":[{a}],"tie_breaker":0.3}}"#)), 1);
        assert_eq!(f.count(r#"{"type":"disjunction_max","queries":[]}"#), 0);
        assert_invalid(
            &f,
            &format!(r#"{{"type":"disjunction_max","queries":[{a}],"tie_breaker":1.5}}"#),
            "between 0 and 1",
        );
        assert_invalid(
            &f,
            &format!(r#"{{"type":"disjunction_max","queries":[{a}],"tie_breaker":-0.1}}"#),
            "between 0 and 1",
        );
    }

    #[test]
    fn regex_exists_and_term_sets() {
        let f = books();
        assert_eq!(f.count(r#"{"type":"regex","field":"title","pattern":"gr.*"}"#), 1);
        assert_invalid(&f, r#"{"type":"regex","field":"title","pattern":"("}"#, "invalid regex");
        assert_invalid(&f, r#"{"type":"regex","field":"year","pattern":"1.*"}"#, "needs a text field");
        assert_eq!(f.count(r#"{"type":"exists","field":"year"}"#), 3);
        assert_eq!(f.count(r#"{"type":"exists","field":"delta"}"#), 2);
        assert_eq!(f.count(r#"{"type":"exists","field":"ip"}"#), 2);
        assert_invalid(&f, r#"{"type":"exists","field":"title"}"#, "isn't a fast field");
        assert_invalid(&f, r#"{"type":"exists","field":"nope"}"#, "unknown field");
        assert_eq!(f.count(r#"{"type":"term_set","field":"id","values":[{"str":"a"},{"str":"c"}]}"#), 2);
        assert_eq!(f.count(r#"{"type":"term_set","field":"year","values":[{"i64":1937},{"u64":1952}]}"#), 2);
        assert_eq!(f.count(r#"{"type":"term_set","field":"id","values":[]}"#), 0);
        assert_invalid(&f, r#"{"type":"term_set","field":"year","values":[{"u64":1},{"str":"x"}]}"#, "string");
    }

    #[test]
    fn more_like_this() {
        let f = books();
        let mlt = |doc: &str| {
            format!(
                r#"{{"type":"more_like_this","document":{doc},
                    "options":{{"min_doc_frequency":1,"min_term_frequency":1,"min_word_length":1}}}}"#
            )
        };
        assert!(f.count(&mlt(r#"{"body":["two families in california"]}"#)) >= 2);
        assert!(f.count(&mlt(r#"{"body":"salinas valley"}"#)) >= 1);
        assert_invalid(&f, &mlt(r#"{"nope":["x"]}"#), "nope");
        assert_invalid(&f, &mlt(r#"{"year":["not a number"]}"#), "field 'year'");
        // Tantivy panics on these (Facet::from unwraps).
        assert_invalid(&f, &mlt(r#"{"cat":["mice"]}"#), "not a facet path");
        assert_invalid(&f, &mlt(r#"{"cat":"mice"}"#), "not a facet path");
        assert!(f.try_count(&mlt(r#"{"cat":["/fiction"]}"#)).is_ok());
    }

    #[test]
    fn parsed_queries() {
        let f = books();
        let parse = |text: &str, extra: &str| format!(r#"{{"type":"parse","text":"{text}"{extra}}}"#);
        assert_eq!(f.count(&parse("mice", "")), 1);
        assert_eq!(f.count(&parse("year:[1937 TO 1939]", "")), 2);
        assert_eq!(f.count(&parse("two california", r#","default_fields":["body"]"#)), 3);
        assert_eq!(f.count(&parse("two california", r#","default_fields":["body"],"conjunction":true"#)), 1);
        assert_eq!(f.count(&parse("mcie", r#","fuzzy":{"title":{"distance":1}}"#)), 1);
        assert_eq!(f.count(&parse("mice", r#","boosts":{"title":2.0}"#)), 1);

        let e = f.query_error(&parse("title:(", ""));
        assert_eq!(e.code, status::QUERY_PARSE, "{}", e.message);
        assert!(f.try_count(&parse("title:( mice", r#","lenient":true"#)).is_ok());

        assert_invalid(&f, &parse("x", r#","default_fields":["nope"]"#), "nope");
        assert_invalid(&f, &parse("x", r#","boosts":{"title":1e39}"#), "finite");
        assert_invalid(&f, &parse("x", r#","fuzzy":{"title":{"distance":3}}"#), "between 0 and 2");
        assert_invalid(&f, &parse("x", r#","fuzzy":{"year":{"distance":1}}"#), "needs a text field");
    }

    #[test]
    fn malformed_query_json() {
        let f = books();
        for json in [
            "",
            "null",
            "{}",
            r#"{"type":"nope"}"#,
            r#"{"type":"term","field":"id"}"#,
            r#"{"type":"term","field":"id","value":{"str":1}}"#,
            r#"{"type":"term","field":"id","value":{"str":"a","u64":1}}"#,
            r#"{"type":"boost","query":{"type":"all"}}"#,
        ] {
            assert_invalid(&f, json, "invalid query JSON");
        }
    }

    #[test]
    fn term_of_accepts_only_term_queries() {
        let f = books();
        let schema = f.index.schema();
        assert!(term_of(&parse_spec(&term("id", r#"{"str":"a"}"#)).unwrap(), &schema).is_ok());
        assert!(term_of(&parse_spec(&term("attrs.color", r#"{"str":"a"}"#)).unwrap(), &schema).is_ok());
        let e = term_of(&parse_spec(r#"{"type":"all"}"#).unwrap(), &schema).unwrap_err();
        assert_eq!(e.message, "expected a term query");
    }

    #[test]
    fn deeply_nested_queries_fail_without_overflowing_the_stack() {
        // serde_json stops at 128 levels of nesting, before `build` recurses that deep.
        let f = books();
        let mut q = String::from(r#"{"type":"all"}"#);
        for _ in 0..200 {
            q = format!(r#"{{"type":"boolean","must":[{q}]}}"#);
        }
        assert_invalid(&f, &q, "recursion limit");
    }
}
