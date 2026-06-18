# Changelog

## 0.50.0

Sprint 43 — search & viewer polish (MEMP-164–168).

- **Range filters** (MEMP-164): the filter DSL gains `<`, `<=`, `>`, `>=` — e.g. `payload.priority >= 5` or
  `updated_utc > '2026-06-01'` (numeric-aware, parameterized).
- **`notes_tags`** (MEMP-165): new read-only tool listing distinct tags with counts (facet discovery),
  scope-restricted and optionally within one domain — to see the tag vocabulary before tagging/filtering.
- **Exact phrase search** (MEMP-166): a fully double-quoted query (e.g. `"thread exhaustion"`) matches the exact
  adjacent, ordered phrase instead of independent prefix tokens.
- **Viewer keyboard navigation** (MEMP-167): `/` focuses search, `j`/`k` (or arrows) move the result selection,
  Enter opens, Esc blurs.
- **Payload as a table** (MEMP-168): the note detail renders the payload as a readable key/value table with a
  collapsible raw-JSON toggle.

## 0.49.0

Sprint 42 — search & viewer polish (MEMP-159, MEMP-160, MEMP-161, MEMP-162, MEMP-163).

- **Searching a key surfaces that note first** (MEMP-159): with no explicit sort, a note whose `dedup_key` equals
  the query (e.g. `HPA-008`) now ranks above notes that merely mention it.
- **Exact title match boost** (MEMP-160): a note whose title equals the query ranks just after an exact-key match
  and above plain relevance.
- **`contains` filter operator** (MEMP-161): the filter DSL gains `field contains 'x'` — a case-insensitive
  substring match for `title` and `payload.<field>` (e.g. `payload.subject contains 'STU-12'`), safely escaped.
- **Search-match highlighting in the viewer** (MEMP-162): result rows show the snippet with the matched terms
  highlighted instead of raw bracket markers.
- **List-row polish** (MEMP-163): rows now show a relative "updated" time (project badge + clickable tag chips
  were already there).

## 0.48.0

Sprint 41 — events & portability (MEMP-155, MEMP-156, MEMP-056, MEMP-157, MEMP-158).

- **Changefeed for subscriptions** (MEMP-155): new read-only `notes_changes(since, domain?, type?)` returns notes
  changed since an opaque cursor (create/update/supersede/archive), oldest-first, scope-restricted, paginated.
  Built on the append-only event log; consumers store the cursor and poll — the server stays stateless.
- **Real-time MQTT events** (MEMP-156, opt-in, default OFF): when configured, the server publishes a small event
  `{id, domain, type, project, tags, op, ts}` (no body/secrets) to MQTT on every note change — so Home Assistant
  automations and agents can react in real time. Best-effort: a broker outage never affects writes.
- **Memory stats as HA sensors** (MEMP-056): when MQTT is on, the server publishes HA MQTT-discovery configs +
  periodic state (note count, DB size, attachment count, per-domain/type counts) so memory shows up as graphable
  Home Assistant sensors.
- **Backup/portability** (MEMP-157/158): root-only `GET /api/admin/export` streams all notes as NDJSON; `POST
  /api/admin/import` loads NDJSON, upserting idempotently by (domain,type,dedupKey) with a dry-run that reports
  created/updated/invalid counts.

New add-on options (all optional, MQTT disabled by default): `mqtt_enabled`, `mqtt_host`, `mqtt_port`,
`mqtt_username`, `mqtt_password`, `mqtt_topic_prefix`.

## 0.47.0

Sprint 40 — viewer Markdown + universal project axis (MEMP-153, MEMP-154).

- **The viewer renders note bodies as Markdown** (MEMP-153). Instead of raw text, the body now shows formatted
  headings, lists, tables, code blocks, blockquotes, links, bold/italic. Rendered by a small self-contained
  renderer that HTML-escapes first (XSS-safe) and only allows http(s)/relative links — no external dependency.
- **Any note type can belong to a project** (MEMP-154). The envelope `project` axis previously couldn't be set
  on types whose schema forbids extra fields (e.g. `reference`, `fact`, `decision`), because `payload.project`
  was rejected at validation. Now the writer validates against the type schema with a top-level `project` lifted
  out, so `payload.project` (or the explicit `project` argument) sets the project axis on **any** type. Stored
  payload is unchanged; the envelope project is derived as before.

## 0.46.0

Sprint 39 — tech-debt + curation (MEMP-103, MEMP-083). Internal/maintenance release — no behavior or API change.

- **Code maintainability (MEMP-103):** the two largest classes were split into partial classes by concern, with
  no behavior change — `NotesReader` (963 lines) → core reads + `NotesReader.Search` + `NotesReader.Discovery`;
  `MemoryTools` (588) → constructor/helpers + `MemoryTools.Reads` (read tools) + `MemoryTools.Writes` (mutating
  tools). Easier to navigate; the tool surface and runtime are identical.
- **Tagging audit (MEMP-083):** audited tag coverage across domains — the corpus is already well-tagged (kitchen
  recipes curated by the kitchen agent; development knowledge notes and backlog items tagged), so no mass re-tag
  was applied. No code change.

(Owner: this release has no functional change — updating the add-on is optional, only to keep `serverVersion` current.)

## 0.45.0

Sprint 36 — consolidation & scoping hygiene (MEMP-027, MEMP-036, MEMP-117).

- **Merge duplicate notes** (MEMP-027): a root-only `POST /api/admin/merge-duplicates` + viewer "Merge
  duplicates" form (dry-run, then Apply) collapses groups of exact-content duplicates (same `content_hash`) within
  a domain into the newest copy — each older copy is **superseded** (with a `supersedes` link) and its incoming
  links are **re-pointed to the canonical**, so references survive and duplicates drop out of active search. Runs
  in one transaction; the dry run reports how many groups/notes would merge.
- **Skill consolidation advice** (MEMP-036): a read-only `skill_consolidate_plan` tool flags **redundant project
  overrides** (a project skill identical to the domain-general one it shadows) and **duplicate skills** (identical
  bodies under different keys), each with a suggested action (delete / merge) and the related key. It only
  proposes — apply via `skill_upsert`.
- **Scope-axis conventions** (MEMP-117): documented `session` / `thread` / `subject` as **payload conventions**
  for fine-grained scoping (vs. `domain` = security boundary and `project` = product), shipped as the world-readable
  `memory-scope-axes` skill. No schema change — these are already filterable (`payload.<field>`) and searchable.

## 0.44.0

Sprint 38 — cut payload-search noise (MEMP-152).

- **Searching a field name no longer matches every note.** The full-text index over a note's payload now indexes
  the field **values**, not the field **names**. Previously the whole `payload_json` (keys + values) was indexed,
  so a query like `status` or `priority` matched every note that merely *has* that field. Now those key names
  drop out while all values stay fully searchable (including Cyrillic values like a recipe's `russian_name`).
  Implemented in SQL via `json_tree(payload_json) WHERE type='text'`; migration 0015 recreates the FTS triggers
  and reindexes existing notes. (Complements 0.43.0's stemmed sidecar, which already indexed values, not keys.)

## 0.43.0

Sprint 37 — bilingual stemmed search (MEMP-024).

- **Search now matches word forms across Russian and English.** Previously a query word only matched by exact
  text or prefix, so `ANRs` found nothing while `ANR` worked, and Russian cases/plurals missed each other. A new
  **stemming sidecar** (Snowball via the pure-managed `libstemmer.net`) indexes a stemmed copy of each note's
  natural-language text in a `stems` FTS column (routed by alphabet: Cyrillic→Russian, Latin→English), and
  queries OR-in the stemmed terms. So `ANRs`↔`ANR` (and Russian declensions like `zadacha`↔`zadache`) now match.
- **Raw search is unchanged — IDs and code are never corrupted.** The existing title/body/tags/dedup_key/payload
  FTS columns are untouched, so exact, prefix, and identifier search behave exactly as before; stems only *add*
  recall. Only natural-language text is stemmed: code blocks, inline code, URLs and file paths are stripped, and
  only pure-letter tokens are stemmed — so note IDs, dedupKeys, JSON keys, tool/MCP command names, file paths and
  versions are never stemmed. Migration 0014 adds the column and reindexes existing notes.

## 0.42.0

Sprint 35 — knowledge graph, dedup integrity & smart capture (MEMP-031, MEMP-035, MEMP-111, MEMP-018).

- **Knowledge-graph traversal** (MEMP-031): new `notes_graph` tool returns a note's **N-hop link
  neighborhood** as nodes (id, title, type, domain, hops-from-root) + edges (fromId, toId, rel), scope-filtered.
  See how a note connects — dependencies, derivations, sprint membership — in one call instead of walking
  `notes_links` by hand. `maxHops` 1–5 (default 2); a large neighborhood is capped (`truncated=true`).
- **Deterministic content hash + duplicate detection** (MEMP-035): every note now carries a `content_hash`
  (SHA-256 of its canonical type+title+body+payload+tags, with JSON keys sorted so equal-but-differently-written
  payloads match). Migration 0013 backfills existing notes. A new `duplicate_content` lint flags active notes
  with **identical content under different keys** (the title-based `duplicate` rule stays).
- **Capture help** (MEMP-111): new read-only `notes_suggest_capture` tool — before writing a note, ask whether
  you should. It returns an action (**save / update / skip / ask**), a reason, and the existing notes behind it,
  by checking for negligible content, an identical note (content hash), a same-title/type note to **update**
  instead, or merely similar notes (full-text) to **review**. Helps avoid duplicating shared memory.
- **Architecture decision records** (MEMP-018): wrote ADRs 0003 (lexical-FTS-first, no vectors), 0004 (typed
  payload is the source of truth; rendered docs are derived), 0005 (procedural work runs agent-side; the server
  stays a data-plane) under `docs/decisions`.

## 0.41.0

Sprint 34 — retrieval & ops polish (MEMP-104, MEMP-038, MEMP-109, MEMP-037).

- **Large notes are windowed in the database now** (MEMP-104): `notes_read` slices a note's body with SQLite
  `substr`/`length`, so a multi-megabyte body is never loaded into memory just to return a few kilobytes. The
  partial-read contract is unchanged; offsets/lengths are Unicode code points.
- **Admin scoped-purge** (MEMP-038): a root-only `POST /api/admin/scoped-purge` + viewer "Scoped purge" form
  (dry-run, then Apply) that permanently deletes notes matching a **domain and/or source agent**, plus their
  satellite rows (events, usage, links, attachments, pending actions), in one transaction — so it reclaims space
  (unlike soft delete). It refuses an unscoped purge (at least one of domain/source-agent is required); the FTS
  index stays in sync via its delete trigger, and orphaned attachment blobs are reclaimed by the gc-blobs pass.
- **Return ergonomics** (MEMP-109): `notes_append_journal` now returns the **created note's envelope** (id,
  derived title, assigned dedupKey, typed tags) instead of a bare id, so a capture needs no follow-up get. And
  `notes_get`/`notes_get_by_key`/`notes_append_journal` now include the payload and tags **already parsed**
  (typed `payload` object + `tags` array) alongside the raw JSON strings, so callers needn't re-parse.
- **Recency-decay ranking** (MEMP-037): `notes_search sort="recency"` orders by **type-aware recency** —
  freshest-relative-to-its-type first, with per-type half-lives (episode/journal fade in days; recipe/reference/
  skill are effectively timeless). So a week-old journal sinks below a year-old recipe.

## 0.40.0

Sprint 33 — domain → project consolidation (MEMP-148).

- **Move a whole domain under another as a project.** New root-only `POST /api/admin/move-domain` + viewer
  "Move domain → project" form (dry-run, then Apply) that re-homes every active note in a source domain into a
  target domain (default `development`) with `project = <source domain>` — e.g. `memory-mcp` →
  `development`/project=memory-mcp. The dry run reports how many notes move and lists any dedup clashes
  (`type:dedupKey` already present in the target); an apply is **refused while any clash exists**, and the
  `(domain, type, dedup_key)` unique index makes the apply atomic (a stray clash rolls the whole move back).

## 0.39.0

Sprint 32 — surface + backfill the project axis.

- **The envelope `project` is now visible and self-populating** (MEMP-150): `notes_get`, `notes_search`/
  `domain_manifest`/`memory_context` results, and the viewer (detail + list) now show a note's `project`.
  And `notes_upsert` auto-derives the envelope `project` from `payload.project` when no explicit project is
  given — so a note that carries its project only in the payload (e.g. a `backlog_item`) still gets the axis
  set. (Previously project was write/filter-only and invisible in reads.)
- **Admin "Backfill project"** (MEMP-151): a root-only `POST /api/admin/backfill-project` + viewer button
  that sets the envelope `project` from `payload.project` for notes written before the auto-derive — so the
  already-imported notes can be fixed from the UI, no shell needed (dry-run, then Apply).

## 0.38.0

Hotfix — structured tool output for strict MCP clients (MEMP-149).

- **Tool output now emits null fields instead of omitting them.** The SDK marks every structured-output
  property `required` in the tool schema, but its default serializer omitted nulls — so a required nullable
  field (e.g. a skill's `targetType`/`summary`, a search hit's `snippet`) went missing and **strict clients
  (Cursor) rejected the response**, breaking `skill_get`, `domain_manifest`, `notes_search`, etc. (Claude Code
  is lenient, so it worked there and the issue slipped past CI.) Fixed by serializing tool output with
  `DefaultIgnoreCondition = Never` (cloned from the SDK defaults to keep its converters), for both transports.

## 0.37.0

Sprint 30 — project envelope axis (MEMP-146).

- **`project` is now a first-class envelope axis** on notes: `notes_upsert` takes `project`, the filter DSL
  has a `project` field, and `memory_context`/`domain_manifest` filter rules by project (the project's rules
  plus the domain-general ones). Works for any type — including `memory_rule`, whose payload can't carry it.
  Migration 0012 adds the column + index and backfills from `payload.project`. Skills also carry the envelope
  project. Project is organizational; scope stays at the domain level (per-project token isolation is out of
  scope by decision). This completes the `development`-domain-with-projects model for clean multi-project work.
- Fix: a filter-DSL clause containing `OR` is now parenthesized when combined with the structured
  (domain/type/status) filters — previously the `OR` could mis-bind. (Latent; surfaced building the project filter.)

## 0.36.0

Sprint 29 — project-scoped skills (multi-project foundation, MEMP-147).

- **Skills can be project-specific and override the domain-general one** of the same key. A domain (e.g.
  `development`) can host several projects (`memory-mcp`, `unity-solitaire`); `skill_upsert(domain, key, …,
  project="unity-solitaire")` stores an override that wins for that project. Resolution precedence is
  **project → domain-general → commons**. `skill_get`, `skill_list`, `memory_context` and `domain_manifest`
  all take an optional `project`. (Project is encoded in a qualified dedup key, so no schema change.)
- Project notes already work today via `payload.project` + the filter DSL — so a project's task notes need
  nothing new. The full `project` envelope axis (uniform filtering, rules-by-project) and migrating the
  existing `memory-mcp` data into a `development` domain are tracked separately (MEMP-146 / MEMP-148) and are
  not required to start a new project.
- commons `memory-mcp-operator` skill → v3 with a Projects section.

## 0.35.0

Sprint 28 — polish & ops (batched).

- **`/health` probe + HA watchdog** (MEMP-144): an unauthenticated `GET /health` returns `200 {"status":"ok"}`
  when the database answers (else `503`). The add-on registers it as the Supervisor `watchdog`, so Home
  Assistant restarts the add-on if it stops responding. No version/detail leaked to unauthenticated callers.
- **`memory_context` refinements** (MEMP-145): warns when included rules are stale (opted into
  `stale_after_days` and unverified past their window), and dedupes skills by key across domain + commons.
- **Docs & skills sync** (MEMP-143): README/DOCS updated for the current tool surface (memory_context,
  domain_manifest, schema_provenance, `notes_search` sort, the admin panel); the `commons` operator-manual
  skill bumped to v2 and the curator runbook refreshed.

## 0.34.0

Sprint 27 — recall, review UX, and sortable search (batched).

- **`memory_context` tool** (MEMP-137): assembles a prompt-ready context block for a task in one call —
  the domain's (and commons') active `memory_rule` notes (always_apply, then priority), its skills, and a
  recall of notes relevant to the query (FTS hits + one-hop neighbors), plus an advisory-policy reminder.
- **Inbox UX** (MEMP-131): the viewer Inbox now leads with open evolution suggestions to review, then lint
  findings under human-friendly labels; the noisy per-row `warn` badge is gone (severity is a subtle border).
- **Sortable search** (MEMP-142): `notes_search` takes a `sort` like `"payload.spice_level desc"` — order by
  any payload field (numeric fields sort numerically) or title/created/updated, NULLs last, injection-safe.
  e.g. top-10 hottest peppers: `type=pepper, sort="payload.spice_level desc", limit=10`. Also in `/api/search`.

## 0.33.0

Sprint 26 — context-layer foundations + memory hygiene (a batched release).

- **`memory_rule` note type** (MEMP-135): a new built-in for durable, non-obvious rules — "what's true /
  must hold" — distinct from skills ("how to do"). Fields: description, scope, priority, trigger_phrases,
  topic_globs, always_apply, status, last_verified_at, stale_after_days, source_refs. Plus a `commons`
  skill `create-memory-rule`. Rules are meant to be compact and loaded on demand.
- **`stale_unverified` lint** (MEMP-136): any note that opted into verification (payload `stale_after_days`)
  but hasn't been re-confirmed within that window (baseline `last_verified_at`, else created) — so an aging
  `memory_rule` surfaces for review instead of silently misleading.
- **`oversized_no_summary` lint** (MEMP-138): a large body (>4000 chars) with no heading or summary — a nudge
  to add an outline.
- **`domain_manifest` tool** (MEMP-139): one-call domain orientation — note counts by type, the domain's
  skills, and its active `memory_rule` notes — instead of dumping everything on entry.

## 0.32.0

Sprint 25 — token management from the admin UI (MEMP-141).

- The viewer **admin** panel gains a **Tokens** section: list per-agent tokens (id / label / domains /
  state), **create** one (label + domains; the raw token is shown exactly once — only its hash is stored),
  and **revoke** by id. Root-only endpoints `GET/POST /api/admin/tokens` and `POST /api/admin/tokens/{id}/revoke`
  (a domain-scoped token gets 403). Completes the owner-UI maintenance story started in 0.31.0 — the
  per-agent token CLIs are now reachable without a container shell.

## 0.31.0

Sprint 24 — owner maintenance from the UI + operator manual.

- **Admin panel in the viewer** (MEMP-140): the add-on container has no shell, so the maintenance CLIs
  were unreachable. The viewer now has an **admin** button with dry-run→Apply for **normalize-identifiers**
  (lowercase legacy domain/type/tags, e.g. `Home`→`home`) and **gc-blobs** (delete orphan blobs), backed by
  **root-only** endpoints `POST /api/admin/normalize-identifiers` and `/gc-blobs` (a domain-scoped token gets
  403). Each returns the same report the CLI prints. Token management from the UI is a follow-up (MEMP-141).
- **Operator manual** (MEMP-134): a new `commons` skill `memory-mcp-operator` — which tool when, reading
  large notes, safe writes (optimistic concurrency), the two-phase destructive flow, artifacts, and context
  hygiene. (Runtime skill; not part of the image.)

## 0.30.0

Sprint 23 — one-click review of evolution suggestions (MEMP-133).

- **Apply / Reject buttons in the viewer** for `memory_evolution_suggestion` notes. **Apply** maps the
  suggestion's `proposed_patch` (title/body/tags/payload — mirroring `notes_patch`: tags REPLACE, payload
  MERGES) and `proposed_links` onto the target, then marks the suggestion `applied`. **Reject** marks it
  `rejected`, target untouched.
- This is the viewer's first write path (the seed of the owner UI). It is bearer-gated and scope-checked
  via the shared authorizer, uses optimistic concurrency (won't clobber a note changed since), and is
  non-destructive + fully audited — no two-phase confirm needed. New endpoints `POST
  /api/suggestions/{id}/apply` and `/reject`; logic lives in a unit-tested core `SuggestionReviewer`.
- Curator skill `agent-memory-enrichment` (commons) bumped to v3: propose the COMPLETE tag set (apply
  replaces tags) and a note that suggestions are now one-click-applyable.

## 0.29.0

Sprint 22 — artifact link lifetime fix (MEMP-132).

- **Read/browser artifact links now default to 1 day** (was 1 hour). The viewer's per-attachment
  links (and the signer's `BuildPath` default) used a 1-hour TTL, so a recipe link opened from the
  viewer died within the hour instead of lasting the day we intended. `artifacts_url` was already 1 day;
  this aligns the viewer path with it. Upload (write-capability) URLs deliberately stay short (1 hour).
  Both still clamp to a 7-day max.

## 0.28.0

Sprint 21 — self-organizing memory: reviewable evolution suggestions (MEMP-115).

- **`memory_evolution_suggestion` note type** (new built-in schema): a curator agent proposes an
  improvement to an existing note — `target_id`, `kind` (retag/summarize/link/dedup/restructure/
  correct), `proposed_patch`, `proposed_links`, `evidence_ids`, `rationale`, `confidence`, `status`.
  The server never auto-applies it: a human/agent reviews and applies via `notes_patch`/`notes_link`,
  all in the note's event log. Memory is self-organizing but not self-authorizing.
- **Curator skill** `agent-memory-enrichment` (authored in the `commons` domain): when/how to enrich
  memory without silently mutating it, and how to review/apply the queue.
- **Initialize guidance** now points agents at the suggestion flow + the curator skill.

## 0.27.0

Sprint 20 — memory inbox + schema provenance.

- **Memory inbox** (MEMP-110): a read-only **Inbox** in the viewer surfaces the review queue —
  `notes_lint` findings (unstructured / stale / possible-secret / no-tags / duplicate / broken-link)
  grouped by rule, each row opening the note. Backed by a new scope-enforced `GET /api/lint`. Mutating
  fixes still go through the MCP tools (two-phase for destructive ones) — the viewer stays read-only.
- **Schema authoring provenance** (MEMP-122): the `schemas` table now records `author` + `updated_utc`
  (migration 0011). `schema_upsert` takes an optional `sourceAgent`; built-ins are authored by `system`.
  A new read tool `schema_provenance` lists who registered each `type@version`. Authoring itself stays
  open and unrestricted — this is audit visibility, not a gate.

## 0.26.0

Sprint 19 — centralized scope authorization (internal; no behavior change).

- **One authorization facade** (MEMP-102): every tool (`MemoryTools`/`ArtifactTools`/`SkillTools`)
  and HTTP/viewer endpoint now authorizes domain access through a single injected `RequestAuthorizer`
  instead of each constructing its own `ScopeGuard`. This removes duplicated scope checks and gives a
  new tool/endpoint one obvious, hard-to-bypass entry point (`AuthorizeWrite` / `CanRead` /
  `ReadRestriction` / `WriteRestriction`). Completes the scope-centralization half of MEMP-102 (the
  fail-closed accessor landed in 0.19.0). Pure refactor — the enforcement semantics are unchanged.

## 0.25.0

Sprint 18 — memory hygiene + viewer polish.

- **Lint for rot and leaks** (MEMP-108): `notes_lint` gains two rules — `stale_unstructured`
  (notes tagged `unstructured` and untouched for 30+ days, a review queue so autonomous appends
  don't quietly rot) and `possible_secret` (heuristic scan of body/payload for embedded
  credentials — AWS/GitHub/Slack tokens, private-key blocks, JWTs, `password:`/`api_key=` style
  assignments). The matched secret is never echoed back; only the note id + heuristic name.
- **Backlinks in the viewer** (MEMP-097): the note detail pane now groups links into labelled
  **outgoing** and **incoming (backlinks)** sections instead of one mixed list.
- **CI off Node 20** (MEMP-130): bumped `actions/checkout`→v6 and `actions/setup-dotnet`→v5 so CI
  runs on the supported Node.js 24 runtime (no functional change).

## 0.24.0

Sprint 17 — discoverability, observability and operations.

- **`memory_capabilities`** (MEMP-124): a runtime-contract tool an agent can call on connect — server
  build version, schema version, a contract version, the note types this build knows (latest schema
  version + whether built-in), and the caller's domain scope (readable/writable + commons). No more
  guessing capabilities from a tool list cached at an older session.
- **DB size in stats** (MEMP-128): `status` now reports `dbSizeBytes` (main file + WAL/SHM sidecars),
  the viewer shows `db <n> MB`, and the startup log line includes the database size.
- **Clickable tags in the viewer** (MEMP-129): tag chips (in the list and the detail pane) are now
  clickable — a click filters the search by that tag.
- **Consistent backups** (MEMP-127): the add-on declares `backup: cold`, so Home Assistant stops it for
  the duration of a backup and captures the WAL-mode SQLite DB + blob store under `/data` as a
  consistent snapshot instead of a hot copy.
- **README refresh** (MEMP-125): documents the current feature set, env vars, add-on options and CLIs.

## 0.23.0

Sprint 16 — agent ergonomics (from the kitchen agent's recipe-render workflow, MEMP-123).

- **Exact lookup by key**: `notes_get_by_key(domain, type, dedupKey)` fetches a template/skill/canonical
  note without remembering its id. The filter DSL also now accepts the camelCase names from tool output
  (`dedupKey`, `updatedUtc`, …) as aliases, and an unknown-field error lists the allowed fields.
- **Less round-tripping**: `notes_upsert` returns the note's `type` and `dedupKey` alongside id/revision,
  so a write needn't be followed by a get.
- **Validate artifacts cheaply**: `artifacts_find_text(id, query, …)` searches text inside a text artifact
  (matches + context + sha256), so a rendered HTML/MD can be checked without download.

## 0.22.0

Sprint 15 — graph integrity + recency/usage discovery.

- **Graph integrity** (MEMP-101): links are now unique per `(from, to, rel)` (idempotent linking, no
  duplicate edges); `notes_link`/assemble validate both endpoints exist; a mutation on a missing note
  errors instead of silently no-op'ing; `notes_unlink` authorizes the target's domain too.
- **`notes_recent`** (MEMP-107): list the most-recently-updated (or most-used) notes in scope, with
  payload — to see what's already in memory and avoid duplicates before writing.
- **Usage signals** (MEMP-116): a `note_usage` table records last-accessed/retrieval-count on explicit
  reads (`notes_get`/`notes_read`), powering `notes_recent sort=used`.

## 0.21.0

- **Evolve built-in types** (MEMP-121): an agent can now author a higher version of a built-in type
  (e.g. `recipe@2`) via `schema_upsert` — the shipped version (`recipe@1`) stays immutable, the new
  version becomes the latest so new writes validate against it, and existing notes keep their version.
  (Previously the whole built-in type was read-only, so a kitchen agent couldn't extend the recipe schema.)
  Note: `type` is the discriminator and the version is a separate field, so the viewer still shows one
  `recipe` type, not two.

## 0.20.0

Sprint 13 — shared commons + searchable payload.

- **`commons` domain** (MEMP-119): a shared domain readable by EVERY agent (even a domain-scoped token),
  writable only by an admin/root token. It holds the core memory rules/skills (`memory-authoring`,
  `agent-memory-use`, `tag-unification`) so a kitchen- or home-scoped agent can still read them. The
  server's onboarding instructions now point at the commons skills.
- **Payload is full-text searchable** (MEMP-120): `notes_search` now indexes `payload` too, so values in a
  note's typed payload (e.g. `russian_name`/`english_name`) are found by text search — not only by the
  exact filter DSL. (title/body/tags/dedup_key remain indexed.)

## 0.19.0

Sprint 12 — per-agent tokens + scope hardening.

- **Multiple bearer tokens, each scoped to domains** (MEMP-032): generate a token for one domain, several,
  or `*` (all), so different agents get different access. Manage from the add-on's shell:
  `tokens add <label> <domains|*>` (prints the raw token once), `tokens list`, `tokens revoke <id>`.
  Only the token hash is stored. The existing `bearer_token` stays the root token — nothing to change for
  current setups.
- **Fail-closed scope** (MEMP-102, part): if a request reaches a handler without a resolved scope, access
  is denied (was: unrestricted) — defense in depth.

## 0.18.0

Sprint 11 — agentic recall (from the A-MEM / memory-frameworks reviews; practical subset).

- **`notes_recall`** (MEMP-112): a prompt-ready context block for a query — top FTS hits (with payload)
  plus their one-hop linked neighbors (both directions), scope-restricted, with relation labels and
  source ids. A case's surrounding context in one call instead of search + many gets. Snippets only,
  no vectors. (`notes_links` now also reports each link's domain.)
- **`notes_related`** (MEMP-113): notes sharing tags with a given note, ranked by overlap — a
  linking/dedup suggestion (linking stays explicit).
- **Agentic memory types** (MEMP-106/114): built-in schemas `preference`, `decision`, `project_state`,
  `fact` (with `as_of`/`confidence`/`valid_from`/`valid_to`/`supersedes` for ADD-only temporal facts),
  and `episode`.

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
