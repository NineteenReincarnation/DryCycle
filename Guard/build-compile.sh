#!/usr/bin/env bash
set -euo pipefail

# Build / Compile
# Consolidated Guard category. Keep checks invariant-focused; implementation-specific checks should be removed or rewritten.


# ============================================================================
# Migrated from Guard/scripts/check-drycycle-build-contract.sh
# ============================================================================
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


# ============================================================================
# Migrated from Guard/scripts/validate-csharp-syntax.sh
# ============================================================================
set -euo pipefail

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet SDK is required for the C# syntax guard." >&2
  exit 1
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

sdk_path="$(dotnet --info | awk -F': ' '/Base Path/{gsub(/^[ \t]+|[ \t]+$/, "", $2); print $2; exit}')"
if [[ -z "$sdk_path" ]]; then
  echo "Could not resolve .NET SDK base path." >&2
  exit 1
fi
roslyn_dir="${sdk_path%/}/Roslyn/bincore"
sdk_version="$(basename "${sdk_path%/}")"
sdk_major="${sdk_version%%.*}"
if [[ ! "$sdk_major" =~ ^[0-9]+$ ]]; then
  echo "Could not resolve .NET SDK major version from: $sdk_version" >&2
  exit 1
fi
guard_tfm="net${sdk_major}.0"

if [[ ! -f "$roslyn_dir/Microsoft.CodeAnalysis.dll" || ! -f "$roslyn_dir/Microsoft.CodeAnalysis.CSharp.dll" ]]; then
  echo "Roslyn compiler assemblies not found under $roslyn_dir." >&2
  exit 1
fi

cat >"$tmp/SyntaxGuard.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>$guard_tfm</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <LangVersion>latest</LangVersion>
    <RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Microsoft.CodeAnalysis" HintPath="$roslyn_dir/Microsoft.CodeAnalysis.dll" />
    <Reference Include="Microsoft.CodeAnalysis.CSharp" HintPath="$roslyn_dir/Microsoft.CodeAnalysis.CSharp.dll" />
  </ItemGroup>
</Project>
EOF

cat >"$tmp/Program.cs" <<'EOF'
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: SyntaxGuard <source-root>");
    return 2;
}

string root = Path.GetFullPath(args[0]);
if (!Directory.Exists(root))
{
    Console.Error.WriteLine("Source root does not exist: " + root);
    return 2;
}

var options = new CSharpParseOptions(
    languageVersion: LanguageVersion.Latest,
    documentationMode: DocumentationMode.Parse,
    kind: SourceCodeKind.Regular);

int files = 0;
int errors = 0;
foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                               .OrderBy(x => x, StringComparer.Ordinal))
{
    files++;
    string text;
    try
    {
        text = File.ReadAllText(file);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{file}: read failed: {ex.Message}");
        errors++;
        continue;
    }

    SyntaxTree tree = CSharpSyntaxTree.ParseText(text, options, path: file);
    foreach (Diagnostic diagnostic in tree.GetDiagnostics())
    {
        if (diagnostic.Severity != DiagnosticSeverity.Error)
            continue;

        FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
        int line = span.StartLinePosition.Line + 1;
        int column = span.StartLinePosition.Character + 1;
        Console.Error.WriteLine(
            $"{file}({line},{column}): {diagnostic.Id}: {diagnostic.GetMessage()}");
        errors++;
    }
}

if (errors > 0)
{
    Console.Error.WriteLine($"C# syntax guard failed: {errors} error(s) across {files} file(s).");
    return 1;
}

Console.WriteLine($"C# syntax guard passed: {files} source files parsed with Roslyn.");
return 0;
EOF

dotnet run --project "$tmp/SyntaxGuard.csproj" --configuration Release --no-launch-profile -- "$(pwd)/src"


# ============================================================================
# Migrated from Guard/scripts/validate-devtool-frontend-build-contract.sh
# ============================================================================
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

# The dependency direction is backend -> frontend contract only through neutral backend callbacks.
# DryCycle.dll cannot name types from DryCycle.DevTool.RWImGui.dll because the frontend already
# references DryCycle.dll. Catch accidental reverse references before they become CS0234/assembly
# cycles in a real local build.
backend_devtool_dir = Path('src/DevUI/DevTool')
reverse_reference_hits = []
for source in backend_devtool_dir.rglob('*.cs'):
    if frontend_dir in source.parents:
        continue
    text = source.read_text(encoding='utf-8')
    if 'DryCycle.DevUI.DevTool.RWImGui' in text:
        reverse_reference_hits.append(str(source))
require(
    not reverse_reference_hits,
    'DryCycle backend source references the RWImGui frontend namespace; use a backend bridge/callback instead: '
    + ', '.join(reverse_reference_hits)
)

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
