#!/usr/bin/env bash
# Hard-gate (MEMP-040): tracked files must be English-only — no Cyrillic characters.
# The public repo is English; internal Russian docs live in gitignored folders
# (Notes~/, implementation_plan/, references/) and never reach `git ls-files`.
# Engine is perl (\p{Cyrillic}) so this runs identically on Linux CI and local macOS.
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

# -CSD makes perl decode input files as UTF-8 so \p{Cyrillic} matches real codepoints, not raw bytes.
# One line per offending file: print the name on first hit, then skip the rest of that file.
matches="$(git ls-files -z | xargs -0 perl -CSD -ne 'print "$ARGV\n" and close ARGV if /\p{Cyrillic}/' | sort -u)"

if [ -n "$matches" ]; then
  {
    echo "Non-English (Cyrillic) characters found in tracked files:"
    echo "$matches"
    echo
    echo "The public repo must be English-only. Move Russian content into a gitignored"
    echo "folder (Notes~/, implementation_plan/, references/) or translate it."
  } >&2
  exit 1
fi

echo "English-only check passed: $(git ls-files | wc -l | tr -d ' ') tracked files scanned, no Cyrillic."
