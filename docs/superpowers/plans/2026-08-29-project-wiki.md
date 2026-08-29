# Cross-Project Knowledge Wiki Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create a filesystem-backed wiki for `/home/jublin/Projects`, seeded with related HexTailSharp memories and transferable UI/UX guidance.

**Architecture:** Keep immutable source captures in `/home/jublin/Projects/raw/<topic>/` and compiled articles in `/home/jublin/Projects/wiki/<topic>/`. Use `HexTailSharp` for project-specific knowledge and `global` for cross-project principles; keep the wiki one topic directory deep and maintain the root index and append-only log.

**Tech Stack:** Markdown, EchoVault CLI, POSIX filesystem tools, `rg`, and existing Git only for the design/plan records in HexTailSharp.

**Spec:** `docs/superpowers/specs/2026-08-29-project-wiki-design.md`

## Global Constraints

- The parent directory is the knowledge-base root; existing project folders remain unchanged.
- `raw/` contains immutable source captures and `wiki/` contains maintained articles.
- Topic directories are one level deep; future projects use `raw/<Project>/` and `wiki/<Project>/`.
- The first ingest covers HexTailSharp only.
- EchoVault is an input source for project memories, not a replacement for the filesystem wiki.
- Do not initialize a Git repository in `/home/jublin/Projects`.
- Do not add a database, search service, generator, or synchronization job.
- Do not rewrite raw source captures to make them sound authoritative.
- Do not modify the pre-existing HexTailSharp working-tree changes.

---

### Task 1: Initialize the parent wiki layers

**Files:**
- Create: `/home/jublin/Projects/raw/.gitkeep`
- Create: `/home/jublin/Projects/wiki/.gitkeep`
- Create: `/home/jublin/Projects/raw/HexTailSharp/`
- Create: `/home/jublin/Projects/raw/global/`
- Create: `/home/jublin/Projects/wiki/HexTailSharp/`
- Create: `/home/jublin/Projects/wiki/global/`

**Interfaces:**
- Consumes: the approved root path `/home/jublin/Projects`.
- Produces: the raw and compiled topic directories used by Tasks 2–4.

- [ ] **Step 1: Confirm the initialization targets are absent or safe to preserve**

  Run:

  ```bash
  test ! -e /home/jublin/Projects/raw || test -d /home/jublin/Projects/raw
  test ! -e /home/jublin/Projects/wiki || test -d /home/jublin/Projects/wiki
  ```

  Expected: both commands exit successfully; any existing directory is preserved and only missing children are added.

- [ ] **Step 2: Create only the missing directories and root keep files**

  Create the six directories with `mkdir -p`. Add empty `.gitkeep` files only at `raw/.gitkeep` and `wiki/.gitkeep`; do not add placeholder articles.

- [ ] **Step 3: Verify the layer layout**

  Run:

  ```bash
  find /home/jublin/Projects/raw /home/jublin/Projects/wiki -maxdepth 2 -type d -print | sort
  ```

  Expected: `raw`, `raw/HexTailSharp`, `raw/global`, `wiki`, `wiki/HexTailSharp`, and `wiki/global` are present.

The parent is not a Git repository, so this task has no Git commit. Do not initialize one.

---

### Task 2: Capture the HexTailSharp and global raw sources

**Files:**
- Create: `/home/jublin/Projects/raw/HexTailSharp/2026-08-29-hextailsharp-echovault-memories.md`
- Create: `/home/jublin/Projects/raw/HexTailSharp/2026-08-29-hextailsharp-project-docs.md`
- Create: `/home/jublin/Projects/raw/global/2026-08-29-transferable-ui-ux-source.md`

**Interfaces:**
- Consumes: related EchoVault memory records, the current `README.md`, `docs/architecture.md`, and the approved global UI/UX requirement.
- Produces: immutable, dated source files linked by the compiled articles.

- [ ] **Step 1: Retrieve the related EchoVault records before writing the capture**

  Search for the HexTailSharp architecture, Elastic, UI, settings, labels, tabs, responsiveness, and bug-fix memories. For every result marked `Details: available`, retrieve its details. Preserve the memory title, date, category, identifier, What, Why, Impact, and Details text in the raw capture; omit credentials or other sensitive values.

- [ ] **Step 2: Write the EchoVault raw capture using the raw template**

  Start the file with:

  ```markdown
  # HexTailSharp EchoVault memories

  > Source: EchoVault local memory records for HexTailSharp
  > Collected: 2026-08-29
  > Published: Unknown
  ```

  Add each retrieved memory as a labeled, faithful capture. Do not synthesize conclusions in this file.

- [ ] **Step 3: Capture the current project documentation**

  Start `hextailsharp-project-docs.md` with the raw metadata header and preserve the current relevant text from `README.md`, `docs/architecture.md`, and `docs/native-build.md`, labeled by source path. Keep the copy point-in-time and do not edit the source project documents.

- [ ] **Step 4: Capture the global UI/UX source material**

  Start `transferable-ui-ux-source.md` with the raw metadata header and include the user's global requirement plus the exact relevant UI/UX memory excerpts and project evidence. Label project-specific examples as examples; do not present them as universal rules in the raw file.

- [ ] **Step 5: Verify raw files are present and dated**

  Run:

  ```bash
  rg -n "^> (Source|Collected|Published):" /home/jublin/Projects/raw/HexTailSharp /home/jublin/Projects/raw/global
  ```

  Expected: all three captures contain the required metadata and no raw file contains unresolved template tokens.

There is no parent Git commit for these files. Their change history is recorded in `wiki/log.md`.

---

### Task 3: Compile HexTailSharp project articles

**Files:**
- Create: `/home/jublin/Projects/wiki/HexTailSharp/architecture-and-runtime.md`
- Create: `/home/jublin/Projects/wiki/HexTailSharp/elastic-integration.md`
- Create: `/home/jublin/Projects/wiki/HexTailSharp/ui-ux-and-product-decisions.md`

**Interfaces:**
- Consumes: the two `raw/HexTailSharp/` captures from Task 2.
- Produces: three concise project articles with relative Raw links and cross-references.

- [ ] **Step 1: Compile the architecture article**

  Use the article template and these sections:

  ```markdown
  # HexTail architecture and runtime
  ## Overview
  ## Runtime boundary
  ## Data flow
  ## Component responsibilities
  ## Deliberate limits
  ## See Also
  ```

  Record the native .NET/Avalonia boundary, shared source-event pipeline, bounded buffers, persistence, credential boundary, and current non-goals. Link both HexTailSharp raw captures.

- [ ] **Step 2: Compile the Elastic article**

  Use the article template and these sections:

  ```markdown
  # HexTail Elastic integration
  ## Overview
  ## Approved architecture
  ## Connection and source behavior
  ## Polling and recovery
  ## UI and persistence decisions
  ## Deferred complexity
  ## See Also
  ```

  Record dual Kibana/Elasticsearch endpoints, source-neutral line ingestion, native credential storage, five-minute lookback, incremental polling, health behavior, stream-row rendering, and explicit deferrals. Link the architecture article and both raw captures.

- [ ] **Step 3: Compile the project UI/UX article**

  Use the article template and these sections:

  ```markdown
  # HexTail UI/UX and product decisions
  ## Overview
  ## Interaction model
  ## Settings and responsive layout
  ## Search, labels, and tabs
  ## State visibility and feedback
  ## Known fixes and constraints
  ## See Also
  ```

  Distill the project-specific decisions and fixes: centered settings modal, server cards, consistent control sizing, responsive scrolling, Tabalonia tabs, global-label behavior, derived context visibility, independent follow state, and inactive-view notification suppression. Link the global UI/UX article even though Task 4 creates it afterward.

- [ ] **Step 4: Check article metadata and links**

  Run:

  ```bash
  rg -n "^> (Sources|Raw|Updated):|^## (Overview|See Also)$" /home/jublin/Projects/wiki/HexTailSharp
  ```

  Expected: each article has the required source metadata, a valid Raw field, an Overview, and See Also links where related articles exist.

No Git commit is made because the compiled articles live outside the Git repository.

---

### Task 4: Compile the global article and root navigation

**Files:**
- Create: `/home/jublin/Projects/wiki/global/ui-ux-principles.md`
- Create: `/home/jublin/Projects/wiki/index.md`
- Create: `/home/jublin/Projects/wiki/log.md`

**Interfaces:**
- Consumes: the global raw capture and the three HexTailSharp articles.
- Produces: a transferable UI/UX article plus complete root navigation and ingest history.

- [ ] **Step 1: Compile the transferable UI/UX article**

  Use the article template and these sections:

  ```markdown
  # Transferable UI/UX principles
  ## Overview
  ## Make the primary workflow obvious
  ## Derive visibility from state
  ## Preserve user context
  ## Make settings responsive and consistent
  ## Match the surface to the data
  ## Make errors and health legible
  ## Translation checklist
  ## See Also
  ```

  Phrase the content as cross-project guidance, with a short HexTailSharp example and a link for each principle. Include the limitation that a principle must be adapted to the product domain.

- [ ] **Step 2: Write the root index**

  Start with `# Knowledge Base Index`. Add `HexTailSharp` and `global` topic sections, one-line descriptions, and one table row for each of the four articles. Use paths relative to `wiki/index.md` and `2026-08-29` Updated dates.

- [ ] **Step 3: Write the initial ingest log**

  Start with `# Wiki Log` and append:

  ```markdown
  ## [2026-08-29] ingest | Cross-Project Knowledge Wiki
  - Updated: HexTail architecture and runtime
  - Updated: HexTail Elastic integration
  - Updated: HexTail UI/UX and product decisions
  - Updated: Transferable UI/UX principles
  ```

- [ ] **Step 4: Verify root navigation**

  Run:

  ```bash
  rg -n "\]\((HexTailSharp|global)/[^)]+\.md\)" /home/jublin/Projects/wiki/index.md
  test -f /home/jublin/Projects/wiki/index.md
  test -f /home/jublin/Projects/wiki/log.md
  ```

  Expected: four article links are listed and both root files exist.

No Git commit is made because the parent is not a Git repository.

---

### Task 5: Validate the completed wiki and preserve the project worktree

**Files:**
- Verify: `/home/jublin/Projects/raw/`
- Verify: `/home/jublin/Projects/wiki/`
- Verify: `/home/jublin/Projects/HexTailSharp/`

**Interfaces:**
- Consumes: all files produced by Tasks 1–4 and the pre-task repository status.
- Produces: evidence that indexes, raw references, internal links, and project isolation are correct.

- [ ] **Step 1: Validate every indexed article exists**

  Check the four index targets with `test -f` and confirm there are no `[MISSING]` entries.

- [ ] **Step 2: Validate every Raw link exists**

  Resolve each Raw link from its article directory and run `test -f` on the target. Expected: all three project articles and the global article resolve their raw files.

- [ ] **Step 3: Validate internal See Also links**

  Resolve every non-Raw markdown link under `wiki/` and run `test -f` on its target. Expected: no broken links.

- [ ] **Step 4: Run whitespace and scope checks**

  Run:

  ```bash
  rg -n "TBD|TODO|FIXME|\{Title\}|\{topic-name\}" /home/jublin/Projects/wiki /home/jublin/Projects/raw || true
  rtk git -C /home/jublin/Projects/HexTailSharp status --short --branch
  ```

  Expected: no unresolved template markers; the HexTailSharp status matches the pre-task status except for the already committed spec and plan records if they were not present in the baseline.

- [ ] **Step 5: Save the durable EchoVault learning**

  Search first, then save one memory recording the chosen parent layout, the EchoVault-as-source boundary, and the created article namespaces. Do not include credentials or sensitive values.

The parent wiki remains uncommitted because `/home/jublin/Projects` is not a Git repository. The design and implementation plan are the only repository changes made by this workflow.
