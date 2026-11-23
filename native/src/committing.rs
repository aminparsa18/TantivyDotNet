//! Commit and rollback for a writer, without half-applied states.
//!
//! Tantivy marks a commit's segments as committed in memory before it writes meta.json. When that
//! write fails (an IO error; on Windows "Access is denied" while another handle has the file
//! open), the writer's memory and the disk disagree, and the next merge to finish writes
//! meta.json from memory: it publishes the failed commit, minus the deletes that landed on the
//! merged segments. So a failed commit rolls the writer back to the last successful commit,
//! which also stops merges that are running from publishing anything (they belong to the old,
//! killed segment updater).

use tantivy::IndexWriter;

use crate::ffi::{FfiError, FfiResult};
use crate::merging::MergePolicySpec;

/// Commits; on failure rolls back to the last successful commit and returns the commit's error.
pub fn commit(writer: &mut IndexWriter, policy: &MergePolicySpec) -> FfiResult<()> {
    let Err(error) = writer.commit() else { return Ok(()) };
    let mut error = FfiError::from(error);
    match rollback(writer, policy) {
        Ok(()) => error.message.push_str(" (the writer was rolled back: changes since the last commit were discarded)"),
        Err(e) => error
            .message
            .push_str(&format!(" (rolling back failed too: {}; dispose the writer and create a new one)", e.message)),
    }
    Err(error)
}

/// Discards everything since the last commit. Tantivy's rollback replaces the writer with a new
/// one, which has the default merge policy, so ours is set again.
pub fn rollback(writer: &mut IndexWriter, policy: &MergePolicySpec) -> FfiResult<()> {
    writer.rollback()?;
    if !matches!(policy, MergePolicySpec::Default) {
        writer.set_merge_policy(policy.build());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::Ordering;

    use tantivy::indexer::NoMergePolicy;
    use tantivy::schema::{Schema, FAST, INDEXED, STORED};
    use tantivy::{doc, Index, IndexWriter, ReloadPolicy, Term};

    use crate::merging::MergePolicySpec;
    use crate::testing::FailingDirectory;

    const NONE: MergePolicySpec = MergePolicySpec::None;

    /// Tantivy marks a commit's segments committed in memory before writing meta.json. If that
    /// write fails and a merge then finishes, the merge publishes the failed commit's new segments
    /// but not its deletes on the merged segments: a document count no commit had (step 9.5).
    #[test]
    fn a_failed_commit_never_becomes_visible() {
        let mut builder = Schema::builder();
        let id = builder.add_u64_field("id", INDEXED | STORED | FAST);
        let dir = FailingDirectory::default();
        let index = Index::create(dir.clone(), builder.build(), Default::default()).unwrap();
        let reader = index.reader_builder().reload_policy(ReloadPolicy::Manual).try_into().unwrap();
        let mut writer: IndexWriter = index.writer_with_num_threads(1, 15_000_000).unwrap();
        writer.set_merge_policy(Box::new(NoMergePolicy));
        let add = |w: &IndexWriter, ids: std::ops::Range<u64>| {
            for i in ids {
                w.add_document(doc!(id => i)).unwrap();
            }
        };
        add(&writer, 0..10);
        super::commit(&mut writer, &NONE).unwrap();
        add(&writer, 10..20);
        super::commit(&mut writer, &NONE).unwrap();
        reader.reload().unwrap();
        assert_eq!(reader.searcher().num_docs(), 20);
        let committed: Vec<_> = reader.searcher().segment_readers().iter().map(|s| s.segment_id()).collect();

        // 10 more documents and one delete; meta.json can't be written.
        add(&writer, 20..30);
        writer.delete_term(Term::from_field_u64(id, 0));
        dir.fail_meta.store(true, Ordering::SeqCst);
        let error = super::commit(&mut writer, &NONE).unwrap_err();
        assert!(error.message.contains("Access is denied (test)"), "{}", error.message);
        assert!(error.message.contains("rolled back"), "{}", error.message);
        dir.fail_meta.store(false, Ordering::SeqCst);
        // The rollback kept our merge policy (Tantivy's new writer has the default one).
        assert_eq!(format!("{:?}", writer.get_merge_policy()), "NoMergePolicy");

        // A merge of the two committed segments rewrites meta.json.
        writer.merge(&committed).wait().unwrap();
        reader.reload().unwrap();
        // 20 = the last successful commit. Plain Tantivy shows 29 here: the failed commit,
        // published by the merge.
        assert_eq!(reader.searcher().num_docs(), 20, "a merge published the failed commit");

        // The writer still works: a new commit publishes only what was added after the failure.
        add(&writer, 30..35);
        super::commit(&mut writer, &NONE).unwrap();
        reader.reload().unwrap();
        assert_eq!(reader.searcher().num_docs(), 25);
    }
}
