# ADR 0001 — Stack: .NET 8 / C# 12

**Status:** Accepted

## Context

Memory MCP is a long-running MCP server that ships as a Home Assistant add-on and is
shared by several agents. The stack has to:

- run as a small, reliable, **multi-arch** (amd64 + aarch64) container on a home server;
- deploy as a single self-contained artifact with no runtime to install in the image;
- have first-class libraries for the three load-bearing concerns — **SQLite** (raw, no ORM),
  **JSON Schema** validation, and the **Model Context Protocol**;
- give strong typing and good static analysis, because the code is maintained by one
  developer plus AI agents and must stay readable and hard to break;
- be an LTS platform we won't have to chase for years.

## Decision

Use **.NET 8 (LTS) with C# 12**:

- **`Microsoft.Data.Sqlite`** directly (ADO.NET, no EF/Dapper) — see [ADR 0002](0002-single-table-envelope-payload.md).
- The official **`ModelContextProtocol`** SDK (stdio + streamable HTTP via `ModelContextProtocol.AspNetCore`).
- **`JsonSchema.Net`** (draft 2020-12) for payload validation.
- Build the add-on as a **self-contained, single-file, musl** binary so the image carries no
  separate runtime and stays small enough for both architectures.
- Central Package Management (`Directory.Packages.props`), shared `Directory.Build.props`,
  SDK pinned in `global.json`; analyzers on (.NET analyzers + Meziantou) with CI `-warnaserror`.

## Consequences

- **+** LTS support window; a single deployable file; first-party MCP SDK that tracks the spec;
  excellent SQLite/JSON tooling; strong analyzers catch defects early.
- **+** The same binary serves stdio (local, trusted) and HTTP (remote, bearer-scoped), so there
  is one codebase for every surface.
- **−** The container is larger than an equivalent Go binary; contributors need .NET familiarity.
- **−** SQLite has no real async I/O in this provider, so we use the synchronous ADO APIs and lean
  on WAL for concurrency rather than `async` (documented in `references/sqlite-data-access.md`).

### Alternatives considered

- **Node.js / TypeScript** — the reference MCP servers use it, but we wanted stronger typing,
  a single self-contained artifact, and heavier-duty SQLite/validation libraries.
- **Python** — fast to start, but weaker for a long-lived typed service and multi-arch single-file packaging.
- **Go** — smallest image and great concurrency, but the MCP SDK and JSON-Schema ecosystem were
  less mature for our needs, and the team is more productive in C#.
