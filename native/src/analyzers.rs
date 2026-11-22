//! Custom analyzers (tokenizer + token filters), described by our own JSON and built from
//! Tantivy's parts. Tantivy doesn't persist tokenizers, so an on-disk index keeps the
//! definitions in `ANALYZERS_FILE` next to its segments, and `Open` registers them again.
//!
//! ```json
//! {"en_text": {"tokenizer": {"type": "simple"},
//!              "filters": [{"type": "lowercase"}, {"type": "stop_words", "language": "english"},
//!                          {"type": "stemmer", "language": "english"}, {"type": "remove_long", "limit": 40}]}}
//! ```

use std::collections::BTreeMap;
use std::io::Write;
use std::path::Path;

use serde::{Deserialize, Serialize};
use tantivy::tokenizer::{
    AsciiFoldingFilter, Language, LowerCaser, NgramTokenizer, RawTokenizer, RegexTokenizer, RemoveLongFilter,
    SimpleTokenizer, Stemmer, StopWordFilter, TextAnalyzer, TextAnalyzerBuilder, TokenizerManager, WhitespaceTokenizer,
};
use tantivy::Index;

use crate::ffi::{status, FfiError, FfiResult};

/// Tokenizers every Tantivy index registers; custom analyzers can't reuse these names.
pub const BUILT_IN: [&str; 4] = ["default", "raw", "en_stem", "whitespace"];

/// Written in the index directory by TantivyDotNet. Tantivy's garbage collector only deletes
/// files it manages, so this file is left alone.
pub const ANALYZERS_FILE: &str = "tantivy-dotnet.json";

pub type Analyzers = BTreeMap<String, AnalyzerSpec>;

#[derive(Serialize, Deserialize, Clone)]
pub struct AnalyzerSpec {
    tokenizer: TokenizerSpec,
    #[serde(default)]
    filters: Vec<FilterSpec>,
}

#[derive(Serialize, Deserialize, Clone)]
#[serde(tag = "type", rename_all = "snake_case")]
enum TokenizerSpec {
    Simple,
    Whitespace,
    Raw,
    Ngram {
        min_gram: usize,
        max_gram: usize,
        #[serde(default)]
        prefix_only: bool,
    },
    Regex {
        pattern: String,
    },
}

#[derive(Serialize, Deserialize, Clone)]
#[serde(tag = "type", rename_all = "snake_case")]
enum FilterSpec {
    Lowercase,
    AsciiFolding,
    StopWords {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        language: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        words: Option<Vec<String>>,
    },
    Stemmer {
        language: String,
    },
    RemoveLong {
        limit: usize,
    },
}

/// The on-disk file: versioned so a later release can extend it.
#[derive(Serialize, Deserialize)]
struct AnalyzersFile {
    version: u32,
    analyzers: Analyzers,
}

/// Checks every definition (so a bad one fails at schema creation, not at first use) and that
/// each text field's tokenizer exists.
pub fn validate<'a>(analyzers: &Analyzers, tokenizers_used: impl Iterator<Item = (&'a str, &'a str)>) -> FfiResult<()> {
    for (name, spec) in analyzers {
        if name.is_empty() {
            return Err(FfiError::invalid("analyzer names can't be empty"));
        }
        if BUILT_IN.contains(&name.as_str()) {
            return Err(FfiError::invalid(format!("'{name}' is a built-in tokenizer and can't be redefined")));
        }
        build(spec).map_err(|e| FfiError::invalid(format!("analyzer '{name}': {}", e.message)))?;
    }
    for (field, tokenizer) in tokenizers_used {
        if !BUILT_IN.contains(&tokenizer) && !analyzers.contains_key(tokenizer) {
            return Err(FfiError::invalid(format!(
                "field '{field}' uses tokenizer '{tokenizer}', which is neither built in ({}) nor defined in the schema",
                BUILT_IN.join(", ")
            )));
        }
    }
    Ok(())
}

pub fn register(manager: &TokenizerManager, analyzers: &Analyzers) -> FfiResult<()> {
    for (name, spec) in analyzers {
        manager.register(name, build(spec)?);
    }
    Ok(())
}

/// Saves the definitions next to a freshly created index (write to a temporary file, then rename).
pub fn save(dir: &Path, analyzers: &Analyzers) -> FfiResult<()> {
    let json = serde_json::to_vec(&AnalyzersFile { version: 1, analyzers: analyzers.clone() })
        .map_err(|e| FfiError::new(status::ERROR, format!("analyzer serialization: {e}")))?;
    let tmp = dir.join(format!("{ANALYZERS_FILE}.tmp"));
    let io = |e: std::io::Error| FfiError::new(status::IO, format!("writing {ANALYZERS_FILE}: {e}"));
    let mut file = std::fs::File::create(&tmp).map_err(io)?;
    file.write_all(&json).map_err(io)?;
    file.sync_all().map_err(io)?;
    drop(file);
    std::fs::rename(&tmp, dir.join(ANALYZERS_FILE)).map_err(io)
}

/// Loads the definitions saved with an index, or none if it was created without TantivyDotNet
/// analyzers (or by another Tantivy client).
pub fn load(dir: &Path) -> FfiResult<Analyzers> {
    let path = dir.join(ANALYZERS_FILE);
    let bytes = match std::fs::read(&path) {
        Ok(b) => b,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(Analyzers::new()),
        Err(e) => return Err(FfiError::new(status::IO, format!("reading {ANALYZERS_FILE}: {e}"))),
    };
    let file: AnalyzersFile = serde_json::from_slice(&bytes).map_err(|e| {
        FfiError::new(status::ERROR, format!("{} is corrupt or from a newer version: {e}", path.display()))
    })?;
    if file.version != 1 {
        return Err(FfiError::new(
            status::ERROR,
            format!("{} has version {}, which this TantivyDotNet doesn't support", path.display(), file.version),
        ));
    }
    Ok(file.analyzers)
}

/// Runs `text` through the index's tokenizer `name` and returns the token texts.
pub fn analyze(index: &Index, name: &str, text: &str) -> FfiResult<Vec<String>> {
    let mut analyzer =
        index.tokenizers().get(name).ok_or_else(|| FfiError::invalid(format!("no tokenizer named '{name}'")))?;
    let mut stream = analyzer.token_stream(text);
    let mut tokens = Vec::new();
    while stream.advance() {
        tokens.push(stream.token().text.clone());
    }
    Ok(tokens)
}

fn build(spec: &AnalyzerSpec) -> FfiResult<TextAnalyzer> {
    let mut builder: TextAnalyzerBuilder = match &spec.tokenizer {
        TokenizerSpec::Simple => TextAnalyzer::builder(SimpleTokenizer::default()).dynamic(),
        TokenizerSpec::Whitespace => TextAnalyzer::builder(WhitespaceTokenizer::default()).dynamic(),
        TokenizerSpec::Raw => TextAnalyzer::builder(RawTokenizer::default()).dynamic(),
        TokenizerSpec::Ngram { min_gram, max_gram, prefix_only } => {
            if *min_gram == 0 || min_gram > max_gram {
                return Err(FfiError::invalid(format!(
                    "ngram needs 0 < min_gram <= max_gram, got {min_gram} and {max_gram}"
                )));
            }
            let t = NgramTokenizer::new(*min_gram, *max_gram, *prefix_only)
                .map_err(|e| FfiError::invalid(format!("ngram: {e}")))?;
            TextAnalyzer::builder(t).dynamic()
        }
        TokenizerSpec::Regex { pattern } => {
            let t = RegexTokenizer::new(pattern).map_err(|e| FfiError::invalid(format!("regex: {e}")))?;
            TextAnalyzer::builder(t).dynamic()
        }
    };
    for filter in &spec.filters {
        builder = match filter {
            FilterSpec::Lowercase => builder.filter_dynamic(LowerCaser),
            FilterSpec::AsciiFolding => builder.filter_dynamic(AsciiFoldingFilter),
            FilterSpec::StopWords { language: Some(lang), words: None } => {
                let lang = language(lang)?;
                let f = StopWordFilter::new(lang)
                    .ok_or_else(|| FfiError::invalid(format!("Tantivy has no stop-word list for {lang:?}")))?;
                builder.filter_dynamic(f)
            }
            FilterSpec::StopWords { language: None, words: Some(words) } => {
                builder.filter_dynamic(StopWordFilter::remove(words.iter().cloned()))
            }
            FilterSpec::StopWords { .. } => {
                return Err(FfiError::invalid("stop_words needs exactly one of 'language' and 'words'"))
            }
            FilterSpec::Stemmer { language: lang } => builder.filter_dynamic(Stemmer::new(language(lang)?)),
            FilterSpec::RemoveLong { limit } => {
                if *limit == 0 {
                    return Err(FfiError::invalid("remove_long limit must be positive"));
                }
                builder.filter_dynamic(RemoveLongFilter::limit(*limit))
            }
        };
    }
    Ok(builder.build())
}

fn language(name: &str) -> FfiResult<Language> {
    use Language::*;
    Ok(match name {
        "arabic" => Arabic,
        "danish" => Danish,
        "dutch" => Dutch,
        "english" => English,
        "finnish" => Finnish,
        "french" => French,
        "german" => German,
        "greek" => Greek,
        "hungarian" => Hungarian,
        "italian" => Italian,
        "norwegian" => Norwegian,
        "portuguese" => Portuguese,
        "romanian" => Romanian,
        "russian" => Russian,
        "spanish" => Spanish,
        "swedish" => Swedish,
        "tamil" => Tamil,
        "turkish" => Turkish,
        other => return Err(FfiError::invalid(format!("unknown language '{other}'"))),
    })
}
