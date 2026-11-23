## What and why

<!-- What this changes, and the problem it solves. Link the issue: "Fixes #123". -->

## Checklist

- [ ] Tests added or updated (and the AOT smoke app, for features usable from AOT apps)
- [ ] `dotnet test --solution TantivyDotNet.slnx` passes
- [ ] For Rust changes: `cargo fmt --check`, `cargo clippy --all-targets --all-features -- -D warnings` and `cargo test` pass in `native/`
- [ ] Public API changes are listed in `PublicAPI.Unshipped.txt` and documented with XML comments
- [ ] `CHANGELOG.md` has an entry under `[Unreleased]` (if users would notice the change)
- [ ] Guides in `docs/` updated (if behavior they describe changed)
