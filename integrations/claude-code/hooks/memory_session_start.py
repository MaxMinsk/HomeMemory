#!/usr/bin/env python3
"""SessionStart hook: pull memory_context from Memory MCP and inject it into the session.

Drop-in for ANY project that talks to a Memory MCP server. Configuration (all optional),
in precedence order env var > .claude/memory.json (in the project dir) > default:

  MEMORY_DOMAIN   the workspace/domain to load (omit -> a cross-domain overview across
                  every domain your token can read; MEMP-213)
  MEMORY_PROJECT  the project sub-axis within the domain (default: the project dir name)
  MEMORY_QUERY    what to recall (default: "project state, active backlog, decisions, rules")
  MEMORY_MCP_URL  the /mcp endpoint (default: discovered from ~/.claude.json "memory" server)
  MEMORY_MCP_TOKEN  the bearer/Authorization value (default: from ~/.claude.json)

Fails OPEN: any error -> no output, exit 0 (the session just starts without memory context).
"""
import json
import os
import sys
import urllib.request

DEFAULT_QUERY = "project state, active backlog, decisions, rules"
BUDGET_CHARS = 6000
TIMEOUT_S = 12


def project_dir():
    return os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd())


def load_config():
    """env var > .claude/memory.json > default. Returns (domain, project, query, url, token)."""
    file_cfg = {}
    path = os.path.join(project_dir(), ".claude", "memory.json")
    if os.path.exists(path):
        try:
            with open(path) as f:
                file_cfg = json.load(f) or {}
        except (OSError, ValueError):
            file_cfg = {}

    def pick(env_key, cfg_key, default=None):
        return os.environ.get(env_key) or file_cfg.get(cfg_key) or default

    domain = pick("MEMORY_DOMAIN", "domain")  # may stay None -> cross-domain overview
    project = pick("MEMORY_PROJECT", "project", os.path.basename(os.path.abspath(project_dir())))
    query = pick("MEMORY_QUERY", "query", DEFAULT_QUERY)
    url = pick("MEMORY_MCP_URL", "url")
    token = pick("MEMORY_MCP_TOKEN", "token")
    return domain, project, query, url, token


def discover_server():
    """Find the 'memory' MCP server url + Authorization from ~/.claude.json (project entry first)."""
    with open(os.path.expanduser("~/.claude.json")) as f:
        cfg = json.load(f)
    for servers in ((cfg.get("projects") or {}).get(project_dir(), {}).get("mcpServers") or {},
                    cfg.get("mcpServers") or {}):
        srv = servers.get("memory")
        if srv and srv.get("url"):
            return srv["url"], (srv.get("headers") or {}).get("Authorization", "")
    raise LookupError("memory MCP server not found in ~/.claude.json")


def call_tool(url, auth, name, arguments):
    body = json.dumps({
        "jsonrpc": "2.0", "id": 1, "method": "tools/call",
        "params": {"name": name, "arguments": arguments},
    }).encode()
    req = urllib.request.Request(url, data=body, headers={
        "Authorization": auth,
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream",
        "User-Agent": "claude-code-hook",
    })
    with urllib.request.urlopen(req, timeout=TIMEOUT_S) as r:
        raw = r.read().decode()
    # The streamable-HTTP transport replies as SSE; the JSON-RPC result is on a "data:" line.
    for line in raw.splitlines():
        if line.startswith("data: "):
            msg = json.loads(line[6:])
            for c in msg.get("result", {}).get("content", []):
                if c.get("type") == "text":
                    return json.loads(c["text"])
    return None


def format_context(ctx, domain, project):
    scope = domain or "all authorized domains"
    if domain and project:
        scope = f"{domain} / {project}"
    lines = [f"## Memory MCP - session-start context (scope: {scope}, advisory)"]
    rules = ctx.get("rules") or []
    if rules:
        lines.append("### Active rules:")
        for r in rules[:10]:
            lines.append(f"- {r.get('title') or r.get('dedupKey') or r.get('id')}")
    skills = ctx.get("skills") or []
    if skills:
        keys = ", ".join(s.get("key") for s in skills[:8] if s.get("key"))
        lines.append(f"### Skills available (skill_get): {keys}")
    hits = (ctx.get("recall") or {}).get("hits") or []
    if hits:
        lines.append("### Relevant notes (fresh/important):")
        for h in hits:
            snippet = (h.get("snippet") or "").replace("\n", " ")[:180]
            lines.append(f"- **{h.get('title')}** [{h.get('type')}, {h.get('domain')}, id {h.get('id')}] - {snippet}")
    for w in (ctx.get("warnings") or [])[:3]:
        lines.append(f"> {w}")
    lines.append(
        "Memory is advisory: live user instructions and data win. Recall before you act; "
        "save durable facts/decisions/state back via notes_upsert (check notes_suggest_capture first; "
        "prefer notes_patch to edit). Run /mcp__memory__end-task to consolidate before finishing."
    )
    return "\n".join(lines)


def main():
    domain, project, query, url, token = load_config()
    if not url:
        url, token = discover_server()
    arguments = {"query": query, "budgetChars": BUDGET_CHARS, "includeLinks": False}
    if domain:
        arguments["domain"] = domain
    if project:
        arguments["project"] = project
    ctx = call_tool(url, token, "memory_context", arguments)
    if not ctx:
        return
    print(json.dumps({
        "suppressOutput": True,
        "hookSpecificOutput": {
            "hookEventName": "SessionStart",
            "additionalContext": format_context(ctx, domain, project),
        },
    }, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except Exception:
        sys.exit(0)  # fail open: no memory context, session starts normally
