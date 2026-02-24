#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: bump-index-versions.sh

Lists all src/Index/*.csproj packages, lets you pick one, shows the current
version(s), prompts for a new version, and updates the file in-place.
EOF
}

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
INDEX_DIR="$REPO_ROOT/src/Index"

if [[ ${1:-} == "-h" || ${1:-} == "--help" ]]; then
  usage
  exit 0
fi

if [[ ! -d "$INDEX_DIR" ]]; then
  echo "Error: src/Index directory not found at $INDEX_DIR" >&2
  exit 1
fi

if ! command -v python3 >/dev/null 2>&1; then
  echo "Error: python3 is required to run this script." >&2
  exit 1
fi

CS_PROJ_FILES=()
while IFS= read -r line; do
  CS_PROJ_FILES+=("$line")
done < <(find "$INDEX_DIR" -name '*.csproj' -type f | sort)

if [[ ${#CS_PROJ_FILES[@]} -eq 0 ]]; then
  echo "Error: no .csproj files found under $INDEX_DIR" >&2
  exit 1
fi

echo "Available Index packages:" 
for idx in "${!CS_PROJ_FILES[@]}"; do
  rel_path="${CS_PROJ_FILES[$idx]#$REPO_ROOT/}"
  printf " %2d) %s\n" "$((idx + 1))" "$rel_path"
done

selected=""
while [[ -z "$selected" ]]; do
  read -rp "Select a package by number (q to quit): " answer
  if [[ -z "$answer" ]]; then
    continue
  elif [[ "$answer" == "q" || "$answer" == "Q" ]]; then
    echo "Aborted."
    exit 0
  elif [[ "$answer" =~ ^[0-9]+$ ]] && (( answer >= 1 && answer <= ${#CS_PROJ_FILES[@]} )); then
    selected=${CS_PROJ_FILES[$((answer - 1))]}
  else
    echo "Invalid selection. Please enter a number between 1 and ${#CS_PROJ_FILES[@]}."
  fi
done

rel_selected="${selected#$REPO_ROOT/}"

CURRENT_INFO=$(python3 - "$selected" <<'PY'
import pathlib
import re
import sys

path = pathlib.Path(sys.argv[1])
text = path.read_text(encoding="utf-8")
versions = re.findall(r"<Version>([^<]+)</Version>", text)
if not versions:
    print("", end="")
    sys.exit(1)

unique = []
seen = set()
for entry in versions:
    if entry not in seen:
        unique.append(entry)
        seen.add(entry)

print("|".join(unique))
PY
)

if [[ -z "$CURRENT_INFO" ]]; then
  echo "Error: could not find a <Version> element in $rel_selected" >&2
  exit 1
fi

IFS='|' read -r -a CURRENT_VERSIONS <<< "$CURRENT_INFO"

if [[ ${#CURRENT_VERSIONS[@]} -eq 1 ]]; then
  echo "Current version: ${CURRENT_VERSIONS[0]}"
else
  echo "Current versions found: ${CURRENT_VERSIONS[*]}"
fi

new_version=""
while [[ -z "$new_version" ]]; do
  read -rp "Enter new version (blank to abort): " new_version
  if [[ -z "$new_version" ]]; then
    echo "Aborted."
    exit 0
  fi
  if [[ ! "$new_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z]+)?$ ]]; then
    echo "Version must match semantic version format, e.g. 0.0.8 or 1.2.3-beta."
    new_version=""
  fi
done

if python3 - "$selected" "$new_version" <<'PY'
import pathlib
import re
import sys

path = pathlib.Path(sys.argv[1])
version = sys.argv[2]
text = path.read_text(encoding="utf-8")
pattern = re.compile(r"(<Version>)([^<]+)(</Version>)")

def repl(match):
  return f"{match.group(1)}{version}{match.group(3)}"

new_text, count = pattern.subn(repl, text)

if count == 0:
    print(f"No <Version> element found in {path}")
    sys.exit(1)

path.write_text(new_text, encoding="utf-8")
print(f"Updated {path} ({count} occurrence{'s' if count != 1 else ''})")
PY
then
  echo "Successfully updated $rel_selected to $new_version."
else
  echo "Failed to update $rel_selected." >&2
  exit 1
fi
