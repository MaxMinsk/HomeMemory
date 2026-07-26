#!/usr/bin/env python3
"""Stop hook: once per session, if the whole session never touched Memory MCP,
show a small reminder to consolidate durable facts.

Drop-in for any project. Quiet by design: no memory tools used + session long
enough + not reminded yet -> one systemMessage. Everything else -> silence.
Fails OPEN on any error.
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
            "Memory MCP: this session never touched shared memory. Decisions, project "
            "state and backlog live there - ask Claude to recall/save if anything durable "
            "came up (or run /mcp__memory__end-task)."
        )
    }, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except Exception:
        sys.exit(0)
