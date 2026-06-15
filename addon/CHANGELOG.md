# Changelog

## 0.3.0

- **Server-hosted skills**: new `skill_upsert` / `skill_list` / `skill_get` tools and a `skill@1` type.
  Skills are shared guidance for how to author each note type; when an `notes_upsert` fails schema
  validation, the error now points at any skill that teaches that type (`skill_hint`).
- **Two-phase confirmation for destructive actions**: `notes_archive` and `notes_supersede` no longer
  apply immediately — they return a confirmation token; call `notes_confirm` to apply it (executed at
  most once via compare-and-swap) or `notes_cancel` to drop it. The `pending_actions` table is the audit trail.
- **Search**: `notes_search` gains `includePayload` — each hit can carry its status and payload JSON
  (still no body), so a board renders without a follow-up `notes_get` per row.
- **Filter DSL**: supports `is null` / `is not null`, e.g. `payload.sprint is null` for the general backlog.
- **Link relations** are validated as active-voice `lower_snake_case` verbs at the `notes_link` boundary.
- Internal: expected, model-visible errors (bad input, out-of-scope, invalid filter) are no longer logged
  as server faults with stack traces.

## 0.2.0

- **Search filter DSL**: `notes_search` gains a `filter` parameter — small, safe expressions
  (`field op value` joined by `AND`/`OR` with parentheses; operators `== != in`) over envelope
  fields and `payload.<x>`, e.g. `payload.sprint == 'S1' AND payload.status in ('ready','next')`.
  Values are always parameterized; field names are whitelisted/validated (no SQL injection).
- **Sprint model**: new `sprint@1` note type (key/goal/status/version_target/dates) and an optional
  `sprint` field on `backlog_item` (empty = not part of a sprint). Additive and backward-compatible.
- Internal (no runtime behavior change): added Meziantou.Analyzer with ReDoS-hardened regexes and
  smaller methods; SQLite connection tidy-up (`Foreign Keys` keyword, `synchronous=NORMAL`); test
  suite split into fast/integration with `FakeTimeProvider`; English-only CI gate; README + ADRs.

## 0.1.6

- Read tools (`notes_search`/`notes_get`, `schema_list_types`, `status`) return **structured content** (typed output schema) and carry behavioral hints (read-only / idempotent / non-destructive) so clients use them correctly.
- Expected failures (invalid payload, out-of-scope domain) surface to the model as **readable error messages** instead of a generic "an error occurred".

## 0.1.5

- `notes_search` is now **paginated**: `limit` is clamped to 1–100, a new `offset` pages results, and the response is a `SearchPage` with `total`/`hasMore` so clients page instead of pulling the whole store.
- Internal: split the notes repository by responsibility (reader / writer / audit + pure mapper & snippet helpers) — no behavior change.

## 0.1.4

- `backlog_item` schema gains an optional `assignee` (e.g. `me` / `agent` / a name) so personal-backlog views can hide agent-owned tasks. Additive and backward-compatible.

## 0.1.3

- Republish (no functional change since 0.1.2). Ensures the stateless `/mcp` build is distributed and gives a clean update to pull.

## 0.1.2

- Streamable HTTP now runs in **stateless** mode — clients no longer need to carry an `Mcp-Session-Id`; more robust behind tunnels and across restarts. A bare browser GET to `/mcp` returns 405 (expected; it is a POST endpoint).

## 0.1.1

- MCP endpoint moved to the conventional `/mcp` path (was `/`). Connect clients to `http://<host>:8099/mcp`.

## 0.1.0

- Initial release.
- SQLite store (WAL + `user_version` migration ladder), FTS5/BM25 search.
- Schema registry + validation (`backlog_item`); note CRUD with append-only audit and soft-delete.
- MCP tool surface over stdio and streamable HTTP; bearer auth with per-token domain scoping.
- Backlog import/export CLI.
