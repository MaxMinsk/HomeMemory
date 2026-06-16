# Memory MCP

A thin, reliable [MCP](https://modelcontextprotocol.io) server for personal memory, notes and skills — shared by multiple agents (a Home Assistant agent, a kitchen/cook agent, Claude Code, a future web UI) over the Model Context Protocol. It stores notes, enforces their schemas, and **hosts the skills that teach every agent how to write each note type the same way** — so different agents keep recipes, backlog items and measurements consistent. The server itself never runs an LLM: the reasoning (what to record, how to phrase it) happens in the agents, *guided by* the schemas and skills they fetch from the server.

> **Status:** in production as a Home Assistant add-on. Dogfoods its own backlog (stored as memory) alongside recipes, notes and file artifacts for the household agents.

## What it does

- **Notes** — one relational envelope (`id, domain, type, title, body, payload_json, tags, dedup_key, status, …`) plus a typed `payload_json` validated by a JSON Schema registry. `domain` is a namespace (home / kitchen / memory-mcp / …); `type` is the schema discriminator (`backlog_item`, `recipe`, `fact`, …). See [ADR 0002](docs/decisions/0002-single-table-envelope-payload.md).
- **Schemas + skills, server-hosted** — the schema is the hard contract (enforced on write); the skill is the soft craft (naming, units, how to render) served to agents on demand. Built-in types are **evolvable**: an agent can `schema_upsert` a higher version (e.g. `recipe@2`); the shipped version stays immutable and the new one becomes latest.
- **Search & recall** — FTS5/BM25 full-text over title/body/tags/dedup_key/**payload**, a structured filter DSL (`payload.status in ('ready','next') AND payload.sprint is null`), plus agentic recall: `notes_recall` (FTS hits + 1-hop graph neighbors) and `notes_related` (shared-tag suggestions).
- **Graph** — typed, idempotent links between notes (`depends_on`, `addresses`, `supersedes`, …), queryable in both directions.
- **Safety** — append-only event log, soft-delete only (no hard delete in the tool surface), and two-phase confirmation for destructive operations (`archive`/`delete`/`unlink` return a pending action that a separate `confirm` must commit).
- **Artifacts** — content-addressed blob store (CAS) for rendered HTML/markdown/files; bytes move out-of-band via short-lived **signed capability URLs**, never through the model context. Orphan blobs are garbage-collected.
- **Multi-tenant** — bearer auth with **per-token domain scoping**: a root token from the environment plus DB-backed tokens each scoped to one/several domains (or `*`). A world-readable **`commons`** domain holds the core rules/skills every agent reads first.
- **Discoverable** — `memory_capabilities` returns the runtime contract (build/schema/contract version, known types, your token's scope) so an agent never guesses from a stale tool list; `status` reports counts, blob bytes and on-disk DB size.
- **Read-only viewer** — a self-contained SPA at `/ui` (search, filters, deep links, clickable tags, artifact preview), scope-enforced behind the same bearer as `/mcp`.
- **Transports** — stdio (local agents) and streamable HTTP at `/mcp`.

See [`AGENTS.md`](AGENTS.md) for conventions and the architectural principles that must hold, and [`docs/decisions/`](docs/decisions/) for the architecture decision records.

## Layout

```
src/MemoryMcp.Core    — domain, storage, schema registry, search (library)
src/MemoryMcp.Server  — MCP host (stdio + streamable HTTP), tools, web viewer, CLIs
tests/MemoryMcp.Tests — unit & integration tests
addon/                — Home Assistant add-on (Dockerfile, config, run.sh)
docs/decisions/       — architecture decision records (ADRs)
```

## Build & test

Requires the .NET 8 SDK (pinned via `global.json`).

```bash
dotnet restore
dotnet build -c Release -warnaserror         # CI keeps main zero-warning
dotnet test                                   # all tests
dotnet test --filter "Category!=Integration"  # fast unit suite only
```

## Run

The server is configured entirely through environment variables:

| Variable | Default | Meaning |
|---|---|---|
| `MEMORY_TRANSPORT` | `stdio` | `stdio` for local agents, `http` for the streamable-HTTP endpoint |
| `MEMORY_DB_PATH` | `memory.sqlite` | SQLite database file (created if missing) |
| `MEMORY_BEARER_TOKEN` | *(unset)* | Root token. **Required for HTTP** (the endpoint refuses to start without it unless `ALLOW_UNAUTHENTICATED_HTTP=true`) |
| `MEMORY_ALLOWED_DOMAINS` | *(unset = all)* | Comma-separated domains the **root** token may access; unset = all |
| `ALLOW_UNAUTHENTICATED_HTTP` | `false` | Escape hatch to run HTTP with no bearer (development only) |
| `MEMORY_ARTIFACT_SIGNING_KEY` | *(bearer)* | HMAC key for signed artifact URLs; falls back to the bearer token |
| `MEMORY_PUBLIC_BASE_URL` | *(unset)* | Public origin (e.g. tunnel URL) used to build absolute `artifacts_url` links |
| `MEMORY_BLOB_ROOT` | *(next to the DB)* | Directory for the content-addressed blob store |
| `MEMORY_BLOB_QUOTA_BYTES` | `0` (unlimited) | Optional cap on total blob bytes |
| `ASPNETCORE_URLS` | framework default | HTTP bind address, e.g. `http://0.0.0.0:8099` |

**stdio** (how MCP clients launch it):

```bash
MEMORY_DB_PATH="$HOME/.memory/memory.sqlite" \
  dotnet run --project src/MemoryMcp.Server
```

**HTTP** (streamable HTTP at `/mcp`, read-only viewer at `/ui`):

```bash
MEMORY_TRANSPORT=http \
MEMORY_DB_PATH="$HOME/.memory/memory.sqlite" \
MEMORY_BEARER_TOKEN="dev-token" \
ASPNETCORE_URLS="http://0.0.0.0:8099" \
  dotnet run --project src/MemoryMcp.Server
# endpoint: http://localhost:8099/mcp · viewer: http://localhost:8099/ui
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

Then ask the agent to call e.g. `memory_capabilities` (what this build supports + your scope), `status`, `schema_list_types`, or `notes_search`.

## Install as a Home Assistant add-on

1. **Settings → Add-ons → Add-on Store → ⋮ → Repositories**, add this repo's URL.
2. Install **Memory MCP** from the store. The image is published multi-arch (amd64/aarch64) to GHCR.
3. In the add-on **Configuration**, set:
   - `db_path` (default `/data/memory.sqlite` — persisted across restarts; blobs live next to it under `/data/blobs`),
   - `bearer_token` (**required** — the add-on refuses to start without it),
   - `allowed_domains` (optional scoping for the root token),
   - `public_base_url` (your external origin, e.g. `https://memory.example.com`, so artifact links are absolute),
   - `artifact_signing_key` (optional; falls back to the bearer token),
   - `log_level`.
4. Start the add-on. It serves streamable HTTP on port **8099** at `/mcp` (and the viewer at `/ui`). Expose it remotely with your own reverse proxy / tunnel and point clients at `https://<host>/mcp`.

**Backups.** Everything the server owns lives under the add-on's `/data` volume (the SQLite DB + the blob store), so a Home Assistant backup that includes this add-on captures all of it. The add-on declares `backup: cold`, so the Supervisor stops it for the duration of a backup — the WAL-mode database is captured as a consistent snapshot rather than hot-copied while connections are open (brief downtime during backups only).

## Operator CLIs

The binary doubles as a small admin CLI (run inside the add-on container; not model-facing):

```bash
# Per-token domain scoping (only the hash is stored; the raw token prints once)
dotnet MemoryMcp.Server.dll tokens add <label> <domains|*>
dotnet MemoryMcp.Server.dll tokens list
dotnet MemoryMcp.Server.dll tokens revoke <id>

# Maintenance (dry-run by default; pass --apply to commit)
dotnet MemoryMcp.Server.dll gc-blobs [--apply]              # delete CAS blobs no attachment references
dotnet MemoryMcp.Server.dll normalize-identifiers [--apply] # lowercase legacy domain/type/tags

# Backlog import/export (the dogfood use case)
dotnet run --project src/MemoryMcp.Server -- import-backlog backlog.md
dotnet run --project src/MemoryMcp.Server -- export-backlog out.md
dotnet run --project src/MemoryMcp.Server -- push-backlog backlog.md <url> <token> [domain]
```

## License

TBD.
