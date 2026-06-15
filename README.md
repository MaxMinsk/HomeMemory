# Memory MCP

A thin, reliable [MCP](https://modelcontextprotocol.io) server for personal memory, notes and skills — shared by multiple agents (a Home Assistant agent, Claude Code, a future web UI) over the Model Context Protocol. It stores notes, enforces their schemas, and **hosts the skills that teach every agent how to write each note type the same way** — so different agents keep recipes, backlog items and measurements consistent. The server itself never runs an LLM: the reasoning (what to record, how to phrase it) happens in the agents, *guided by* the schemas and skills they fetch from the server.

> **Status:** early scaffold (milestone M0 complete). The first use case hosts the project's own backlog as memory — dogfooding the store before adding recipes, measurements and files.

## Design at a glance

- **Single `notes` table**: a relational envelope (`id, domain, type, title, body, payload_json, tags, dedup_key, status, …`) plus a typed `payload_json` validated by a JSON Schema registry. `domain` is a namespace (work / kitchen / …), `type` is the schema discriminator (`backlog_item`, `recipe`, …). See [ADR 0002](docs/decisions/0002-single-table-envelope-payload.md).
- **SQLite** (WAL + a `PRAGMA user_version` migration ladder). **FTS5/BM25** search; no vectors in the MVP.
- **Append-only event log** + soft-delete only (no hard delete in the tool surface).
- **Schemas and skills are server-hosted, shared conventions**: the schema is the hard contract (enforced on write); the skill is the soft craft (naming, units, how to render) served to agents on demand — so every agent writes a given note type the same way.
- Small MCP tool surface; bearer auth with per-token domain scoping; stdio + streamable HTTP transports.

See [`AGENTS.md`](AGENTS.md) for conventions and the architectural principles that must hold, and [`docs/decisions/`](docs/decisions/) for the architecture decision records.

## Layout

```
src/MemoryMcp.Core    — domain, storage, schema registry (library)
src/MemoryMcp.Server  — MCP host (stdio + streamable HTTP)
tests/MemoryMcp.Tests — unit & integration tests
addon/                — Home Assistant add-on (Dockerfile, config, run.sh)
docs/decisions/       — architecture decision records (ADRs)
```

## Build & test

Requires the .NET 8 SDK (pinned via `global.json`).

```bash
dotnet restore
dotnet build -c Release
dotnet test                                   # all tests
dotnet test --filter "Category!=Integration"  # fast unit suite only
```

## Run

The server is configured entirely through environment variables:

| Variable | Default | Meaning |
|---|---|---|
| `MEMORY_TRANSPORT` | `stdio` | `stdio` for local agents, `http` for the streamable-HTTP endpoint |
| `MEMORY_DB_PATH` | `memory.sqlite` | SQLite database file (created if missing) |
| `MEMORY_BEARER_TOKEN` | *(unset)* | If set (HTTP only), requests must send `Authorization: Bearer <token>` |
| `MEMORY_ALLOWED_DOMAINS` | *(unset = all)* | Comma-separated domains the token may access (per-token scoping) |
| `ASPNETCORE_URLS` | framework default | HTTP bind address, e.g. `http://0.0.0.0:8099` |

**stdio** (how MCP clients launch it):

```bash
MEMORY_DB_PATH="$HOME/.memory/memory.sqlite" \
  dotnet run --project src/MemoryMcp.Server
```

**HTTP** (streamable HTTP at `/mcp`):

```bash
MEMORY_TRANSPORT=http \
MEMORY_DB_PATH="$HOME/.memory/memory.sqlite" \
MEMORY_BEARER_TOKEN="dev-token" \
ASPNETCORE_URLS="http://0.0.0.0:8099" \
  dotnet run --project src/MemoryMcp.Server
# endpoint: http://localhost:8099/mcp
```

## Connect from Claude Code

**Local, over stdio** (development build):

```bash
claude mcp add memory \
  --env MEMORY_DB_PATH="$HOME/.memory/memory.sqlite" \
  -- dotnet run --project /absolute/path/to/src/MemoryMcp.Server
```

**Remote, over HTTP** (e.g. the Home Assistant add-on behind a tunnel):

```bash
claude mcp add --transport http memory https://your-host/mcp \
  --header "Authorization: Bearer <your-token>"
```

Then ask the agent to call e.g. `status`, `schema_list_types`, or `notes_search`.

## Install as a Home Assistant add-on

1. **Settings → Add-ons → Add-on Store → ⋮ → Repositories**, add this repo's URL.
2. Install **Memory MCP** from the store. The image is published multi-arch (amd64/aarch64) to GHCR.
3. In the add-on **Configuration**, set:
   - `db_path` (default `/data/memory.sqlite` — persisted across restarts),
   - `bearer_token` (**set one** for any remote access; empty = unauthenticated),
   - `allowed_domains` (optional per-token scoping),
   - `log_level`.
4. Start the add-on. It serves streamable HTTP on port **8099** at `/mcp`. Expose it remotely with your own reverse proxy / tunnel and point clients at `https://<host>/mcp`.

## Backlog CLI

The server doubles as a small CLI for the dogfood backlog use case:

```bash
dotnet run --project src/MemoryMcp.Server -- import-backlog backlog.md
dotnet run --project src/MemoryMcp.Server -- export-backlog out.md
dotnet run --project src/MemoryMcp.Server -- push-backlog backlog.md <url> <token> [domain]
```

## License

TBD.
