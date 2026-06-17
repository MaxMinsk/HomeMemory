# Memory MCP

A shared memory / notes / skills data-plane (MCP) for your Home Assistant agents.

## What it does

Stores notes as a relational envelope plus a typed JSON payload, with full-text search (FTS5/BM25), an append-only audit log and soft-delete (no hard delete). It exposes an MCP **streamable-HTTP** endpoint on port `8099` that any MCP client (the Home Assistant agent, Claude Code, …) can use, so multiple agents share one consistent memory.

## Configuration

| Option | Meaning |
|---|---|
| `db_path` | SQLite database path. Default `/data/memory.sqlite` (persisted across restarts). |
| `bearer_token` | Required for remote access. Sent by clients as `Authorization: Bearer <token>`. Leave empty only for trusted local testing. |
| `allowed_domains` | Comma-separated domains the **root** token may access (e.g. `kitchen,personal`). Empty = all domains. Per-agent scoped tokens are created with the `tokens` CLI (below). |
| `public_base_url` | Your external origin (e.g. `https://memory.example.com`), so artifact links are absolute. Optional. |
| `artifact_signing_key` | HMAC key for signed artifact URLs. Optional — falls back to the bearer token. |
| `log_level` | `Trace` / `Debug` / `Information` / `Warning` / `Error`. |

Advanced (environment variables, optional): `MEMORY_BLOB_ROOT` (blob store dir, default `/data/blobs`), `MEMORY_BLOB_QUOTA_BYTES` (default 1 GiB), `MEMORY_INGEST_ROOT` (directory that `artifacts_put`'s `sourcePath` files must live under; unset disables file ingestion).

### Per-agent tokens (CLI)

Beyond the root `bearer_token`, you can mint additional tokens each scoped to one or more domains. Run inside the add-on container (only the hash is stored; the raw token prints once):

```bash
dotnet MemoryMcp.Server.dll tokens add kitchen-agent kitchen     # one domain
dotnet MemoryMcp.Server.dll tokens add web-ui "*"                # all domains
dotnet MemoryMcp.Server.dll tokens list
dotnet MemoryMcp.Server.dll tokens revoke <id>
```

## Viewer (read-only)

A minimal web viewer is served at **`/ui`** (also the add-on's "Open Web UI" button). Open it, paste
the bearer token once (kept in the browser), then browse: filter notes by type/domain/tag or full-text,
and open a note to see its structured payload, body and attachments. Tag chips are clickable — a click
filters the search by that tag. The **Inbox** button shows the review queue (open suggestions + lint
findings). It is read-only except for two reviewed-action surfaces backed by the same bearer as `/mcp`.

### Admin panel (root token)

With the **admin** button (signed in with the root, all-domains token) you can run maintenance from the
UI — no container shell needed: **normalize-identifiers** (lowercase legacy domain/type/tags), **gc-blobs**
(delete orphan blobs), each dry-run first then Apply; and **token management** (list / create / revoke
per-agent tokens — the raw token is shown once). A domain-scoped token sees these as forbidden.

### Health

An unauthenticated `GET /health` returns `200 {"status":"ok"}` when the database answers (else `503`). The
add-on registers it as the Supervisor **watchdog**, so Home Assistant restarts the add-on if it stops responding.

## Stats

On startup the add-on logs a one-line snapshot of what's stored, e.g.:

```
Memory stats: schema v10, 56 notes (backlog_item=49, skill=2, sprint=3, ...), 0 attachments, 0 blob bytes, db 1.32 MB.
```

The same breakdown (note counts by type, attachment count, blob bytes, on-disk DB size) is available any
time via the MCP `status` tool, so an agent can report or chart it. The `memory_capabilities` tool reports
the runtime contract (build/schema version, known note types, the caller's domain scope) so an agent can
discover what the server supports on connect. (A native Home-Assistant sensor feed for graphing is a
planned follow-up.)

## Backups

Everything the add-on owns lives under its `/data` volume — the SQLite database and the blob store — so a
Home Assistant backup that includes this add-on captures all of it. The add-on uses **cold backups**: the
Supervisor stops it for the duration of a backup so the WAL-mode database is captured as a consistent
snapshot rather than hot-copied while connections are open (brief downtime during backups only).

## Connecting

Point your MCP client at the **streamable-HTTP** endpoint `http://<home-assistant-host>:8099/mcp` with the bearer token (`Authorization: Bearer <token>`). It is a `POST` endpoint that negotiates over `text/event-stream`; opening it in a browser (GET) returns 400 — that is expected, it is not a web page. The database lives in the add-on's `/data` volume and survives restarts and updates.

For remote access through a Cloudflare Tunnel, route a hostname to `http://<home-assistant-host>:8099` and connect to `https://<your-host>/mcp`. The bearer token is the only gate on a public endpoint, so use a long random one.
