//! Ordering search hits: by relevance through Tantivy's `TopDocs` (fast path, with Block-WAND); by
//! one numeric fast field through `FastTop`; or by any list of keys (fast fields and/or score)
//! through `SortedTop`. Both of ours are top-K collectors that also take a search-after cursor.
//!
//! Request part: `"sort": [{"type": "field", "field": "year", "order": "desc"}, {"type": "score"}]`
//! (absent or empty = by score), and `"search_after": {"keys": [...], "addr": [segment, doc]}`.
//! Per hit: `"sort": [...]`, one tagged value per key (`{"score": f}`, `{"u64": n}`, `{"str": s}`, `{"ip": s}`,
//! dates as `{"date": unix_micros}`, ...) or `null` when the document has no value. Documents
//! without a value come last in both orders; the document address breaks ties, so paging with a
//! cursor is stable within one snapshot.

use std::cmp::Ordering;
use std::net::{IpAddr, Ipv6Addr};
use std::sync::Arc;

use serde::Deserialize;
use serde_json::{json, Value};
use tantivy::collector::{Collector, Count, FruitHandle, MultiCollector, MultiFruit, SegmentCollector, TopDocs};
use tantivy::columnar::{BytesColumn, Column, MonotonicallyMappableToU64, StrColumn, TermOrdHit};
use tantivy::query::Query;
use tantivy::schema::{FieldType, Schema};
use tantivy::{DateTime, DocAddress, DocId, Score, Searcher, SegmentOrdinal, SegmentReader, TantivyError};

use crate::ffi::{FfiError, FfiResult};

#[derive(Deserialize)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum SortKeyRequest {
    Score,
    Field {
        field: String,
        #[serde(default)]
        order: SortOrder,
    },
}

#[derive(Deserialize, Clone, Copy, Default)]
#[serde(rename_all = "snake_case")]
pub enum SortOrder {
    #[default]
    Asc,
    Desc,
}

#[derive(Deserialize)]
pub struct SearchAfter {
    keys: Vec<Value>,
    addr: [u32; 2],
}

/// A top hit: its score (NaN when the score isn't a sort key, because scoring is skipped), its
/// tagged sort values (always present, so every hit can produce a cursor), and its address.
pub type TopHit = (Score, Value, DocAddress);

/// The collector for the requested page of hits, chosen by [`plan`].
pub enum TopPlan {
    /// By relevance, without a cursor: Tantivy's `TopDocs` (Block-WAND when it runs on its own).
    Score { limit: usize, offset: usize },
    /// By one numeric fast field.
    Fast(FastTop),
    /// Anything else.
    Sorted(SortedTop),
}

pub enum TopHandle {
    Score(FruitHandle<Vec<(Score, DocAddress)>>),
    Sorted { handle: FruitHandle<SortedFruit>, scored: bool },
}

/// Picks the collector for the requested order and page.
pub fn plan(
    sort: &[SortKeyRequest],
    after: Option<&SearchAfter>,
    limit: usize,
    offset: usize,
    schema: &Schema,
) -> FfiResult<TopPlan> {
    if sort.is_empty() && after.is_none() {
        return Ok(TopPlan::Score { limit, offset });
    }
    let keys: Vec<Key> = if sort.is_empty() {
        vec![Key { source: Source::Score, desc: true }]
    } else {
        sort.iter().map(|k| resolve(k, schema)).collect::<FfiResult<_>>()?
    };
    let cursor = after.map(|a| parse_cursor(a, &keys)).transpose()?;
    if let [Key { source: Source::Field { name, kind }, desc }] = keys.as_slice() {
        if kind.is_numeric() {
            let cursor = cursor.map(|c| (to_u64(&c.values[0]), c.addr));
            return Ok(TopPlan::Fast(FastTop { field: name.clone(), kind: *kind, desc: *desc, cursor, limit, offset }));
        }
    }
    Ok(TopPlan::Sorted(SortedTop { keys: keys.into(), cursor, limit, offset }))
}

impl TopPlan {
    /// Adds the collector to `multi` (when facets or aggregations share the pass).
    pub fn add_to(self, multi: &mut MultiCollector) -> TopHandle {
        match self {
            TopPlan::Score { limit, offset } => {
                TopHandle::Score(multi.add_collector(TopDocs::with_limit(limit).and_offset(offset).order_by_score()))
            }
            TopPlan::Fast(c) => TopHandle::Sorted { handle: multi.add_collector(c), scored: false },
            TopPlan::Sorted(c) => {
                let scored = c.requires_scoring();
                TopHandle::Sorted { handle: multi.add_collector(c), scored }
            }
        }
    }

    /// Runs the search for the total count and this page of hits, without a `MultiCollector`
    /// (whose dynamic dispatch per document is measurable). With `separate_count`, the count is a
    /// search of its own, which Tantivy answers from the posting list's length for a term query
    /// and which lets `TopDocs` skip documents with Block-WAND; for other queries one pass is
    /// cheaper, because both passes would visit every match.
    pub fn search_with_count(
        self,
        searcher: &Searcher,
        query: &dyn Query,
        separate_count: bool,
    ) -> tantivy::Result<(usize, Vec<TopHit>)> {
        fn run<C: Collector>(
            searcher: &Searcher,
            query: &dyn Query,
            top: C,
            separate_count: bool,
        ) -> tantivy::Result<(usize, C::Fruit)> {
            if separate_count {
                Ok((searcher.search(query, &Count)?, searcher.search(query, &top)?))
            } else {
                searcher.search(query, &(Count, top))
            }
        }
        Ok(match self {
            TopPlan::Score { limit, offset } => {
                let top = TopDocs::with_limit(limit).and_offset(offset).order_by_score();
                let (count, fruit) = run(searcher, query, top, separate_count)?;
                (count, score_hits(fruit))
            }
            TopPlan::Fast(c) => {
                let (count, fruit) = run(searcher, query, c, separate_count)?;
                (count, sorted_hits(fruit, false))
            }
            TopPlan::Sorted(c) => {
                let scored = c.requires_scoring();
                let (count, fruit) = run(searcher, query, c, separate_count)?;
                (count, sorted_hits(fruit, scored))
            }
        })
    }
}

pub fn extract(handle: TopHandle, fruits: &mut MultiFruit) -> Vec<TopHit> {
    match handle {
        TopHandle::Score(h) => score_hits(h.extract(fruits)),
        TopHandle::Sorted { handle, scored } => sorted_hits(handle.extract(fruits), scored),
    }
}

fn score_hits(fruit: Vec<(Score, DocAddress)>) -> Vec<TopHit> {
    fruit.into_iter().map(|(score, addr)| (score, json!([{ "score": score }]), addr)).collect()
}

fn sorted_hits(fruit: SortedFruit, scored: bool) -> Vec<TopHit> {
    fruit
        .into_iter()
        .map(|(values, addr, score)| {
            let tagged = Value::Array(values.iter().map(tagged).collect());
            (if scored { score } else { Score::NAN }, tagged, addr)
        })
        .collect()
}

// ---------------------------------------------------------------------------------------------
// Keys and values
// ---------------------------------------------------------------------------------------------

#[derive(Clone, Copy, PartialEq, Debug)]
enum Kind {
    U64,
    I64,
    F64,
    Bool,
    Date,
    Str,
    Bytes,
    Ip,
}

impl Kind {
    /// Kinds whose fast column Tantivy can read as order-preserving `u64`s.
    fn is_numeric(self) -> bool {
        matches!(self, Kind::U64 | Kind::I64 | Kind::F64 | Kind::Bool | Kind::Date)
    }
}

enum Source {
    Score,
    Field { name: String, kind: Kind },
}

struct Key {
    source: Source,
    desc: bool,
}

fn resolve(request: &SortKeyRequest, schema: &Schema) -> FfiResult<Key> {
    let (name, order) = match request {
        SortKeyRequest::Score => return Ok(Key { source: Source::Score, desc: true }),
        SortKeyRequest::Field { field, order } => (field, *order),
    };
    let field = schema.get_field(name)?;
    let entry = schema.get_field_entry(field);
    let kind = match entry.field_type() {
        FieldType::U64(_) => Kind::U64,
        FieldType::I64(_) => Kind::I64,
        FieldType::F64(_) => Kind::F64,
        FieldType::Bool(_) => Kind::Bool,
        FieldType::Date(_) => Kind::Date,
        FieldType::Str(_) => Kind::Str,
        FieldType::Bytes(_) => Kind::Bytes,
        FieldType::IpAddr(_) => Kind::Ip,
        _ => return Err(FfiError::invalid(format!("field '{name}' can't be sorted by: its type isn't supported"))),
    };
    if !entry.is_fast() {
        return Err(FfiError::invalid(format!(
            "field '{name}' can't be sorted by: it isn't a fast field (create it with fast: true)"
        )));
    }
    Ok(Key { source: Source::Field { name: name.clone(), kind }, desc: matches!(order, SortOrder::Desc) })
}

/// A sort value. `Ord` is a segment-local term ordinal of a text/bytes column, converted to
/// `Str`/`Bytes` when the segment's hits are harvested.
#[derive(Clone, Debug)]
pub enum KeyValue {
    Missing,
    Score(f32),
    U64(u64),
    I64(i64),
    F64(f64),
    Bool(bool),
    Date(i64),
    Str(String),
    Bytes(Vec<u8>),
    Ip(Ipv6Addr),
    Ord(u64),
}

fn natural(a: &KeyValue, b: &KeyValue) -> Ordering {
    use KeyValue::*;
    match (a, b) {
        (Score(x), Score(y)) => x.total_cmp(y),
        (U64(x), U64(y)) | (Ord(x), Ord(y)) => x.cmp(y),
        (I64(x), I64(y)) | (Date(x), Date(y)) => x.cmp(y),
        (F64(x), F64(y)) => x.total_cmp(y),
        (Bool(x), Bool(y)) => x.cmp(y),
        (Str(x), Str(y)) => x.as_bytes().cmp(y.as_bytes()),
        (Bytes(x), Bytes(y)) => x.cmp(y),
        (Ip(x), Ip(y)) => x.cmp(y),
        _ => Ordering::Equal, // never mixed within one key
    }
}

/// `Less` means `a` comes first. Missing values come last in both orders.
fn cmp_value(a: &KeyValue, b: &KeyValue, desc: bool) -> Ordering {
    match (a, b) {
        (KeyValue::Missing, KeyValue::Missing) => Ordering::Equal,
        (KeyValue::Missing, _) => Ordering::Greater,
        (_, KeyValue::Missing) => Ordering::Less,
        _ if desc => natural(a, b).reverse(),
        _ => natural(a, b),
    }
}

fn cmp_values(keys: &[Key], a: &[KeyValue], b: &[KeyValue]) -> Ordering {
    keys.iter()
        .zip(a.iter().zip(b))
        .map(|(k, (x, y))| cmp_value(x, y, k.desc))
        .find(|o| o.is_ne())
        .unwrap_or(Ordering::Equal)
}

fn tagged(value: &KeyValue) -> Value {
    match value {
        KeyValue::Missing | KeyValue::Ord(_) => Value::Null,
        KeyValue::Score(s) => json!({ "score": s }),
        KeyValue::U64(n) => json!({ "u64": n }),
        KeyValue::I64(n) => json!({ "i64": n }),
        KeyValue::F64(x) => json!({ "f64": x }),
        KeyValue::Bool(b) => json!({ "bool": b }),
        KeyValue::Date(us) => json!({ "date": us }),
        KeyValue::Str(s) => json!({ "str": s }),
        KeyValue::Bytes(b) => json!({ "bytes": b }),
        KeyValue::Ip(ip) => json!({ "ip": ip_text(ip) }),
    }
}

/// IPv4-mapped addresses are shown as IPv4, like Tantivy's stored values.
fn ip_text(ip: &Ipv6Addr) -> String {
    ip.to_ipv4_mapped().map_or_else(|| ip.to_string(), |v4| v4.to_string())
}

fn parse_ip(text: &str) -> Option<Ipv6Addr> {
    match text.parse::<IpAddr>().ok()? {
        IpAddr::V4(v4) => Some(v4.to_ipv6_mapped()),
        IpAddr::V6(v6) => Some(v6),
    }
}

fn parse_cursor(after: &SearchAfter, keys: &[Key]) -> FfiResult<Cursor> {
    let mismatch = || FfiError::invalid("the search-after cursor doesn't match the sort order of this search");
    if after.keys.len() != keys.len() {
        return Err(mismatch());
    }
    let values = keys
        .iter()
        .zip(&after.keys)
        .map(|(key, v)| {
            if v.is_null() {
                return Ok(KeyValue::Missing);
            }
            let (tag, inner) =
                v.as_object().filter(|o| o.len() == 1).and_then(|o| o.iter().next()).ok_or_else(mismatch)?;
            let value = match (&key.source, tag.as_str()) {
                (Source::Score, "score") => inner.as_f64().map(|x| KeyValue::Score(x as f32)),
                (Source::Field { kind: Kind::U64, .. }, "u64") => inner.as_u64().map(KeyValue::U64),
                (Source::Field { kind: Kind::I64, .. }, "i64") => inner.as_i64().map(KeyValue::I64),
                (Source::Field { kind: Kind::F64, .. }, "f64") => inner.as_f64().map(KeyValue::F64),
                (Source::Field { kind: Kind::Bool, .. }, "bool") => inner.as_bool().map(KeyValue::Bool),
                (Source::Field { kind: Kind::Date, .. }, "date") => inner
                    .as_i64()
                    .filter(|us| (crate::query_spec::MIN_DATE_MICROS..=crate::query_spec::MAX_DATE_MICROS).contains(us))
                    .map(KeyValue::Date),
                (Source::Field { kind: Kind::Str, .. }, "str") => inner.as_str().map(|s| KeyValue::Str(s.to_owned())),
                (Source::Field { kind: Kind::Ip, .. }, "ip") => inner.as_str().and_then(parse_ip).map(KeyValue::Ip),
                (Source::Field { kind: Kind::Bytes, .. }, "bytes") => inner
                    .as_array()
                    .and_then(|a| {
                        a.iter().map(|b| b.as_u64().and_then(|b| u8::try_from(b).ok())).collect::<Option<Vec<u8>>>()
                    })
                    .map(KeyValue::Bytes),
                _ => None,
            };
            value.ok_or_else(mismatch)
        })
        .collect::<FfiResult<Vec<_>>>()?;
    Ok(Cursor { values, addr: DocAddress::new(after.addr[0], after.addr[1]) })
}

// ---------------------------------------------------------------------------------------------
// FastTop: one numeric key
// ---------------------------------------------------------------------------------------------

/// Top-K by one numeric fast field (u64, i64, f64, bool, date). Values are read through
/// Tantivy's order-preserving `u64` mapping, and each document becomes one `u128`,
/// `missing (1 bit) | rank (64 bits) | doc (32 bits)`, where `rank` is the value, inverted for a
/// descending order. Smaller is better, so missing values come last in both orders, ties go to
/// the lower document, and a comparison is one integer compare.
pub struct FastTop {
    field: String,
    kind: Kind,
    desc: bool,
    /// The cursor's value (`None` = missing, in the `u64` mapping) and address.
    cursor: Option<(Option<u64>, DocAddress)>,
    limit: usize,
    offset: usize,
}

fn to_u64(value: &KeyValue) -> Option<u64> {
    match *value {
        KeyValue::U64(n) => Some(n),
        KeyValue::I64(n) => Some(n.to_u64()),
        KeyValue::F64(x) => Some(x.to_u64()),
        KeyValue::Bool(b) => Some(b.to_u64()),
        KeyValue::Date(us) => Some(DateTime::from_timestamp_micros(us).to_u64()),
        _ => None,
    }
}

impl FastTop {
    fn pack(&self, value: Option<u64>, doc: DocId) -> u128 {
        match value {
            Some(v) => (u128::from(if self.desc { !v } else { v }) << 32) | u128::from(doc),
            None => (1u128 << 96) | u128::from(doc),
        }
    }

    fn unpack(&self, packed: u128) -> KeyValue {
        if packed >> 96 != 0 {
            return KeyValue::Missing;
        }
        let rank = (packed >> 32) as u64;
        let v = if self.desc { !rank } else { rank };
        match self.kind {
            Kind::U64 => KeyValue::U64(v),
            Kind::I64 => KeyValue::I64(i64::from_u64(v)),
            Kind::F64 => KeyValue::F64(f64::from_u64(v)),
            Kind::Bool => KeyValue::Bool(bool::from_u64(v)),
            _ => KeyValue::Date(DateTime::from_u64(v).into_timestamp_micros()),
        }
    }
}

impl Collector for FastTop {
    type Fruit = SortedFruit;
    type Child = FastTopSegment;

    fn for_segment(&self, segment_ord: SegmentOrdinal, reader: &SegmentReader) -> tantivy::Result<FastTopSegment> {
        let column = reader.fast_fields().u64_lenient(&self.field)?.map(|(c, _)| c);
        // Documents must sort strictly after the cursor: compare the key, then the address. Within
        // this segment that is "packed > bound"; `None` accepts everything.
        let after = self.cursor.and_then(|(value, addr)| {
            let at = |doc| self.pack(value, doc);
            match segment_ord.cmp(&addr.segment_ord) {
                Ordering::Equal => Some(at(addr.doc_id)),
                Ordering::Less => Some(at(DocId::MAX)),
                Ordering::Greater => at(0).checked_sub(1),
            }
        });
        let k = self.limit + self.offset;
        let cap = k.max(32).saturating_mul(2);
        Ok(FastTopSegment {
            column,
            segment_ord,
            desc: self.desc,
            after,
            k,
            cap,
            threshold: u128::MAX,
            // Grows on demand: a huge limit must not reserve its whole buffer up front.
            buffer: Vec::with_capacity(cap.min(4096)),
        })
    }

    fn requires_scoring(&self) -> bool {
        false
    }

    fn merge_fruits(&self, fruits: Vec<Vec<(u128, SegmentOrdinal)>>) -> tantivy::Result<SortedFruit> {
        let mut all: Vec<(u128, SegmentOrdinal)> = fruits.into_iter().flatten().collect();
        // Key first, then the address (segment, doc).
        all.sort_unstable_by_key(|&(packed, seg)| (packed >> 32, seg, packed as u32));
        Ok(all
            .into_iter()
            .skip(self.offset)
            .take(self.limit)
            .map(|(packed, seg)| (vec![self.unpack(packed)], DocAddress::new(seg, packed as u32), Score::NAN))
            .collect())
    }
}

pub struct FastTopSegment {
    column: Option<Column<u64>>,
    segment_ord: SegmentOrdinal,
    desc: bool,
    after: Option<u128>,
    k: usize,
    /// Buffer size that triggers pruning back to `k`.
    cap: usize,
    /// The worst kept entry once the buffer has been pruned; later documents must beat it.
    threshold: u128,
    buffer: Vec<u128>,
}

impl FastTopSegment {
    #[inline]
    fn push(&mut self, doc: DocId) {
        let packed = match self.column.as_ref().and_then(|c| c.first(doc)) {
            Some(v) => (u128::from(if self.desc { !v } else { v }) << 32) | u128::from(doc),
            None => (1u128 << 96) | u128::from(doc),
        };
        if packed >= self.threshold || self.after.is_some_and(|b| packed <= b) {
            return;
        }
        self.buffer.push(packed);
        if self.buffer.len() >= self.cap {
            self.prune();
        }
    }

    fn prune(&mut self) {
        let k = self.k;
        self.buffer.select_nth_unstable(k - 1);
        self.buffer.truncate(k);
        self.threshold = self.buffer[k - 1];
    }
}

impl SegmentCollector for FastTopSegment {
    type Fruit = Vec<(u128, SegmentOrdinal)>;

    fn collect(&mut self, doc: DocId, _score: Score) {
        self.push(doc);
    }

    fn collect_block(&mut self, docs: &[DocId]) {
        for &doc in docs {
            self.push(doc);
        }
    }

    fn harvest(mut self) -> Self::Fruit {
        self.buffer.sort_unstable();
        self.buffer.truncate(self.k);
        let seg = self.segment_ord;
        self.buffer.into_iter().map(|p| (p, seg)).collect()
    }
}

// ---------------------------------------------------------------------------------------------
// SortedTop: any list of keys
// ---------------------------------------------------------------------------------------------

struct Cursor {
    values: Vec<KeyValue>,
    addr: DocAddress,
}

/// Merged result: sort values (text/bytes resolved), address, score.
pub type SortedFruit = Vec<(Vec<KeyValue>, DocAddress, Score)>;

pub struct SortedTop {
    keys: Arc<[Key]>,
    cursor: Option<Cursor>,
    limit: usize,
    offset: usize,
}

impl Collector for SortedTop {
    type Fruit = SortedFruit;
    type Child = SortedTopSegment;

    fn for_segment(&self, segment_ord: SegmentOrdinal, reader: &SegmentReader) -> tantivy::Result<SortedTopSegment> {
        let readers =
            self.keys.iter().map(|k| KeyReader::open(&k.source, reader)).collect::<tantivy::Result<Vec<_>>>()?;
        let cursor = match &self.cursor {
            None => None,
            Some(c) => {
                let bounds = readers
                    .iter()
                    .zip(&c.values)
                    .map(|(r, v)| r.cursor_bound(v))
                    .collect::<tantivy::Result<Vec<_>>>()?;
                Some((bounds, c.addr))
            }
        };
        Ok(SortedTopSegment {
            keys: self.keys.clone(),
            readers,
            segment_ord,
            cursor,
            k: self.limit + self.offset,
            buffer: Vec::new(),
            threshold: None,
            scratch: Vec::with_capacity(self.keys.len()),
        })
    }

    fn requires_scoring(&self) -> bool {
        self.keys.iter().any(|k| matches!(k.source, Source::Score))
    }

    fn merge_fruits(&self, fruits: Vec<Result<SortedFruit, String>>) -> tantivy::Result<SortedFruit> {
        let mut all = Vec::new();
        for f in fruits {
            all.extend(f.map_err(TantivyError::InternalError)?);
        }
        all.sort_by(|a, b| cmp_values(&self.keys, &a.0, &b.0).then(a.1.cmp(&b.1)));
        Ok(all.into_iter().skip(self.offset).take(self.limit).collect())
    }
}

/// How a segment compares documents with the cursor for one key: directly, or (for text/bytes)
/// through the cursor's position among the segment's term ordinals.
enum CursorBound {
    Value(KeyValue),
    /// The cursor equals the term at `ord` (`exact`) or sorts just before it.
    Ord {
        ord: u64,
        exact: bool,
    },
}

pub struct SortedTopSegment {
    keys: Arc<[Key]>,
    readers: Vec<KeyReader>,
    segment_ord: SegmentOrdinal,
    cursor: Option<(Vec<CursorBound>, DocAddress)>,
    k: usize,
    buffer: Vec<(Vec<KeyValue>, DocId, Score)>,
    /// The worst kept entry once the buffer has been pruned; later documents must beat it.
    threshold: Option<(Vec<KeyValue>, DocId)>,
    scratch: Vec<KeyValue>,
}

impl SortedTopSegment {
    fn is_after_cursor(&self, doc: DocId) -> bool {
        let Some((bounds, addr)) = &self.cursor else { return true };
        for ((key, value), bound) in self.keys.iter().zip(&self.scratch).zip(bounds) {
            let o = match bound {
                CursorBound::Value(c) => cmp_value(value, c, key.desc),
                CursorBound::Ord { ord, exact } => match value {
                    KeyValue::Missing => Ordering::Greater,
                    KeyValue::Ord(o) => {
                        let natural = if *exact {
                            o.cmp(ord)
                        } else if o < ord {
                            Ordering::Less
                        } else {
                            Ordering::Greater
                        };
                        if key.desc {
                            natural.reverse()
                        } else {
                            natural
                        }
                    }
                    _ => Ordering::Equal,
                },
            };
            if o.is_ne() {
                return o.is_gt();
            }
        }
        DocAddress::new(self.segment_ord, doc) > *addr
    }

    fn cmp_entries(keys: &[Key], a: (&[KeyValue], DocId), b: (&[KeyValue], DocId)) -> Ordering {
        cmp_values(keys, a.0, b.0).then(a.1.cmp(&b.1))
    }

    fn prune(&mut self) {
        let keys = &*self.keys;
        let k = self.k;
        self.buffer.select_nth_unstable_by(k - 1, |a, b| Self::cmp_entries(keys, (&a.0, a.1), (&b.0, b.1)));
        self.buffer.truncate(k);
        let worst = self
            .buffer
            .iter()
            .max_by(|a, b| Self::cmp_entries(keys, (&a.0, a.1), (&b.0, b.1)))
            .map(|(v, d, _)| (v.clone(), *d));
        self.threshold = worst;
    }
}

impl SegmentCollector for SortedTopSegment {
    type Fruit = Result<SortedFruit, String>;

    fn collect(&mut self, doc: DocId, score: Score) {
        self.scratch.clear();
        for r in &self.readers {
            self.scratch.push(r.value(doc, score));
        }
        if !self.is_after_cursor(doc) {
            return;
        }
        if let Some((values, tdoc)) = &self.threshold {
            if Self::cmp_entries(&self.keys, (&self.scratch, doc), (values, *tdoc)).is_ge() {
                return;
            }
        }
        self.buffer.push((self.scratch.clone(), doc, score));
        if self.buffer.len() >= self.k.saturating_mul(2).max(64) {
            self.prune();
        }
    }

    fn harvest(mut self) -> Self::Fruit {
        let keys = self.keys.clone();
        self.buffer.sort_by(|a, b| Self::cmp_entries(&keys, (&a.0, a.1), (&b.0, b.1)));
        self.buffer.truncate(self.k);
        let segment_ord = self.segment_ord;
        let readers = &self.readers;
        self.buffer
            .into_iter()
            .map(|(values, doc, score)| {
                let values =
                    values.into_iter().zip(readers).map(|(v, r)| r.resolve(v)).collect::<Result<Vec<_>, String>>()?;
                Ok((values, DocAddress::new(segment_ord, doc), score))
            })
            .collect()
    }
}

enum KeyReader {
    Score,
    U64(Option<Column<u64>>),
    I64(Option<Column<i64>>),
    F64(Option<Column<f64>>),
    Bool(Option<Column<bool>>),
    Date(Option<Column<DateTime>>),
    Str(Option<StrColumn>),
    Bytes(Option<BytesColumn>),
    Ip(Option<Column<Ipv6Addr>>),
}

impl KeyReader {
    fn open(source: &Source, reader: &SegmentReader) -> tantivy::Result<Self> {
        let Source::Field { name, kind } = source else { return Ok(Self::Score) };
        let ff = reader.fast_fields();
        Ok(match kind {
            Kind::U64 => Self::U64(ff.column_opt(name)?),
            Kind::I64 => Self::I64(ff.column_opt(name)?),
            Kind::F64 => Self::F64(ff.column_opt(name)?),
            Kind::Bool => Self::Bool(ff.column_opt(name)?),
            Kind::Date => Self::Date(ff.column_opt(name)?),
            Kind::Str => Self::Str(ff.str(name)?),
            Kind::Bytes => Self::Bytes(ff.bytes(name)?),
            Kind::Ip => Self::Ip(ff.column_opt(name)?),
        })
    }

    /// The document's value for this key. For multi-valued fields, the first value.
    fn value(&self, doc: DocId, score: Score) -> KeyValue {
        fn first<T: PartialOrd + Copy + std::fmt::Debug + Send + Sync + 'static>(
            c: &Option<Column<T>>,
            doc: DocId,
        ) -> Option<T> {
            c.as_ref().and_then(|c| c.first(doc))
        }
        let v = match self {
            Self::Score => Some(KeyValue::Score(score)),
            Self::U64(c) => first(c, doc).map(KeyValue::U64),
            Self::I64(c) => first(c, doc).map(KeyValue::I64),
            Self::F64(c) => first(c, doc).map(KeyValue::F64),
            Self::Bool(c) => first(c, doc).map(KeyValue::Bool),
            Self::Date(c) => first(c, doc).map(|d| KeyValue::Date(d.into_timestamp_micros())),
            Self::Str(c) => c.as_ref().and_then(|c| c.ords().first(doc)).map(KeyValue::Ord),
            Self::Bytes(c) => c.as_ref().and_then(|c| c.ords().first(doc)).map(KeyValue::Ord),
            Self::Ip(c) => first(c, doc).map(KeyValue::Ip),
        };
        v.unwrap_or(KeyValue::Missing)
    }

    fn cursor_bound(&self, value: &KeyValue) -> tantivy::Result<CursorBound> {
        let bytes: &[u8] = match value {
            KeyValue::Str(s) => s.as_bytes(),
            KeyValue::Bytes(b) => b,
            other => return Ok(CursorBound::Value(other.clone())),
        };
        let dict = match self {
            Self::Str(Some(c)) => c.dictionary(),
            Self::Bytes(Some(c)) => c.dictionary(),
            // No column in this segment: every document is missing, so after any value.
            _ => return Ok(CursorBound::Value(value.clone())),
        };
        Ok(match dict.term_ord_or_next(bytes)? {
            TermOrdHit::Exact(ord) => CursorBound::Ord { ord, exact: true },
            TermOrdHit::Next(ord) => CursorBound::Ord { ord, exact: false },
        })
    }

    /// Converts a segment-local ordinal into the term it stands for.
    fn resolve(&self, value: KeyValue) -> Result<KeyValue, String> {
        let KeyValue::Ord(ord) = value else { return Ok(value) };
        match self {
            Self::Str(Some(c)) => {
                let mut s = String::new();
                c.ord_to_str(ord, &mut s).map_err(|e| e.to_string())?;
                Ok(KeyValue::Str(s))
            }
            Self::Bytes(Some(c)) => {
                let mut b = Vec::new();
                c.ord_to_bytes(ord, &mut b).map_err(|e| e.to_string())?;
                Ok(KeyValue::Bytes(b))
            }
            _ => Ok(KeyValue::Missing),
        }
    }
}

#[cfg(test)]
mod tests {
    use std::collections::HashMap;

    use serde_json::json;

    use super::*;
    use crate::testing::{Fixture, Rng};

    const SCHEMA: &str = r#"{"fields":[
        {"name":"id","type":"u64","fast":true,"stored":true},
        {"name":"n","type":"i64","fast":true},
        {"name":"f","type":"f64","fast":true},
        {"name":"s","type":"text","tokenizer":"raw","fast":true},
        {"name":"d","type":"date","fast":true},
        {"name":"flag","type":"bool","fast":true},
        {"name":"y","type":"bytes","fast":true},
        {"name":"ip","type":"ip","fast":true},
        {"name":"body","type":"text"},
        {"name":"slow","type":"u64"},
        {"name":"cat","type":"facet"}]}"#;

    /// A reference value of one document's sort field.
    #[derive(Clone, Debug)]
    enum V {
        U(u64),
        I(i64),
        F(f64),
        S(String),
        D(i64),
        B(bool),
        Y(Vec<u8>),
        Ip(Ipv6Addr),
    }

    fn natural_v(a: &V, b: &V) -> Ordering {
        match (a, b) {
            (V::U(x), V::U(y)) => x.cmp(y),
            (V::I(x), V::I(y)) | (V::D(x), V::D(y)) => x.cmp(y),
            (V::F(x), V::F(y)) => x.total_cmp(y),
            (V::S(x), V::S(y)) => x.as_bytes().cmp(y.as_bytes()),
            (V::B(x), V::B(y)) => x.cmp(y),
            (V::Y(x), V::Y(y)) => x.cmp(y),
            (V::Ip(x), V::Ip(y)) => x.cmp(y),
            _ => unreachable!("mixed kinds"),
        }
    }

    fn tagged_v(v: &Option<V>) -> Value {
        match v {
            None => Value::Null,
            Some(V::U(n)) => json!({ "u64": n }),
            Some(V::I(n)) => json!({ "i64": n }),
            Some(V::F(x)) => json!({ "f64": x }),
            Some(V::S(s)) => json!({ "str": s }),
            Some(V::D(us)) => json!({ "date": us }),
            Some(V::B(b)) => json!({ "bool": b }),
            Some(V::Y(b)) => json!({ "bytes": b }),
            Some(V::Ip(ip)) => json!({ "ip": ip_text(ip) }),
        }
    }

    type RefDoc = HashMap<&'static str, V>;

    /// Days since 1970-01-01 of a proleptic Gregorian date (Howard Hinnant's algorithm).
    fn days_from_civil(y: i64, m: i64, d: i64) -> i64 {
        let y = if m <= 2 { y - 1 } else { y };
        let era = y.div_euclid(400);
        let yoe = y - era * 400;
        let doy = (153 * (m + if m > 2 { -3 } else { 9 }) + 2) / 5 + d - 1;
        let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        era * 146097 + doe - 719468
    }

    /// Documents with random values (each field sometimes missing, with many ties), in segments of
    /// random sizes; segment 2 has no `s` values at all, so its text column doesn't exist.
    fn corpus(seed: u64) -> (Fixture, HashMap<u64, RefDoc>) {
        const STRS: [&str; 7] = ["a", "b", "ab", "B", "é", "zz", ""];
        const BYTES: [(&[u8], &str); 6] =
            [(&[0], "AA=="), (&[1], "AQ=="), (&[1, 0], "AQA="), (&[1, 2], "AQI="), (&[2], "Ag=="), (&[255], "/w==")];
        const IPS: [&str; 5] = ["10.0.0.1", "10.0.0.2", "::1", "2001:db8::1", "192.168.1.1"];
        let mut rng = Rng::new(seed);
        let mut reference = HashMap::new();
        let mut batches = Vec::new();
        let mut id = 0u64;
        for segment in 0..6 {
            let mut batch = Vec::new();
            for _ in 0..rng.below(120) + 1 {
                let mut doc = serde_json::Map::new();
                let mut r: RefDoc = HashMap::new();
                doc.insert("id".into(), json!(id));
                r.insert("id", V::U(id));
                if rng.chance(80) {
                    let n = rng.below(41) as i64 - 20;
                    doc.insert("n".into(), json!(n));
                    r.insert("n", V::I(n));
                }
                if rng.chance(90) {
                    let x = (rng.below(41) as f64 - 20.0) / 4.0;
                    doc.insert("f".into(), json!(x));
                    r.insert("f", V::F(x));
                }
                if segment != 2 && rng.chance(75) {
                    let s = STRS[rng.below(STRS.len() as u64) as usize];
                    doc.insert("s".into(), json!(s));
                    r.insert("s", V::S(s.into()));
                }
                if rng.chance(85) {
                    let (y, m, d) = (1960 + rng.below(80) as i64, 1 + rng.below(12) as i64, 1 + rng.below(28) as i64);
                    doc.insert("d".into(), json!(format!("{y:04}-{m:02}-{d:02}T00:00:00Z")));
                    r.insert("d", V::D(days_from_civil(y, m, d) * 86_400_000_000));
                }
                if rng.chance(70) {
                    let b = rng.chance(50);
                    doc.insert("flag".into(), json!(b));
                    r.insert("flag", V::B(b));
                }
                if rng.chance(70) {
                    let (bytes, b64) = BYTES[rng.below(BYTES.len() as u64) as usize];
                    doc.insert("y".into(), json!(b64));
                    r.insert("y", V::Y(bytes.to_vec()));
                }
                if rng.chance(70) {
                    let ip = IPS[rng.below(IPS.len() as u64) as usize];
                    doc.insert("ip".into(), json!(ip));
                    r.insert("ip", V::Ip(parse_ip(ip).unwrap()));
                }
                let mut body = "x ".repeat(rng.below(4) as usize + 1);
                body.push_str(&"filler ".repeat(rng.below(6) as usize));
                if rng.chance(50) {
                    body.push_str("half");
                }
                doc.insert("body".into(), json!(body));
                batch.push(Value::Object(doc).to_string());
                reference.insert(id, r);
                id += 1;
            }
            batches.push(batch);
        }
        (Fixture::from_owned(SCHEMA, &batches), reference)
    }

    struct Hit {
        id: u64,
        addr: (u32, u32),
        score: Option<f64>,
        sort: Value,
    }

    fn hits(result: &Value) -> Vec<Hit> {
        result["hits"]
            .as_array()
            .unwrap()
            .iter()
            .map(|h| Hit {
                id: h["doc"]["id"][0].as_u64().unwrap(),
                addr: (h["addr"][0].as_u64().unwrap() as u32, h["addr"][1].as_u64().unwrap() as u32),
                score: h["score"].as_f64(),
                sort: h["sort"].clone(),
            })
            .collect()
    }

    fn request(query: &str, sort: &str, limit: usize, offset: usize, extra: &str) -> String {
        format!(r#"{{"query":{query},"sort":{sort},"limit":{limit},"offset":{offset}{extra}}}"#)
    }

    /// Pages through the whole result with a cursor, `page` hits at a time.
    fn page_with_cursor(f: &Fixture, query: &str, sort: &str, page: usize) -> Vec<u64> {
        let mut ids = Vec::new();
        let mut after = String::new();
        loop {
            let result = f.search(&request(query, sort, page, 0, &after)).unwrap();
            let raw = result["hits"].as_array().unwrap();
            ids.extend(hits(&result).iter().map(|h| h.id));
            let Some(last) = raw.last() else { return ids };
            after = format!(r#","search_after":{{"keys":{},"addr":{}}}"#, last["sort"], last["addr"]);
            assert!(ids.len() <= 10_000, "cursor paging doesn't end");
        }
    }

    fn page_with_offset(f: &Fixture, query: &str, sort: &str, page: usize) -> Vec<u64> {
        let mut ids = Vec::new();
        loop {
            let result = f.search(&request(query, sort, page, ids.len(), "")).unwrap();
            let page_ids: Vec<u64> = hits(&result).iter().map(|h| h.id).collect();
            if page_ids.is_empty() {
                return ids;
            }
            ids.extend(page_ids);
        }
    }

    const ALL: &str = r#"{"type":"all"}"#;
    const HALF: &str = r#"{"type":"term","field":"body","value":{"str":"half"}}"#;

    fn sort_json(keys: &[(&str, bool)]) -> String {
        let keys: Vec<Value> = keys
            .iter()
            .map(|&(field, desc)| match field {
                "_score" => json!({"type": "score"}),
                _ => json!({"type": "field", "field": field, "order": if desc { "desc" } else { "asc" }}),
            })
            .collect();
        Value::Array(keys).to_string()
    }

    #[test]
    fn field_orders_match_a_reference_sort() {
        let orders: &[&[(&str, bool)]] = &[
            &[("n", false)],
            &[("n", true)],
            &[("f", false)],
            &[("f", true)],
            &[("d", true)],
            &[("flag", false)],
            &[("id", true)],
            &[("s", false)],
            &[("s", true)],
            &[("y", false)],
            &[("y", true)],
            &[("ip", true)],
            &[("s", false), ("n", true)],
            &[("flag", true), ("f", false), ("y", true)],
            &[("ip", false), ("s", true)],
        ];
        for seed in [1, 2, 3] {
            let (f, reference) = corpus(seed);
            let all = f.search(&format!(r#"{{"query":{ALL},"limit":100000}}"#)).unwrap();
            let addr: HashMap<u64, (u32, u32)> = hits(&all).iter().map(|h| (h.id, h.addr)).collect();
            assert_eq!(addr.len(), reference.len());

            for keys in orders {
                let sort = sort_json(keys);
                for query in [ALL, HALF] {
                    let mut expected: Vec<u64> = match query {
                        ALL => reference.keys().copied().collect(),
                        _ => hits(&f.search(&format!(r#"{{"query":{HALF},"limit":100000}}"#)).unwrap())
                            .iter()
                            .map(|h| h.id)
                            .collect(),
                    };
                    expected.sort_by(|a, b| {
                        let (ra, rb) = (&reference[a], &reference[b]);
                        keys.iter()
                            .map(|&(field, desc)| match (ra.get(field), rb.get(field)) {
                                (None, None) => Ordering::Equal,
                                (None, _) => Ordering::Greater,
                                (_, None) => Ordering::Less,
                                (Some(x), Some(y)) if desc => natural_v(x, y).reverse(),
                                (Some(x), Some(y)) => natural_v(x, y),
                            })
                            .find(|o| o.is_ne())
                            .unwrap_or(Ordering::Equal)
                            .then(addr[a].cmp(&addr[b]))
                    });

                    let full = f.search(&request(query, &sort, 100_000, 0, "")).unwrap();
                    assert_eq!(full["count"].as_u64().unwrap() as usize, expected.len());
                    let full_hits = hits(&full);
                    let full_ids: Vec<u64> = full_hits.iter().map(|h| h.id).collect();
                    assert_eq!(full_ids, expected, "seed {seed}, sort {sort}, query {query}");
                    for h in &full_hits {
                        let tagged: Vec<Value> =
                            keys.iter().map(|&(field, _)| tagged_v(&reference[&h.id].get(field).cloned())).collect();
                        assert_eq!(h.sort, Value::Array(tagged), "sort values of {}", h.id);
                        assert!(h.score.is_none(), "no score when not sorting by it");
                    }

                    for page in [1, 7, 50] {
                        assert_eq!(page_with_cursor(&f, query, &sort, page), expected, "cursor, page {page}, {sort}");
                    }
                    assert_eq!(page_with_offset(&f, query, &sort, 13), expected, "offset, {sort}");
                    // With facets requested, the same collectors run inside a MultiCollector.
                    let multi = f.search(&request(query, &sort, 100_000, 0, r#","facets":[]"#)).unwrap();
                    assert_eq!(hits(&multi).iter().map(|h| h.id).collect::<Vec<_>>(), expected, "multi, {sort}");
                }
            }
        }
    }

    #[test]
    fn score_orders_page_consistently() {
        let (f, _) = corpus(7);
        for sort in [
            sort_json(&[("_score", true)]),
            sort_json(&[("_score", true), ("n", false)]),
            sort_json(&[("n", true), ("_score", true)]),
        ] {
            let full = hits(&f.search(&request(HALF, &sort, 100_000, 0, "")).unwrap());
            let ids: Vec<u64> = full.iter().map(|h| h.id).collect();
            assert!(full.iter().all(|h| h.score.is_some()), "scores are returned when sorting by them");
            if sort.starts_with(r#"[{"type":"score"}"#) {
                assert!(full.windows(2).all(|w| w[0].score >= w[1].score), "{sort}: by score, descending");
            }
            for page in [1, 5, 64] {
                assert_eq!(page_with_cursor(&f, HALF, &sort, page), ids, "{sort}, page {page}");
            }
            assert_eq!(page_with_offset(&f, HALF, &sort, 9), ids);
        }
        // Without a sort, the relevance path (TopDocs) agrees with the score key.
        let plain = f.search(&format!(r#"{{"query":{HALF},"limit":100000}}"#)).unwrap();
        let by_score = f.search(&request(HALF, &sort_json(&[("_score", true)]), 100_000, 0, "")).unwrap();
        let scores = |r: &Value| hits(r).iter().map(|h| h.score.unwrap()).collect::<Vec<_>>();
        assert_eq!(scores(&plain), scores(&by_score));
        // A cursor without a sort pages by score too.
        let first = &plain["hits"][0];
        let after = format!(r#","search_after":{{"keys":{},"addr":{}}}"#, first["sort"], first["addr"]);
        let rest = f.search(&format!(r#"{{"query":{HALF},"limit":100000{after}}}"#)).unwrap();
        assert_eq!(hits(&rest).len(), hits(&plain).len() - 1);
    }

    #[test]
    fn plan_picks_the_cheapest_collector() {
        let (f, _) = corpus(1);
        let schema = f.index.schema();
        let keys = |json: &str| serde_json::from_str::<Vec<SortKeyRequest>>(json).unwrap();
        let plan_for = |json: &str| plan(&keys(json), None, 10, 0, &schema).ok().unwrap();
        assert!(matches!(plan_for("[]"), TopPlan::Score { limit: 10, offset: 0 }));
        for numeric in ["id", "n", "f", "d", "flag"] {
            let json = format!(r#"[{{"type":"field","field":"{numeric}","order":"desc"}}]"#);
            assert!(matches!(plan_for(&json), TopPlan::Fast(_)), "{numeric}");
        }
        for other in ["s", "y", "ip"] {
            let json = format!(r#"[{{"type":"field","field":"{other}"}}]"#);
            assert!(matches!(plan_for(&json), TopPlan::Sorted(_)), "{other}");
        }
        assert!(matches!(plan_for(r#"[{"type":"score"}]"#), TopPlan::Sorted(_)));
        assert!(matches!(
            plan_for(r#"[{"type":"field","field":"n"},{"type":"field","field":"f"}]"#),
            TopPlan::Sorted(_)
        ));
    }

    #[test]
    fn unsortable_fields_and_mismatched_cursors_are_errors() {
        let (f, _) = corpus(1);
        let invalid = |req: &str, fragment: &str| {
            let e = f.search(req).err().unwrap_or_else(|| panic!("{req} succeeded"));
            assert_eq!(e.code, crate::ffi::status::INVALID_ARGUMENT, "{req}: {}", e.message);
            assert!(e.message.contains(fragment), "{req}: '{}' lacks '{fragment}'", e.message);
        };
        let by = |field: &str| format!(r#"{{"query":{ALL},"sort":[{{"type":"field","field":"{field}"}}]}}"#);
        invalid(&by("slow"), "isn't a fast field");
        invalid(&by("body"), "isn't a fast field");
        invalid(&by("cat"), "type isn't supported");
        invalid(&by("nope"), "nope");
        invalid(
            &format!(r#"{{"query":{ALL},"sort":[{{"type":"field","field":"n","order":"up"}}]}}"#),
            "invalid search request",
        );

        let after = |sort: &str, keys: &str| {
            format!(r#"{{"query":{ALL},"sort":{sort},"search_after":{{"keys":{keys},"addr":[0,0]}}}}"#)
        };
        let n = sort_json(&[("n", false)]);
        let s = sort_json(&[("s", false)]);
        let y = sort_json(&[("y", false)]);
        invalid(&after(&n, "[]"), "doesn't match the sort order");
        invalid(&after(&n, r#"[{"i64":1},{"i64":2}]"#), "doesn't match the sort order");
        invalid(&after(&n, r#"[{"u64":1}]"#), "doesn't match the sort order");
        invalid(&after(&n, r#"[{"i64":"1"}]"#), "doesn't match the sort order");
        invalid(&after(&n, r#"[{"i64":1,"u64":1}]"#), "doesn't match the sort order");
        invalid(&after(&n, r#"[1]"#), "doesn't match the sort order");
        invalid(
            &after(&sort_json(&[("d", false)]), &format!(r#"[{{"date":{}}}]"#, i64::MAX)),
            "doesn't match the sort order",
        );
        invalid(&after(&s, r#"[{"str":1}]"#), "doesn't match the sort order");
        invalid(&after(&y, r#"[{"bytes":[256]}]"#), "doesn't match the sort order");
        invalid(&after(&sort_json(&[("ip", false)]), r#"[{"ip":"nope"}]"#), "doesn't match the sort order");
        invalid(&after("[]", r#"[{"i64":1}]"#), "doesn't match the sort order");
        // A null key (missing value) and out-of-range addresses are valid cursors.
        for (sort, keys) in [(&n, "[null]"), (&s, r#"[{"str":"not in any segment"}]"#), (&y, r#"[{"bytes":[]}]"#)] {
            let req = after(sort, keys).replace("[0,0]", "[4294967295,4294967295]");
            f.search(&req).unwrap_or_else(|e| panic!("{req}: {}", e.message));
        }
    }

    #[test]
    fn fast_keys_pack_in_order() {
        fn packed(kind: Kind, desc: bool, values: &[KeyValue]) -> Vec<u128> {
            let top = FastTop { field: String::new(), kind, desc, cursor: None, limit: 1, offset: 0 };
            values
                .iter()
                .map(|v| {
                    let p = top.pack(to_u64(v), 7);
                    // And back.
                    assert_eq!(format!("{:?}", top.unpack(p)), format!("{v:?}"));
                    p
                })
                .collect()
        }
        let cases: Vec<(Kind, Vec<KeyValue>)> = vec![
            (Kind::U64, [0, 1, u64::MAX - 1, u64::MAX].map(KeyValue::U64).to_vec()),
            (Kind::I64, [i64::MIN, -1, 0, 1, i64::MAX].map(KeyValue::I64).to_vec()),
            (Kind::F64, [f64::NEG_INFINITY, -1.5, -0.0, 0.0, 1e-300, 1.5, f64::INFINITY].map(KeyValue::F64).to_vec()),
            (Kind::Bool, [false, true].map(KeyValue::Bool).to_vec()),
            (Kind::Date, [-86_400_000_000, 0, 1_000_000].map(KeyValue::Date).to_vec()),
        ];
        for (kind, mut values) in cases {
            values.push(KeyValue::Missing);
            let asc = packed(kind, false, &values);
            assert!(asc.windows(2).all(|w| w[0] < w[1]), "{kind:?} ascending: {values:?}");
            let desc = packed(kind, true, &values);
            let (present, missing) = desc.split_at(desc.len() - 1);
            assert!(present.windows(2).all(|w| w[0] > w[1]), "{kind:?} descending");
            assert!(present.iter().all(|p| p < &missing[0]), "{kind:?}: missing last when descending");
        }
    }

    #[test]
    fn missing_values_sort_last_in_both_orders() {
        for desc in [false, true] {
            assert_eq!(cmp_value(&KeyValue::Missing, &KeyValue::I64(0), desc), Ordering::Greater);
            assert_eq!(cmp_value(&KeyValue::I64(0), &KeyValue::Missing, desc), Ordering::Less);
            assert_eq!(cmp_value(&KeyValue::Missing, &KeyValue::Missing, desc), Ordering::Equal);
        }
        assert_eq!(cmp_value(&KeyValue::Str("a".into()), &KeyValue::Str("b".into()), true), Ordering::Greater);
        assert_eq!(cmp_value(&KeyValue::F64(f64::NAN), &KeyValue::F64(f64::INFINITY), false), Ordering::Greater);
    }

    #[test]
    fn huge_pages_neither_overflow_nor_reserve_memory_up_front() {
        let (f, reference) = corpus(4);
        for sort in ["[]", &sort_json(&[("n", true)]), &sort_json(&[("s", false)])] {
            let all = f.search(&request(ALL, sort, i32::MAX as usize, 0, "")).unwrap();
            assert_eq!(hits(&all).len(), reference.len(), "{sort}");
            let none = f.search(&request(ALL, sort, i32::MAX as usize, i32::MAX as usize, "")).unwrap();
            assert_eq!(hits(&none).len(), 0);
            assert_eq!(none["count"].as_u64().unwrap() as usize, reference.len());
        }
        // Clamped to the snapshot, so even values .NET can't send neither overflow nor fail.
        for (limit, offset, expected) in [(u64::MAX, 0, reference.len()), (u64::MAX, u64::MAX, 0), (1, u64::MAX, 0)] {
            let r = f.search(&format!(r#"{{"query":{ALL},"limit":{limit},"offset":{offset}}}"#)).unwrap();
            assert_eq!(hits(&r).len(), expected);
        }
        // An empty page still validates the sort.
        let e = f.search(&format!(r#"{{"query":{ALL},"offset":100000,"sort":[{{"type":"field","field":"body"}}]}}"#));
        assert!(e.err().unwrap().message.contains("isn't a fast field"));
        // limit 0 only counts, whatever the offset.
        let count = f.search(&format!(r#"{{"query":{ALL},"limit":0,"offset":{}}}"#, u32::MAX)).unwrap();
        assert_eq!(count["count"].as_u64().unwrap() as usize, reference.len());
    }
}
