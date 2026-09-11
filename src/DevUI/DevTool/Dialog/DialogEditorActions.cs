using System;
using System.IO;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Dialog;

internal static class DialogEditorActions
{
    internal static bool SelectDialog(EditorSession session, string path)
    {
        if (session?.Owner?.activePage is not DialogPage page || string.IsNullOrEmpty(path) || !File.Exists(path))
            return false;
        if (!IsKnownDialogPath(page, path)) return false;

        try
        {
            page.ClearDialogs();
            page.shiftY = 0f;
            page.convoLoader.LoadEvents(path);
            DialogEditorState state = DialogEditorStateHub.Get(session);
            if (state != null) state.SelectedPath = path;
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool dialog load failed: " + error.Message);
            return false;
        }
    }

    private static bool IsKnownDialogPath(DialogPage page, string path)
    {
        string[] paths = page?.dialogPanel?.dialogPaths;
        if (paths == null) return false;
        for (int i = 0; i < paths.Length; i++)
            if (string.Equals(paths[i], path, StringComparison.Ordinal)) return true;
        return false;
    }
}
