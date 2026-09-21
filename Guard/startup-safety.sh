#!/usr/bin/env bash
set -euo pipefail

# Startup / Lifecycle Safety
#
# This guard protects only durable lifecycle boundaries that static analysis can verify
# without freezing DryCycle's current bootstrap implementation. Behavioural guarantees
# such as transactional rollback, cleanup continuation and irreversible registry state
# belong in focused tests rather than grep-based architecture locks.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

python3 - <<'PY'
from pathlib import Path
import re

SRC = Path("src")
if not SRC.is_dir():
    raise SystemExit("src directory is missing")

def fail(message):
    raise SystemExit(message)

def read(path):
    try:
        return path.read_text(encoding="utf-8", errors="ignore")
    except OSError as exc:
        fail(f"Could not read {path}: {exc}")

# ---------------------------------------------------------------------------
# 1. Optional frontend assemblies must not own Rain World's core mod-init hooks.
#
# Frontends may consume any DryCycle lifecycle abstraction the core exposes. This rule
# deliberately does not require DryCycleLifecycleEvents, fixed event names, fixed
# BridgePlugin names, or a particular subscription topology.
# ---------------------------------------------------------------------------
optional_frontend_roots = [
    SRC / "DevUI" / "DevTool" / "RWImGui",
    SRC / "DryCycle.AIObservatory.RWImGui",
]

rainworld_startup_hook = re.compile(
    r"On\s*\.\s*RainWorld\s*\.\s*(?:PreModsInit|OnModsInit|PostModsInit)\s*\+="
)

direct_hook_hits = []
for root in optional_frontend_roots:
    if not root.exists():
        continue
    for source in root.rglob("*.cs"):
        if rainworld_startup_hook.search(read(source)):
            direct_hook_hits.append(str(source))

if direct_hook_hits:
    fail(
        "Optional frontend assemblies directly own Rain World startup hooks. "
        "Route frontend startup through a core-owned lifecycle surface instead: "
        + ", ".join(sorted(direct_hook_hits))
    )

# ---------------------------------------------------------------------------
# 2. Independent optional BepInEx entrypoints need a local exception boundary.
#
# We intentionally do not prescribe StartupDiagnostics, AuxiliaryPluginStartupGuard,
# ShutdownBridgeState, exact log text, or rollback method names. The static guard only
# rejects the clearest unsafe shape: an optional plugin OnEnable with no catch boundary
# anywhere in that method body.
#
# Primary src/Plugin.cs is excluded because core startup can contain irreversible state;
# "always swallow and continue" is not a valid universal rule for the primary plugin.
# ---------------------------------------------------------------------------
plugin_attribute = re.compile(r"\[\s*BepInPlugin\s*\(")
on_enable = re.compile(
    r"\b(?:public|private|protected|internal)?\s*(?:static\s+)?void\s+OnEnable\s*\([^)]*\)\s*\{",
    re.MULTILINE,
)

def method_body(text, match):
    brace = text.find("{", match.start())
    if brace < 0:
        return None
    depth = 0
    i = brace
    in_string = False
    verbatim = False
    escape = False
    quote = None
    while i < len(text):
        ch = text[i]
        nxt = text[i + 1] if i + 1 < len(text) else ""
        if in_string:
            if verbatim:
                if ch == '"' and nxt == '"':
                    i += 2
                    continue
                if ch == '"':
                    in_string = False
            else:
                if escape:
                    escape = False
                elif ch == "\\":
                    escape = True
                elif ch == quote:
                    in_string = False
            i += 1
            continue
        if ch == '@' and nxt == '"':
            in_string = True
            verbatim = True
            quote = '"'
            i += 2
            continue
        if ch in ('"', "'"):
            in_string = True
            verbatim = False
            quote = ch
            i += 1
            continue
        if ch == '/' and nxt == '/':
            end = text.find("\n", i + 2)
            i = len(text) if end < 0 else end + 1
            continue
        if ch == '/' and nxt == '*':
            end = text.find("*/", i + 2)
            i = len(text) if end < 0 else end + 2
            continue
        if ch == '{':
            depth += 1
        elif ch == '}':
            depth -= 1
            if depth == 0:
                return text[brace + 1:i]
        i += 1
    return None

unsafe_optional_entrypoints = []
for source in sorted(SRC.rglob("*.cs")):
    if source == SRC / "Plugin.cs":
        continue
    text = read(source)
    if not plugin_attribute.search(text):
        continue
    matches = list(on_enable.finditer(text))
    if not matches:
        # A BepInPlugin may intentionally bootstrap through Awake/Start or a base class.
        # Do not invent a required lifecycle method.
        continue
    for match in matches:
        body = method_body(text, match)
        if body is None:
            fail(f"Could not parse OnEnable body in optional BepInEx plugin: {source}")
        if not re.search(r"\bcatch\s*(?:\([^)]*\))?\s*\{", body):
            unsafe_optional_entrypoints.append(str(source))

if unsafe_optional_entrypoints:
    fail(
        "Optional BepInEx OnEnable entrypoint has no local exception-isolation boundary: "
        + ", ".join(sorted(set(unsafe_optional_entrypoints)))
    )

# ---------------------------------------------------------------------------
# 3. Keep obvious silent exception swallowing out of BepInEx lifecycle entrypoints.
#
# This is deliberately narrow. It does not require a logger API or fixed diagnostic text.
# It catches only an empty catch block at a plugin lifecycle boundary, which would make a
# startup/shutdown failure invisible and is never an acceptable rollback strategy.
# ---------------------------------------------------------------------------
lifecycle_names = ("OnEnable", "OnDisable", "Awake", "Start")
empty_catch = re.compile(r"\bcatch\s*(?:\([^)]*\))?\s*\{\s*\}", re.DOTALL)
silent_hits = []

for source in sorted(SRC.rglob("*.cs")):
    text = read(source)
    if not plugin_attribute.search(text):
        continue
    for name in lifecycle_names:
        pattern = re.compile(
            rf"\b(?:public|private|protected|internal)?\s*(?:static\s+)?void\s+{name}\s*\([^)]*\)\s*\{{",
            re.MULTILINE,
        )
        for match in pattern.finditer(text):
            body = method_body(text, match)
            if body is not None and empty_catch.search(body):
                silent_hits.append(f"{source}:{name}")

if silent_hits:
    fail(
        "BepInEx lifecycle entrypoint contains an empty catch block; failures must remain observable: "
        + ", ".join(sorted(set(silent_hits)))
    )

print(
    "Startup/lifecycle safety guard passed: optional frontends do not own Rain World "
    "startup hooks, optional plugin OnEnable entrypoints have local isolation boundaries, "
    "and plugin lifecycle failures are not silently swallowed by empty catch blocks."
)
PY
