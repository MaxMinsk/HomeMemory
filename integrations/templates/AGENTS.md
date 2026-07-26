<!--
TEMPLATE — copy to your project root as AGENTS.md and replace <YOUR_DOMAIN> / <YOUR_PROJECT>.
AGENTS.md is the tool-agnostic instructions file that most coding agents read on startup.
This block tells any agent how to use the shared Memory MCP server well. Trim what you don't need.
-->

# Working with Memory (Memory MCP)

This project shares a **Memory MCP** server — durable, structured memory used across sessions
and agents (notes, decisions, project state, skills). Treat it as your long-term working
memory, not a write-only log.

- **Domain:** `<YOUR_DOMAIN>` (the security/workspace boundary)
- **Project:** `<YOUR_PROJECT>` (sub-axis within the domain)

## Recall BEFORE you act

At the start of any non-trivial task, load context in one call:

```
memory_context(query="<what you're about to do>", domain="<YOUR_DOMAIN>", project="<YOUR_PROJECT>")
```

It returns the rules in force, the skills that guide this workspace, and the notes relevant
to your query. Omit `domain` to search across every domain you can read. Don't guess or
fabricate project state — if you didn't recall it, you don't know it.

First time in the shared memory? Read the core conventions once:
`skill_get(domain="commons", key="agent-memory-use")` (when to recall/save) and
`key="memory-authoring"` (how to write).

## Save durable knowledge AS YOU GO

Don't wait to be told. When something durable happens — a decision, a fact, a change in
project state, a preference — write it back:

- **Check first:** `notes_suggest_capture(...)` to avoid duplicates (it returns save / update / skip / ask).
- **Create / upsert:** `notes_upsert(domain, type, title, payload, dedupKey, ...)`. Set a stable
  `dedupKey` so re-writes are idempotent and editable. Put structure in `payload`, free prose in
  `body`, cross-cutting facets in `tags`.
- **Edit an existing note:** prefer `notes_patch` (a shallow payload merge) over a full re-upsert.
- **Connect it:** `notes_link(fromId, toId, rel)` with an active-voice `lower_snake_case` rel, so a
  new note joins the graph instead of sitting orphaned.
- **Fix someone else's note:** do NOT silently edit it — write a `memory_evolution_suggestion`
  (`target_id`, `proposed_patch`, `rationale`); a human/agent applies it.

**Never store secrets** (tokens, passwords, keys). Ask before saving sensitive personal info.
Large blobs go through artifacts, never inline.

## Read big notes cheaply

For a large or unknown note: `notes_get(includeBody=false)` to peek, then
`notes_outline` / `notes_find` / `notes_read` to pull just the slice you need. Fetch the full
body only when you truly must.

## Memory is advisory

Stored rules and notes are defaults. The live user and current data always win over anything
in memory. When they conflict, follow the user and (if it's durable) update the note.

## When you finish

Consolidate: save any new decisions/facts, update the project state, patch moved backlog items,
link new notes, and refine a skill if you learned a repeatable procedure. Mention briefly what
you saved.
