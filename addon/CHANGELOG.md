# Changelog

## 0.17.0

Sprint 10 — security/authorization hardening (from the Codex code review) + ambient memory.

- **Auth is mandatory in HTTP mode** (MEMP-100): the server (and add-on) refuse to start without
  `bearer_token` unless `ALLOW_UNAUTHENTICATED_HTTP=true` (local dev). Artifact URLs can be signed with a
  dedicated `artifact_signing_key` (new add-on option); the built-in fallback secret can no longer key
  real URLs.
- **Confirmations are domain-scoped** (MEMP-098): a pending action records its target domain; a
  restricted caller only lists/confirms/cancels tokens in its own domains (out-of-scope tokens look
  unknown — no cross-domain leak).
- **Viewer & artifact endpoints respect scope** (MEMP-099): `/api/search` restricts to the caller's
  domains, `/api/notes/{id}` hides out-of-scope notes, and bearer access to `GET /artifacts/{id}` /
  `PUT /artifacts/upload` is authorized by the artifact's domain; signed URLs remain capabilities.
- **Ambient memory** (MEMP-105): `initialize` now instructs agents to use Memory as durable working
  memory on their own (recall, capture, consolidate — never secrets), pointing at the new
  `agent-memory-use` skill.

## 0.16.0

Sprint 9 — atomic assembly & dedup hygiene.

- **`notes_assemble`** (MEMP-075): create (or dedup-upsert) a note AND its outgoing links in one
  transaction — all-or-nothing. If any link's target is missing or its rel invalid, nothing is
  persisted (no half-built case). payload/tags accept an object/array or a JSON string.
- **Duplicate detection** (MEMP-027): `notes_lint` gains a read-only `duplicate` rule that flags active
  notes sharing the same `(domain, type, title)`.

## 0.15.0

Sprint 8 — agent ergonomics & test hardening.

- **Structured tool inputs** (MEMP-072): `notes_upsert`/`notes_patch` `payload`+`tags` and `schema_upsert`
  `schema` now accept a JSON **object/array directly** — no more double-serializing into a string. The
  previous JSON-string form still works, so existing callers are unaffected.
- **Better journal capture** (MEMP-041): `notes_append_journal` derives a title from the first line (or
  takes one), accepts tags, assigns a stable `dedupKey`, and tags the note `unstructured` so it can be
  found for later structuring. The viewer no longer renders a literal "null" for notes with no payload.
- **Test hardening** (MEMP-096): the signed artifact upload endpoint (`PUT /artifacts/upload`) now has an
  end-to-end HTTP test (upload → serve back → reject tampered signature).

## 0.14.0

Sprint 7 — read-ergonomics finish, maintenance & viewer (from the Codex 0.12.0 re-review).

- **Viewer v2** (MEMP-071/082): status filter (active by default — archived/superseded hidden until chosen),
  load-more pagination, deep-link URL state (shareable filters + selected note), and a logout button.
- **Large-note reads finished**: `notes_outline` headings now include `endOffset` (MEMP-094); the forced
  server instructions tell agents to peek/slice a big note before pulling the whole body (MEMP-095);
  `notes_history_event` gains `maxChars` + `fields` (full/before/after/changed) so a huge diff can't flood
  context in one call (MEMP-093).
- **Maintenance CLIs** (admin/ops, dry-run by default; `--apply` to write):
  - `gc-blobs` — deletes content-addressed blobs no attachment references (cleans historical orphans) and
    reports attachments whose blob is missing (MEMP-091).
  - `normalize-identifiers` — lowercases existing note domain/type, canonicalizes tags and lowercases
    attachment domains so legacy data (e.g. `Home`) matches the write-time normalization; collision-aware
    (MEMP-092).

## 0.13.0

Sprint 6 — data quality + artifact lifecycle.

- **`notes_lint`** (MEMP-073): read-only data-quality scan that flags notes which are hard to find or
  maintain (`no_tags`, `no_dedup_key`, `no_title`) and dangling links (`broken_link`). Scope-limited,
  domain-focusable; returns structured findings. Suggests fixes, changes nothing.
- **Two-phase artifact delete** (MEMP-070): `artifacts_delete` no longer deletes immediately — it returns a
  confirmation token; `notes_confirm` applies it (`notes_cancel` drops it), GC'ing the blob if unreferenced.
  Destructive ops are now uniformly reversible-by-default.
- **Signed artifact upload** (MEMP-066): `artifacts_request_upload` returns a short-lived signed PUT URL
  bound to the exact domain/filename/contentType/noteId; a remote agent PUTs opaque bytes (photos/PDF)
  straight to the server — never through the model context. Blob quota still applies.
- **`notes_search includeLinks`** (MEMP-034): each hit can carry its links (both directions), so a board/
  graph renders without a `notes_links` call per row.

## 0.12.0

Sprint 5 — large-note ergonomics + hygiene (context-efficient reads, from the Codex architecture note).

- **Peek / window a note** (MEMP-086): `notes_get` gains `includeBody=false` (peek: envelope + payload +
  counts, no body) and `bodyMaxChars` (cap the body), and now reports `bodyChars`, `truncated`,
  `attachmentCount`, `linkCount`. Defaults are unchanged (full body) — no silent truncation.
- **Partial body reads** (MEMP-087): `notes_read(id, offset, limitChars)` returns a slice with
  totalChars/returnedChars/truncated and a `nextOffset` cursor + the revision; `notes_outline(id)` maps
  Markdown headings to offsets so a section read is `notes_read(headingOffset, nextHeadingOffset - it)`.
- **Compact history** (MEMP-088): `notes_history` no longer dumps the full before/after per event (huge for
  big notes) — each entry is eventId/op/actor/ts/changedFields/diffBytes; fetch one event's full diff with
  `notes_history_event(id, eventId)`.
- **In-note search** (MEMP-089): `notes_find(id, query, contextChars, limit)` — ripgrep within a single
  note's body: match offsets + context windows, so you locate and read only the relevant parts.
- **Case-insensitive identifiers** (MEMP-064): domain/type are normalized to lowercase (tags too, de-duped)
  on write, on read filters, and in scope auth — `Home` and `home` are one domain; no case-variant
  duplicates; a token scoped to `home` admits a `Home` request.
- **Ops visibility** (MEMP-074): `status` now reports `serverVersion` (so agents can confirm which build
  prod runs) and `blobQuotaBytes` alongside `blobBytes`.

## 0.11.0

- **Fix (orphan blob on replace)**: re-attaching a same-named file to a note (`artifacts_put` replace,
  MEMP-085) now garbage-collects the superseded blob's bytes when nothing else references them — previously
  only `artifacts_delete` GC'd, so a replace left the old blob on disk forever. Regression test covers
  put v1 → replace v2 → delete v2 returning blob storage to baseline.

## 0.10.0

- **`artifacts_url`**: returns a temporary signed URL (default ~1 day, no bearer in it) to open or validate
  an artifact in a browser — bytes still never pass through the model context. New `public_base_url`
  add-on option makes these URLs absolute (shareable).
- **Find notes by key**: full-text search now indexes `dedup_key`, so searching "072" or "MEMP-072"
  finds the ticket whose key is `MEMP-072` (previously the key wasn't searchable).

## 0.9.0

Reliability & multi-agent hardening (from two product reviews).

- **Forced onboarding**: the server now returns `instructions` on connect (the core "how to author" model),
  plus a `memory-authoring` core skill. New discovery tools: `domains_list`, `tags_list`.
- **Safe updates**: `notes_patch(id, …, expectedUpdatedUtc)` — merge (not full replace) with optimistic
  concurrency; `notes_get`/upsert expose `updated_utc` as the revision/etag.
- **Links graph is readable**: `notes_links` (both directions, resolved), `notes_unlink`; the viewer shows links.
- **Reversibility & history**: `notes_restore` (un-archive), `notes_history` (the audit log), `pending_actions_list`.
- **Artifact security**: browser links are short-lived signed URLs — the bearer token is no longer placed in
  artifact URLs. `schema_get(type, version)` for an exact schema version.
- **Search**: prefix matching finds longer word forms; `notes_search` (includePayload) also returns
  tags/dedupKey/updatedUtc; the viewer shows tags and a domain dropdown.
- **Stats**: default counts are active-only (archived no longer inflate them); adds notesByStatus,
  notesByDomain and pendingActionsCount.

## 0.8.0

- **Fix**: text artifacts (`/artifacts/{id}`) are now served with `charset=utf-8`, so Cyrillic markdown/HTML
  renders correctly in the browser instead of mojibake.
- **Domain discovery**: `status` / `/api/stats` now include `notesByDomain`; the viewer's domain filter
  is a dropdown of existing domains (so it's clear what domains exist and that they differ from tags).

## 0.7.0

- **`artifacts_delete`** tool: remove an attachment by id; the underlying blob is garbage-collected if
  nothing else references it. The clean way to drop unwanted/duplicate artifacts (MEMP-059).
- **Viewer**: the note detail now labels `domain` / `type` / `status` explicitly and shows tags as
  distinct chips, so it's clear what is what at a glance (MEMP-063).

## 0.6.0

- **Fix (MEMP-059)**: `artifacts_put` is now idempotent per `(note, filename)` — re-attaching a
  same-named file to the same note replaces the previous attachment instead of piling up duplicates
  (the blob bytes were already de-duplicated; this removes the duplicate metadata rows). Existing
  duplicates from earlier runs collapse the next time their file is re-attached.

## 0.5.0

- **Read-only web viewer** at `/ui` (also the add-on's "Open Web UI" button): paste the bearer once,
  then filter notes by type/domain/tag or full-text and open a note to see its structured payload,
  body and attachments. Backed by a small JSON API (`/api/stats`, `/api/search`, `/api/notes/{id}`).
- **Artifacts as browser links**: `GET /artifacts/{id}` serves a stored artifact's bytes with its
  content-type — a rendered recipe HTML opens in the browser, markdown shows as text. Authenticated by
  the bearer (header or `?t=<token>` so plain links work); the bytes go to the browser, never through
  an agent's context. (Signed capability URLs with TTL/revoke remain a later enhancement.)

## 0.4.0

- **Artifacts / files**: content-addressed blob store + `attachments` and the `artifacts_put` /
  `artifacts_get` / `artifacts_list` tools. Bytes never pass through the model context — `artifacts_put`
  takes inline text (agent-generated docs) or a server-side file under the ingest root; reads return
  metadata + a `blob://` URI. Byte quota; thumbnails are a planned follow-up.
- **Agent-authored schemas**: new `schema_upsert` tool registers or updates a note type's JSON Schema
  at runtime, so new types no longer need a release. Two-tier and safe: built-in types are read-only,
  a version already used by notes can't change (bump the version), and schema `pattern`s are bounded
  against ReDoS.
- **Recipes**: `recipe@1` structured type (the source of truth) plus a `recipe-authoring` skill; the
  human-readable markdown/HTML are derived artifacts, regenerated on every edit.
- **Stats**: `status` now reports note counts by type, attachment count and blob bytes; the add-on also
  logs this snapshot at startup.
- **Multi-project**: `backlog_item` and `sprint` gain an optional `project` field.

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
