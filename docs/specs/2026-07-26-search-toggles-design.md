# Search Toggle Design

**Date:** 2026-07-26  
**Topic:** Add VS Code-style search toggles to HexTail (Match Case, Match Whole Word, Use Regex).

## Background

HexTail already has a search panel that supports:

- Literal vs. Regex matching (`MatchMode::Literal` / `MatchMode::Regex`).
- Case-sensitive matching via an `Aa` checkbox (`case_sensitive: bool`).

The user wants the standard set of search options found in VS Code / Zed:

- Match Case (A/a).
- Match Whole Word (`ab|`).
- Use Regular Expressions (`.*`).

These should behave as **independent toggles**, with Match Whole Word disabled when Regex is active.

## Goals

1. Add a **Match Whole Word** toggle to the search panel.
2. Keep **Match Case** and **Regex** working exactly as they do today.
3. Disable the **Match Whole Word** checkbox when **Regex** is enabled.
4. Persist the new toggle across sessions.
5. Maintain backward compatibility with existing saved configs.

## Non-Goals

- Find/replace.
- Search history / saved queries.
- Keyboard shortcuts for toggles.
- Changing the highlighting color mechanism.

## Design

### 1. Data Model

Add a `whole_word: bool` field to `SearchQuery` and `PersistedSearch`.

```rust
// src/search.rs
pub struct SearchQuery {
    pub text: String,
    pub mode: MatchMode,
    pub case_sensitive: bool,
    pub whole_word: bool,
    pub color: SearchColor,
}
```

`MatchMode` remains `Literal | Regex`. Whole word is modeled as a modifier rather than a mode because it can combine with Literal and is disabled for Regex.

### 2. Matching Engine

`Search::new` continues to compile every query into a `regex::Regex`, extending the current logic:

- **Literal mode:**
  1. `regex::escape(&query.text)`.
  2. If `whole_word` is true, wrap with `\b...\b`.
  3. If `case_sensitive` is false, prepend `(?i)`.
- **Regex mode:**
  1. Use `query.text` as-is.
  2. If `case_sensitive` is false, prepend `(?i)`.
  3. `whole_word` is ignored — the UI prevents enabling it.

This keeps a single matching/highlighting path and minimizes code change.

### 3. UI Changes

`SearchPanelState` gains `whole_word: bool`. The panel shows three toggles in one horizontal row:

```
[Search: ____________] [Regex] [Aa] [ab|] [🎨] [Add]
```

- **Regex** — toggles `MatchMode::Regex` / `MatchMode::Literal`.
- **Aa** — toggles case sensitivity.
- **ab|** — toggles whole-word matching, disabled (grayed out) when Regex is checked.

### 4. Persistence

Add `whole_word: bool` to `PersistedSearch`:

```rust
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct PersistedSearch {
    pub text: String,
    pub regex: bool,
    pub case_sensitive: bool,
    pub whole_word: bool,
    pub color: [u8; 3],
}
```

`#[serde(default)]` ensures configs written before this change load successfully: missing `whole_word` defaults to `false`.

### 5. Testing

Update unit tests in `src/search.rs`:

- Existing: substring literal match, case-insensitive literal, regex match, no match.
- **New:** whole-word literal matches at a word boundary.
- **New:** whole-word literal does not match a substring.
- **New:** case-insensitive whole-word literal match.
- Update `src/persistence.rs` round-trip test to include `whole_word`.

## Trade-offs

- **Regex-backed matching for all modes:** Chosen for consistency and minimal change. A faster dedicated string matcher could be added later if profiling shows search is a bottleneck, without affecting the UI or persistence format.
- **Whole word disabled for regex:** Matches the VS Code/Zed behavior the user requested and avoids ambiguity about whether word boundaries wrap the entire regex or each alternative.
- **Single `whole_word` field:** Simple to persist and reason about; no need for a new enum.

## Open Questions

None at this time.
