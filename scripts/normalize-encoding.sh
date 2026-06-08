#!/usr/bin/env bash
set -euo pipefail

# Normalize C# source encoding to the Swarmwright house style (per CLAUDE.md):
#   - UTF-8 with BOM
#   - CRLF line endings
#   - no trailing whitespace
#   - exactly one final newline
#
# Hand-written files (via editors that emit LF/no-BOM) are normalized in place.
# Imported files already in this style are left byte-identical.
#
# Usage:
#   ./scripts/normalize-encoding.sh                 # normalize src/ and tests/
#   ./scripts/normalize-encoding.sh path [path...]  # normalize specific files/dirs

cd "$(dirname "$0")/.."

targets=("$@")
if [[ ${#targets[@]} -eq 0 ]]; then
  targets=("src" "tests")
fi

mapfile -t files < <(
  for t in "${targets[@]}"; do
    if [[ -d "$t" ]]; then
      find "$t" -type f -name '*.cs' \
        -not -path '*/obj/*' -not -path '*/bin/*'
    elif [[ -f "$t" ]]; then
      printf '%s\n' "$t"
    fi
  done | sort -u
)

count=0
for f in "${files[@]}"; do
  perl -i -pe '
    s/[ \t]+(?=\r?\n?$)//;   # strip trailing whitespace
    s/\r?\n$/\r\n/;          # normalize line ending to CRLF
  ' "$f"
  # ensure the final line ends with CRLF (perl pass leaves last line untouched if no newline)
  if [[ -n "$(tail -c1 "$f")" ]]; then
    printf '\r\n' >> "$f"
  fi
  # collapse multiple trailing blank lines to exactly one final newline
  perl -i -0pe 's/(\r\n)+$/\r\n/' "$f"
  # ensure UTF-8 BOM
  if ! head -c3 "$f" | grep -q $'\xEF\xBB\xBF'; then
    printf '\xEF\xBB\xBF' | cat - "$f" > "$f.tmp" && mv "$f.tmp" "$f"
  fi
  count=$((count + 1))
done

echo "Normalized ${count} .cs file(s)."
