# ADR 0004 — The typed payload is the source of truth; rendered docs are derived

**Status:** Accepted

## Context

A recipe (and many other note kinds) has two faces: a **structured form** — the validated
`recipe@N` payload with ingredients, steps, yields, timings — and a **human-readable render** —
an HTML or Markdown page an agent or person reads, prints, or shares. Both are useful, but they can
drift: if someone edits the rendered HTML, the structured data is now stale, and vice versa. We had
to decide which one is authoritative.

## Decision

**The typed payload is the single source of truth. Any rendered HTML/Markdown is a derived,
disposable artifact** — regenerable from the payload at any time, stored as an attachment/blob, and
never treated as the canonical copy:

- Edits happen on the **structured payload** (validated against the schema, searchable, diffable in
  the audit log). To refresh the view, **re-render** from the payload.
- A rendered artifact is a **cache of a projection**, not a record. It may be deleted and rebuilt; a
  hand-edited render is, by design, **not** authoritative.
- This generalizes beyond recipes: for any kind with both structured data and a presentation, the
  structured note is the truth and the rendering is a view.

## Consequences

- **+** Edits stay validated, searchable, and audited; renders can never silently diverge from the
  data, and the same payload can be rendered in any format (HTML, Markdown, print).
- **+** Artifacts remain safe to **garbage-collect** (gc-blobs) — they are reproducible.
- **−** A view must be **regenerated** after an edit to stay current; there is no "edit the page
  directly" shortcut. This is intentional.

### Alternatives considered

- **Store the rendered document as the record** — easiest to display, but loses validation, search
  over structured fields, and clean diffs; data extraction becomes scraping.
- **Dual-write (keep both authoritative)** — guarantees drift; requires reconciliation logic and a
  conflict policy for no real benefit.
