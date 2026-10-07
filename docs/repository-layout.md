# Repository layout

Production source lives under `src/`. `FractalKVS.csproj` and `README.md` remain at the repository root so existing solution references and build commands remain valid. Namespaces, public APIs, package identity, and storage formats are unchanged by this move.

The main-branch workflow builds and packs on Windows and Linux with .NET SDKs 8, 9, and 10. It does not publish. Independent local package consumers validate the candidate's store write/read/reopen behavior on .NET 8, 9, and 10 using source mapping and an isolated package cache.

Layout commits must not absorb unrelated local edits. A source-layout-only update does not replace a published NuGet package or move a release tag; existing packages retain their original source-link commit.
