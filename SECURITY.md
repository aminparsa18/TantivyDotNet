# Security policy

## Supported versions

| Version | Security fixes |
| --- | --- |
| Latest release | Yes |
| Older releases | No |

Before 1.0, only the latest preview gets fixes, so upgrade to receive them. From 1.0 on, fixes go into the latest minor version of the current major version, and into the latest minor version of the previous major version for six months after the new major version ships.

TantivyDotNet compiles [Tantivy](https://github.com/quickwit-oss/tantivy) and its Rust dependencies into its native library. A vulnerability in one of them is fixed by a TantivyDotNet release that updates it. CI checks the Rust dependencies against the [RustSec advisory database](https://rustsec.org) with cargo-deny, and Dependabot proposes updates weekly.

## Reporting a vulnerability

**Please don't report security vulnerabilities in public issues.**

Report them privately through GitHub: on the repository's **Security** tab, choose **Report a vulnerability** ([direct link](https://github.com/aminparsa18/TantivyDotNet/security/advisories/new)). Include:

- the TantivyDotNet version, operating system and .NET version
- what an attacker can do, and under which conditions (for example, which input they need to control)
- steps or code to reproduce it

You'll get an acknowledgment within 7 days. We'll keep you informed while we work on a fix, agree with you on a disclosure date, and credit you in the advisory unless you'd rather not be named.

If the vulnerability is in Tantivy itself rather than in this binding, we'll coordinate with the Tantivy maintainers.

## Scope

Examples of what counts as a vulnerability here:

- input that makes the native library crash the process, corrupt memory, or read or write outside its buffers
- query text, documents or search options (for example, from untrusted users) that make the process allocate memory or use CPU far out of proportion to the input
- index files that, when opened, crash the process or run code

Untrusted query text is meant to be safe to pass to `Query.Parse` with `QueryParserOptions.Lenient = true`, and untrusted documents to `IndexWriter.AddDocument`. A crash or runaway allocation from either is a bug we want to hear about. Opening an index directory that an attacker could have modified isn't a supported scenario, but reports that it leads to code execution are welcome.

Panics in the native code are caught and become a `TantivyException` with code `Panic`. These are bugs, not vulnerabilities, unless they leave the process in a bad state: report them as regular issues.
