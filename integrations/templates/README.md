# Project templates for a Memory-MCP-aware project

Copy one of these to a new project's root and replace `<YOUR_DOMAIN>` / `<YOUR_PROJECT>`.
They tell an agent *how* to use the shared [Memory MCP](../../README.md) server well:
recall before acting, save durable facts as you go, link notes, and consolidate at the end.

- **`AGENTS.md`** — tool-agnostic (the file most coding agents read on startup). Start here.
- **`CLAUDE.md`** — Claude Code flavored: same guidance plus the `/mcp__memory__start-task`
  and `/mcp__memory__end-task` capability prompts and a pointer to the hooks kit.

If you keep both in one repo, have one point at the other so the guidance can't drift.

To automate recall/consolidate instead of relying on the agent reading these files, add the
[`../claude-code/`](../claude-code/) hooks kit.
