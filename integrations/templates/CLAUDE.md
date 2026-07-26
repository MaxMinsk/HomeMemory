<!--
TEMPLATE — copy to your project root as CLAUDE.md and replace <YOUR_DOMAIN> / <YOUR_PROJECT>.
CLAUDE.md is loaded into every Claude Code session for this project. This is the Claude Code
flavored version of AGENTS.md (same intent, plus slash-command prompts and the hooks kit).
If you keep both files, let one point at the other so guidance doesn't drift.
-->

# Working with Memory (Memory MCP)

This project shares a **Memory MCP** server — durable, structured memory across sessions and
agents. Use it as long-term working memory: recall before you act, save durable facts as you go.

- **Domain:** `<YOUR_DOMAIN>` — **Project:** `<YOUR_PROJECT>`

## Fast path: capability prompts (MEMP-211)

If the memory server is connected as an MCP server, two slash-command prompts do the heavy lifting:

- `/mcp__memory__start-task` — loads the rules, skills and relevant notes for your task
  (args: `task`, optional `domain`, optional `project`). Run it first.
- `/mcp__memory__end-task` — an end-of-task consolidation checklist (args: optional `domain`,
  `project`). Run it before you finish.

Prefer these when available; they wrap the tool calls below.

## Recall BEFORE you act

```
memory_context(query="<what you're about to do>", domain="<YOUR_DOMAIN>", project="<YOUR_PROJECT>")
```

Returns the rules in force, the skills that guide this workspace, and notes relevant to your
query. Omit `domain` to span every domain you can read. Never fabricate project state you
didn't recall. First time here? `skill_get(domain="commons", key="agent-memory-use")` and
`key="memory-authoring"`.

## Save durable knowledge AS YOU GO

- **Check first:** `notes_suggest_capture(...)` (save / update / skip / ask) to avoid duplicates.
- **Write:** `notes_upsert(domain, type, title, payload, dedupKey, ...)` — stable `dedupKey`;
  structure in `payload`, prose in `body`, facets in `tags`.
- **Edit:** prefer `notes_patch` over a full re-upsert.
- **Connect:** `notes_link(fromId, toId, rel)` (active-voice `lower_snake_case`).
- **Fix another note:** write a `memory_evolution_suggestion` — don't silently edit it.
- **Never** store secrets; ask before sensitive personal info; blobs go through artifacts.

## Read big notes cheaply

`notes_get(includeBody=false)` to peek, then `notes_outline` / `notes_find` / `notes_read`
for the slice you need.

## Memory is advisory

Stored notes are defaults; the live user and current data win. When they conflict, follow the
user — and update the note if the change is durable.

## Automate it (optional): hooks kit

The [`integrations/claude-code/`](../claude-code/) hooks inject memory context at
`SessionStart` and nudge you to consolidate at `Stop`. Copy the hook scripts to
`.claude/hooks/`, merge the `settings.json` fragment, and set `domain`/`project` in
`.claude/memory.json`.
