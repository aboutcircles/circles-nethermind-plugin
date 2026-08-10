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
# CHECK 2 (shared framework) exists because check 1 above is not sufficient either.
# A shared-framework assembly such as Microsoft.Extensions.Diagnostics.HealthChecks
# lives in /usr/share/dotnet/shared/Microsoft.AspNetCore.App/, so it appears NOWHERE
# in the runtime root and check 1 is blind to it. A version comparison would be the
# wrong test anyway: publishing a framework assembly into plugins/ is a hazard even
# when the versions match, because the private copy becomes a separate assembly
# identity inside the plugin load context, and two identities of the same type do
# not satisfy each other's casts or DI registrations.
#
# Unlike check 1, this check is precautionary — it is not backed by an observed
# outage in this repo. It was added while investigating the /health 404 on 1.39.3;
# that turned out to have an unrelated cause (Nethermind now gates the endpoint on
# the Health RPC module being listed in JsonRpc.EnabledModules), and a controlled
# repro confirmed the shadowed HealthChecks assemblies did NOT break the route.
# The check is kept because the failure mode it guards is silent by construction —
# no crash, no log line, just a feature that quietly stops working.
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
# Best-effort: some images may lay the shared framework out differently. A missing
# directory downgrades check 2 to a warning rather than failing the build.
docker cp "$CID:/usr/share/dotnet/shared" "$WORK/shared" >/dev/null 2>&1 || true

python3 - "$WORK" <<'PY'
import os, re, subprocess, sys

work = sys.argv[1]
plug_dir = os.path.join(work, "plugins")
runt_dir = os.path.join(work, "runtime")
shared_dir = os.path.join(work, "shared")


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

print(f"Check 1: compared {len(shared)} assemblies present in both plugins/ and the runtime root.")

failed = False

if mismatches:
    print("\nMISMATCH — the node will fail during init with FileLoadException:\n")
    for name, pv, rv in mismatches:
        print(f"  {name:<55} plugin={pv:<12} runtime={rv}")
    print("\nPin the offending package in Directory.Packages.props to the runtime's")
    print("version exactly. Higher is not safer here: it builds and tests clean, then")
    print("crash-loops the deployed node.")
    failed = True

# --- Check 2: no shared-framework assembly may be published into plugins/ ---
if not os.path.isdir(shared_dir):
    print("\nCheck 2: SKIPPED — no /usr/share/dotnet/shared in the image.")
    print("         Framework-shadowing cannot be detected. Investigate if unexpected.")
else:
    framework = {}
    for root, _dirs, files in os.walk(shared_dir):
        for f in files:
            if f.endswith(".dll"):
                # Record the first framework that provides it; enough to name it.
                framework.setdefault(f, os.path.relpath(root, shared_dir))

    # Known-present before the 1.39.3 bump and not implicated in any observed
    # failure. Both are pure interface contracts that the plugin tree pulls in
    # transitively, and both have shipped in plugins/ across every release this
    # repo has deployed. They are NOT proven safe — the same separate-identity
    # hazard applies in principle — but removing them risks breaking plugin
    # loading outright, so that is its own change with its own verification.
    # Do not extend this list to make a new finding go away.
    ALLOWLIST = {
        "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
        "Microsoft.Extensions.Logging.Abstractions.dll",
    }

    shadowed = sorted((plug & set(framework)) - ALLOWLIST)
    allowed = sorted((plug & set(framework)) & ALLOWLIST)
    print(f"Check 2: {len(framework)} shared-framework assemblies, "
          f"{len(shadowed)} shadowed by plugins/ "
          f"({len(allowed)} allowlisted).")
    for name in allowed:
        print(f"         allowlisted: {name}")

    if shadowed:
        print("\nFRAMEWORK SHADOWING — these are provided by the shared framework and")
        print("should NOT be published into plugins/. A private copy becomes a separate")
        print("assembly identity in the plugin load context: the node starts and logs")
        print("normally, and the affected feature silently does nothing.\n")
        for name in shadowed:
            print(f"  {name:<55} framework={framework[name]}")
        print("\nRemove the PackageReference from the project that publishes into")
        print("plugins/ (src/Index/Circles.Index). If the code genuinely needs the API,")
        print("keep the reference but add ExcludeAssets=\"runtime\" so it compiles")
        print("against the package without copying the assembly to the output.")
        failed = True

if failed:
    sys.exit(1)

print("\nOK: no version mismatches and no framework shadowing.")
PY
