# Changelog

All notable changes to this project are documented in this file.

## 1.0.1 - 2026-10-12

- Repair healthy-index maintenance: preserve reachable routing tiers, A/B selections, record locations, ownership and durable atomic state; validate before and after replacement, with rollback on failure.
- Clarify that `RebuildIndex` requires a healthy authoritative index and exclusive offline access; it does not recover a missing/corrupt index by scanning physical payload history.
- Add internal read-only snapshot validation for AbraxasDB backup/restore without adopting ownership or creating missing storage.
- Add a precompiled library archive and package-only rebuild/reopen checks on .NET 8, 9 and 10.
- Added a package-consumer smoke test that restores the packed NuGet artifact and verifies write, read, close, and reopen behavior through only the public API.
- Updated the build-time SourceLink dependency to a current non-vulnerable version.
- Modernized trusted publishing workflow dependencies and required provenance-attestation permissions.
- Added contribution, conduct, security, and changelog documentation.
- Made the existing internal Fractal diagnostic types consistently available to the repository's internal test harness without making them public API.

## 1.0.0 - 2026-10-03

- Initial public release.
- Added a .NET-native, file-backed durable dictionary for `ulong` keys and binary values.
- Added incrementally grown, hash-directed indexing with separate index and data files.
- Added concurrent read and bucket-striped write paths with per-thread file handles.
- Added paired A/B record and index updates for record-level update resilience.
- Added configurable index growth, cache budgeting, bulk-write parallelism, deferred and immediate write behavior, backup, index rebuild, and online defragmentation controls.
- Added Apache-2.0 licensing, SourceLink, symbol packages, deterministic builds, package validation, provenance attestation, and trusted NuGet publishing automation.
