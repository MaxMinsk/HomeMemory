#!/usr/bin/env python3
"""SessionStart hook: pull memory_context from Memory MCP and inject it into the session.

Reads the Memory MCP endpoint + bearer from ~/.claude.json (project entry first,
then global mcpServers) so the token is never duplicated here. Fails open: any
error -> no output, exit 0 (the session just starts without memory context).
"""
import json
import os
import sys
import urllib.request

QUERY = "project state, active backlog, sprint, decisions, rules"
DOMAIN = "development"
PROJECT = "memory-mcp"
BUDGET_CHARS = 6000
TIMEOUT_S = 12


def find_memory_server():
    cfg_path = os.path.expanduser("~/.claude.json")
    with open(cfg_path) as f:
        cfg = json.load(f)
    project_dir = os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd())
    candidates = []
    proj = (cfg.get("projects") or {}).get(project_dir) or {}
    candidates.append(proj.get("mcpServers") or {})
    candidates.append(cfg.get("mcpServers") or {})
    for servers in candidates:
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
    for line in raw.splitlines():
        if line.startswith("data: "):
            msg = json.loads(line[6:])
            for c in msg.get("result", {}).get("content", []):
                if c.get("type") == "text":
                    return json.loads(c["text"])
    return None


def format_context(ctx):
    lines = [f"## Memory MCP — session-start context (memory_context, project={PROJECT}, advisory)"]
    rules = ctx.get("rules") or []
    if rules:
        lines.append("### Active rules:")
        for r in rules[:10]:
            lines.append(f"- {r.get('title') or r.get('key')}")
    skills = ctx.get("skills") or []
    if skills:
        keys = ", ".join(s.get("key") for s in skills[:8] if s.get("key"))
        lines.append(f"### Server skills available (skill_get): {keys}")
    hits = (ctx.get("recall") or {}).get("hits") or []
    if hits:
        lines.append("### Relevant notes (fresh/important):")
        for h in hits:
            snippet = (h.get("snippet") or "").replace("\n", " ")[:180]
            lines.append(f"- **{h.get('title')}** [{h.get('type')}, id {h.get('id')}] — {snippet}")
    lines.append(
        "Memory is advisory: live user instructions and data win. "
        "the backlog/board lives in Memory (type=backlog_item, payload.project == '" + PROJECT + "'). "
        "Save durable decisions/state back via notes_upsert (check notes_suggest_capture first)."
    )
    return "\n".join(lines)


def main():
    url, auth = find_memory_server()
    ctx = call_tool(url, auth, "memory_context", {
        "query": QUERY, "domain": DOMAIN, "project": PROJECT,
        "budgetChars": BUDGET_CHARS, "includeLinks": False,
    })
    if not ctx:
        return
    print(json.dumps({
        "suppressOutput": True,
        "hookSpecificOutput": {
            "hookEventName": "SessionStart",
            "additionalContext": format_context(ctx),
        },
    }, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except Exception:
        sys.exit(0)  # fail open: no memory context, session starts normally
