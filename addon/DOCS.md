# Memory MCP

A shared memory / notes / skills data-plane (MCP) for your Home Assistant agents.

## What it does

Stores notes as a relational envelope plus a typed JSON payload, with full-text search (FTS5/BM25), an append-only audit log and soft-delete (no hard delete). It exposes an MCP **streamable-HTTP** endpoint on port `8099` that any MCP client (the Home Assistant agent, Claude Code, …) can use, so multiple agents share one consistent memory.

## Configuration

| Option | Meaning |
|---|---|
| `db_path` | SQLite database path. Default `/data/memory.sqlite` (persisted across restarts). |
| `bearer_token` | Required for remote access. Sent by clients as `Authorization: Bearer <token>`. Leave empty only for trusted local testing. |
| `allowed_domains` | Comma-separated domains this token may access (e.g. `kitchen,personal`). Empty = all domains. |
| `log_level` | `Trace` / `Debug` / `Information` / `Warning` / `Error`. |

## Connecting

Point your MCP client at `http://<home-assistant-host>:8099/` with the bearer token. The database lives in the add-on's `/data` volume and survives restarts and updates.
