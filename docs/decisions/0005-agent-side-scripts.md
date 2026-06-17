# ADR 0005 — Procedural work runs agent-side; the server stays a data-plane

**Status:** Accepted

## Context

Some operations are procedural rather than declarative: render a recipe to HTML, build a backlog
board, run a bulk transform over many notes. The question is *where the code runs*. A server that
executes arbitrary agent-supplied scripts would be convenient, but this is a **shared, multi-agent**
store running as a Home Assistant add-on — arbitrary code execution there is a large attack surface
and a sandboxing problem, and the agents calling it already have a capable runtime of their own.

## Decision

**Procedural logic runs agent-side; the MCP server stays a pure data-plane.** Concretely:

- The server exposes **declarative, vetted building blocks** — notes/skills/artifacts/search, typed
  tools, signed artifact upload/serve — **not** a code-execution endpoint.
- Shared procedures are distributed as **skills** (guidance) and **vetted templates** stored as
  notes (e.g. an HTML recipe-render template), which agents fetch and apply locally.
- A future **server-side sandbox for approved scripts** is explicitly **deferred** (MEMP-029). If it
  is ever built it will be opt-in, run only **signed/approved** bundles, and be sandboxed — never an
  open `eval`.

## Consequences

- **+** Small server attack surface: no arbitrary code runs inside the shared add-on; the data-plane
  stays simple and auditable.
- **+** Agents keep full flexibility in their own runtime, where they already operate.
- **−** Procedural logic is **duplicated per agent**, and there is no server-enforced "one true"
  renderer. Mitigated by shared skills + vetted templates so agents converge on the same approach.

### Alternatives considered

- **Server-side scripting engine** — one canonical place to run procedures, but a serious attack
  surface and sandbox-complexity burden in a shared multi-agent store; rejected for now (the parked
  MEMP-029 keeps the door open under strict approval/sandboxing).
- **Database stored procedures** — SQLite has no real procedural layer; brittle and limiting.
