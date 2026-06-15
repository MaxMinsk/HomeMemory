# AGENTS.md — Memory MCP

Instructions for agents (and humans) working in this repository. Short version: what the project is, how we work, what not to do.

## What this is

**Memory MCP** is a thin, reliable MCP server for personal memory, notes and skills, shared by multiple agents (a Home Assistant agent, Claude Code, a work agent, a future web UI) over the MCP protocol. It **stores, validates, searches and returns** notes, and it **hosts the schemas and skills** that define how each note type is written — so every agent maintains a given type (recipes, backlog items, …) the same way. The server never runs an LLM itself: the reasoning (what to write, how to phrase it) happens in the agents, guided by the schemas and skills they fetch from the server.

Internal planning docs (design overview, master plan, M0 detail, backlog) live in `implementation_plan/`, and working notes/references in `Notes~/`. **Both directories are gitignored and written in Russian** — they are not part of the public repo.

## Language policy (the repo is public)

- **Everything tracked by git is in English**: identifiers, XML docs, `README.md`, `AGENTS.md`, ADRs, JSON schemas, add-on config, and commit messages.
- Internal, gitignored docs (`implementation_plan/`, `Notes~/`) may be in Russian.
- A CI hard-gate (task `MEMP-040`) will enforce English on tracked files. Until it lands, keep new tracked files English by hand.

## Stack & structure

- **.NET 8** + `ModelContextProtocol` 1.2.0 + `Microsoft.Data.Sqlite`.
- We reuse patterns from a sibling project (a Home Assistant personal-agent add-on): the generic Confirmation flow, the add-on Dockerfile, and the GHCR CI pipeline. We fix its gaps: a migration ladder (`PRAGMA user_version`), WAL/`busy_timeout` set in code, and **no fake embeddings** — FTS5 is honest lexical search.
- Two projects: `src/MemoryMcp.Server` (host + `[McpServerTool]`) and `src/MemoryMcp.Core` (domain, schema registry, repositories, SQLite, migrations). Tests in `tests/MemoryMcp.Tests`.
- XML documentation is required on public C# classes, interfaces and records.

## Architectural principles (do not violate)

- **Domain = namespace/tag, type = schema.** Never a table or database per section.
- **Layered schema:** a relational envelope + `payload_json` validated by a JSON Schema from a registry. No schema-less blobs.
- **Conventions live in the server, not only in the agents.** Schemas (the hard contract, enforced on write) and skills (the soft craft — naming, units, when to split, how to render) are first-class server-hosted objects, served to agents on demand, so every agent writes a given note type the same way. The server hosts these conventions but never runs an LLM itself; the reasoning happens agent-side, guided by them.
- **Source of truth is the structured note; human-readable views (markdown/HTML) are derived render artifacts**, produced on create per the record type's skill. A recipe note is strict and structured; its informal human form is a rendered attachment. The same pattern dogfoods the backlog: structured `backlog_item` notes regenerate the flat backlog file.
- **Soft-delete + append-only `note_events` only.** No hard delete in the tool surface (admin-only, outside MCP).
- **Auth with domain scoping is mandatory**, not "later". Bearer token + an allowed-domains list per token.
- **Small tool surface** (~8 in M0). Skills and schemas are loaded via progressive disclosure, never injected wholesale.
- **Bytes never live in SQLite or pass through the LLM context** — content-addressed blob store; agents get a URI + preview (from M1).
- **The LLM never executes arbitrary code** — it only selects a vetted script; the default is agent-side execution.
- **FTS5 first.** Vectors are an optional, pluggable phase-2 layer, and only real embeddings, honestly named.

## Code quality & conventions

Detailed, source-cited best-practices for the stack live in `references/` (local, gitignored): `dotnet-style.md`, `sqlite-data-access.md`, `mcp-sdk.md`, `testing.md`. The actionable rules:

- **Analyzers on:** `EnableNETAnalyzers`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild`, plus a repo `.editorconfig`. CI builds with `-warnaserror` — **keep `main` zero-warning**. Muted rules carry a rationale comment in `.editorconfig`.
- **One public type per file**, file name == type name. **Folder-per-feature** (group by domain concept, not `Models/`/`Services/`). Namespace == folder path; file-scoped namespaces; `using`s outside the namespace.
- **No god classes (SRP):** a file under ~200 lines; 300 is a smell, 400 means split now. One reason to change. If you can't name a class without "and"/"Manager"/"Helper", it does too much. Split data access into reader / writer / search + pure `static` leaves (row mapper, SQL/query text, snippet builder); inject collaborators via primary constructors.
- **Modern C#:** records for data; `required`/primary constructors to cut ceremony; collection expressions; pattern matching / switch expressions; raw string literals for SQL. Don't force expression bodies onto multi-statement logic.
- **Nullable** honored — no unexplained `!`; model absence as `?` or `Try…`.
- **Async:** I/O is async-all-the-way with the `Async` suffix; never `.Result`/`.Wait()`; `CancellationToken` is the last parameter and is flowed down. **Exception: SQLite has no async I/O** — DB access uses the synchronous ADO APIs by design (see `references/sqlite-data-access.md`); use WAL, not async, for concurrency.
- **SQLite:** per-call connection from the factory (`using`); always parameterize (`$`); one row-mapper per record; transactions for multi-step writes; **never edit a shipped migration** (add the next `user_version` step); sanitize FTS5 `MATCH` input even when parameterized.
- **MCP tools:** keep the surface small; rich `[Description]` on tools and parameters; set behavioral hints (`ReadOnly`/`Destructive`/`Idempotent`); throw `McpException` for model-visible errors (plain exceptions are collapsed to a generic message); return 401/403 from middleware, never from a tool.
- **Tests:** `Method_Scenario_Expectation` naming; per-test isolation (temp DB); deterministic clock via `TimeProvider`; keep integration tests tagged and separable from units.

## Backlog conventions

Task key prefix: **MEMP-NNN** (three or more digits, monotonic, never reused). The backlog currently lives in `implementation_plan/backlog.md` (gitignored, Russian) and migrates into memory itself (`domain=memory-mcp`, `type=backlog_item`) after M0, then is driven via MCP tools.

- On closing a task: move it to `Done` as a one-line summary and remove its detailed body from `Ready`/`Next`/`Later`. (After the migration to memory, the full body and history live in `note_events`, so this stripping rule relaxes.)
- `Ready`/`Next` carry full bodies (title, **Acceptance**, what to do); `Later` is one line each plus a phase tag `[M1]`/`[M2]`/`[M3]`.
- Do not bump add-on or release versions without an explicit request.

## Decisions (ADR)

Key forks are recorded in `docs/decisions/` using the format `Decision` / `Context` / `Consequence`. The full list of decisions is in the master plan (`implementation_plan/README.md §0`).

## Git

No commits or pushes without an explicit request. Branch off the default branch before making changes. Commit messages are in English and reference the relevant MEMP-NNN.
