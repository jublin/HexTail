# Husky.Net CSharpier hook plan

Date: 2026-08-03  
Status: Implemented

## Outcome

Install Husky.Net and CSharpier as repository-local .NET tools and run
CSharpier against staged C# files from a pre-commit hook. Formatted files are
re-staged so the commit contains the formatter output.

## Tasks

1. Add a local tool manifest pinning Husky.Net and CSharpier.
2. Attach Husky.Net through MSBuild and configure a staged-C# formatting task.
3. Document setup/usage and verify the hook, tests, and build.

Implementation commits: `ddd5a17`, `b2c5e85`, and this documentation commit.

## Constraints

- Keep the hook .NET-only; no Node/npm Husky dependency.
- Format only staged `*.cs` files; do not rewrite unrelated working-tree files.
- Skip automatic hook installation when `HUSKY=0` is set (CI-friendly).
