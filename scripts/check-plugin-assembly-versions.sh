#!/usr/bin/env bash
#
# check-plugin-assembly-versions.sh — fail if any assembly the plugin publishes
# into /nethermind/plugins/ has a different version from the copy the Nethermind
# runtime image already ships in /nethermind/.
#
# Why this exists: the plugin is loaded into a Nethermind process that has already
# resolved its own copies of shared libraries. When our published copy and the
# runtime's copy are different assembly *identities*, the CLR refuses to load ours:
#
#     System.IO.FileLoadException: Could not load file or assembly
#     '/nethermind/plugins/<X>.dll'. The located assembly's manifest definition
#     does not match the assembly reference. (0x80131040)
#
# The node then dies during init and restarts forever. Two real instances while
# moving the runtime image from 1.37.2 to 1.39.3:
#
#   Nethermind.Numerics.Int256 1.6.0 -> aborts before any Circles code runs.
#     Caught locally, because that assembly loads early enough that even
#     `nethermind --version` trips it.
#   Autofac 9.3.2 -> `--version` succeeds, the container starts, and it only
#     dies once plugin init resolves Autofac. Reached a deployed host.
#
# So "the image builds and reports its version" is NOT sufficient evidence. Neither
# is `dotnet build` — CS1705 only catches the pin being too LOW. Being too HIGH
# builds clean, passes every unit test, and fails at runtime. This script closes
# that gap by comparing the two directories directly.
#
# Usage:
#   scripts/check-plugin-assembly-versions.sh [image]
# Defaults to circles-index:local. Build the image first, e.g.
#   docker build -f docker/Index.Dockerfile -t circles-index:local .
#
# Exit codes: 0 = every shared assembly matches, 1 = at least one mismatch.

set -euo pipefail

IMAGE="${1:-circles-index:local}"

command -v docker >/dev/null 2>&1 || { echo "ERROR: docker not found" >&2; exit 1; }

if ! docker image inspect "$IMAGE" >/dev/null 2>&1; then
    echo "ERROR: image '$IMAGE' not found locally. Build it first:" >&2
    echo "  docker build -f docker/Index.Dockerfile -t $IMAGE ." >&2
    exit 1
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

CID="$(docker create "$IMAGE")"
trap 'docker rm -f "$CID" >/dev/null 2>&1 || true; rm -rf "$WORK"' EXIT
docker cp "$CID:/nethermind/plugins/." "$WORK/plugins" >/dev/null
docker cp "$CID:/nethermind/." "$WORK/runtime" >/dev/null

python3 - "$WORK" <<'PY'
import os, re, subprocess, sys

work = sys.argv[1]
plug_dir = os.path.join(work, "plugins")
runt_dir = os.path.join(work, "runtime")


def assembly_version(path):
    """First 4-part version literal in the assembly metadata."""
    try:
        out = subprocess.run(["strings", path], capture_output=True, text=True).stdout
    except Exception:
        return None
    m = re.findall(r"^\d+\.\d+\.\d+\.\d+$", out, re.M)
    return m[0] if m else None


plug = {f for f in os.listdir(plug_dir) if f.endswith(".dll")}
runt = {f for f in os.listdir(runt_dir) if f.endswith(".dll")}
shared = sorted(plug & runt)

if not shared:
    print("ERROR: no assemblies found in both plugins/ and the runtime root.")
    print("       The image layout probably changed — this check is now blind.")
    sys.exit(1)

mismatches = []
for name in shared:
    pv = assembly_version(os.path.join(plug_dir, name))
    rv = assembly_version(os.path.join(runt_dir, name))
    if pv and rv and pv != rv:
        mismatches.append((name, pv, rv))

print(f"Compared {len(shared)} assemblies present in both plugins/ and the runtime root.")

if mismatches:
    print("\nMISMATCH — the node will fail during init with FileLoadException:\n")
    for name, pv, rv in mismatches:
        print(f"  {name:<45} plugin={pv:<12} runtime={rv}")
    print("\nPin the offending package in Directory.Packages.props to the runtime's")
    print("version exactly. Higher is not safer here: it builds and tests clean, then")
    print("crash-loops the deployed node.")
    sys.exit(1)

print("OK: every shared assembly matches the runtime image.")
PY
