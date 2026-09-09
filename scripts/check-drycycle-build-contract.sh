#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

python3 - <<'PY'
from pathlib import Path
import xml.etree.ElementTree as ET

project_path = Path('src/DryCycle.csproj')
targets_path = Path('src/Directory.Build.targets')
if not project_path.exists() or not targets_path.exists():
    raise SystemExit('DryCycle build contract sources are missing')

project = ET.parse(project_path).getroot()
targets = ET.parse(targets_path).getroot()


def children(root, tag):
    return [node for node in root.iter() if node.tag.rsplit('}', 1)[-1] == tag]


def one(root, tag, *, name=None):
    matches = children(root, tag)
    if name is not None:
        matches = [node for node in matches if node.attrib.get('Name') == name]
    if len(matches) != 1:
        raise SystemExit(f'expected exactly one {tag} {name or ""}, found {len(matches)}')
    return matches[0]


def require(text, token, message):
    if token not in text:
        raise SystemExit(message)

project_text = project_path.read_text(encoding='utf-8')
targets_text = targets_path.read_text(encoding='utf-8')

# Preserve the historical developer workflow unless the caller explicitly opts out.
deploy = one(project, 'DeployToGame')
if (deploy.text or '').strip().lower() != 'true' or "'$(DeployToGame)' == ''" not in deploy.attrib.get('Condition', ''):
    raise SystemExit('DeployToGame must default to true only when the caller did not set it')

# Compile-only builds must use SDK output and must not require the game-mod destination.
output = one(project, 'OutputPath')
if "'$(DeployToGame)' == 'true'" not in output.attrib.get('Condition', ''):
    raise SystemExit('custom game OutputPath must be gated by DeployToGame=true')

require(project_text,
        "'$(DeployToGame)' == 'true' and '$(GameModOutputDir)' == ''",
        'GameModOutputDir empty validation must apply only to deploy builds')
require(project_text,
        "'$(DeployToGame)' == 'true' and !Exists('$(GameModOutputDir)')",
        'GameModOutputDir existence validation must apply only to deploy builds')

# Release packaging copies assets/world data and therefore must never execute in compile-only mode.
generate = one(project, 'Target', name='GenerateMod')
if "'$(DeployToGame)' == 'true'" not in generate.attrib.get('Condition', ''):
    raise SystemExit('GenerateMod must be gated by DeployToGame=true')

# RWImGUI migration targets delete/build files beside the game plugin. All three are writes.
for name in (
    'RemoveLegacyDryCycleImGuiRuntime',
    'BuildOptionalRWImGuiBridge',
    'ReportMissingOptionalRWImGuiBridge',
):
    target = one(targets, 'Target', name=name)
    if "'$(DeployToGame)' == 'true'" not in target.attrib.get('Condition', ''):
        raise SystemExit(f'{name} must be gated by DeployToGame=true')

# Compile-only mode must not silently weaken the exact Rain World reference contract.
for assembly in (
    'BepInEx.dll',
    'MonoMod.RuntimeDetour.dll',
    'PUBLIC-Assembly-CSharp.dll',
    'HOOKS-Assembly-CSharp.dll',
    'Assembly-CSharp-firstpass.dll',
    'UnityEngine.CoreModule.dll',
):
    if assembly not in project_text:
        raise SystemExit('exact Rain World reference validation missing: ' + assembly)

print('DryCycle build contract passed: compile-only output is isolated while exact Rain World references remain mandatory.')
PY
