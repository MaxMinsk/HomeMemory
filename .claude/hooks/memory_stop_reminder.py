#!/usr/bin/env python3
"""Stop hook: once per session, if the whole session never touched Memory MCP,
show a small reminder to consolidate durable facts.

Quiet by design: no memory tools used + session long enough + not reminded yet
-> one systemMessage. Everything else -> silence. Fails open on any error.
"""
import json
import os
import sys
import tempfile

MIN_ASSISTANT_TURNS = 6


def main():
    data = json.load(sys.stdin)
    if data.get("stop_hook_active"):
        return
    session_id = data.get("session_id") or "unknown"
    sentinel = os.path.join(tempfile.gettempdir(), f"claude-memstop-{session_id}")
    if os.path.exists(sentinel):
        return

    transcript_path = data.get("transcript_path")
    if not transcript_path or not os.path.exists(transcript_path):
        return

    memory_used = False
    assistant_turns = 0
    with open(transcript_path) as f:
        for line in f:
            if '"mcp__memory__' in line:
                memory_used = True
                break
            if '"role":"assistant"' in line or '"role": "assistant"' in line:
                assistant_turns += 1

    if memory_used or assistant_turns < MIN_ASSISTANT_TURNS:
        return

    open(sentinel, "w").close()
    print(json.dumps({
        "systemMessage": (
            "💾 Memory MCP: this session never touched Memory. Backlog, decisions and "
            "project state live there (backlog MEMP-NNN lives in Memory) — ask Claude to "
            "recall/save if anything durable came up."
        )
    }, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except Exception:
        sys.exit(0)
