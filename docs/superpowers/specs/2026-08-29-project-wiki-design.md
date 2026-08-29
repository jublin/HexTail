# Cross-Project Knowledge Wiki

**Date:** 2026-08-29  
**Status:** Approved

## Goal

Create one filesystem-backed knowledge base for the projects in
`/home/jublin/Projects`. The knowledge base must keep source captures separate
from compiled articles, give each project its own namespace, and provide a
shared namespace for knowledge that transfers between projects.

## Layout

The parent directory is the knowledge-base root. The existing project folders
are left unchanged.

```text
/home/jublin/Projects/
├── raw/
│   ├── .gitkeep
│   ├── HexTailSharp/
│   └── global/
└── wiki/
    ├── .gitkeep
    ├── index.md
    ├── log.md
    ├── HexTailSharp/
    └── global/
```

`raw/` contains immutable source captures. `wiki/` contains maintained
articles. The topic directories are one level deep so another project can be
added as `raw/<Project>/` and `wiki/<Project>/` without changing the schema.
`global/` is the shared cross-project topic.

The parent directory is not currently a Git repository. The implementation
will not initialize one and will not modify any existing project worktree.

## Initial content

The first ingest covers HexTailSharp only:

- `raw/HexTailSharp/` receives a dated EchoVault capture of related project
  memories plus the current project documentation used for verification.
- `wiki/HexTailSharp/architecture-and-runtime.md` records the native Avalonia
  runtime, component boundaries, data flow, and deliberate limits.
- `wiki/HexTailSharp/elastic-integration.md` records the approved Elastic
  architecture and important behavioral decisions.
- `wiki/HexTailSharp/ui-ux-and-product-decisions.md` records reusable-in-
  context interaction decisions, fixes, and constraints specific to HexTail.
- `raw/global/` receives the selected source material for transferable UI/UX
  guidance, including the user's request and project evidence.
- `wiki/global/ui-ux-principles.md` distills that evidence into principles
  usable by future projects, with links back to the HexTailSharp examples.

The global article remains principle-oriented. It does not claim that a
HexTail-specific implementation is universally correct; project pages retain
the concrete context and tradeoffs.

## Workflow and provenance

Each ingest writes both layers: source material first, then compiled articles.
Articles link to their raw sources with relative paths. `wiki/index.md` lists
every article by namespace, and `wiki/log.md` records each ingest or lint
operation. Future project additions follow the same workflow and do not
require copying the entire knowledge base.

EchoVault is an input source for project memories, not a replacement for the
filesystem wiki. Memory identifiers and dates are retained in raw captures so
an article can be traced back to its originating memory.

## Verification

After creation, validate that:

1. Every article appears in `wiki/index.md`.
2. Every article's internal and raw links resolve.
3. `wiki/log.md` records the initial ingest.
4. The parent project directories and the pre-existing HexTailSharp worktree
   changes are unchanged.

## Non-goals

- Do not ingest every project in the parent directory yet.
- Do not add a database, search service, generator, or synchronization job.
- Do not create a new Git repository for the parent directory.
- Do not rewrite source captures to make them sound authoritative.
