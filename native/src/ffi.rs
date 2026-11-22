//! Plumbing shared by every export: status codes, error conversion, panic containment, and
//! reading pointer + length arguments.

use std::panic::{catch_unwind, AssertUnwindSafe};

use tantivy::aggregation::AggregationError;
use tantivy::TantivyError;

use crate::TdnString;

/// Status codes returned by every fallible export. Mirrored by `TantivyErrorCode` in C#.
pub mod status {
    pub const OK: i32 = 0;
    pub const ERROR: i32 = 1;
    pub const INVALID_ARGUMENT: i32 = 2;
    pub const QUERY_PARSE: i32 = 3;
    pub const INDEX_ALREADY_EXISTS: i32 = 4;
    pub const IO: i32 = 5;
    pub const LOCK_BUSY: i32 = 6;
    pub const PANIC: i32 = 7;
}

#[derive(Debug)]
pub struct FfiError {
    pub code: i32,
    pub message: String,
}

impl FfiError {
    pub fn new(code: i32, message: impl Into<String>) -> Self {
        Self { code, message: message.into() }
    }

    pub fn invalid(message: impl Into<String>) -> Self {
        Self::new(status::INVALID_ARGUMENT, message)
    }
}

impl From<TantivyError> for FfiError {
    fn from(e: TantivyError) -> Self {
        let code = match &e {
            TantivyError::IndexAlreadyExists => status::INDEX_ALREADY_EXISTS,
            TantivyError::LockFailure(..) => status::LOCK_BUSY,
            TantivyError::OpenDirectoryError(_)
            | TantivyError::OpenReadError(_)
            | TantivyError::OpenWriteError(_)
            | TantivyError::IoError(_) => status::IO,
            TantivyError::FieldNotFound(_)
            | TantivyError::InvalidArgument(_)
            | TantivyError::SchemaError(_)
            | TantivyError::AggregationError(
                AggregationError::InvalidRequest(_) | AggregationError::DateHistogramParseError(_),
            ) => status::INVALID_ARGUMENT,
            _ => status::ERROR,
        };
        Self::new(code, e.to_string())
    }
}

pub type FfiResult<T> = Result<T, FfiError>;

/// Runs `f`, converting both errors and panics into a status code, and writes the error message
/// (if any) to `err_out`. No panic escapes this function.
pub fn guard(err_out: *mut TdnString, f: impl FnOnce() -> FfiResult<()>) -> i32 {
    let (code, message) = match catch_unwind(AssertUnwindSafe(f)) {
        Ok(Ok(())) => return status::OK,
        Ok(Err(e)) => (e.code, e.message),
        Err(payload) => {
            let detail = payload
                .downcast_ref::<&str>()
                .map(|s| s.to_string())
                .or_else(|| payload.downcast_ref::<String>().cloned())
                .unwrap_or_else(|| "unknown panic payload".to_owned());
            (status::PANIC, format!("native panic: {detail}"))
        }
    };
    if !err_out.is_null() {
        // Writing a POD struct through a caller-provided pointer; cannot panic.
        unsafe { err_out.write(TdnString::from_string(message)) };
    }
    code
}

/// Borrows a UTF-8 argument passed as pointer + length. `len == 0` accepts a null pointer.
///
/// # Safety
/// `ptr` must point to `len` readable bytes that stay valid for the duration of the call.
pub unsafe fn str_arg<'a>(ptr: *const u8, len: usize, what: &str) -> FfiResult<&'a str> {
    if len == 0 {
        return Ok("");
    }
    if ptr.is_null() {
        return Err(FfiError::invalid(format!("{what}: null pointer with non-zero length")));
    }
    std::str::from_utf8(std::slice::from_raw_parts(ptr, len))
        .map_err(|e| FfiError::invalid(format!("{what}: invalid UTF-8 ({e})")))
}

/// Borrows a handle created by one of the `*_create`/`*_open` exports.
///
/// # Safety
/// `ptr` must be null or a live handle of type `T` produced by this library.
pub unsafe fn handle<'a, T>(ptr: *const T, what: &str) -> FfiResult<&'a T> {
    ptr.as_ref().ok_or_else(|| FfiError::invalid(format!("{what} handle is null")))
}

/// Writes `value` to an out-parameter, failing cleanly if the caller passed null.
///
/// # Safety
/// `out` must be null or valid for writes.
pub unsafe fn write_out<T>(out: *mut T, value: T, what: &str) -> FfiResult<()> {
    if out.is_null() {
        return Err(FfiError::invalid(format!("{what} out-parameter is null")));
    }
    out.write(value);
    Ok(())
}

/// Drops a handle created with `Box::into_raw`, swallowing any panic from its destructor.
///
/// # Safety
/// `ptr` must be null or a live handle of type `T` produced by this library, not freed before.
pub unsafe fn free_box<T>(ptr: *mut T) {
    if !ptr.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| drop(Box::from_raw(ptr))));
    }
}
