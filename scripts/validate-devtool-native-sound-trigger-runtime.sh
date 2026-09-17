#!/usr/bin/env bash
set -euo pipefail

root="src/DevUI/DevTool"
sound_runtime="$root/Sound/SoundEditorRuntime.cs"
trigger_runtime="$root/Triggers/TriggerEditorRuntime.cs"
trigger_catalog="$root/Triggers/TriggerSongCatalog.cs"
selection_bridge="$root/Compatibility/LegacySoundTriggerSelectionBridge.cs"
sound_hydrator="$root/Compatibility/LegacySoundPageHydrator.cs"
trigger_hydrator="$root/Compatibility/LegacyTriggerPageHydrator.cs"
top_level_pump="$root/Compatibility/LegacyNativeSoundTriggerTopLevelPump.cs"
scheduler="$root/Compatibility/NativeSoundTriggerDevUiScheduler.cs"
native_tool_scheduler="$root/Core/NativeToolScheduler.cs"
runtime="$root/Core/DevToolRuntime.cs"
quiescence="$root/Compatibility/LegacyDevUiQuiescenceController.cs"
removed_sound_ctor="$root/Compatibility/SoundPageConstructorOptimization.cs"
coordinator="$root/Core/DevToolSubsystemCoordinator.cs"
misc_runtime="src/Misc/MiscRuntime.cs"

for file in "$sound_runtime" "$trigger_runtime" "$trigger_catalog" "$selection_bridge" \
            "$sound_hydrator" "$trigger_hydrator" "$top_level_pump" "$scheduler" \
            "$native_tool_scheduler" "$runtime" "$quiescence" "$coordinator" "$misc_runtime"; do
  if [[ ! -f "$file" ]]; then
    echo "Native Sound/Trigger runtime contract file missing: $file" >&2
    exit 1
  fi
done

if [[ -e "$removed_sound_ctor" ]]; then
  echo "Obsolete SoundPage constructor optimization returned: $removed_sound_ctor" >&2
  exit 1
fi

# Native runtime/presentation is allowed to use Rain World model types, but never DevInterface UI
# nodes/pages as state owners. Legacy selection import lives exclusively in Compatibility.
if grep -Eq '^using DevInterface;|global::DevInterface|is[[:space:]]+SoundPage|as[[:space:]]+SoundPage|SoundPage\.|AmbientSoundPanel[[:space:]]+[A-Za-z_]|DevUINode[[:space:]]+[A-Za-z_]' "$sound_runtime"; then
  echo "Native Sound runtime regained DevInterface presentation ownership." >&2
  exit 1
fi
if grep -Eq '^using DevInterface;|global::DevInterface|is[[:space:]]+TriggersPage|as[[:space:]]+TriggersPage|TriggersPage\.|TriggerPanel[[:space:]]+[A-Za-z_]|DevUINode[[:space:]]+[A-Za-z_]|\.songNames' "$trigger_runtime"; then
  echo "Native Trigger runtime regained DevInterface presentation ownership." >&2
  exit 1
fi

# Trigger metadata discovery must stay headless. It may document the old page in comments, but must
# not import/type-check/instantiate it or publish through page.songNames.
if grep -Eq '^using DevInterface;|global::DevInterface|typeof\(TriggersPage\)|is[[:space:]]+TriggersPage|as[[:space:]]+TriggersPage|TriggersPage\.|\.songNames' "$trigger_catalog"; then
  echo "Trigger song catalogue regained a TriggersPage dependency." >&2
  exit 1
fi
if ! grep -Fq 'TriggerSongCatalog.CurrentNames' "$trigger_runtime"; then
  echo "Trigger presentation no longer consumes the headless song catalogue." >&2
  exit 1
fi

# Exact legacy UI translation is isolated in Compatibility and runs before native queues so a native
# selection command from the same frame stays authoritative.
if ! grep -Fq 'AmbientSoundPanel panel' "$selection_bridge" ||
   ! grep -Fq 'TriggerPanel panel' "$selection_bridge" ||
   ! grep -Fq 'LegacySoundTriggerSelectionBridge.Synchronize(session);' "$coordinator"; then
  echo "Legacy Sound/Trigger selection translation is no longer isolated at the compatibility boundary." >&2
  exit 1
fi

# Headless metadata is projected back into vanilla pages only when compatibility presentation is
# actually active. The native runtime must never read those page fields back.
if ! grep -Fq 'page.fileNames = names;' "$sound_hydrator" ||
   ! grep -Fq 'page.songNames = names;' "$trigger_hydrator" ||
   ! grep -Fq 'LegacySoundPageHydrator.Step(session);' "$coordinator" ||
   ! grep -Fq 'LegacyTriggerPageHydrator.Step(session);' "$coordinator"; then
  echo "Legacy Sound/Trigger metadata hydration is not isolated in Compatibility." >&2
  exit 1
fi

# Runtime lifetime is centralized. A new DevUI session must not reuse stale trigger metadata or page
# projection state from a previous room/editor lifetime.
if ! grep -Fq 'LegacySoundPageHydrator.Reset();' "$coordinator" ||
   ! grep -Fq 'LegacyTriggerPageHydrator.Reset();' "$coordinator" ||
   ! grep -Fq 'TriggerSongCatalog.ResetRuntimeState();' "$coordinator"; then
  echo "Native Sound/Trigger compatibility/catalog lifetime reset is incomplete." >&2
  exit 1
fi

# Native Sound/Trigger workspace selection is virtual: normal rebuilt navigation must hit the native
# scheduler before the generic DevUI SwitchPage fallback. The only stable legacy anchor is exact
# RoomSettingsPage; concrete SoundPage/TriggersPage are compatibility materializations only.
if ! grep -Fq 'internal static bool TryActivate(EditorSession session, EditorToolMode mode)' "$native_tool_scheduler" ||
   ! grep -Fq 'session.Owner.SwitchPage(RoomAnchorPageIndex);' "$native_tool_scheduler" ||
   ! grep -Fq 'MaterializeLegacyTool' "$native_tool_scheduler" ||
   ! grep -Fq 'session.Owner.SwitchPage(mode == EditorToolMode.Sound ? SoundPageIndex : TriggerPageIndex);' "$native_tool_scheduler"; then
  echo "Native Sound/Trigger tool scheduler lost its Room-anchor / legacy-materialization split." >&2
  exit 1
fi
if ! grep -Fq 'page is RoomSettingsPage && page.GetType() == typeof(RoomSettingsPage)' "$native_tool_scheduler"; then
  echo "Native Sound/Trigger scheduler no longer requires an exact RoomSettingsPage anchor." >&2
  exit 1
fi

native_select_line="$(grep -n 'NativeToolScheduler.TryActivate(this, mode)' "$runtime" | head -1 | cut -d: -f1)"
legacy_switch_line="$(grep -n 'Owner.SwitchPage(pageIndex);' "$runtime" | head -1 | cut -d: -f1)"
if [[ -z "$native_select_line" || -z "$legacy_switch_line" || "$native_select_line" -ge "$legacy_switch_line" ]]; then
  echo "EditorSession.SetToolMode no longer gives native Sound/Trigger virtualization priority over legacy SwitchPage." >&2
  exit 1
fi
if ! grep -Fq 'NativeToolScheduler.MaterializeLegacyTool(this, ToolMode, explicitLegacyUi: true)' "$runtime" ||
   ! grep -Fq 'NativeToolScheduler.ReturnToNativeTool(this)' "$runtime"; then
  echo "Per-workspace Legacy UI no longer materializes/retires Sound/Trigger through NativeToolScheduler." >&2
  exit 1
fi
if ! grep -Fq 'NativeToolScheduler.SynchronizePresentationOwnership(session);' "$runtime" ||
   ! grep -Fq 'NativeToolScheduler.SynchronizePresentationOwnership(DevToolRuntime.ActiveSession);' "$scheduler"; then
  echo "Global New UI/Vanilla ownership is not synchronized on the main-thread Sound/Trigger scheduler boundary." >&2
  exit 1
fi

# Native Sound/Trigger own no dedicated derived Page.Update hooks at all. The top-level DevUI
# scheduler consumes virtual workspaces before activePage.Update; explicit Vanilla/Legacy pages fail
# open to the original DevUI lifecycle. This is a structural rule, not an install-then-unsubscribe trick.
if grep -R -n -E --include='*.cs' 'On\.DevInterface\.(SoundPage|TriggersPage)\.Update' "$root" >/tmp/devtool_sound_trigger_page_hooks.txt 2>/dev/null; then
  echo "Dedicated Sound/Trigger Page.Update hook returned:" >&2
  cat /tmp/devtool_sound_trigger_page_hooks.txt >&2
  exit 1
fi
if grep -Eq 'SoundPage_Update|TriggersPage_Update|RetireNativeSoundTriggerPageUpdateHooks' "$quiescence" "$top_level_pump" "$scheduler"; then
  echo "Obsolete Sound/Trigger derived-page update handler/retirement code returned." >&2
  exit 1
fi
if ! grep -Fq 'TryRunNativeSoundTriggerTopLevelUpdate' "$top_level_pump" ||
   ! grep -Fq 'NativeToolScheduler.IsVirtualToolActive(session)' "$top_level_pump" ||
   ! grep -Fq 'TryRunNativeSoundTriggerTopLevelUpdate(self)' "$scheduler"; then
  echo "Native Sound/Trigger top-level update ownership is incomplete." >&2
  exit 1
fi
if ! grep -Fq 'EditorUiModeState.UseVanilla' "$top_level_pump" ||
   ! grep -Fq 'session?.LegacyUiVisible == true' "$top_level_pump"; then
  echo "Visible Vanilla/Legacy Sound/Trigger presentation no longer fails open before native pumping." >&2
  exit 1
fi

# Once page virtualization is active, there is no reason to intercept SoundPage construction or its
# file-list refresh. Native never constructs SoundPage; explicit Vanilla/Legacy must run the complete
# original constructor. Reintroducing either hook would duplicate ownership and add startup risk.
if grep -R -n -E --include='*.cs' 'IL\.DevInterface\.SoundPage\.ctor|On\.DevInterface\.SoundPage\.RefreshFilesPage|SoundPageConstructorOptimization' \
    "$root" "$misc_runtime" >/tmp/devtool_sound_ctor_hits.txt 2>/dev/null; then
  echo "Obsolete SoundPage constructor/file-list interception returned:" >&2
  cat /tmp/devtool_sound_ctor_hits.txt >&2
  exit 1
fi

# Hook order is semantic: quiescence installs generic compatibility hooks first, scheduler becomes the
# inner DevUI layer second, and DevToolRuntime becomes the outer authoritative editor lifecycle last.
q_line="$(grep -n 'LegacyDevUiQuiescenceController.Enable();' "$misc_runtime" | head -1 | cut -d: -f1)"
s_line="$(grep -n 'NativeSoundTriggerDevUiScheduler.Enable();' "$misc_runtime" | head -1 | cut -d: -f1)"
r_line="$(grep -n 'DevToolRuntime.Enable();' "$misc_runtime" | head -1 | cut -d: -f1)"
if [[ -z "$q_line" || -z "$s_line" || -z "$r_line" || "$q_line" -ge "$s_line" || "$s_line" -ge "$r_line" ]]; then
  echo "Sound/Trigger scheduler hook ordering changed; expected quiescence -> scheduler -> DevToolRuntime." >&2
  exit 1
fi

if ! grep -Fq 'NativeSoundTriggerDevUiScheduler.Disable();' "$misc_runtime"; then
  echo "Native Sound/Trigger scheduler lifetime is not paired on shutdown." >&2
  exit 1
fi

echo "DevTool Native Sound/Trigger runtime boundary guard passed."
