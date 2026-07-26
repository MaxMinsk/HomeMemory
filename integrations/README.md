# Integrations — onboarding a new project onto Memory MCP

Everything a new project needs to adopt the shared [Memory MCP](../README.md) server well.
Three complementary layers — use any or all:

| Layer | What it does | Where |
|-------|--------------|-------|
| **Project templates** | `AGENTS.md` / `CLAUDE.md` telling the agent *how* to use memory (recall-first, save durable facts, link notes, consolidate). | [`templates/`](./templates/) |
| **Claude Code hooks kit** (MEMP-208) | Auto-inject memory context at `SessionStart`; nudge to consolidate at `Stop`. Fails open. | [`claude-code/`](./claude-code/) |
| **MCP capability prompts** (MEMP-211) | `/mcp__memory__start-task` and `/mcp__memory__end-task` — on-demand recall / consolidate, served by the memory server itself. `/mcp__memory__onboard-project` scaffolds a fresh project (returns the templates + hooks + config below, filled in). | built into the server |

> `onboard-project` serves each kit file from a live `commons` `reference` note (`dedupKey=onboard-kit-*`) so you can
> edit the templates/hooks with `notes_patch` without a release, falling back to the copy embedded in the server image.
> Run [`seed-onboard-kit.py`](./seed-onboard-kit.py) to seed/refresh those commons notes from this repo.

## Recommended setup for a new project

**Fastest path:** in the new project's session run `/mcp__memory__onboard-project domain=<...> project=<...>`
— it returns every file below (templates + hooks + config) filled in; create them and restart. Or do it
manually:

1. Copy [`templates/AGENTS.md`](./templates/AGENTS.md) (and/or `CLAUDE.md`) to the project root;
   set `<YOUR_DOMAIN>` / `<YOUR_PROJECT>`.
2. (Claude Code) install the [hooks kit](./claude-code/) so context loads automatically.
3. Use the `start-task` / `end-task` prompts during work.

All three point at the same conventions the server enforces in `initialize` instructions and in
the `commons` skills (`agent-memory-use`, `memory-authoring`).
