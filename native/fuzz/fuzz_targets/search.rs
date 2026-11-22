//! Arbitrary bytes as search JSON (see `tantivy_dotnet::fuzzing::search`). Seeds: corpus/search, dictionary: json.dict.
#![no_main]

use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    if let Ok(json) = std::str::from_utf8(data) {
        tantivy_dotnet::fuzzing::search(json);
    }
});
