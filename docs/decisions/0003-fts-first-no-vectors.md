# ADR 0003 — Lexical FTS first, no vectors

**Status:** Accepted

## Context

Agents need to *recall* relevant notes from shared memory. The fashionable answer is embeddings +
a vector index (ANN) for semantic search. But this server ships as a **single-file SQLite Home
Assistant add-on** running on modest hardware, the corpus is **small** (hundreds–low thousands of
notes), and embeddings add a model dependency, an extra store to keep in sync, and operational
weight that does not fit a self-contained add-on. We needed retrieval that is good enough now and
cheap to run, without betting the architecture on a vector stack.

## Decision

**Lexical full-text search is the only retrieval backend.** Concretely:

- A contentless **FTS5** index over `title`/`body`/`tags`/`dedup_key`/`payload`, ranked by **BM25**.
- **Recall** (`notes_recall`) = FTS hits **plus one-hop link-graph neighbors**; **`notes_related`**
  uses **shared-tag overlap**. These graph/tag signals stand in for "semantic" association without
  embeddings.
- Non-English (e.g. Russian) is handled by **prefix matching** on the tokenizer, not stemming.
- A **semantic sidecar** (embeddings / entity extraction / hybrid rank) is explicitly **parked**
  (MEMP-118) as a *separate* store to be added only if recall quality demands it — never bloating
  the primary notes DB.

## Consequences

- **+** Single file, no model dependency, deterministic, and fast at this scale — fits the add-on's
  cold-backup / single-artifact deploy.
- **+** Payload values are searchable (FTS indexes the payload), so typed fields are findable too.
- **−** No semantic / synonym matching; a query and a note must share vocabulary. Morphology is
  limited to prefixes.
- **−** Recall quality leans on **consistent tagging and linking**. Mitigated by the
  tag-unification skill, the link graph, recency-decay ranking, and capture-help that discourages
  near-duplicates.

### Alternatives considered

- **Vector DB / embeddings** — best semantic recall, but adds a model + index dependency and a
  second store to sync; disproportionate for a single-file add-on at this corpus size.
- **Hybrid lexical + vector** — the likely long-term answer, but premature now; kept as the parked
  sidecar (MEMP-118) so it can be added without reworking the lexical core.
- **External search service** — loses SQLite's single-file simplicity and the easy add-on deploy.
