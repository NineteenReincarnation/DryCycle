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

# Trigger is intentionally canvas-first: transient add/scene panels must not regress back into the
# old always-open Browser + Inspector + center Scene layout. Its special controls live in the
# shared NewDevtool top-status window rather than a second page-owned top bar.
python3 - <<'PY'
from pathlib import Path

workspace_path = Path("src/DevUI/DevTool/RWImGui/Pages/Triggers/TriggerEditorWorkspace.cs")
view_path = Path("src/DevUI/DevTool/RWImGui/Pages/Triggers/TriggerEditorView.cs")
gizmo_path = Path("src/DevUI/DevTool/RWImGui/Pages/Objects/Gizmos/NativeSpatialGizmoView.cs")
pages_path = Path("src/DevUI/DevTool/RWImGui/Shell/Pages/BuiltinDevToolPages.cs")
font_path = Path("src/DevUI/DevTool/RWImGui/Settings/Fonts/FontSettingsWindow.cs")

for path in (workspace_path, view_path, gizmo_path, pages_path, font_path):
    if not path.is_file():
        raise SystemExit(f"Trigger canvas-first contract input is missing: {path}")

workspace = workspace_path.read_text(encoding="utf-8")
view = view_path.read_text(encoding="utf-8")
gizmo = gizmo_path.read_text(encoding="utf-8")
pages = pages_path.read_text(encoding="utf-8")
font = font_path.read_text(encoding="utf-8")

start = pages.find("internal sealed class TriggersDevToolPage")
end = pages.find("internal sealed class MapDevToolPage", start)
if start < 0 or end < 0:
    raise SystemExit("Could not isolate TriggersDevToolPage for canvas-first guard.")
trigger_page = pages[start:end]

required_workspace = (
    "DrawTopControls",
    "DrawAddPopup",
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
if "HasTopControls => true" not in trigger_page or "TriggerEditorView.DrawTopControls" not in trigger_page:
    raise SystemExit("Trigger special controls must remain registered with the shared top-status window.")
if "TriggerCanvasCommandBar" in workspace or "DrawCommandBar" in workspace:
    raise SystemExit("Trigger must not recreate a private top command-bar window.")
if 'ImGui.BeginPopup("##TriggerAddPopup")' not in workspace or 'ImGui.CloseCurrentPopup()' not in workspace:
    raise SystemExit("Trigger Add must remain a compact popup menu attached to the shared top bar.")
if 'new Num.Vector2(245f, 0f)' not in workspace or 'new Num.Vector2(390f, 760f)' not in workspace:
    raise SystemExit("Trigger Add popup must remain large enough to read the complete type catalog comfortably.")
if 'new Num.Vector2(285f, 0f)' not in workspace or 'new Num.Vector2(430f, 300f)' not in workspace:
    raise SystemExit("Trigger naming confirmation popup must retain its enlarged readable bounds.")
add_start = workspace.find('private static void DrawAddPopup')
confirm_start = workspace.find('private static void DrawCreateConfirmPopup', add_start)
scene_start = workspace.find('private static void DrawCompactScene', confirm_start)
if add_start < 0 or confirm_start < 0 or scene_start < 0:
    raise SystemExit("Could not isolate Trigger Add/type-confirm popup flow.")
add_popup = workspace[add_start:confirm_start]
confirm_popup = workspace[confirm_start:scene_start]
if '"TriggerCreateName"' in add_popup or 'name: addTriggerName' in add_popup:
    raise SystemExit("Trigger type list must not contain the name field or create the trigger immediately.")
if 'pendingTriggerType = type;' not in add_popup or 'createConfirmPopupRequested = true;' not in add_popup:
    raise SystemExit("Selecting a Trigger type must stage it and open the naming confirmation popup.")
if 'ImGui.BeginPopup("##TriggerCreateConfirmPopup")' not in confirm_popup:
    raise SystemExit("Trigger creation must use a second compact naming confirmation popup.")
if '"TriggerCreateName"' not in confirm_popup or 'name: addTriggerName' not in confirm_popup:
    raise SystemExit("Trigger name must be entered only after selecting a type and passed into atomic creation.")
if 'TriggerCreateConfirm' not in confirm_popup or 'TriggerCreateCancel' not in confirm_popup:
    raise SystemExit("Trigger naming confirmation popup must provide explicit Confirm and Cancel actions.")
if 'DrawAddPalette' in workspace or 'TriggerAddPalette' in workspace or 'TriggerWorkspaceAddSearch' in workspace:
    raise SystemExit("Trigger Add must not regress to an independent/search window.")
if 'snapshot.TriggerTypes ??' not in workspace or 'for (int i = 0; i < types.Length; i++)' not in workspace:
    raise SystemExit("Trigger Add popup must expose the full trigger-type catalog directly.")
if "Select a trigger in the room to edit it" in workspace or "选择场景中的触发器进行编辑" in workspace:
    raise SystemExit("Trigger top controls must not restore the empty-selection instruction text.")
if '"场景 "' in workspace or '"Scene "' in workspace:
    raise SystemExit("Trigger Scene top button must stay label-only and must not append a trigger count.")
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
lower_workspace = workspace.lower()
if "recent" in lower_workspace or "最近使用" in workspace or "最近选中" in workspace:
    raise SystemExit("Trigger workspace must not add recent-use/recent-selection UI.")

print("Trigger canvas-first workspace guard passed.")
PY

# New UI owns one shared top status surface. Rain World's yellow developer label is hidden only
# while the rebuilt frontend is healthy/active and is restored when Vanilla takes ownership.
python3 - <<'PY'
from pathlib import Path

contract = Path("src/DevUI/DevTool/RWImGui/Shell/Pages/IDevToolPageView.cs").read_text(encoding="utf-8")
top = Path("src/DevUI/DevTool/RWImGui/Shell/Windows/DevToolTopStatusWindow.cs").read_text(encoding="utf-8")
label = Path("src/DevUI/DevTool/RWImGui/Shell/VanillaDevToolsLabelVisibility.cs").read_text(encoding="utf-8")
bridge = Path("src/DevUI/DevTool/RWImGui/Shell/BridgePlugin.cs").read_text(encoding="utf-8")

if 'bool HasTopControls { get; }' not in contract or 'void DrawTopControls(EditorPresentationSnapshot snapshot);' not in contract:
    raise SystemExit("DevTool page contract must expose optional shared top controls.")
if 'DevToolTopStatusWindow.Draw' not in bridge:
    raise SystemExit("Every rebuilt page must pass through the shared top-status window.")
if 'snapshot.RoomName' not in top or '" : NewDevtool Active"' not in top:
    raise SystemExit("Shared top status must display the current room and NewDevtool Active.")
if '(display.X - width) * 0.5f' not in top or '6f),' not in top:
    raise SystemExit("Shared top status must remain locked to the physical screen top-center.")
if 'DevToolWidgets.CenteredPrimaryTitle' not in top or '1.56f' not in top:
    raise SystemExit("Shared top status must retain the enlarged primary gold title treatment.")
if 'new Num.Vector2(13f, 9f)' not in top or 'ImGui.SetWindowFontScale(1.14f)' not in top:
    raise SystemExit("Shared top status must keep its enlarged padding and readable action row.")
if 'page?.HasTopControls == true' not in top or 'page.DrawTopControls(snapshot)' not in top:
    raise SystemExit("Shared top status must host the active page's optional special controls.")
if 'devToolsLabel.isVisible' not in label or 'newUiOwnsStatus' not in label:
    raise SystemExit("Vanilla developer-label ownership bridge is missing.")
if 'DevToolFrontend.NativeBackendReady' not in bridge or 'VanillaDevToolsLabelVisibility.Apply' not in bridge:
    raise SystemExit("Vanilla yellow label must be hidden only while the healthy New UI owns developer status.")
if 'VanillaDevToolsLabelVisibility.Restore' not in bridge:
    raise SystemExit("Vanilla yellow label must be restored when the frontend shuts down.")

print("Shared NewDevtool top-status guard passed.")
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
if 'TriggerEditorMetadata.SetName(' not in actions or 'name = null' not in actions:
    raise SystemExit("Trigger creation must accept and persist the author name before the creation snapshot.")
if 'unrecognizedSaveStrings' not in metadata or 'DryCycleName64' not in metadata or 'Convert.ToBase64String' not in metadata:
    raise SystemExit("Trigger names must persist safely through Rain World's forward-compatible trigger metadata.")
if 'private static bool scenePanelOpen = true;' not in workspace:
    raise SystemExit("Trigger Scene must default visible in the canvas-first workspace.")
if 'DrawResidentInspector(snapshot, selected, display);' not in workspace:
    raise SystemExit("Trigger Inspector must remain resident even while Scene is visible.")

scene_list_start = workspace.find('private static void DrawCompactScene')
scene_list_end = workspace.find('private static void DrawResidentInspector', scene_list_start)
if scene_list_start < 0 or scene_list_end < 0:
    raise SystemExit("Could not isolate compact Trigger Scene list.")
scene_list = workspace[scene_list_start:scene_list_end]
if 'DrawSceneCategory(' not in scene_list or 'ImGui.CollapsingHeader(' not in scene_list:
    raise SystemExit("Trigger Scene must remain grouped into collapsible type categories.")
if 'snapshot.TriggerTypes ?? Array.Empty<string>()' not in scene_list:
    raise SystemExit("Trigger Scene category order must follow the Trigger type catalog.")
if 'visibleLabel =\n                string.IsNullOrWhiteSpace(trigger.Name)' not in scene_list:
    raise SystemExit("Trigger Scene category rows must use authored names with a clear unnamed fallback.")
if 'projectedSceneLabels[i]' in scene_list:
    raise SystemExit("Compact Trigger Scene must not regress to the old flat projected-label list.")
if 'No matching triggers.' not in scene_list or '没有匹配的触发器。' not in scene_list:
    raise SystemExit("Grouped Trigger Scene must preserve clear empty-search feedback.")
if 'scenePanelOpen = !scenePanelOpen;' not in workspace:
    raise SystemExit("The top Scene button must continue to show/hide the Trigger scene panel.")
if 'TriggerCompactSceneV2' not in workspace or 'TriggerInspectorResidentV5' not in workspace:
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
if 'ImGuiWindowFlags.AlwaysAutoResize' in workspace[inspector_start:]:
    raise SystemExit("Trigger Inspector must stay manually resizable; AlwaysAutoResize must not return.")
if 'TriggerInspectorResidentV5' not in workspace or '"TriggerInspectorResidentV5"' not in workspace:
    raise SystemExit("Trigger Inspector must use the resizable/persisted V5 geometry identity.")
if 'new Num.Vector2(335f, 260f)' not in workspace:
    raise SystemExit("Trigger Inspector minimum width must remain large enough to show field labels fully.")
if 'Math.Min(\n                380f' not in workspace or 'Math.Max(\n                    350f' not in workspace:
    raise SystemExit("Trigger Inspector compact default width must remain within the readable 350-380 px range.")
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

# Third-party Object compatibility is protocol-based, not a growing allowlist of mod/type names.
# Keep the durable architecture contract executable on hosted CI: ManagedData-like schemas are
# structurally adapted, incomplete objects fall through to one headless Representation host, and
# world-space semantics are compiled without materializing visible vanilla DevUI.
python3 - <<'PY'
from pathlib import Path

managed_path = Path("src/DevUI/DevTool/Objects/ManagedObjectProtocolInspector.cs")
bootstrap_path = Path("src/DevUI/DevTool/Objects/NativeObjectInspectorBootstrap.cs")
presentation_path = Path("src/DevUI/DevTool/Core/EditorObjectPresentationPartial.cs")
host_path = Path("src/DevUI/DevTool/Compatibility/HeadlessObjectCompatibilityHost.cs")
gizmo_path = Path("src/DevUI/DevTool/Compatibility/HeadlessRepresentationGizmoBridge.cs")
coverage_path = Path("src/DevUI/DevTool/Compatibility/DevUiMigrationCoverage.cs")
bridge_path = Path("src/DevUI/DevTool/Compatibility/LegacyDevInterfaceBridge.cs")
actions_path = Path("src/DevUI/DevTool/Commands/EditorActions.cs")
factory_path = Path("src/DevUI/DevTool/Factories/NativePlacedObjectFactory.cs")
runtime_path = Path("src/DevUI/DevTool/Core/DevToolRuntime.cs")
scheduler_path = Path("src/DevUI/DevTool/Core/NativeToolScheduler.cs")
quiescence_path = Path("src/DevUI/DevTool/Compatibility/LegacyDevUiQuiescenceController.cs")
project_path = Path("src/DryCycle.csproj")

for path in (
    managed_path,
    bootstrap_path,
    presentation_path,
    host_path,
    gizmo_path,
    coverage_path,
    bridge_path,
    actions_path,
    factory_path,
    runtime_path,
    scheduler_path,
    quiescence_path,
    project_path,
):
    if not path.is_file():
        raise SystemExit(f"Object compatibility contract input is missing: {path}")

managed = managed_path.read_text(encoding="utf-8")
bootstrap = bootstrap_path.read_text(encoding="utf-8")
presentation = presentation_path.read_text(encoding="utf-8")
host = host_path.read_text(encoding="utf-8")
gizmo = gizmo_path.read_text(encoding="utf-8")
coverage = coverage_path.read_text(encoding="utf-8")
bridge = bridge_path.read_text(encoding="utf-8")
actions = actions_path.read_text(encoding="utf-8")
factory = factory_path.read_text(encoding="utf-8")
runtime = runtime_path.read_text(encoding="utf-8")
scheduler = scheduler_path.read_text(encoding="utf-8")
quiescence = quiescence_path.read_text(encoding="utf-8")
project = project_path.read_text(encoding="utf-8")

def require(condition, message):
    if not condition:
        raise SystemExit(message)

# Optional ecosystem support must stay structural: no compile-time POM/RegionKit dependency and no
# concrete ecosystem type in the protocol adapter.
for forbidden in ("<Reference Include=\"Pom", "<Reference Include=\"RegionKit"):
    require(forbidden not in project, "Optional Object compatibility must not add a hard POM/RegionKit reference.")
for forbidden in ("using Pom", "using RegionKit", "typeof(Pom.", "typeof(RegionKit."):
    require(forbidden not in managed, "Managed Object adapter regressed to a concrete ecosystem type dependency.")

require('"GetValue"' in managed and '"SetValue"' in managed and '"fields"' in managed,
        "Managed Object adapter must continue to recognize the fields + GetValue/SetValue protocol.")
require("IObjectInspectorCoverageProvider" in managed and "IObjectInspectorGizmoAdapter" in managed,
        "Managed Object protocol must publish both coverage and scene-gizmo capabilities.")

managed_pos = bootstrap.find("ManagedObjectProtocolInspector.Instance")
reflection_pos = bootstrap.find("NativeDataReflectionInspector.Instance")
require(managed_pos >= 0 and reflection_pos > managed_pos,
        "Managed Object protocol must run before generic Data reflection.")

require("needsHeadlessControls" in presentation and "needsHeadlessGizmo" in presentation,
        "Third-party Object fallback ownership must distinguish inspector and gizmo requirements.")
require("HeadlessRepresentationGizmoBridge.Capture" in presentation,
        "Incomplete scene geometry or Representation coverage must merge from the headless Representation.")
require("!coverage.GizmoComplete" in presentation and "LegacyUiAvailable" in presentation,
        "A scene-gizmo coverage gap must remain visible as an explicit compatibility escape hatch.")
require("HeadlessObjectCompatibilityHost.HasCompatibilityGap" in presentation,
        "Object presentation must include the Representation tree in compatibility coverage.")
require("HeadlessObjectCompatibilityHost.TryCreateExternalObject" in factory and
        "TryCreateLegacy" not in factory and
        "DevTool_LegacyFactorySandbox" not in factory,
        "External object creation must delegate to the isolated headless compatibility host instead of constructing its own ObjectsPage.")
require("internal static bool TryCreateExternalObject" in host and
        "RestorePlacedObjectList" in host and
        "DryCycle_Headless_Object_Creation" in host,
        "Headless external object creation must keep a rollback-capable isolated creation boundary.")

create_start = host.find("internal static bool TryCreateExternalObject")
create_end = host.find("internal static LegacyControlSnapshot[] Capture", create_start)
require(create_start >= 0 and create_end > create_start,
        "Could not isolate headless external object creation.")
create_external = host[create_start:create_end]
restore_owner_pos = create_external.rfind("session.Owner.activePage = previous;")
cleanup_pos = create_external.rfind("QuarantineVisualTree(page, quarantine);")
require(restore_owner_pos >= 0 and cleanup_pos > restore_owner_pos,
        "Temporary ObjectsPage ownership must be restored before best-effort visual cleanup can throw.")
require("else if (!externalObject)" in presentation and
        "External selections retain one quarantined proof host" in presentation and
        "HeadlessObjectCompatibilityHost.Release(session);" in presentation,
        "Selected external Objects must retain one quarantined proof host for cheap re-audit, while built-in/non-external selections release it.")

capture_start = host.find("internal static LegacyControlSnapshot[] Capture")
capture_end = host.find("internal static bool Run(", capture_start)
require(capture_start >= 0 and capture_end > capture_start, "Could not isolate headless Object capture.")
capture = host[capture_start:capture_end]
require("activePage = state.Page" not in host,
        "Headless Object compatibility must never borrow or swap the visible DevUI activePage, including semantic mutations.")
require("QuarantineContainer" in host and "CachedControls" in host and "ControlsDirty" in host,
        "Headless Object host must retain detached visuals and cached semantic controls.")
require("page.Refresh()" not in host and "Page.Refresh()" not in host,
        "Headless Object host must not revive full ObjectsPage refresh work.")
require("FGameObjectNode" in host and "shouldDestroyOnRemoveFromStage = false" in host,
        "Custom Futile GameObject nodes must remain invisible without destroying their semantic source.")
require("HasCompatibilityGap" in host and "EnsureCompatibilityAudit" in host,
        "Headless Object host must cache a conservative Representation compatibility verdict.")
require("TreeSignature" in host and "ComputeTreeSignature" in host and
        "AuditModel" in host and "AuditTree" in host and
        "ModelAuditIntervalFrames" in host and "TreeAuditIntervalFrames" in host and
        "ReferencedRendererFields" in host and
        "sprite.element?.name" in host and "sprite.shader?.name" in host,
        "Dynamic third-party control/visual trees must use sparse structural audits instead of per-frame rescans.")
require("MarkDirtyAfterMutation" in host,
        "Headless control actions must invalidate dynamic control-tree caches immediately.")
require("Func<PlacedObjectRepresentation, bool>" in host and
        "MutateRepresentation(session, target, action)" in host,
        "Headless semantic control execution must receive an explicit Representation root instead of resolving through activePage.")
require("ResolveLiveRepresentation(owner, target)" in bridge and
        "PlacedObjectRepresentation representation" in bridge,
        "Legacy control bridge must keep live-page wrappers separate from explicit-root semantic overloads.")
require("representation => LegacyDevInterfaceBridge.ClickButton" in actions and
        "representation => LegacyDevInterfaceBridge.SetSlider" in actions and
        "Func<PlacedObjectRepresentation, bool> action" in actions,
        "Editor actions must route headless control writes through the explicit Representation supplied by the host.")
require("HasUnsupportedNodes" in bridge and "IsStructurallyCoveredNode" in bridge and
        "UsesOnlyFrameworkUpdate" in bridge and
        "root " in bridge,
        "Unknown interactive DevUI nodes, including the root Representation, and custom Update() surfaces must remain explicit compatibility gaps instead of being silently accepted.")
require("node is ArrowButton" in bridge and "arrowButton.Clicked()" in bridge,
        "Vanilla/custom ArrowButton controls must remain bridged through their original Clicked() semantic boundary.")
require("SynchronizePollingParent" in bridge and
        "owner.mouseClick = false" in bridge and
        "owner.mouseDown = false" in bridge and
        "owner.draggedNode = null" in bridge,
        "Dynamic legacy Panels must advance deferred state only through an input-quarantined semantic synchronization pass.")

required_gizmo = (
    "HandlePrefix",
    "TryWriteManagedVectorArrayElement",
    "CaptureMultiPointGeometry",
    "CapturePixelGeometry",
    "CaptureVectorCircleGeometry",
    "IsVectorCircleSprite",
    "CaptureLineRendererGeometry",
    "CaptureGameObjectNodeGeometry",
    "HasUnsupportedVisualGeometry",
    "HasUnsupportedSpriteGeometry",
    "HasUnsupportedGameObjectGeometry",
)
for token in required_gizmo:
    require(token in gizmo, f"Headless Object gizmo compiler lost required generic capability: {token}")
require("CaptureSceneTreeBounded" in gizmo and
        "CollectPanelPositionsBounded" in gizmo and
        "int remaining = 4096" in gizmo and
        "HashSet<DevUINode> visited" in gizmo,
        "Headless Object gizmo capture must use bounded/cycle-safe tree traversal.")
require("private static void CaptureChildren(" not in gizmo and
        "private static void CapturePixelGeometry(" not in gizmo and
        "private static void CaptureVectorCircleGeometry(" not in gizmo,
        "Recursive legacy gizmo tree walkers must not return after bounded traversal consolidation.")
require("ResolveNode(representation, path)" in gizmo,
        "Headless Handle edits must resolve the current tree by path instead of caching stale node references.")

require("HeadlessRepresentationGizmoBridge.HasUnsupportedVisualGeometry" in host and
        "unsupportedNodes || unsupportedGeometry" in host,
        "Object compatibility proof must include Representation visual geometry, not only control nodes.")

gap_start = host.find("internal static bool HasCompatibilityGap")
gap_end = host.find("internal static T InspectRepresentation", gap_start)
require(gap_start >= 0 and gap_end > gap_start,
        "Could not isolate the selected-object compatibility proof.")
gap = host[gap_start:gap_end]
require("HasUnsupportedVisualGeometry" in gap and
        "HasUnsupportedNodes" in gap and
        "unsupportedNodes || unsupportedGeometry" in gap,
        "Live Object compatibility proof must evaluate control semantics and visual geometry before authorizing fallback.")

audit_start = host.find("private static void EnsureCompatibilityAudit")
audit_end = host.find("private static bool AuditDue", audit_start)
require(audit_start >= 0 and audit_end > audit_start,
        "Could not isolate the cached headless compatibility audit.")
audit = host[audit_start:audit_end]
require("HasUnsupportedVisualGeometry" in audit and
        "HasUnsupportedNodes" in audit and
        "unsupportedNodes || unsupportedGeometry" in audit,
        "Headless Object compatibility proof must evaluate control semantics and visual geometry.")


require("ObserveHeadlessRepresentation" in coverage,
        "Headless third-party Representation protocols must remain observable by compatibility diagnostics.")
require("DevUiDiagnosticsPolicy.Enabled" in host and "ObserveHeadlessRepresentation" in host,
        "Expensive compatibility auditing must stay diagnostics-only on normal editor frames.")

require("ToolMode == EditorToolMode.Objects" in runtime and
        "!CanOpenObjectLegacyUi()" in runtime and
        "ObjectInspectorRegistry.GetCoverage(selected)" in runtime and
        "!coverage.IsComplete || !coverage.GizmoComplete" in runtime and
        "return HeadlessObjectCompatibilityHost.HasCompatibilityGap(" in runtime,
        "Objects legacy materialization must be re-authorized from current selection/coverage and the reusable selected-object proof host.")

toggle_start = runtime.find("public void ToggleLegacyUi()")
toggle_end = runtime.find("private bool CanOpenObjectLegacyUi()", toggle_start)
require(toggle_start >= 0 and toggle_end > toggle_start,
        "Could not isolate native/legacy presentation switching.")
toggle = runtime[toggle_start:toggle_end]
require("bool transitioned = LegacyUiVisible" in toggle and
        "keeping the current workspace ownership." in toggle and
        "return;" in toggle,
        "Native workspace legacy transitions must fail closed instead of flipping presentation state after scheduler failure.")

deferred_start = runtime.find("internal bool ApplyDeferredViewRestore()")
deferred_end = runtime.find("public void SetToolMode", deferred_start)
require(deferred_start >= 0 and deferred_end > deferred_start,
        "Could not isolate deferred workspace restore.")
deferred = runtime[deferred_start:deferred_end]
require("if (NativeToolScheduler.ShowExplicitLegacyTool(" in deferred and
        "deferred legacy workspace restore failed" in deferred and
        "return false;" in deferred,
        "Deferred native legacy restore must report materialization failure instead of pretending the workspace was restored.")
require("new(typeof(ObjectsPage)" not in quiescence,
        "ObjectsPage must never re-enter the hidden legacy quiescence/pump backend; normal Objects editing is page-less.")

require("DiagnosticsRequireLegacyPage" in scheduler and
        "mode != EditorToolMode.Objects" in scheduler and
        "if (CanOwnNativePresentation(session, mode))" in scheduler and
        "!DiagnosticsRequireLegacyPage(mode)" in scheduler,
        "Objects diagnostics and native ownership must be evaluated against the target tool mode without auto-materializing ObjectsPage.")

require("private static bool MaterializeLegacyToolCore" in scheduler and
        "internal static bool ShowExplicitLegacyTool" in scheduler,
        "Legacy page materialization must expose only an explicit public-in-assembly entry point.")

require("internal static bool SwitchTool" in scheduler and
        "return MaterializeLegacyToolCore(" in scheduler and
        "NativeToolScheduler.SwitchTool(this, mode)" in runtime and
        "if (NativeToolScheduler.Supports(mode))" in runtime and
        "refusing unmanaged legacy page materialization" in runtime,
        "Objects/Sound/Trigger page ownership must remain centralized in NativeToolScheduler with no raw fallback bypass.")
require("NativeToolScheduler.TryActivate(this, mode)" not in runtime,
        "EditorSession must not bypass centralized native workspace page ownership through the retired TryActivate path.")

materialize_core_callers = []
explicit_entry_callers = []
for source_path in Path("src/DevUI/DevTool").rglob("*.cs"):
    source = source_path.read_text(encoding="utf-8")
    if "MaterializeLegacyToolCore(" in source:
        materialize_core_callers.append(source_path.as_posix())
    if "ShowExplicitLegacyTool(" in source:
        explicit_entry_callers.append(source_path.as_posix())

require(materialize_core_callers == ["src/DevUI/DevTool/Core/NativeToolScheduler.cs"],
        "Legacy materialization core escaped scheduler ownership: " + ", ".join(materialize_core_callers))
require(set(explicit_entry_callers).issubset({
            "src/DevUI/DevTool/Core/NativeToolScheduler.cs",
            "src/DevUI/DevTool/Core/DevToolRuntime.cs",
        }),
        "Explicit legacy materialization acquired an unexpected caller: " + ", ".join(explicit_entry_callers))
require("DevUiDiagnosticsPolicy.Enabled" in host and
        "DevUiMigrationCoverage.ObserveHeadlessRepresentation(state.Representation)" in host,
        "Object diagnostics must collect compatibility evidence from the quarantined Representation itself.")

retired_object_host_refs = []
for source_path in Path("src/DevUI/DevTool").rglob("*.cs"):
    source = source_path.read_text(encoding="utf-8")
    if ("LegacyObjectSandbox" in source or
        "Legacy_Object_Sandbox" in source):
        retired_object_host_refs.append(source_path.as_posix())
require(not retired_object_host_refs,
        "Retired visible-legacy Object sandbox name returned to the headless Object pipeline: " +
        ", ".join(retired_object_host_refs))

# Full ObjectsPage construction is forbidden outside the isolated headless host. Visible legacy
# pages must be created through DevUI.SwitchPage under NativeToolScheduler ownership.
import re
direct_objects_page_constructors = []
for source_path in Path("src/DevUI/DevTool").rglob("*.cs"):
    source = source_path.read_text(encoding="utf-8")
    if (re.search(r"\bnew\s+ObjectsPage\s*\(", source) or
        re.search(r"\bObjectsPage\s+\w+\s*=\s*new\s*\(", source)):
        direct_objects_page_constructors.append(source_path.as_posix())

require(direct_objects_page_constructors == [
            "src/DevUI/DevTool/Compatibility/HeadlessObjectCompatibilityHost.cs"
        ],
        "Concrete ObjectsPage construction escaped the isolated headless host: " +
        ", ".join(direct_objects_page_constructors))
require("session.Owner.activePage = state.Page" not in host,
        "The retained selected-object proof host must never become DevUI.activePage.")
require("session.Owner.activePage = page;" in create_external and
        "session.Owner.activePage = previous;" in create_external and
        restore_owner_pos >= 0 and cleanup_pos > restore_owner_pos,
        "Temporary CreateObjRep compatibility may borrow activePage only inside the synchronous external-object creation boundary and must restore ownership before cleanup.")





print("Protocol-based third-party Object compatibility guard passed.")
PY

# Vanilla map config stores canonical/player-map and developer-map positions independently.
# Protect authored developer layouts from being collapsed onto the canonical map coordinates.
python3 - <<'PY'
from pathlib import Path

path = Path("src/DevUI/DevTool/Map/NativeMapAuthoringState.cs")
source = path.read_text(encoding="utf-8")

if "room.InheritedDevPosition = panel.pos;" in source:
    raise SystemExit(
        "Native Map authoring must never import RoomPanel.pos as the developer-map layout; "
        "use RoomPanel.devPos so existing authored layouts are preserved."
    )

required = (
    "room.MapPosition = panel.pos;",
    "room.InheritedDevPosition = panel.devPos;",
    "panel.devPos = room.InheritedDevPosition;",
)
for token in required:
    if token not in source:
        raise SystemExit(
            "Native Map canonical/dev position separation lost required mapping: " + token
        )

print("Native Map canonical/dev position separation guard passed.")
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
