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

Advanced (environment variables, optional): `MEMORY_BLOB_ROOT` (blob store dir, default `/data/blobs`), `MEMORY_BLOB_QUOTA_BYTES` (default 1 GiB), `MEMORY_INGEST_ROOT` (directory that `artifacts_put`'s `sourcePath` files must live under; unset disables file ingestion).

## Viewer (read-only)

A minimal web viewer is served at **`/ui`** (also the add-on's "Open Web UI" button). Open it, paste
the bearer token once (kept in the browser), then browse: filter notes by type/domain/tag or full-text,
and open a note to see its structured payload, body and attachments. It is read-only and backed by a
small JSON API (`/api/stats`, `/api/search`, `/api/notes/{id}`) that requires the same bearer as `/mcp`.

## Stats

On startup the add-on logs a one-line snapshot of what's stored, e.g.:

```
Memory stats: schema v4, 56 notes (backlog_item=49, skill=2, sprint=3, ...), 0 attachments, 0 blob bytes.
```

The same breakdown (note counts by type, attachment count, blob bytes) is available any time via the MCP `status` tool, so an agent can report or chart it. (A native Home-Assistant sensor feed for graphing is a planned follow-up.)

## Connecting

Point your MCP client at the **streamable-HTTP** endpoint `http://<home-assistant-host>:8099/mcp` with the bearer token (`Authorization: Bearer <token>`). It is a `POST` endpoint that negotiates over `text/event-stream`; opening it in a browser (GET) returns 400 — that is expected, it is not a web page. The database lives in the add-on's `/data` volume and survives restarts and updates.

For remote access through a Cloudflare Tunnel, route a hostname to `http://<home-assistant-host>:8099` and connect to `https://<your-host>/mcp`. The bearer token is the only gate on a public endpoint, so use a long random one.
