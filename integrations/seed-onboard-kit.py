#!/usr/bin/env python3
"""Seed (or refresh) the onboarding kit into a Memory MCP server's `commons` domain, so the
`onboard-project` MCP prompt serves live, owner-editable copies (edit them later with notes_patch)
instead of the version embedded in the server image.

The onboard-project prompt reads each file from a commons `reference` note (by dedupKey) and falls
back to its embedded copy when the note is missing - so seeding is optional but lets you customize
the templates/hooks without a release.

Usage:
  python3 integrations/seed-onboard-kit.py                 # discover server from ~/.claude.json
  MEMORY_MCP_URL=... MEMORY_MCP_TOKEN=... python3 integrations/seed-onboard-kit.py

Run from the repo root (paths are resolved relative to this file).
"""
import json
import os
import sys
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))

# (repo file, commons dedupKey, note title)
KIT = [
    ("templates/AGENTS.md", "onboard-kit-agents-md", "Onboarding kit: AGENTS.md template"),
    ("templates/CLAUDE.md", "onboard-kit-claude-md", "Onboarding kit: CLAUDE.md template"),
    ("claude-code/hooks/memory_session_start.py", "onboard-kit-hook-session-start", "Onboarding kit: SessionStart hook"),
    ("claude-code/hooks/memory_stop_reminder.py", "onboard-kit-hook-stop-reminder", "Onboarding kit: Stop hook"),
    ("claude-code/settings.json", "onboard-kit-settings-json", "Onboarding kit: settings.json hooks block"),
]


def discover_server():
    if os.environ.get("MEMORY_MCP_URL"):
        return os.environ["MEMORY_MCP_URL"], os.environ.get("MEMORY_MCP_TOKEN", "")
    with open(os.path.expanduser("~/.claude.json")) as f:
        cfg = json.load(f)
    for servers in ((cfg.get("projects") or {}).get(os.getcwd(), {}).get("mcpServers") or {},
                    cfg.get("mcpServers") or {}):
        srv = servers.get("memory")
        if srv and srv.get("url"):
            return srv["url"], (srv.get("headers") or {}).get("Authorization", "")
    raise LookupError("memory MCP server not found; set MEMORY_MCP_URL / MEMORY_MCP_TOKEN")


def call_tool(url, auth, name, arguments):
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                       "params": {"name": name, "arguments": arguments}}).encode()
    req = urllib.request.Request(url, data=body, headers={
        "Authorization": auth, "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream", "User-Agent": "curl/8",
    })
    with urllib.request.urlopen(req, timeout=30) as r:
        raw = r.read().decode()
    for line in raw.splitlines():
        if line.startswith("data: "):
            return json.loads(line[6:])
    return json.loads(raw)


def main():
    url, auth = discover_server()
    ok = 0
    for rel, key, title in KIT:
        with open(os.path.join(HERE, rel)) as f:
            content = f.read()
        resp = call_tool(url, auth, "notes_upsert", {
            "domain": "commons", "type": "reference", "title": title, "body": content,
            "dedupKey": key, "payload": {"source": f"integrations/{rel}"},
            "sourceAgent": "seed-onboard-kit",
        })
        err = resp.get("result", {}).get("isError") or resp.get("error")
        print(f"{'FAIL' if err else 'ok  '} {key} <- integrations/{rel}" + (f"  {err}" if err else ""))
        ok += 0 if err else 1
    print(f"Seeded {ok}/{len(KIT)} kit files into commons.")


if __name__ == "__main__":
    main()
