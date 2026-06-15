# Memory MCP

A thin, reliable [MCP](https://modelcontextprotocol.io) server for personal memory, notes and skills — shared by multiple agents (a Home Assistant agent, Claude Code, a future web UI) over the Model Context Protocol. It stores notes, enforces their schemas, and **hosts the skills that teach every agent how to write each note type the same way** — so different agents keep recipes, backlog items and measurements consistent. The server itself never runs an LLM: the reasoning (what to record, how to phrase it) happens in the agents, *guided by* the schemas and skills they fetch from the server.

> **Status:** early scaffold (milestone M0). The first milestone hosts the project's own backlog as the first memory use case — dogfooding the store before adding recipes, measurements and files.

## Design at a glance

- **Single `notes` table**: a relational envelope (`id, domain, type, title, body, payload_json, tags, dedup_key, status, …`) plus a typed `payload_json` validated by a JSON Schema registry. `domain` is a namespace (work / kitchen / …), `type` is the schema discriminator (`backlog_item`, `recipe`, …).
- **SQLite** (WAL + a `PRAGMA user_version` migration ladder). **FTS5/BM25** search; no vectors in the MVP.
- **Append-only event log** + soft-delete only (no hard delete in the tool surface).
- **Schemas and skills are server-hosted, shared conventions**: the schema is the hard contract (enforced on write); the skill is the soft craft (naming, units, how to render) served to agents on demand — so every agent writes a given note type the same way.
- Small MCP tool surface; bearer auth with per-token domain scoping; stdio + streamable HTTP transports.

See [`AGENTS.md`](AGENTS.md) for conventions and the architectural principles that must hold.

## Layout

```
src/MemoryMcp.Core    — domain, storage, schema registry (library)
src/MemoryMcp.Server  — MCP host (stdio + streamable HTTP)
tests/MemoryMcp.Tests — unit & integration tests
```

## Build & test

Requires the .NET 8 SDK (pinned via `global.json`).

```bash
dotnet restore
dotnet build
dotnet test
```

## License

TBD.
