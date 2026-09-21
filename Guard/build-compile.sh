#!/usr/bin/env bash
set -euo pipefail

# Build / Compile Guard
# Only protect durable build/deployment invariants here. Do not freeze private type names,
# target names, exact MSBuild spelling, or one historical implementation.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# ---------------------------------------------------------------------------
# 1. C# syntax: broad, implementation-neutral, and safe to run in GitHub CI.
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

# ---------------------------------------------------------------------------
# 2. Durable MSBuild relationships.
#    Check safety/dependency relationships, not exact target/interface names.
# ---------------------------------------------------------------------------
python3 - <<'PY'
from pathlib import Path
import xml.etree.ElementTree as ET

project_path = Path("src/DryCycle.csproj")
targets_path = Path("src/Directory.Build.targets")
frontend_project_path = Path("src/DevUI/DevTool/RWImGui/DryCycle.DevTool.RWImGui.csproj")
frontend_dir = frontend_project_path.parent

for path in (project_path, targets_path, frontend_project_path):
    if not path.is_file():
        raise SystemExit(f"Build input is missing: {path}")

def parse(path):
    try:
        return ET.parse(path).getroot()
    except ET.ParseError as exc:
        raise SystemExit(f"Invalid MSBuild XML in {path}: {exc}")

backend = parse(project_path)
targets = parse(targets_path)
frontend = parse(frontend_project_path)

def local(node):
    return node.tag.rsplit("}", 1)[-1]

def nodes(root, name):
    return [node for node in root.iter() if local(node) == name]

def values(root, name):
    return [(node.text or "").strip() for node in nodes(root, name)]

def require(condition, message):
    if not condition:
        raise SystemExit(message)

# Compatibility baseline, deliberately treated as upgradeable rather than a permanent architecture
# law. Backend and optional frontend run in the same Rain World/BepInEx process and must not silently
# drift to different target frameworks. An intentional platform migration may update both together.
backend_tfms = values(backend, "TargetFramework")
frontend_tfms = values(frontend, "TargetFramework")
require(len(backend_tfms) == 1 and len(frontend_tfms) == 1,
        "Backend/frontend must each declare one TargetFramework")
require(backend_tfms[0] == frontend_tfms[0],
        f"Backend/frontend target-framework drift: {backend_tfms[0]} vs {frontend_tfms[0]}")

# Compile-only safety: any MSBuild operation that directly names the game deployment properties must
# be deployment-gated. This intentionally does not care what the target is called.
deployment_tokens = ("$(GameModOutputDir)", "$(GameModRootDir)")
write_tags = {"Copy", "Delete", "MakeDir", "Exec", "MSBuild"}
unsafe = []
for target in nodes(project, "Target") + nodes(targets, "Target"):
    target_xml = ET.tostring(target, encoding="unicode")
    has_deployment_path = any(token in target_xml for token in deployment_tokens)
    has_write = any(local(child) in write_tags for child in target.iter())
    if not (has_deployment_path and has_write):
        continue
    condition = target.attrib.get("Condition", "")
    if "$(DeployToGame)" not in condition or "true" not in condition.lower():
        unsafe.append(target.attrib.get("Name", "<unnamed>"))
require(not unsafe,
        "Targets can write the game/mod deployment tree without an explicit DeployToGame=true gate: "
        + ", ".join(unsafe))

# Core must stay independent from the optional frontend and its ImGui runtime. Namespace/type names
# inside the frontend may evolve; the forbidden dependency direction may not.
backend_sources = Path("src").rglob("*.cs")
forbidden_source_tokens = (
    "DryCycle.DevUI.DevTool.RWImGui",
    "ImGuiNET",
    "rain_world_imgui_api",
)
hits = []
for source in backend_sources:
    if frontend_dir in source.parents or Path("src/DryCycle.AIObservatory.RWImGui") in source.parents:
        continue
    text = source.read_text(encoding="utf-8", errors="ignore")
    if any(token in text for token in forbidden_source_tokens):
        hits.append(str(source))
require(not hits,
        "Core source acquired an optional RWImGui/ImGui frontend dependency: " + ", ".join(hits))

backend_refs = {node.attrib.get("Include", "").strip() for node in nodes(backend, "Reference")}
forbidden_backend_refs = {"DryCycle.DevTool.RWImGui", "rain-world-imgui-api", "ImGui.NET"}
bad_refs = sorted(ref for ref in backend_refs if ref in forbidden_backend_refs)
require(not bad_refs,
        "DryCycle core project directly references optional frontend/runtime assemblies: " + ", ".join(bad_refs))

# Frontend identity is allowed to change. If it consumes backend internals, however, its declared
# AssemblyName and backend InternalsVisibleTo must remain consistent. Do not freeze a literal name.
frontend_names = values(frontend, "AssemblyName")
require(len(frontend_names) == 1 and frontend_names[0],
        "DevTool frontend must declare one non-empty AssemblyName")
frontend_name = frontend_names[0]
friend_names = {node.attrib.get("Include", "").strip() for node in nodes(backend, "InternalsVisibleTo")}
frontend_source_text = "\n".join(
    p.read_text(encoding="utf-8", errors="ignore") for p in frontend_dir.rglob("*.cs")
)
backend_internal_contracts = (
    "IDevToolPage",
    "DevToolSession",
    "DevToolSubsystem",
)
if any(token in frontend_source_text for token in backend_internal_contracts):
    require(frontend_name in friend_names,
            f"Frontend '{frontend_name}' consumes backend internal contracts but backend InternalsVisibleTo does not match it")

# Frontend source inclusion is checked as a relationship. We deliberately do not require SDK default
# globs: explicit Compile lists/removals are allowed, provided they do not accidentally exclude every
# frontend source file.
frontend_cs = list(frontend_dir.rglob("*.cs"))
require(frontend_cs, "DevTool frontend contains no C# source files")
enable_default = [v.lower() for v in values(frontend, "EnableDefaultCompileItems")]
if "false" in enable_default:
    includes = [node.attrib.get("Include", "") for node in nodes(frontend, "Compile") if node.attrib.get("Include")]
    require(includes,
            "DevTool frontend disables default Compile items but declares no explicit Compile inputs")

print(
    "Build relationship guard passed: syntax, compile-only deployment isolation, "
    "backend/frontend framework alignment, optional dependency direction and frontend identity consistency are intact."
)
PY

# A real Rain World-reference build intentionally remains a local/high-fidelity validation step.
# GitHub-hosted CI does not fabricate game assemblies merely to make dotnet build appear successful.
