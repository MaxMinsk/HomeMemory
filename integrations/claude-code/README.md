# Claude Code + Memory MCP hooks kit (MEMP-208)

Drop-in hooks that wire a Claude Code project into a [Memory MCP](../../README.md) server:

- **SessionStart** -> calls `memory_context` and injects the workspace's rules, skills and
  the notes relevant to your project at the top of every session, so the agent recalls
  before it acts.
- **Stop** -> if a whole session never touched Memory, prints a one-line reminder to
  consolidate durable facts.

Both hooks **fail open**: if the memory server is unreachable or misconfigured, they emit
nothing and the session proceeds normally.

## Prerequisites

- `python3` on PATH.
- The Memory MCP server registered in Claude Code as an MCP server named `memory`
  (the hook reads its URL + bearer from `~/.claude.json`), **or** set `MEMORY_MCP_URL` /
  `MEMORY_MCP_TOKEN` explicitly (see config below).

## Install

From your project root:

```sh
mkdir -p .claude/hooks
cp path/to/HomeMemory/integrations/claude-code/hooks/*.py .claude/hooks/
```

Then merge `settings.json` from this folder into your project's `.claude/settings.json`
(or your user-level `~/.claude/settings.json`). If you already have a `hooks` block, add
the `SessionStart` / `Stop` entries to it rather than replacing the whole file.

Restart Claude Code (hooks are read at startup). On the next session you should see
`Loading Memory MCP context...` and a memory summary injected.

## Configure (all optional)

Precedence: **env var > `.claude/memory.json` > default**.

| Setting            | Env var            | `.claude/memory.json` key | Default |
|--------------------|--------------------|---------------------------|---------|
| Domain / workspace | `MEMORY_DOMAIN`    | `domain`                  | *(omitted -> cross-domain overview across every domain your token can read)* |
| Project sub-axis   | `MEMORY_PROJECT`   | `project`                 | the project directory name |
| Recall query       | `MEMORY_QUERY`     | `query`                   | `project state, active backlog, decisions, rules` |
| MCP endpoint       | `MEMORY_MCP_URL`   | `url`                     | discovered from `~/.claude.json` |
| Bearer / auth      | `MEMORY_MCP_TOKEN` | `token`                   | discovered from `~/.claude.json` |

Omitting `domain` leans on cross-domain default recall (MEMP-213): the hook loads relevant
notes from every domain your token can read. Set `domain` (and usually `project`) once your
project's notes live in a specific workspace.

Example project-local config — copy `memory.json.example` to `.claude/memory.json`:

```json
{
  "domain": "development",
  "project": "my-project",
  "query": "project state, active backlog, sprint, decisions, rules"
}
```

## See also

- [`../templates/`](../templates/) — `AGENTS.md` / `CLAUDE.md` templates that tell the agent
  *how* to use memory (recall-first, save durable facts, link notes).
- MCP prompts (MEMP-211): in a prompt-aware session, `/mcp__memory__start-task` and
  `/mcp__memory__end-task` do the same recall / consolidate on demand, no hooks required.
