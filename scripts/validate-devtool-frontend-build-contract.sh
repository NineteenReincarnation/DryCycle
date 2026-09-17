#!/usr/bin/env bash
set -euo pipefail

python3 - <<'PY'
from pathlib import Path
import xml.etree.ElementTree as ET

backend_project = Path('src/DryCycle.csproj')
targets_path = Path('src/Directory.Build.targets')
core_page = Path('src/DevUI/DevTool/Core/IDevToolPage.cs')
frontend_dir = Path('src/DevUI/DevTool/RWImGui')
frontend_project = frontend_dir / 'DryCycle.DevTool.RWImGui.csproj'
page_view = frontend_dir / 'IDevToolPageView.cs'
registry_path = frontend_dir / 'DevToolPageViewRegistry.cs'

required_paths = (
    backend_project,
    targets_path,
    frontend_project,
    core_page,
    page_view,
    frontend_dir / 'BuiltinDevToolPages.cs',
    registry_path,
    frontend_dir / 'ObjectSceneWorkspaceView.cs',
    frontend_dir / 'SceneWorkspaceWindow.cs',
    frontend_dir / 'ScenePlacementWindow.cs',
    frontend_dir / 'PAGE_VIEW_ARCHITECTURE.md',
)
for path in required_paths:
    if not path.exists():
        raise SystemExit(f'DevTool frontend build-contract input is missing: {path}')

backend = ET.parse(backend_project).getroot()
frontend = ET.parse(frontend_project).getroot()
targets = ET.parse(targets_path).getroot()


def local_name(node):
    return node.tag.rsplit('}', 1)[-1]


def nodes(root, tag):
    return [node for node in root.iter() if local_name(node) == tag]


def property_values(root, tag):
    return [(node.text or '').strip() for node in nodes(root, tag)]


def require(condition, message):
    if not condition:
        raise SystemExit(message)

# The frontend intentionally relies on the SDK default Compile glob. This is what guarantees every
# new view/interface .cs file below RWImGui enters DryCycle.DevTool.RWImGui.dll without maintaining a
# second hand-written source list.
require(frontend.attrib.get('Sdk') == 'Microsoft.NET.Sdk',
        'DevTool RWImGui frontend must remain an SDK-style project so default Compile globs apply')
require('net48' in property_values(frontend, 'TargetFramework'),
        'DevTool RWImGui frontend must continue targeting net48')
require('DryCycle.DevTool.RWImGui' in property_values(frontend, 'AssemblyName'),
        'DevTool RWImGui frontend assembly name changed unexpectedly')

for value in property_values(frontend, 'EnableDefaultCompileItems'):
    require(value.lower() != 'false',
            'DevTool RWImGui disabled SDK default Compile items; new view files could silently be omitted')

compile_items = nodes(frontend, 'Compile')
for item in compile_items:
    remove = item.attrib.get('Remove', '').strip()
    exclude = item.attrib.get('Exclude', '').strip()
    require(not remove and not exclude,
            'DevTool RWImGui project removes/excludes Compile items; keep source inclusion automatic')

# Page/View composition is deliberately an implementation detail, not DevToolApi 1.x. Keep all three
# lifecycle/rendering contracts and the registry internal even though InternalsVisibleTo lets the
# separate frontend assembly consume the backend lifecycle interface.
core_page_text = core_page.read_text(encoding='utf-8')
page_view_text = page_view.read_text(encoding='utf-8')
registry_text = registry_path.read_text(encoding='utf-8')
require('internal interface IDevToolPage' in core_page_text,
        'IDevToolPage became public; internal page lifecycle must not enter DevToolApi ABI')
require('internal interface IDevToolPageView' in page_view_text,
        'IDevToolPageView became public; frontend rendering contract must remain internal')
require('internal interface IDevToolFrontendPage : IDevToolPage, IDevToolPageView' in page_view_text,
        'IDevToolFrontendPage internal composite contract changed or became public')
require('internal static class DevToolPageViewRegistry' in registry_text,
        'DevToolPageViewRegistry became public; page registration is not a DevToolApi 1.x capability')

# The gameplay/core assembly must never absorb the ImGui frontend source. The frontend is a separate
# optional assembly and depends one-way on DryCycle.dll.
targets_text = targets_path.read_text(encoding='utf-8')
require('<Compile Remove="DevUI/DevTool/RWImGui/**/*.cs" />' in targets_text,
        'DryCycle backend no longer excludes the DevTool RWImGui frontend source tree')

reference_nodes = {
    item.attrib.get('Include', ''): item
    for item in nodes(frontend, 'Reference')
}
for reference in ('DryCycle', 'rain-world-imgui-api', 'ImGui.NET'):
    require(reference in reference_nodes,
            f'DevTool RWImGui frontend lost required assembly reference: {reference}')

# The frontend intentionally consumes several internal backend contracts (IDevToolPage and related
# scheduler/performance helpers). Because it is a separate DLL, its AssemblyName and the backend
# InternalsVisibleTo entry are one compile-time contract. A rename on either side otherwise turns a
# structurally-correct source tree into CS0122 accessibility failures.
friend_names = {
    item.attrib.get('Include', '').strip()
    for item in nodes(backend, 'InternalsVisibleTo')
}
require('DryCycle.DevTool.RWImGui' in friend_names,
        'DryCycle backend lost InternalsVisibleTo for the DevTool RWImGui frontend')

backend_ref = reference_nodes['DryCycle']
hint_paths = [
    (child.text or '').strip()
    for child in list(backend_ref)
    if local_name(child) == 'HintPath'
]
require('$(GameModOutputDir)/DryCycle.dll' in hint_paths,
        'DevTool RWImGui frontend no longer compiles against the backend DLL produced in GameModOutputDir')

# Parent DryCycle deployment is the authoritative build entry. It must restore and forcibly rebuild
# the exact frontend project, then fail if the expected DLL was not actually deployed.
frontend_project_expr = '$(MSBuildProjectDirectory)/DevUI/DevTool/RWImGui/DryCycle.DevTool.RWImGui.csproj'
frontend_msbuild = [
    item for item in nodes(targets, 'MSBuild')
    if item.attrib.get('Projects') == frontend_project_expr
]
frontend_targets = {item.attrib.get('Targets', '') for item in frontend_msbuild}
require('Restore' in frontend_targets,
        'DryCycle parent build no longer restores the DevTool RWImGui frontend')
require('Rebuild' in frontend_targets,
        'DryCycle parent build no longer forces a fresh DevTool RWImGui frontend rebuild')
require("!Exists('$(GameModOutputDir)/DryCycle.DevTool.RWImGui.dll')" in targets_text,
        'DryCycle parent build no longer verifies the deployed DevTool RWImGui DLL exists')

print('DevTool RWImGui frontend build contract passed: source glob, internal page ABI, friend assembly boundary, backend reference and forced rebuild are intact.')
PY
