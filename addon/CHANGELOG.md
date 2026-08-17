# Changelog

## 0.74.0

Sprint 67 — types can share a definition instead of repeating it, and memory_context stops handing you a
catalogue you did not ask for (MEMP-268, MEMP-257).

- **You can now pin a note or set its importance on any type.** Both are signals the ranker already used, but
  the strict built-in types declared neither, so the only way to pin something was a tag. A single shared
  definition — a "trait" — fixed all twelve types at once, which is the point of letting types compose.
- **Shared definitions.** A concept several types have in common is declared once and referenced, rather than
  restated per type and left to drift. References pin the version they were written against, so a published
  definition cannot change underneath the types already validated against it.
- **A broken reference is caught when the schema is written**, not later when some unrelated note fails to
  save. The error names what could not be resolved and what a valid reference looks like.
- **memory_context now picks the skills your task needs.** It used to return every skill in scope with every
  instruction body empty — so you paid for a list of everything that exists, learned nothing you could act on,
  and still had to make a second call. It now offers a handful of matching candidates, and delivers the
  instructions for the one or two that clearly apply, each saying why it was chosen. A plain lookup gets no
  skills at all.
- **Instructions and remembered facts arrive in separate sections.** An activated skill is something to follow;
  a note from recall is something to know. They used to look alike in the response, and now cannot.

## 0.73.3

Two fixes to schema annotations, both found by annotating the live corpus rather than by reading the code
(MEMP-266, MEMP-263).

- **Fixed: some types could never be annotated again.** Editing a type's retrieval annotations is allowed
  without a new version, because it changes how notes are indexed and not what they may contain. But the check
  compared the two schema documents as TEXT, so a schema stored with an escaped apostrophe read as different
  from the identical schema written with a plain one — and the edit was refused with "bump the version", which
  points nowhere near the cause. Six of eight live schemas accepted their annotations and the two richest
  refused. Schemas are now compared by meaning.
- **Fixed: an annotation edit needed a restart to take effect.** The mapping and the per-type policy were both
  cached against the type's version, which an annotation edit deliberately does not change — so the new
  annotations sat unused until the next restart, defeating the point of allowing the edit at all.
- **The embedding model can now be the smaller build.** `embedding_variant: quantized` fetches a 113 MB model
  instead of 453 MB, and embeds about twice as fast. It is NOT the default: measured against the same corpus it
  answered one golden-set question worse, and the questions it loses are the cross-language ones the feature
  exists for. Use it if the download or the first index build is a real problem on your hardware.
- **Model downloads are verified.** Each file is checked against a known hash before use, so a truncated
  download can no longer load, produce plausible-looking results, and quietly corrupt the whole index.

## 0.73.2

The MQTT failure that 0.73.1 was supposed to make visible was still invisible (MEMP-267).

- **A refused connection is now reported.** 0.73.1 logged connection failures — but only ones that raised an
  error. A broker that REFUSES a connection does not raise anything: it answers with a refusal code, which the
  client returns as a result. So the single most likely failure, a rejected login, went to silence: the broker
  logged "not authorised" and the add-on logged nothing at all.
- **The log now names the broker's own reason**, alongside the username that was sent and whether a password
  was sent with it — which is what distinguishes "the broker refused these credentials" from "no credentials
  ever reached us and we connected anonymously". Those need opposite fixes, and until now looked identical.

## 0.73.1

Follow-up to 0.73.0, from a broker rejecting the add-on in the field (MEMP-267).

- **A rejected connection no longer hammers the broker.** A connection was attempted on every publish, and
  Home Assistant discovery publishes several messages back to back, so a broker refusing the credentials saw
  the same client reconnect eight times per second, indefinitely. A refusal is a standing answer rather than a
  transient one, so a failed connect now waits 30 seconds before the next attempt.
- **The log now says which credentials were sent.** A broker reports "not authorised" both when the wrong
  credentials arrive and when none do, and those need opposite fixes. The add-on now names the username it
  used and states whether a password was sent at all, so the two are distinguishable without guessing.

## 0.73.0

Sprint 66 — a note type's ranking and lint behaviour now comes from its own schema, and MQTT stops failing in
silence (MEMP-253, MEMP-267).

- **Fixed: MQTT could be misconfigured with no way to find out.** Switching MQTT on and getting no device in
  Home Assistant produced **no log line at all, even at trace level** — because when the settings are
  incomplete the whole MQTT stack is skipped, leaving nothing behind that could report it. The add-on now
  always states at startup whether publishing is on and where it is pointed, so the answer is in the log
  before you go looking.
- **The host field now accepts what people actually type.** `mqtt://192.168.0.131` was passed straight to the
  name resolver and failed as an unknown host, with the broker sitting right there at that address. A
  `mqtt://`, `mqtts://`, `tcp://`, `ssl://`, `ws://` or `wss://` prefix is now stripped, a trailing slash is
  ignored, and a `host:port` form is understood.
- **A broker that cannot be reached now says so once.** Connection failures were logged at debug only, so an
  unreachable or wrongly-credentialled broker was invisible at normal log levels. The first failure is now a
  warning naming the likely causes, and a later reconnection is reported too.
- **A type's ranking, ageing and lint behaviour is declared by the type.** Which types count as durable
  knowledge, how fast each kind of note goes stale, and which are exempt from the "no tags" and "not linked"
  checks were four separate lists inside the server — so a note type added by an agent could never get sensible
  treatment without a new release. Each type now declares its own, and behaviour is unchanged: every value was
  carried over from the lists it replaced and is pinned by test.
- **What has not moved yet is now visible.** `memory_capabilities` reports which types still fall back to the
  server's built-in table instead of declaring for themselves — a list that should shrink to nothing.

## 0.72.0

Sprint 65 — a note's TYPE now decides how it is indexed, instead of every string being swept into the vector
regardless of what it means (MEMP-252, MEMP-247, MEMP-258, MEMP-261, MEMP-264, MEMP-265, MEMP-196 closed).

- **Types can declare how they are retrieved.** A schema property can now carry an `x-retrieval` annotation
  saying what part it plays in search: which passage group it is embedded into, its weight in the full-text
  index, and whether it supplies a universal signal such as "when this was observed". `fact`, `decision`,
  `recipe`, `backlog_item` and `episode` ship with these declared. Any type that declares nothing keeps the old
  behaviour exactly, so this rolls out one type at a time rather than as a cutover.
- **Measured before believed, and the honest answer is a tie.** Against 1515 real notes, selecting fields
  scored **9/12 on recall, the same as embedding every string**. Its real wins are an **8% smaller index**
  (6099 vectors down to 5603) and explainability. Reported that way rather than dressed up as a search
  improvement, because that is not what the numbers say. The step that DID earn its place is vectors
  themselves: 8/12 lexical to 9/12, and the query that motivated the whole feature goes from unfindable to
  fifth. Two of twelve queries got worse — vectors reorder losers as well as winners.
- **A search result can now tell you why it matched.** For a semantic hit, `explain` names the passage that
  won and the exact fields it was built from: "matched on `ingredients`, from `payload.ingredients[2].name`".
  Previously the only answer available was a number.
- **Retuning retrieval no longer needs a new type version.** Editing annotations changes how a type is
  indexed, not what it is allowed to contain, so no stored note can become invalid — the schema is compared
  with its annotations stripped, and only a real contract change is refused.
- **Fixed: search reported "no vectors" while running vectors.** Both `status` and `memory_capabilities`
  carried a fixed string claiming the engine was lexical-only, whatever was actually loaded. They now derive it
  from the live model, so they cannot contradict each other or the engine.
- **Fixed: a semantically-found result was reported as zero results.** A note found by meaning shares no word
  with the query, so it was not counted — a page could return an item while reporting `total: 0`, which also
  told anything paging through the results to stop early. The count now includes what was actually returned,
  and says when it is a floor rather than an exact figure.
- **You can see what semantic search costs.** `memory_load` reports the model, how much of your corpus is
  indexed, and how long embedding takes; Home Assistant gets two more sensors for the same. Index coverage
  below 100% means search is answering from part of your notes — worth knowing before trusting a result.
- **Skills are easier to find and to fetch.** `skill_get` now falls back to the shared `commons` domain, so a
  skill you were offered can always be fetched back instead of silently returning nothing, and it tells you
  which scope answered. Skills can also carry tags now, and the shipped ones were rewritten to say when to
  reach for them rather than only what they are.

## 0.71.1

Semantic recall now sets itself up. Switching `embeddings_enabled` on is the entire procedure (MEMP-196).

- **No terminal, no second step.** 0.71.0 shipped the feature with two undocumented prerequisites that could
  only be met from a shell inside the container — fetch the model, then build the index. Turning the option on
  therefore produced a warning and lexical results, which is indistinguishable from a broken feature. The
  server now downloads the model and builds the index itself.
- **Both happen in the background, after the server is already answering.** The download is hundreds of
  megabytes and the first index pass walks the whole corpus; doing either before the port opens would leave the
  add-on looking hung and invite the watchdog to restart it mid-download, forever. Search answers lexically
  throughout, and turns semantic the moment the model lands — no restart needed.
- **Progress is in the log**, batch by batch, so a long first index is visibly working rather than silent.
- **A failed download is not a failure to start.** No network, no disk, a blocked host: each is logged and the
  server keeps serving lexical search. The next start tries again.
- `fetch-model` is still there for a box with no outbound access at the moment the feature is enabled, but it
  is no longer part of the normal path.

## 0.71.0

Sprint 64 — semantic recall arrives, opt-in and off by default, behind a seam that stops the next retrieval
change from being hardcoded too (MEMP-251, MEMP-196, MEMP-241, MEMP-243, MEMP-244, MEMP-245, MEMP-247).

**Nothing below changes anything unless you turn it on.** With `embeddings_enabled` off — the default — no model
is loaded, no vectors are written, and search behaves exactly as it did in 0.70.0.

- **Search can now find a note that shares no words with your query (MEMP-196).** A Russian note whose title says "tryohfaznyy vvod" (three-phase supply)
  is returned first for the English query "three phase electricity monitoring device" — a
  query that previously returned it nowhere at all. That gap is the whole reason for the feature: measured
  against a twelve-query golden set, **every single query's strict lexical pass returned zero**, so what looked
  like 8/12 recall was really whichever token happened to be shared, usually a loanword or a dedup slug.
- **The model was chosen by measurement, not preference.** `multilingual-e5-small` beat the cheaper static
  alternative on identical data (6/12 against 5/12, and position 1 against 32 on the query that started this
  work). Its 20x speed disadvantage is not a reason to prefer the other one until telemetry says the hardware
  minds.
- **Notes are indexed as passages, not as one vector each.** A single mean-pooled vector per note measured
  WORSE than indexing only the title — the topic drifts to a generic centre and short queries stop landing near
  it. Chunked and scored by best passage, a one-word-titled note went from rank 143 to 12, and mean rank across
  the golden set improved from 23.0 to 17.2.
- **Semantic hits enter the results, they do not merely re-order them.** Re-ranking what the keyword search
  already found can never answer a query the keyword search returns nothing for. Candidates are fetched through
  the same filters as everything else, so a semantic hit cannot escape a domain, scope, type or status
  restriction.
- **When the keyword pass finds no real match, meaning leads.** The engine already knew when it had fallen back
  to matching any single word; now that admission is used, instead of letting one incidental match outrank a
  note that is genuinely about the question.
- **Setup is two commands.** `fetch-model` downloads the model into `/share` (kept out of the image, which
  carries only the ~25 MB runtime), and `index-embeddings` builds the index once for notes that already exist.
  New notes index themselves as they are written — after the transaction commits, so writes never queue behind
  the model. `memory_capabilities` reports the live model, how many notes are indexed, and how many passages
  are stale after a model change.
- **A retrieval seam, so the next change is a declaration rather than a rewrite (MEMP-251).** Every indexer now
  obtains a note's text from one projector, which reports the JSON path each piece came from. That provenance
  is what makes selective reindex and "which field produced this hit" possible; the old code walked the payload
  in several places and threw the paths away. Landed behaviour-neutral — all 424 pre-existing tests passed
  unchanged, which is the proof.

Also in this release:

- **Superseded notes no longer come back as recall neighbours (MEMP-241).** They correctly left the hits in
  0.70.0, but superseding CREATES a link from the replacement to the replaced note, so the one-hop expansion
  dragged every retired note back in through the very link that retired it.
- **`notes_patch` accepts what `notes_upsert` accepts (MEMP-245).** A note of a strict type carrying a project
  in its payload — most of the corpus — could not be patched at all, for any field.
- **A competing note is flagged on update, not only on create (MEMP-244)**, and **`expired_content` lint**
  reports notes whose stated validity has passed, so ageing can be reviewed in bulk (MEMP-243).
- **`memory_load` reports what the server costs to run (MEMP-247)** — per-operation p50/p95, memory and CPU —
  and publishes it as Home Assistant sensors, so "is this too heavy for the box" is a reading rather than an
  argument. It landed before the vector work on purpose.

**Schema change: migration 0018** adds `note_passages` (empty and unused unless embeddings are enabled). 444 tests.

## 0.70.0

Sprint 63 — Notes that know when they have aged, and a ranking fix that a measurement chose rather than a guess
(MEMP-239, MEMP-240, MEMP-236, MEMP-235). The sprint's fifth item, semantic/vector recall (MEMP-196), delivered
its decision and its measured baseline but ships no code yet — see the notes at the end.

- **A note titled after your query no longer loses to one that mentions it once (MEMP-239).** BM25 normalises
  every term by the row's TOTAL length across all indexed columns, so a note with an empty body and a large
  structured payload is penalised as "long" even though the match is in its short title: on the reported query
  the recipe named after the search term sat at lexical rank 17 of 44. The follow-up proposed two knobs; both
  were measured and rejected. Raising the BM25 title weight only reverses the pair at about x20 rather than the
  proposed x12, and by then a note that merely CONTAINS the word in its title has climbed most of the way to a
  note genuinely about it. Raising the fusion weight moves everything equally and reorders almost nothing.
  The real cause was underneath both: the title takes only a handful of distinct values, so its competition
  ranks bunch (1, 2 and 4 out of 44 hits) and `w/(k+rank)` left the whole signal a 0.006 spread against
  relevance's 0.032 — no weight makes a three-value rank compete with a two-hundred-value one. **The title is
  now scored from its match quality instead of ranked**, and scaled to the pool's own relevance spread so a
  full title match stays proportionate — decisive near the top, unable to overturn a large margin in
  relevance. Weights are unchanged. `rank=lexical` deliberately stays pure BM25.
- **Search and recall hits can now say that a note has aged (MEMP-240).** A note that declares `valid_to`
  (alias `valid_until`), `stale_after_days` or `as_of` gets a `staleness` hint on every hit — `expired` or
  `past_window`, with the age. It is advisory: nothing is filtered or reordered. The hint is computed on the
  row, so it survives the lean recall projection that drops the payload it comes from. A note's own date beats
  the envelope timestamp, because retagging a note is not re-verifying it. Facts with only an `as_of` age
  against an opt-in horizon (`MEMORY_STALENESS_FACT_DAYS`, off by default) — a hint that fires on most of the
  corpus is one an agent learns to ignore.
- **Writing a note that restates an existing one now says so (MEMP-240).** The post-write related-notes hint
  marks an existing note of the same type and project with a near-identical title as a `supersede_candidate`
  and points at `notes_supersede`, instead of letting two parallel truths sit side by side with neither marked
  as replaced. Notes merely about the same topic are not flagged.
- **Superseding is now verified to do what the advice above assumes**: a superseded note measurably leaves
  search and recall (only `active` rows are queried), while staying reachable through its `supersedes` link
  and an explicit `status: "superseded"` search. This held already; nothing asserted it until now.
- **`memory_capabilities` tells an unrestricted token which domains exist (MEMP-236).** It returned two empty
  arrays, which read as "you may reach nothing" but meant "you may reach everything" — indistinguishable to
  the caller, and it left `domains_list` as the only way to enumerate domains. The lists are now populated;
  `scope.unrestricted` says whether to read them as a limit or as an inventory.
- **`tags_list` is marked deprecated (MEMP-235)** in favour of `notes_tags`, which it has been an alias of
  since 0.68.0. Removal waits for a later release, once no known client calls it by name.
- `memory_capabilities.contractVersion` -> **3**: an agent that special-cased the old empty-means-all domain
  lists, or that wants to use the staleness hint, has to be able to detect the change.

No schema change (migrations through **0017**). 412 tests.

**On semantic recall (MEMP-196), which did not ship code:** a 12-query golden set of paraphrase and
cross-language queries was built from the real corpus and measured against 0.69.0. The headline is 8 of 12
found in the top 8 — but **all twelve fell through to the any-term fallback**, meaning not one was a real
lexical match, and the successes trace to incidental Latin tokens (a brand name, a dedup slug) rather than
meaning. "three phase electricity monitoring device" does not retrieve the note about exactly that, because
the note is written in Russian. The decision to add an opt-in embedding layer beside FTS5 is recorded; which
embedder is deliberately left to be scored against the golden set rather than guessed, since guessing the
analogous number is what MEMP-239 just had to undo.

## 0.69.0

Sprint 62 — Search relevance for the owner's own notes (MEMP-237, MEMP-238). Both were found in the field on
2026-08-10 by the owner searching their kitchen notes for the Russian word for "chili": the notes that carry
that word in their TITLE ranked below notes that merely mention it, and the one filter that could have worked
around it (`title contains '...'`) silently returned nothing in lower case.

- **A note's title now carries weight in ranking (MEMP-237).** `bm25()` was called with no column weights, so
  a title counted for exactly as much as a passing mention buried in a long body — the defect was in BM25
  itself, before any fusion. The title is now weighted x5. On top of that, a **partial** title match became its
  own ranking signal (how much of the query the title covers, plus a bonus when the title opens with it);
  before, only an exact whole-title match got a boost, so "Chili in a kazan with minced beef" got nothing for
  the query "chili".
- **The hybrid blend no longer lets recency and note type outvote relevance.** All six signals were weighted
  equally with an RRF damping constant of 60, which flattened everything: across a real query the top twelve
  fused scores spanned 0.005 in total, and a hit ranked **first** on relevance landed second while one ranked
  **sixteenth** took the top slot on recency and type. The two text signals (BM25 3, title 2) now outweigh the
  four contextual ones, and k drops 60 -> 20 so ranks actually separate. Contextual signals still order hits
  that relevance leaves tied, and still overturn a close call when several of them agree.
- `explain=true` reports the new `titleRank` alongside the other per-signal ranks.
- **The filter DSL's `contains` folds case for non-ASCII text (MEMP-238).** It compiled to SQLite `LIKE`, whose
  folding covers ASCII A-Z only, so it behaved correctly for English keys and silently failed for Russian
  notes: `title contains` found two notes in title case and **zero** in lower case, while the tool description
  promised a case-insensitive substring. `contains` now runs on a .NET-backed `mem_contains` covering the whole
  Unicode range, and takes the needle literally (no LIKE wildcards to escape).
- **The same ASCII-only fold was fixed everywhere else it hid a match**, not just in `contains`: the exact-title
  ranking tier, the duplicate probe behind `notes_suggest_capture`, and the `duplicate` lint all compared
  `lower(title)` and so treated two identical Russian titles in different cases as different notes. The one
  remaining `COLLATE NOCASE` is on `dedup_key`, which is reached only for the ASCII ticket-key shape.

No schema change (migrations through **0017**) and no index rebuild — the BM25 weights are per query. 401 tests.

## 0.68.0

Sprint 61 — Scope-aware discovery (MEMP-232, MEMP-233, MEMP-234). Found in the field: a domain-scoped agent
listed tags, reported "74 notes on feature:mining-rush", then retrieved none of them and said so. The agent was
right — the discovery tools ignored the caller's scope while search honored it, so they advertised a corpus the
token could not read.

- **`tags_list`, `domains_list` and `status` now honor the caller's scope.** `tags_list` was calling the tag
  facets with both the domain filter AND the auth restriction hardcoded to null; it is now an alias of
  `notes_tags` and takes an optional `domain`. `domains_list` and the `status` note counts (`noteCount`,
  `notesByType`, `notesByDomain`, `notesByStatus`) are filtered to the domains the token may read; storage and
  operations figures (attachments, blob bytes, database size, pending confirmations) stay server-wide. The
  unscoped `TagCounts()` primitive is deleted, so the unsafe path cannot be called again.
- **The web viewer's `GET /api/stats` had the same hole** and is now scope-filtered like the sibling
  `/api/tags` already was.
- **A guard test covers the whole read surface, not the three tools that were broken.** It starts the server
  with a single-domain token, enumerates every tool advertised with `readOnlyHint`, calls each one, and fails if
  any response carries content from an out-of-scope domain. A tool added later is covered as soon as it appears
  in `tools/list`.
- **`notes_recall` and `memory_context` take a `tags` filter** (`query` is now optional, so a tag-only recall
  works). Previously only `notes_search` accepted tags, so a discovered facet could only be pasted into the
  query text — where the tokenizer splits `feature:mining-rush` into three words and matches them against prose
  instead of the facet.
- `memory_capabilities.contractVersion` -> **2**: an agent has to be able to detect that tag-recall exists.

No schema change (migrations through **0017**). 392 tests.

## 0.67.0

Sprint 60 — Dependency-drift lint reframed (MEMP-231), after the MEMP-228 corpus sweep found that
binance-maf-trader and home-dashboard record task dependencies as **graph links** (`depends_on`/`blocked_by`
rels), while memory-mcp uses **`payload.blocked_by`** — both internally consistent, neither wrong.

- **Owner decision: both dependency forms are valid.** `payload.blocked_by` (a key array; DSL-filterable) and
  graph links (traversable via `notes_graph`) are each fine on their own. The real drift is encoding the SAME
  dependency in BOTH forms on one note (two sources of truth).
- **`dependency_representation_drift` reframed**: instead of flagging any `depends_on` graph link, it now flags
  a backlog item that has a non-empty `payload.blocked_by` AND ALSO a graph dep link (`depends_on`/`blocked_by`)
  — genuine dual-encoding. A graph-only or payload-only note is not flagged.
- The dependency decision note, the `memory-authoring` skill (v3), and the `notes_lint` tool description are
  updated to "pick one form, don't dual-encode."

No schema change (migrations through **0017**). 385 tests. (Docs/decision/skill changes ship in shared memory,
already live.)

## 0.66.0

Sprint 59 — Rule relevance (MEMP-224). `memory_context` now honors the `always_apply`-vs-on-demand rule
semantics the schema always described (`always_apply` = "baseline context; otherwise loaded on demand") but the
code ignored — it had been loading *every* rule.

- A **domain-general (project-`null`) non-`always_apply` rule** that declares `trigger_phrases`/`topic_globs` is
  now surfaced **only when the task query matches a trigger** — so a situational/environment rule (e.g. the npm
  registry gotcha) no longer blankets unrelated projects. **Project-scoped rules always load in their project**,
  `always_apply` rules stay baseline, and a trigger-less rule can't be gated (kept). Verified blast radius across
  the live corpus = exactly one rule (the npm rule); all 67 project-scoped rules are unaffected.
- Reclassified the npm rule to `always_apply=false` (it has triggers → on-demand). The `create-memory-rule`
  commons skill (v2) documents the enforcement.

No schema change (migrations through **0017**). 385 tests.

## 0.65.0

Sprint 58 — Viewer improvements from owner field use.

- **Inbox → detail panel (MEMP-230)**: the Inbox review queue (open evolution suggestions + lint findings) now
  renders into the RIGHT detail panel, like Adoption/Activity — instead of the left results list, where it was
  easy to miss and looked like "nothing happened." (Verified earlier: the button and endpoints always worked;
  this is purely placement.)
- **Adoption shows each agent's project(s) (MEMP-229)**: `notes_adoption` now returns, per agent, the
  workspace(s) it wrote to — the envelope `project`, or the note's domain when it has none — heaviest first
  (top 3). The viewer's Adoption panel shows a new "project(s)" column, so it's obvious WHAT an agent worked on,
  not just how much. (The near-duplicate agent labels themselves — e.g. `claude-code` vs `Claude Code` — are
  a caller-side identity issue; the project column disambiguates what each did.)

No schema change (migrations through **0017**). 383 tests.

## 0.64.1

Hotfix — restore the add-on image build (broken since 0.60.0). The server csproj embeds the onboarding kit
(hooks + templates) from `integrations/` as assembly resources (MEMP-211, 0.60.0), but `addon/Dockerfile`
only copied `src/`, so the in-container `dotnet publish` failed with `CS1566: Error reading resource
'onboard.memory_session_start.py'` and **no image was published for 0.60.0–0.64.0** (CI stayed green because
tests build with the full repo checkout). Fix: `COPY integrations ./integrations` into the Docker build. This
is the first successfully published image since 0.59.0 and contains all of 0.60.0–0.64.1.

## 0.64.0

Sprint 57 — Provenance & polish (closing the TRD-131-134 authoring review).

- **Structured schema_get (MEMP-222)**: `schema_get` now returns structured output — `{type, version, found,
  schema}` (the JSON Schema as a string) with an explicit `found` flag — instead of a bare JSON-string-or-null,
  so strict MCP clients get a typed result and an unambiguous miss. (Behavior change: the schema text is now
  under `schema`.)
- **Authoring docs reconciliation (MEMP-227)** — shipped in shared memory, not this image: the commons
  `memory-authoring` skill (v2) corrects "one project = one domain" to the real multi-project model (a domain
  hosts many projects via the `project` sub-axis; operator manual is canonical), and adds the envelope-lifecycle
  vs payload-workflow `status` distinction, exact-key lookup, `notes_assemble_many`, and lean-recall guidance.

Deferred: MEMP-226 (session/token source-agent identity — near-no-op on the env root token prod uses), MEMP-224
(rule relevance — needs an always_apply-semantics decision). No schema change (migrations through **0017**). 382 tests.

## 0.63.0

Sprint 56 — Composable authoring finish + retrieval knobs. The clean, additive remainder of the TRD-131-134
authoring review: fewer round-trips to author and to fetch, and finer control over what a recall returns.

- **notes_get_many_by_key (MEMP-219)**: resolve many notes by their stable (domain, type, dedupKey) in ONE
  call, each with an EXPLICIT `found` flag (a miss is unambiguous, never a bare null) — instead of many
  notes_get_by_key round-trips.
- **next_key (MEMP-220)**: allocate the next unused ticket key for a project — `next_key('memory-mcp','MEMP')`
  returns `MEMP-229` when 228 is the current max (one past the highest numeric suffix, >=3 digits). Read-only
  peek, not a hard reservation.
- **memory_context / notes_recall knobs (MEMP-223)**: new optional controls — `types` (restrict recall hits to
  given types), `includeRules` / `includeSkills` (drop a section you don't need), `noRelax` (forbid the
  AND->any-term auto-widening so a precise query stays precise), and `maxNeighbors` exposed on the tools.
  Builds on 0.61.0's lean recall.
- **Exact ticket-key search (MEMP-225)**: a query that IS a ticket key (`TRD-131`, `MEMP-215`) is now treated
  as an exact dedupKey lookup and returns just that note, instead of the hyphenated key scattering into
  TRD-or-131 matches. Documented on the search tool.

No schema change (migrations through **0017**). 382 tests.

## 0.62.0

Sprint 55 — Authoring workflow: semantic integrity + composable API. Acts on the TRD-131-134 authoring-workflow
review: the schema and write-safety primitives are strong, but the JSON schema can't catch contradictions, and
authoring a set of linked tasks took ~10 operations. (The review's context-efficiency half already shipped in 0.61.0.)

- **Semantic lint (MEMP-215)**: `notes_lint` gains cross-field rules for backlog items the schema can't express —
  `inconsistent_workflow_state` (status ready/next/in_progress while blocked by an OPEN dependency),
  `unresolved_dependency` (a `blocked_by` key resolving to no active item), and `satisfied_dependency` (status
  blocked but every dependency done). Dependency keys resolve across the caller's whole readable scope.
- **Canonical dependencies (MEMP-216)**: decided — `payload.blocked_by` is the ONE source of truth for task
  dependencies; the graph `depends_on` relation is not used for them. New lint `dependency_representation_drift`
  flags a backlog item that still carries a `depends_on` graph link. (Decision recorded in memory.)
- **lifecycle vs workflow status (MEMP-217)**: the envelope `status` (active/archived = lifecycle) is easy to
  confuse with a typed note's `payload.status` (e.g. backlog ready/blocked/done). Added a filter-DSL alias
  `lifecycleStatus` for the envelope field and spelled the distinction out in the search filter description.
- **notes_lint scoping (MEMP-221)**: lint can now be narrowed by `project`, `types`, `noteIds` and `dedupKeys`
  (not just domain) — so an agent can lint exactly the notes it just wrote.
- **notes_assemble_many (MEMP-218)**: author many notes AND the links among them in ONE all-or-nothing
  transaction — closing the gap where `notes_upsert_many` had projects but no links and `notes_assemble` had
  links but no project. Link endpoints are addressed by a batch item's dedupKey (so new notes link to each
  other immediately) or by an existing note id. Max 100 items / 100 links.

No schema change (migrations through **0017**). 375 tests. Follow-up MEMP-228 (corpus hygiene sweep) migrates
existing notes to these conventions once the cluster ships.

## 0.61.0

Sprint 54 — Context economy. A plain `memory_context` was returning ~8-10K tokens (every recall hit carried
its full payload — for a `backlog_item` the `acceptance` spec is multi-KB — plus uncapped neighbors and full
rule payloads), which floods the agent's window. This makes recall lean by default (MEMP-214).

- **Lean recall hits**: `memory_context` and `notes_recall` hits now carry snippet + identity
  (id/title/type/domain/dedupKey/project/status) but NOT the full payload/tags JSON — the snippet already
  conveys relevance, and the full payload is one `notes_get` away. Pass `includePayload=true` for a board/status
  view; `notes_search` (the board tool) is unchanged.
- **Capped neighbors**: recall returns at most ~15 linked neighbors (was every link of every hit — a hub note
  could dump 60+), keeping the neighbors of the top-ranked hits.
- **Default recall budget**: `memory_context` self-limits its recall to a ~6000-char snippet budget when the
  caller gives none; widen with `budgetChars`.
- **Lean rules**: the rules in the context block keep the decision-relevant fields (description, priority,
  always_apply, scope) and drop verbose arrays (trigger_phrases, source_refs) and tags. Staleness is still
  computed from the full rule before trimming.

Net: the same `memory_context(development, project=…)` call drops from ~10K tokens to ~2-3K without losing
"what's relevant". Behavior change (opt-out via `includePayload`): recall hits no longer include payload by
default. No schema change (migrations through **0017**). 367 tests.

## 0.60.0

Sprint 53 — Adoption / onboarding kit. Make it effortless for a new project (and a new agent) to
use shared memory well: capability prompts, drop-in hooks, and project templates.

- **MCP capability prompts (MEMP-211)**: the server now exports three prompts, so a prompt-aware
  harness gets memory adoption as a slash command. `start-task` (args: `task`, optional `domain`,
  optional `project`) assembles the rules in force, the workspace's skills, and the notes relevant
  to your task into one ready-to-use message — omit `domain` for a cross-domain overview. `end-task`
  (optional `domain`/`project`) returns a consolidation checklist (save durable facts, update project
  state, link new notes, refine skills, flag stale notes) plus the workspace's skills. `onboard-project`
  (optional `domain`/`project`) scaffolds a fresh project onto Memory MCP in one shot: it returns the
  exact files to create — `AGENTS.md`/`CLAUDE.md`, the two hooks, the `settings.json` block, and
  `.claude/memory.json` — with your domain/project filled in, so the target project needs no repo access. The kit is
  sourced from live `commons` memory (a `reference` note per file, `dedupKey=onboard-kit-*`) so the owner can edit the
  templates with `notes_patch` without a release; it falls back to the copy embedded in the image when a note is
  missing (`integrations/seed-onboard-kit.py` seeds/refreshes commons from the repo). In Claude Code:
  `/mcp__memory__start-task`, `/mcp__memory__end-task`, `/mcp__memory__onboard-project`.
- **Claude Code hooks kit (MEMP-208)**: `integrations/claude-code/` ships drop-in `SessionStart` and
  `Stop` hooks. SessionStart injects `memory_context` (rules + skills + relevant notes) at the top of
  every session; Stop nudges you to consolidate if the session never touched memory. Generalized and
  configurable (env var > `.claude/memory.json` > default; domain/project/query/url/token); both fail
  open. Omitting `domain` leans on cross-domain default recall (MEMP-213).
- **Project templates**: `integrations/templates/AGENTS.md` and `CLAUDE.md` — copy-paste starting
  points that tell an agent HOW to use memory (recall-first, save durable facts as you go, prefer
  patch, link notes, use `memory_evolution_suggestion` to fix others' notes, never store secrets,
  consolidate at the end). Placeholders for domain/project.

These are additive: prompts are new server capability; the kit/templates live in `integrations/`
(repo only, not in the add-on image). No schema change — migrations through **0017**. 362 tests.

## 0.59.0

Sprint 52 — Ergonomics: recover from the two most common domain/project mistakes so a caller gets useful
context instead of an empty result and a hallucinated fallback.

- **Project-name-as-domain auto-resolve (MEMP-212)**: passing a project name where a domain is expected
  (e.g. `memory_context(domain="unity-solitaire")`) no longer returns nothing. When the value is not a real
  domain but matches a known project, `memory_context` and `domain_manifest` resolve it to the real domain +
  project (the domain holding the most of that project's notes) and return a corrective warning with the exact
  fixed call — instead of an empty block that sends the agent off to `domains_list`. Real domain calls are
  untouched; scope is respected (a resolved domain is only offered if the caller may read it). `domain_manifest`
  gains a `warnings` field.
- **Cross-domain default when `domain` is omitted (MEMP-213)**: `memory_context` now takes an OPTIONAL domain —
  omit it to get a cross-domain overview across every domain you're authorized for (commons rules/skills plus a
  domain-diverse recall), rather than being forced to guess one domain. `notes_search` / `notes_recall` already
  searched all authorized domains when `domain` was omitted; that is now stated plainly in their descriptions.
  The overview recall round-robins hits across domains so one large domain (development, 900+ notes) doesn't
  drown the smaller ones. `project=` still boosts/filters across domains. The domain security boundary is hard:
  "all domains" always means "all domains THIS caller is authorized for" — a scoped caller never sees another
  domain. Unblocks agents whose harness hardcoded a single domain.

Migrations unchanged (through **0017**). 362 tests.

## 0.58.0

Sprint 51 — Hygiene, bulk maintenance & measurability. Smarter lint, bulk edits, and per-agent adoption metrics.

- **Type-aware lint (MEMP-202)**: the `no_tags` rule now skips types found by key/list rather than by tag
  (sprint, skill, saved_search, memory_evolution_suggestion), so a full-corpus `notes_lint` drops from ~100+
  findings dominated by noise to the handful of real problems. The tool description documents the profiles.
- **orphan_note lint (MEMP-200)**: a new connectivity rule flags an eligible knowledge note with no links in
  or out, untouched for 30+ days — a nudge to connect it into the graph. Ephemeral/standalone types (journal,
  episode, sprint, skill, saved_search, memory_evolution_suggestion, memory_rule, preference) are exempt.
- **Bulk patch / retag (MEMP-203)**: new `notes_patch_many` applies an array of partial updates
  ({id, title?, body?, payload?, tags?, expectedUpdatedUtc?}) in ONE all-or-nothing transaction — payload
  shallow-merges, tags replace — returning a compact {id, updatedUtc} per item. Retag 50 notes in one call
  instead of 50 round-trips. Max 100 items.
- **Adoption metrics (MEMP-207)**: new `notes_adoption` tool + viewer "Adoption" panel report per-agent reads
  vs writes (create/update/patch), flagging agents that write without reading first. Writes come from the
  change log (`note_events.actor`); reads are counted (new `agent_reads` table, migration 0017) only when a
  caller passes `sourceAgent` on recall/search.

Also: the `source-ingest` convention (a skill + a "source vs reference" decision, MEMP-199) is authored
directly in the shared memory, not in this image. Migrations through **0017**. 349 tests.

## 0.57.0

Sprint 50 — Adoption: make agents use memory well. Five items that push agents toward recalling before
writing, linking new notes, keeping project state fresh, and staying in their project's context.

- **Project-aware recall (MEMP-209)**: `memory_context` and `notes_recall` take a `project` and lift that
  project's notes via a soft ranking signal — a note in the asked-for project edges out an equally-relevant
  note from another project, but cross-project hits still appear. Pass `projectOnly` to hard-restrict the
  recall to one project. Previously `project` affected only which skills/rules were loaded, not the recall.
  The score breakdown (`explain`) gains a `projectRank`.
- **Staleness hint (MEMP-206)**: `memory_context` now warns when the domain/project has a `project_state`
  older than a default window (14 days, measured from its `updated` field), or any note past its own
  `payload.stale_after_days` — with the note id and age, nudging you to refresh it at the end of the task.
- **Post-write related hint (MEMP-205)**: creating a note with `notes_upsert` / `notes_assemble` now returns
  up to three `related` notes (by shared tags / text / links) as a linking suggestion — so a new note gets
  connected into the graph instead of sitting orphaned. Advisory; skipped on updates and idempotent no-ops.
- **Recall-before-write nudge (MEMP-204)**: when an agent that identifies itself (a stable `sourceAgent`)
  writes without having recalled/searched first this session, the write response carries an advisory `nudge`
  to recall first. Pass `sourceAgent` on `memory_context` / `notes_recall` / `notes_search` so the server can
  see you recalled. Non-blocking; the process-local tracking resets on restart.
- **Tool-description imperatives (MEMP-210)**: the always-visible tool descriptions now say it outright —
  `memory_context` is "CALL THIS FIRST", `notes_upsert` / `notes_assemble` point at `notes_suggest_capture`
  before creating, and `notes_patch` says to prefer it over upsert for edits.

Both write-time hints (nudge + related) can be turned off with the new `adoption_hints` add-on option
(env `MEMORY_ADOPTION_HINTS`), on by default. No schema migration (through 0016).

## 0.56.2

Hotfix (MEMP-198) — types whose schema declares `project` (e.g. `project_state`) can be written again.

The envelope-axis lift (0.47.0) stripped a top-level `project` from every payload before schema validation, so a
type that legitimately defines `project` — `project_state` requires it — failed validation as "missing required
property project" on both `notes_upsert` and `notes_upsert_many`. The lift is now schema-aware: it only removes
`project` for types whose schema does not declare it (so `backlog_item` still carries `payload.project` to set the
envelope), and keeps it for types that own the field.

## 0.56.1

Hotfix (MEMP-197) — `notes_patch` / `notes_assemble` now return the note's envelope `project`.

The patch/assemble responses were omitting the `project` field (it defaulted to null), which looked like the
write had dropped the note out of its project scope. It had not — the patch `UPDATE` never touched the `project`
column, so the stored value was always preserved. This only fixes the response: `notes_patch` returns the
preserved project, and `notes_assemble` echoes the effective project (including the value preserved on a
dedup-update whose payload omits `project`).

## 0.56.0

Sprint 49 — RU morphology (MEMP-192), finishing the lexical side of the search report MEMP-189.

- **Fleeting-vowel matching**: a Russian dictionary form now matches its own inflections even when Snowball
  leaves them on different stems — the fleeting vowel (perec/perca/percev: the nominative keeps an -ets that
  oblique forms drop to -ts). The stemmer folds yo-&gt;ye and collapses a trailing -ets to -ts, so all forms share one
  key. Non-fleeting words (hleb, mesyac) are untouched. Migration 0016 reindexes existing notes' stems.

Remaining from MEMP-189: semantic/vector search (MEMP-196) — deferred.

## 0.55.0

Sprint 48 — search & recall overhaul (MEMP-190–195; addresses the external-consumer report MEMP-189).

Natural-language recall used to return 0 (AND-only, function-word-sensitive) and LLMs hallucinated. Fixed:

- **Any-term + auto fallback** (MEMP-190): `notes_search` gains `match=all|any|auto` (default **auto**) — it tries
  AND, then automatically widens to ranked any-term partial matches when AND finds nothing (`relaxed: true` on the
  page). A natural-language question now returns ranked partials instead of 0.
- **Stop-word stripping** (MEMP-191): common RU+EN function words and punctuation are dropped, so
  "how many peppers do I have?" effectively searches "peppers".
- **Hybrid ranking by default** (MEMP-193): results are ordered by relevance + recency + link-degree +
  importance/pinned + a per-type weight (canonical types above ephemeral) — *most important on top*. `rank=lexical`
  for pure BM25.
- **OR operator + documented DSL** (MEMP-194): `OR` / `|` force any-term; the full query syntax is documented on
  the tool and in the new commons skill `memory-search-syntax`.
- **Recall path** (MEMP-195): `notes_recall` / `memory_context` inherit the relaxed + stop-word + hybrid pipeline.

Still planned: RU fleeting-vowel morphology (MEMP-192, e.g. perec/percev) and semantic/vector search (MEMP-196).

## 0.54.0

Sprint 47 — reactivity & views (MEMP-184–188).

- **HTTP webhook** (MEMP-184): an opt-in webhook POSTs each note-change event (the same body-free facets MQTT
  sends) as JSON to `webhook_url`; when `webhook_secret` is set, an `X-Memory-Signature: sha256=…` header lets the
  receiver verify it. MQTT and the webhook can run together. Best-effort — a slow endpoint never blocks a write.
- **Saved searches** (MEMP-185): a new `saved_search` note type stores a named query; `notes_saved_search_run`
  runs it (query/domain/type/tags/filter/sort/rank/limit). List them with `notes_search type=saved_search`.
- **Activity stats** (MEMP-186): `notes_activity` summarizes recent write activity over the last N days — total
  plus counts by operation and by type, scope-filtered.
- **Viewer** (MEMP-187): a Saved Searches list (click to run) and an Activity overview panel in `/ui`.
- **Webhook test** (MEMP-188): a root-only admin action (and a viewer button) sends a synthetic event to the
  webhook and reports the delivered status, so you can verify the config; deliveries are logged.

## 0.53.0

Sprint 46 — concurrency safety (MEMP-179–183).

- **Optimistic concurrency on `notes_upsert`** (MEMP-179): pass `expectedRevision` (the `updated_utc` from a prior
  get/upsert) and the write is rejected if another writer changed the note meanwhile (or it's gone) — no silent
  clobber. Omit it for the previous blind-upsert behavior.
- **Bulk upsert** (MEMP-180): new `notes_upsert_many` upserts an array of notes in ONE transaction, all-or-nothing —
  every payload is validated first, a bad item names its index and rolls the whole batch back. Returns a per-item
  result. Up to 100 items.
- **Bulk link** (MEMP-181): new `notes_link_many` creates many links in one transaction, all-or-nothing and
  idempotent; returns `created` vs `alreadyPresent`. Up to 100 links.
- **Richer conflict info** (MEMP-182): a concurrency conflict (upsert or patch) now reports the current revision,
  the last writer, and which fields your write would change — so you can re-read and reconcile, not blind-retry.
- **Idempotent no-op** (MEMP-183): re-upserting identical content is a quiet no-op — `unchanged=true`, no revision
  bump, no change event/MQTT publish. Makes retries and concurrent identical writes safe and noise-free.

## 0.52.0

Sprint 45 — recall quality (MEMP-174–178).

- **Hybrid recall ranking** (MEMP-174): `notes_recall` and `memory_context` now rank by Reciprocal Rank Fusion
  over relevance (BM25), recency, and link-degree — surfacing notes that are useful, not just lexically closest.
  `notes_search` gains `rank=hybrid` (default stays `lexical`, pure BM25); an exact key/title match still wins.
- **Importance / pin boost** (MEMP-175): a `pinned` tag (or `payload.pinned`) and an `importance:N` tag (or
  `payload.importance`) lift a note in the hybrid blend. Default-neutral — notes without it rank exactly as before.
- **Budgeted context packing** (MEMP-176): `notes_recall` and `memory_context` accept `budgetChars` to pack the
  highest-ranked hits that fit a character budget (≈ tokens × 4) instead of a fixed count; they report
  `usedChars`/`droppedCount` and trim the last snippet to fit.
- **Explain ranking** (MEMP-177): pass `explain=true` to a hybrid `notes_search`/`notes_recall` to get a per-hit
  score breakdown (lexical/recency/link/importance ranks + the fused score). Off by default.
- **Blended `notes_related`** (MEMP-178): related suggestions now combine shared tags, lexical similarity, and
  direct links (with `reasons`), so even an untagged note gets useful "more like this" candidates.

## 0.51.0

Sprint 44 — search & viewer polish (MEMP-169–173).

- **Exclude terms from search** (MEMP-169): a `-term` in the query removes matches, e.g. `anr -mintegral`.
- **Tag facets in `domain_manifest`** (MEMP-170): the manifest now includes the domain's most-used tags with counts.
- **Tag-facet sidebar in the viewer** (MEMP-171): the result list shows the domain's top tags (with counts); click
  one to filter. Backed by a new `GET /api/tags`.
- **Export/Import in the admin panel** (MEMP-172): one-click NDJSON Export (download) and Import (paste →
  dry-run → Apply) over the existing admin endpoints.
- **Copy buttons** (MEMP-173): the note detail offers copy-to-clipboard for the note id, dedupKey, and a permalink.

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
