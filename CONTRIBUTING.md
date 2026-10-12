# Contributing

Thank you for considering a contribution.

Please open an issue before investing in a large change so the intended API, file-format compatibility impact, and validation approach can be agreed first.

## Pull requests

- Keep changes focused and include tests or a reproducible validation case.
- Preserve the public API and on-disk store compatibility unless the pull request explicitly proposes a major-version change or an intentional migration path.
- Do not introduce allocations, ownership changes, I/O amplification, synchronization costs, or durability regressions without documenting the reason and measuring the affected path where practical.
- Keep reader ownership and disposal contracts explicit.
- Run the repository build and relevant storage-integrity checks before submitting.

## Contribution license

By submitting a contribution, you agree that it is your original work and that it is licensed under Apache-2.0.
