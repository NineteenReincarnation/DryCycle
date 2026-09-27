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

# Cartography's hosted suite targets the game-era framework, but all of its regression sources can
# still be parsed on hosted CI. Keep new author-state/rendering tests inside the syntax contract.
dotnet run --project "$tmp/SyntaxGuard.csproj" --configuration Release --no-launch-profile -- \
  "$(pwd)/tests/Cartography.Tests"

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

# Hidden/OH map mode has a distinct scheduling contract: background thumbnail preparation continues
# at a bounded cadence while the retained GPU surface must never render. Keep that policy executable
# in hosted CI instead of relying only on Unity/runtime smoke tests.
dotnet run --project tests/WorldMapBackground.Tests/WorldMapBackground.Tests.csproj \
  --configuration Release \
  --no-launch-profile

# Trigger is intentionally canvas-first: transient add/scene/advanced panels must not regress
# back into the old always-open Browser + Inspector + center Scene layout.
python3 - <<'PY'
from pathlib import Path

workspace_path = Path("src/DevUI/DevTool/RWImGui/Pages/Triggers/TriggerEditorWorkspace.cs")
view_path = Path("src/DevUI/DevTool/RWImGui/Pages/Triggers/TriggerEditorView.cs")
gizmo_path = Path("src/DevUI/DevTool/RWImGui/Pages/Objects/Gizmos/NativeSpatialGizmoView.cs")
pages_path = Path("src/DevUI/DevTool/RWImGui/Shell/Pages/BuiltinDevToolPages.cs")
font_path = Path("src/DevUI/DevTool/RWImGui/Settings/Fonts/FontSettingsWindow.cs")
migration_path = Path("src/DevUI/DevTool/RWImGui/Diagnostics/Compatibility/MigrationCoverageWindow.cs")

for path in (workspace_path, view_path, gizmo_path, pages_path, font_path, migration_path):
    if not path.is_file():
        raise SystemExit(f"Trigger canvas-first contract input is missing: {path}")

workspace = workspace_path.read_text(encoding="utf-8")
view = view_path.read_text(encoding="utf-8")
gizmo = gizmo_path.read_text(encoding="utf-8")
pages = pages_path.read_text(encoding="utf-8")
font = font_path.read_text(encoding="utf-8")
migration = migration_path.read_text(encoding="utf-8")

start = pages.find("internal sealed class TriggersDevToolPage")
end = pages.find("internal sealed class MapDevToolPage", start)
if start < 0 or end < 0:
    raise SystemExit("Could not isolate TriggersDevToolPage for canvas-first guard.")
trigger_page = pages[start:end]

required_workspace = (
    "DrawCommandBar",
    "DrawAddPalette",
    "DrawCompactScene",
    "DrawResidentInspector",
)
for token in required_workspace:
    if token not in workspace:
        raise SystemExit(f"Trigger canvas-first workspace lost required surface: {token}")

if "UsesDedicatedWorkspace => true" not in trigger_page:
    raise SystemExit("Trigger page must remain a dedicated canvas-first workspace.")
if "SupportsSceneSurface => false" not in trigger_page:
    raise SystemExit("Trigger page must not restore the large shared center Scene window.")
if "DrawSlugcatCompactSelector" not in view or "TriggerEditorCommandKind.SetSlugcats" not in view:
    raise SystemExit("Trigger slugcat editing must remain compact and preset-capable.")
if "ImGuiSelectableFlags.DontClosePopups" in view:
    raise SystemExit("Current ImGui.NET does not expose DontClosePopups; keep the slugcat combo on compatible widgets.")

trigger_gizmo_marker = "internal static void DrawTriggers"
trigger_gizmo_pos = gizmo.find(trigger_gizmo_marker)
if trigger_gizmo_pos < 0:
    raise SystemExit("Could not isolate Trigger gizmo section.")
sound_gizmo = gizmo[:trigger_gizmo_pos]
trigger_gizmo = gizmo[trigger_gizmo_pos:]
if "RadiusHandleX" in sound_gizmo or "RadiusHandleY" in sound_gizmo:
    raise SystemExit("Trigger radius-handle fields must not be referenced from EditorSoundSnapshot code.")
if "RadiusHandleX" not in trigger_gizmo or "RadiusHandleY" not in trigger_gizmo:
    raise SystemExit("Spot trigger gizmo must retain its authored 360-degree radius-handle direction.")

if "EditorToolMode.Triggers" not in font:
    raise SystemExit("Font diagnostics must stay out of the Trigger workspace.")
if "EditorToolMode.Triggers" not in migration:
    raise SystemExit("Migration diagnostics must stay out of the Trigger workspace.")

lower_workspace = workspace.lower()
if "recent" in lower_workspace or "最近使用" in workspace or "最近选中" in workspace:
    raise SystemExit("Trigger workspace must not add recent-use/recent-selection UI.")

print("Trigger canvas-first workspace guard passed.")
PY

# Named Trigger authoring and the lower-right canvas workspace are one UX contract: names must
# persist through Rain World's unknown-trigger fields, named Spots get clickable world labels, and
# Scene/Inspector remain movable rather than being pinned every frame.
python3 - <<'PY'
from pathlib import Path

runtime = Path("src/DevUI/DevTool/Triggers/TriggerEditorRuntime.cs").read_text(encoding="utf-8")
actions = Path("src/DevUI/DevTool/Triggers/TriggerEditorActions.cs").read_text(encoding="utf-8")
metadata = Path("src/DevUI/DevTool/Triggers/TriggerEditorMetadata.cs").read_text(encoding="utf-8")
workspace = Path("src/DevUI/DevTool/RWImGui/Pages/Triggers/TriggerEditorWorkspace.cs").read_text(encoding="utf-8")
view = Path("src/DevUI/DevTool/RWImGui/Pages/Triggers/TriggerEditorView.cs").read_text(encoding="utf-8")
labels = Path("src/DevUI/DevTool/RWImGui/Pages/Triggers/TriggerSceneLabelView.cs").read_text(encoding="utf-8")
pages = Path("src/DevUI/DevTool/RWImGui/Shell/Pages/BuiltinDevToolPages.cs").read_text(encoding="utf-8")

if 'public string Name { get; init; }' not in runtime or 'Name = TriggerEditorMetadata.GetName(trigger)' not in runtime:
    raise SystemExit("Trigger presentation must expose persisted custom names.")
if 'public const string Name = "name";' not in actions or 'TriggerEditorMetadata.SetName(trigger, value.Text)' not in actions:
    raise SystemExit("Trigger names must remain editable through the normal history-backed SetValue path.")
if 'unrecognizedSaveStrings' not in metadata or 'DryCycleName64' not in metadata or 'Convert.ToBase64String' not in metadata:
    raise SystemExit("Trigger names must persist safely through Rain World's forward-compatible trigger metadata.")
if 'private static bool scenePanelOpen = true;' not in workspace:
    raise SystemExit("Trigger Scene must default visible in the canvas-first workspace.")
if 'DrawResidentInspector(snapshot, selected, display);' not in workspace:
    raise SystemExit("Trigger Inspector must remain resident even while Scene is visible.")
if 'scenePanelOpen = !scenePanelOpen;' not in workspace:
    raise SystemExit("The top Scene button must continue to show/hide the Trigger scene panel.")
if 'TriggerCompactSceneV2' not in workspace or 'TriggerInspectorResidentV3' not in workspace:
    raise SystemExit("Trigger Scene/Inspector must use the movable lower-right window identities.")
scene_start = workspace.find('private static void DrawCompactScene')
scene_end = workspace.find('private static void DrawResidentInspector', scene_start)
if scene_start < 0 or scene_end < 0 or 'ImGuiCond.Always' in workspace[scene_start:scene_end]:
    raise SystemExit("Trigger Scene must not be pinned every frame; developers must be able to move it.")
inspector_start = workspace.find('private static void DrawResidentInspector')
if inspector_start < 0 or 'ImGuiCond.Always' in workspace[inspector_start:]:
    raise SystemExit("Trigger Inspector must not be pinned every frame; developers must be able to move it.")
if 'advancedInspectorOpen' in workspace or 'DrawAdvancedInspector' in workspace or 'TriggerWorkspaceAdvanced' in workspace:
    raise SystemExit("Trigger workspace must remain single-inspector; Advanced mode must not return.")
if 'TriggerEditorKeys.Name' not in view or 'trigger.Name + "  ["' not in view:
    raise SystemExit("Trigger inspector/scene list must expose custom names.")
if 'SPOT AREA' in view or 'DevToolUiSettings.T("区域", "SPOT AREA")' in view:
    raise SystemExit("Trigger Inspector must not restore the redundant Spot area section.")
inspector_start = view.find('internal static void DrawInspector')
library_start = view.find('private static void DrawLibrary', inspector_start)
if inspector_start < 0 or library_start < 0:
    raise SystemExit("Could not isolate Trigger Inspector for simplified-layout guard.")
inspector = view[inspector_start:library_start]
if 'TriggerEditorKeys.Position' in inspector or 'TriggerEditorKeys.Radius' in inspector:
    raise SystemExit("Spot position/radius belong to the world gizmo, not the Trigger Inspector.")
if 'DrawSlugcatCompactSelector(snapshot, selected);' not in inspector:
    raise SystemExit("Trigger Inspector must expose the compact Allowed Slugcats control inline.")
if '蛞蝓猫##TriggerSlugcats' in view or 'Slugcats##TriggerSlugcats' in view:
    raise SystemExit("Allowed Slugcats must not regain a separate collapsible section.")
if 'DeleteTriggerTopRight' not in inspector or 'DevToolButtonTone.Danger' not in inspector:
    raise SystemExit("Delete Trigger must stay as the danger action in the Inspector top-right title row.")
if 'DeleteTriggerTopRight' in inspector[inspector.find('DrawEventEditor'):]:
    raise SystemExit("Delete Trigger must not return to the bottom of the Inspector.")
if 'string.IsNullOrWhiteSpace(trigger.Name)' not in labels or 'TriggerEditorCommandKind.Select' not in labels:
    raise SystemExit("Named spatial triggers must render clickable world labels.")
if 'TriggerSceneLabelView.Draw' not in pages:
    raise SystemExit("Trigger page must project named trigger labels into the room.")

print("Trigger naming and resident workspace guard passed.")
PY

# Rebuilt DevTool layout is a user preference, not session state. Every tracked floating window
# must restore from BepInEx/config on a fresh consumer context, and common shell code must not
# overwrite authored geometry after ImGui.Begin.
python3 - <<'PY'
from pathlib import Path

store_path = Path("src/DevUI/DevTool/RWImGui/Settings/DevToolUserSettingsStore.cs")
settings_path = Path("src/DevUI/DevTool/RWImGui/Settings/DevToolUiSettings.cs")
snap_path = Path("src/DevUI/DevTool/RWImGui/Widgets/Windows/FloatingWindowSnap.cs")
overlay_path = Path("src/DevUI/DevTool/RWImGui/Shell/DevToolOverlay.cs")
bridge_path = Path("src/DevUI/DevTool/RWImGui/Shell/BridgePlugin.cs")
image_browser_path = Path("src/DevUI/DevTool/RWImGui/Pages/Map/CartographyImageBrowser.cs")
creature_picker_path = Path("src/DevUI/DevTool/RWImGui/Pages/World/Creatures/WorldCreatureCatalogPicker.cs")

for path in (store_path, settings_path, snap_path, overlay_path, bridge_path, image_browser_path, creature_picker_path):
    if not path.is_file():
        raise SystemExit(f"Persistent DevTool layout input is missing: {path}")

store = store_path.read_text(encoding="utf-8")
settings = settings_path.read_text(encoding="utf-8")
snap = snap_path.read_text(encoding="utf-8")
overlay = overlay_path.read_text(encoding="utf-8")
bridge = bridge_path.read_text(encoding="utf-8")
image_browser = image_browser_path.read_text(encoding="utf-8")
creature_picker = creature_picker_path.read_text(encoding="utf-8")

if 'Path.Combine(Paths.ConfigPath, FileName)' not in store or 'DryCycle.DevTool.UI.xml' not in store:
    raise SystemExit("DevTool user layout must persist under BepInEx/config.")
if 'TryGetWindow' not in store or 'RememberWindow' not in store or 'FlushIfDue' not in store:
    raise SystemExit("DevTool user settings store lost window restore/save behavior.")
if 'RememberWindowGroups' not in store or 'browserInspectorSplit' not in store:
    raise SystemExit("Window groups and Browser/Inspector splitter must persist with geometry.")
if 'RestorePersistedPreferences' not in settings or 'NotifyPresentationChanged' not in settings:
    raise SystemExit("Language/font/scene presentation preferences must share the persistent user store.")
if 'DevToolUserSettingsStore.TryGetWindow' not in snap or 'ImGui.SetWindowSize' not in snap:
    raise SystemExit("Floating windows must restore persisted size after ImGui.Begin.")
if 'DevToolUserSettingsStore.RememberWindow' not in snap or 'FlushIfDue' not in snap:
    raise SystemExit("Floating windows must publish authored geometry and debounce disk writes.")
if 'BeginContextSession' not in snap or 'ReloadPersistedLayout' not in snap:
    raise SystemExit("A rebuilt RWImGui consumer context must restore persistent layout state.")
if 'browserInspectorDisplayWidth' in overlay:
    raise SystemExit("Editor Panel must not reset width/position merely because a game session or display frame starts.")
if 'RememberBrowserInspectorSplit' not in overlay:
    raise SystemExit("Editor Panel Browser/Inspector splitter preference must persist.")
if 'BridgePlugin/DevToolUserSettingsStore.Load' not in bridge or 'DevToolUserSettingsStore.FlushNow' not in bridge:
    raise SystemExit("DevTool user settings must load at frontend startup and flush during shutdown.")
if 'FloatingWindowSnap.BeginContextSession' not in bridge:
    raise SystemExit("Fresh RWImGui contexts must reapply persisted window geometry.")
if 'FloatingWindowSnap.TrackCurrentWindow(' not in image_browser or '"CartographyImageBrowser"' not in image_browser:
    raise SystemExit("Cartography image browser must participate in persistent window geometry.")
if 'FloatingWindowSnap.TrackCurrentWindow(' not in creature_picker or '"CreatureCatalog:" + popupId' not in creature_picker:
    raise SystemExit("Movable creature catalog popups must participate in persistent window geometry.")

print("Persistent DevTool UI layout guard passed.")
PY

# Cartography solid-terrain mode is shared author state: it must default off, persist with a
# backwards-compatible false fallback, sit beside the room-name toggle, and drive the common room
# raster consumed by canvas and all exporters.
python3 - <<'PY'
from pathlib import Path

document = Path("src/DevUI/DevTool/Map/Cartography/CartographyDocument.cs").read_text(encoding="utf-8")
storage = Path("src/DevUI/DevTool/Map/Cartography/CartographyStorage.cs").read_text(encoding="utf-8")
raster = Path("src/DevUI/DevTool/Map/Cartography/CartographyRoomRasterizer.cs").read_text(encoding="utf-8")
inspector = Path("src/DevUI/DevTool/RWImGui/Pages/Map/CartographyView.Inspector.cs").read_text(encoding="utf-8")
scene = Path("src/DevUI/DevTool/Map/Cartography/CartographyScene.cs").read_text(encoding="utf-8")
editing = Path("src/DevUI/DevTool/Map/Cartography/CartographyEditing.cs").read_text(encoding="utf-8")

if "public bool SolidTerrain = false;" not in document:
    raise SystemExit("Cartography SolidTerrain must default to disabled.")
if 'A("solidTerrain", document.SolidTerrain)' not in storage:
    raise SystemExit("Cartography SolidTerrain must be persisted.")
if 'root.Attribute("solidTerrain") != null && Bool(root, "solidTerrain")' not in storage:
    raise SystemExit("Old cartography documents missing solidTerrain must keep the default false state.")
if "document.SolidTerrain && hasCurves" not in raster or "RasterizeSolidBelowSurfaces" not in raster:
    raise SystemExit("Cartography SolidTerrain must drive the curved-surface-to-floor raster fill.")
if "forcedSolid" not in raster or "(forcedSolid == null || !forcedSolid[i])" not in raster:
    raise SystemExit("Solid curved terrain must survive CropSolid trimming.")
room_names = inspector.find("##AtlasRoomNames")
solid = inspector.find("##AtlasSolidTerrain")
if room_names < 0 or solid < 0 or solid <= room_names:
    raise SystemExit("Solid terrain toggle must remain immediately after room-name visibility controls.")
if "presentation?.Document?.SolidTerrain ?? false" not in inspector:
    raise SystemExit("Cartography SolidTerrain toolbar must present disabled before a document is available.")
if "SetSolidTerrain" not in inspector:
    raise SystemExit("Cartography SolidTerrain must commit through document style state.")
if "next.SolidTerrain = style.SolidTerrain" not in editing:
    raise SystemExit("Cartography Style commands must apply SolidTerrain so the toolbar toggle can turn off and back on.")
if "entry.SolidTerrain != document.SolidTerrain" not in scene:
    raise SystemExit("Cartography room cache must invalidate when SolidTerrain changes.")

print("Cartography solid-terrain contract guard passed.")
PY

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
