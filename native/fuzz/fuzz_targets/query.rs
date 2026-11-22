//! Arbitrary bytes as query JSON (see `tantivy_dotnet::fuzzing::query`). Seeds: corpus/query, dictionary: json.dict.
#![no_main]

use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    if let Ok(json) = std::str::from_utf8(data) {
        tantivy_dotnet::fuzzing::query(json);
    }
});
