//! Arbitrary bytes as schema JSON (see `tantivy_dotnet::fuzzing::schema`). Seeds: corpus/schema, dictionary: json.dict.
#![no_main]

use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    if let Ok(json) = std::str::from_utf8(data) {
        tantivy_dotnet::fuzzing::schema(json);
    }
});
