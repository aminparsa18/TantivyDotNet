//! The directory on-disk indexes use: Tantivy's `MmapDirectory` with an `atomic_write` that works
//! on Windows while other handles have the file open.
//!
//! Tantivy replaces `meta.json` (each commit and merge) and `.managed.json` (each new file) by
//! writing a temp file and renaming it over the old one. Its rename is `MoveFileExW`, which fails
//! with "Access is denied" while any other handle has the target open: a searcher reloading
//! meta.json, or another `SearchIndex` opening the directory. A commit then fails, and on Windows
//! that happened within a second of creating searchers during commits (step 9.5).
//!
//! Here the rename is `std::fs::rename`, which on Windows 10+ uses POSIX semantics: the target is
//! replaced even while open, since Rust opens files with `FILE_SHARE_DELETE`. Where POSIX
//! semantics aren't supported (FAT, some network shares) the error is retried for a while, as
//! readers keep the file open only for the time it takes to read it.

use std::fs::OpenOptions;
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use tantivy::directory::error::{DeleteError, LockError, OpenReadError, OpenWriteError};
use tantivy::directory::{DirectoryLock, FileHandle, Lock, MmapDirectory, WatchCallback, WatchHandle, WritePtr};
use tantivy::Directory;

/// How long a write or read that fails with "Access is denied" is retried.
const RETRY_FOR: Duration = Duration::from_secs(3);

#[derive(Clone, Debug)]
pub struct SafeDirectory {
    inner: MmapDirectory,
    root: PathBuf,
}

impl SafeDirectory {
    pub fn open(path: &Path) -> tantivy::Result<Self> {
        Ok(Self { inner: MmapDirectory::open(path)?, root: path.to_path_buf() })
    }
}

/// Runs `op` until it succeeds, fails with something other than `PermissionDenied`, or
/// `RETRY_FOR` has passed.
fn retry_denied<T>(mut op: impl FnMut() -> io::Result<T>) -> io::Result<T> {
    let start = Instant::now();
    let mut pause = Duration::from_millis(1);
    loop {
        match op() {
            Err(e) if e.kind() == io::ErrorKind::PermissionDenied && start.elapsed() < RETRY_FOR => {
                std::thread::sleep(pause);
                pause = (pause * 2).min(Duration::from_millis(50));
            }
            result => return result,
        }
    }
}

/// Writes `content` to a temp file next to `path`, syncs it, and renames it over `path`.
fn atomic_write(path: &Path, content: &[u8]) -> io::Result<()> {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let name = path.file_name().map_or_else(Default::default, |n| n.to_string_lossy());
    // Starts with '.' like Tantivy's own temp files; never registered as a managed file.
    let temp =
        path.with_file_name(format!(".{name}.{}.{}.tmp", std::process::id(), COUNTER.fetch_add(1, Ordering::Relaxed)));
    let result = (|| {
        let mut file = OpenOptions::new().write(true).create_new(true).open(&temp)?;
        file.write_all(content)?;
        file.sync_data()?;
        drop(file);
        retry_denied(|| std::fs::rename(&temp, path))
    })();
    if result.is_err() {
        let _ = std::fs::remove_file(&temp);
    }
    result
}

impl Directory for SafeDirectory {
    fn get_file_handle(&self, path: &Path) -> Result<Arc<dyn FileHandle>, OpenReadError> {
        self.inner.get_file_handle(path)
    }

    fn delete(&self, path: &Path) -> Result<(), DeleteError> {
        self.inner.delete(path)
    }

    fn exists(&self, path: &Path) -> Result<bool, OpenReadError> {
        self.inner.exists(path)
    }

    fn open_write(&self, path: &Path) -> Result<WritePtr, OpenWriteError> {
        self.inner.open_write(path)
    }

    fn atomic_read(&self, path: &Path) -> Result<Vec<u8>, OpenReadError> {
        let full = self.root.join(path);
        retry_denied(|| std::fs::read(&full)).map_err(|e| match e.kind() {
            io::ErrorKind::NotFound => OpenReadError::FileDoesNotExist(path.to_path_buf()),
            _ => OpenReadError::wrap_io_error(e, path.to_path_buf()),
        })
    }

    fn atomic_write(&self, path: &Path, data: &[u8]) -> io::Result<()> {
        atomic_write(&self.root.join(path), data)
    }

    fn acquire_lock(&self, lock: &Lock) -> Result<DirectoryLock, LockError> {
        self.inner.acquire_lock(lock)
    }

    fn watch(&self, callback: WatchCallback) -> tantivy::Result<WatchHandle> {
        self.inner.watch(callback)
    }

    fn sync_directory(&self) -> io::Result<()> {
        self.inner.sync_directory()
    }
}

#[cfg(test)]
mod tests {
    use std::io::Read;
    use std::path::Path;

    use tantivy::Directory;

    use super::SafeDirectory;

    /// A fresh directory under the system temp dir, removed on drop.
    struct TempDir(std::path::PathBuf);

    impl TempDir {
        fn new() -> Self {
            let path = std::env::temp_dir().join(format!(
                "tdn-dir-test-{}-{:?}",
                std::process::id(),
                std::thread::current().id()
            ));
            let _ = std::fs::remove_dir_all(&path);
            std::fs::create_dir_all(&path).unwrap();
            Self(path)
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn atomic_write_replaces_a_file_another_handle_has_open() {
        let tmp = TempDir::new();
        let dir = SafeDirectory::open(&tmp.0).unwrap();
        let meta = Path::new("meta.json");
        dir.atomic_write(meta, b"one").unwrap();
        // A reader in the middle of reading it (on Windows, MoveFileExW fails here).
        let mut open = std::fs::File::open(tmp.0.join(meta)).unwrap();
        dir.atomic_write(meta, b"two").unwrap();
        assert_eq!(dir.atomic_read(meta).unwrap(), b"two");
        // The open handle still reads the old content.
        let mut old = String::new();
        open.read_to_string(&mut old).unwrap();
        assert_eq!(old, "one");
        drop(open);
        // No temp file left behind.
        let names: Vec<_> = std::fs::read_dir(&tmp.0).unwrap().map(|e| e.unwrap().file_name()).collect();
        assert_eq!(names, ["meta.json"]);
        assert!(matches!(
            dir.atomic_read(Path::new("missing.json")),
            Err(tantivy::directory::error::OpenReadError::FileDoesNotExist(_))
        ));
    }
}
