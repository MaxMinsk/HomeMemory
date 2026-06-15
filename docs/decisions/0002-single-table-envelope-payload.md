# ADR 0002 — Single `notes` table: relational envelope + typed `payload_json`

**Status:** Accepted

## Context

The store must hold many *kinds* of note — backlog items, recipes, measurements, journal
entries, skills — across several *domains* (work, kitchen, …). New kinds appear over time and
are mostly added by agents. Every kind needs the same cross-cutting machinery: full-text search,
links between notes, an audit trail, soft-delete, dedup/upsert, and pagination. We did not want a
table (and a migration) per kind, nor a heavyweight ORM, nor to give up validation.

## Decision

Store everything in **one `notes` table** shaped as a **relational envelope plus a typed payload**:

- **Envelope columns** common to all notes: `id, domain, type, title, body, payload_json,
  tags_json, dedup_key, status, source_agent, schema_ver, created_utc, updated_utc, deleted`.
  `domain` is the namespace; `type` is the schema discriminator.
- **`payload_json`** holds the kind-specific fields, validated on write against a **JSON Schema
  registry** keyed by `type@version` (draft 2020-12). The schema is the hard, enforced contract.
- Supporting tables: **`note_links`** (graph-lite relations like `depends_on`, `supersedes`),
  **`note_events`** (append-only audit), and a contentless **FTS5** index over `title`/`body`
  with BM25 ranking (snippets rebuilt in C#).
- Reads can filter the payload via `json_extract`, exposed safely to agents through a small,
  parameterized **filter DSL** (`payload.sprint == 'S1'`).
- Deletes are **soft** (`status`/`deleted`); there is no hard delete in the tool surface.

## Consequences

- **+** One uniform set of queries, pagination, search, links and audit serves every note kind.
- **+** Adding a kind is *just registering a schema* — no table, no migration, no new SQL paths.
- **+** Schema versioning (`type@version`, `schema_ver`) lets payloads evolve without breaking rows.
- **−** Payload fields are not first-class relational columns; querying them goes through
  `json_extract`/FTS rather than indexed columns (mitigated by the filter DSL and full-text search;
  hot payload fields can be promoted to generated columns later if needed).
- **−** Validation lives in the application layer, not the database, so all writes must go through
  the repository (which they do — the DB is never written directly).

### Alternatives considered

- **Table per type** — strong relational queries per kind, but every new kind needs schema work and
  migrations, and the cross-cutting features (search/links/audit) would be duplicated per table.
- **Document database** — natural for typed payloads, but adds an operational dependency and loses
  SQLite's single-file simplicity, FTS5, and the easy single-artifact add-on deploy.
- **EF Core / ORM over many entities** — heavier, and the genuinely tricky parts (FTS5, migrations,
  contentless snippets) get no help from it; raw ADO with one mapper keeps the data layer thin.
