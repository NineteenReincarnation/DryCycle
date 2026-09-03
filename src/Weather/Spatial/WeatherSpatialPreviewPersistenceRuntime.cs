using System;
using System.Reflection;
using DevInterface;

namespace DryCycle.Weather.Spatial;

/// <summary>
/// Keeps an active Weather Zones Preview independent from the lifetime of DevUI.
/// Closing H destroys/recreates the editor, so the recreated panel is synchronized
/// back to the still-running preview instead of silently showing Preview: OFF.
/// </summary>
internal static class WeatherSpatialPreviewPersistenceRuntime
{
    private const string EditorNodeId = "DryCycle_WeatherSpatial";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.DevInterface.MapPage.Update += MapPage_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.DevInterface.MapPage.Update -= MapPage_Update;
        _enabled = false;
    }

    private static void MapPage_Update(
        On.DevInterface.MapPage.orig_Update orig,
        MapPage self)
    {
        orig(self);

        if (self?.world == null)
        {
            return;
        }

        DevUINode editor = FindEditor(self);
        if (editor == null)
        {
            return;
        }

        Type editorType = editor.GetType();
        FieldInfo previewActiveField = editorType.GetField("_previewActive", PrivateInstance);
        FieldInfo previewIntensityField = editorType.GetField("_previewIntensity", PrivateInstance);
        FieldInfo targetIndexField = editorType.GetField("_targetIndex", PrivateInstance);

        bool editorPreviewActive =
            previewActiveField?.GetValue(editor) is bool active && active;

        if (!WeatherSpatialPreview.IsActiveFor(self.world))
        {
            // Normally an explicit Preview OFF already changed the editor field. This
            // also prevents a stale recreated panel from claiming Preview is active.
            if (editorPreviewActive)
            {
                previewActiveField?.SetValue(editor, false);
            }
            return;
        }

        if (!editorPreviewActive)
        {
            // A new WeatherSpatialEditorNode was created after H/DevUI was reopened.
            // Restore all editor-facing state without re-setting the preview itself.
            previewActiveField?.SetValue(editor, true);
            previewIntensityField?.SetValue(editor, WeatherSpatialPreview.Intensity);

            int restoredIndex = FindSavedTargetIndex();
            if (restoredIndex >= 0)
            {
                targetIndexField?.SetValue(editor, restoredIndex);
            }
        }

        // While the original editor is alive, remember its current selected target.
        // This makes a later H reopen restore Family vs exact-weather selection too.
        int currentIndex = ReadTargetIndex(targetIndexField, editor);
        if (currentIndex >= 0 &&
            currentIndex < WeatherSpatialCatalog.AllTargets.Count)
        {
            WeatherSpatialPreview.SetEditorTargetKey(
                WeatherSpatialCatalog.AllTargets[currentIndex].Key);
        }
    }

    private static DevUINode FindEditor(MapPage page)
    {
        if (page?.subNodes == null)
        {
            return null;
        }

        for (int i = 0; i < page.subNodes.Count; i++)
        {
            DevUINode node = page.subNodes[i];
            if (node != null &&
                string.Equals(node.IDstring, EditorNodeId, StringComparison.Ordinal))
            {
                return node;
            }
        }
        return null;
    }

    private static int FindSavedTargetIndex()
    {
        string targetKey = WeatherSpatialPreview.TargetKey;
        if (!string.IsNullOrEmpty(targetKey))
        {
            for (int i = 0; i < WeatherSpatialCatalog.AllTargets.Count; i++)
            {
                if (string.Equals(
                        WeatherSpatialCatalog.AllTargets[i].Key,
                        targetKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        // Fallback for a preview created before target-key persistence existed.
        // Prefer an exact weather target; if none exists, use a family whose preview
        // resolves to the same concrete runtime weather.
        int familyFallback = -1;
        string previewId = WeatherSpatialCatalog.NormalizeId(WeatherSpatialPreview.WeatherId);
        for (int i = 0; i < WeatherSpatialCatalog.AllTargets.Count; i++)
        {
            WeatherSpatialTarget target = WeatherSpatialCatalog.AllTargets[i];
            if (!target.IsFamily &&
                target.Kind == WeatherSpatialPreview.Kind &&
                WeatherSpatialCatalog.NormalizeId(target.WeatherId) == previewId)
            {
                return i;
            }

            if (familyFallback < 0)
            {
                WeatherSpatialMember member = WeatherSpatialCatalog.PreviewFor(target);
                if (member.Kind == WeatherSpatialPreview.Kind &&
                    WeatherSpatialCatalog.NormalizeId(member.Id) == previewId)
                {
                    familyFallback = i;
                }
            }
        }
        return familyFallback;
    }

    private static int ReadTargetIndex(FieldInfo field, DevUINode editor)
    {
        return field?.GetValue(editor) is int index ? index : -1;
    }
}
