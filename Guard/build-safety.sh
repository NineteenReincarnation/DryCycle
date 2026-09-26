#!/usr/bin/env bash
set -euo pipefail

# Build Safety Guard
# Only protect durable build/deployment invariants here. Do not freeze private type names,
# target names, exact MSBuild spelling, or one historical implementation.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# ---------------------------------------------------------------------------
# 1. C# syntax validation: CI-safe and intentionally not a substitute for a real build.
# ---------------------------------------------------------------------------
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

# World Map GPU/runtime tests need the real game + Unity editor and therefore remain a local
# high-fidelity step, but their C# sources are still part of the CI syntax contract. This catches
# broken regression-test edits on main instead of silently ignoring the worldmap suite.
dotnet run --project "$tmp/SyntaxGuard.csproj" --configuration Release --no-launch-profile --   "$(pwd)/tests/WorldMapRenderIsolation.Tests"

# World Map persistent cache is a binary compatibility boundary. Run its production-code-linked
# regression suite in CI so V2 fallback, V3 round-trip/corruption handling and shutdown flush cannot
# silently regress behind syntax-only validation.
dotnet run --project tests/WorldMapPersistentCache.Tests/WorldMapPersistentCache.Tests.csproj \
  --configuration Release \
  --no-launch-profile

# The orthogonal router is pure System.Numerics code, so unlike GPU presentation it can and should
# execute on hosted CI. This catches short-link, dense-lane and obstacle-detour regressions on main.
dotnet run --project tests/WorldMapRouting.Tests/WorldMapRouting.Tests.csproj \
  --configuration Release \
  --no-launch-profile

# Direction-marker placement is pure screen-space geometry. Run it on hosted CI so the original
# missing-bidirectional-arrow regression cannot return even when the Unity GPU suite is unavailable.
dotnet run --project tests/WorldMapDirection.Tests/WorldMapDirection.Tests.csproj \
  --configuration Release \
  --no-launch-profile

# ---------------------------------------------------------------------------
# 2. Durable MSBuild safety relationships.
#    Check safety/dependency relationships, not exact target/interface names.
# ---------------------------------------------------------------------------
python3 - <<'PY'
from pathlib import Path
import xml.etree.ElementTree as ET

project_path = Path("src/DryCycle.csproj")
targets_path = Path("src/Directory.Build.targets")
for path in (project_path, targets_path):
    if not path.is_file():
        raise SystemExit(f"Build input is missing: {path}")

def parse(path):
    try:
        return ET.parse(path).getroot()
    except ET.ParseError as exc:
        raise SystemExit(f"Invalid MSBuild XML in {path}: {exc}")

backend = parse(project_path)
targets = parse(targets_path)

def local(node):
    return node.tag.rsplit("}", 1)[-1]

def nodes(root, name):
    return [node for node in root.iter() if local(node) == name]

def require(condition, message):
    if not condition:
        raise SystemExit(message)

# Direct deployment-write sanity check: obvious writes to the real game/mod tree must be gated by
# DeployToGame=true. This is deliberately not a complete MSBuild data-flow proof.
deployment_tokens = ("$(GameModOutputDir)", "$(GameModRootDir)", "$(GameModAssetsDir)")
write_tags = {"Copy", "Delete", "MakeDir", "Exec", "MSBuild"}

def deployment_gate(condition):
    normalized = "".join((condition or "").lower().split())
    return "$(deploytogame)" in normalized and "true" in normalized

unsafe = []
for target in nodes(backend, "Target") + nodes(targets, "Target"):
    target_gate = deployment_gate(target.attrib.get("Condition", ""))
    for operation in target.iter():
        if local(operation) not in write_tags:
            continue
        operation_xml = ET.tostring(operation, encoding="unicode")
        if not any(token in operation_xml for token in deployment_tokens):
            continue
        if not target_gate and not deployment_gate(operation.attrib.get("Condition", "")):
            unsafe.append(f"{target.attrib.get('Name', '<unnamed>')}:{local(operation)}")
require(not unsafe,
        "Game/mod deployment writes are reachable without DeployToGame=true gating: "
        + ", ".join(unsafe))

# Core must stay independent from optional RWImGui/ImGui frontends. Check declared build
# dependencies rather than source text, which would create false positives from comments/names.
forbidden_dependency_names = {
    "drycycle.devtool.rwimgui",
    "drycycle.aiobservatory.rwimgui",
    "rain-world-imgui-api",
    "imgui.net",
}
bad_dependencies = []
for tag in ("Reference", "PackageReference", "ProjectReference"):
    for node in nodes(backend, tag):
        include = node.attrib.get("Include", "").strip()
        normalized = include.replace("\\", "/").lower()
        if any(name in normalized for name in forbidden_dependency_names):
            bad_dependencies.append(f"{tag}:{include}")
require(not bad_dependencies,
        "DryCycle core project directly declares optional frontend/runtime dependencies: "
        + ", ".join(sorted(bad_dependencies)))

print(
    "Build safety guard passed: syntax, direct deployment-write gating and "
    "optional frontend dependency direction are intact."
)
PY

# A real Rain World-reference build intentionally remains a local/high-fidelity validation step.
# GitHub-hosted CI does not fabricate game assemblies merely to make dotnet build appear successful.
