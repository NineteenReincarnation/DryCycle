using System;
using System.IO;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Small in-game folder browser used by DevTool workflows. It deliberately avoids native OS
/// dialogs so Rain World never loses focus or jumps to the desktop. Directory enumeration is
/// cached and happens only when navigation changes, not every ImGui frame.
/// </summary>
internal static class InGameFolderPicker
{
    private static bool visible;
    private static bool showRoots;
    private static string currentPath = string.Empty;
    private static string pathEdit = string.Empty;
    private static string newFolderName = string.Empty;
    private static string[] entries = Array.Empty<string>();
    private static string error = string.Empty;
    private static bool refreshPending;

    internal static bool Visible => visible;

    internal static void Open(string initialDirectory)
    {
        visible = true;
        error = string.Empty;
        newFolderName = string.Empty;

        string initial = NormalizeExistingDirectory(initialDirectory);
        if (string.IsNullOrEmpty(initial))
        {
            initial = NormalizeExistingDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (string.IsNullOrEmpty(initial))
                initial = NormalizeExistingDirectory(Environment.CurrentDirectory);
        }

        if (string.IsNullOrEmpty(initial))
        {
            showRoots = true;
            currentPath = string.Empty;
            pathEdit = string.Empty;
        }
        else
        {
            showRoots = false;
            currentPath = initial;
            pathEdit = initial;
        }

        refreshPending = true;
    }

    internal static bool Draw(string title, out string selectedPath)
    {
        selectedPath = string.Empty;
        if (!visible) return false;

        Num.Vector2 display = ImGui.GetIO().DisplaySize;
        float width = Math.Min(760f, Math.Max(520f, display.X - 48f));
        float height = Math.Min(600f, Math.Max(420f, display.Y - 48f));
        Num.Vector2 size = new(width, height);
        ImGui.SetNextWindowSize(size, ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(
            new Num.Vector2(Math.Max(12f, (display.X - width) * 0.5f), Math.Max(12f, (display.Y - height) * 0.5f)),
            ImGuiCond.Appearing);
        ImGui.SetNextWindowBgAlpha(0.96f);

        string windowTitle = (string.IsNullOrWhiteSpace(title) ? "Folder" : title) + "###DryCycleInGameFolderPicker";
        if (!ImGui.Begin(windowTitle, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return false;
        }

        if (refreshPending)
            RefreshEntries();

        DrawNavigation();
        ImGui.Spacing();
        DrawPathEditor();
        ImGui.Spacing();

        float footerReserve = 126f;
        float listHeight = Math.Max(160f, ImGui.GetContentRegionAvail().Y - footerReserve);
        if (ImGui.BeginChild("##DryCycleFolderEntries", new Num.Vector2(0f, listHeight), ImGuiChildFlags.Borders))
        {
            if (showRoots)
                DrawRoots();
            else
                DrawDirectories();
        }
        ImGui.EndChild();

        if (!string.IsNullOrWhiteSpace(error))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(1f, 0.58f, 0.36f, 1f));
            ImGui.TextWrapped(error);
            ImGui.PopStyleColor();
        }

        if (!showRoots)
            DrawCreateFolderRow();

        ImGui.Spacing();
        bool canChoose = !showRoots && Directory.Exists(currentPath);
        if (!canChoose) ImGui.BeginDisabled();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("选择当前文件夹", "Select Current Folder"),
                "FolderPickerChoose",
                DevToolButtonTone.Primary))
        {
            selectedPath = currentPath;
            visible = false;
        }
        if (!canChoose) ImGui.EndDisabled();

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("取消", "Cancel"),
                "FolderPickerCancel",
                DevToolButtonTone.Subtle))
        {
            visible = false;
        }

        ImGui.End();
        return !string.IsNullOrWhiteSpace(selectedPath);
    }

    private static void DrawNavigation()
    {
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("根目录", "Roots"),
                "FolderPickerRoots",
                showRoots ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
        {
            showRoots = true;
            currentPath = string.Empty;
            pathEdit = string.Empty;
            refreshPending = true;
            error = string.Empty;
        }

        ImGui.SameLine();
        bool hasParent = !showRoots && !string.IsNullOrWhiteSpace(currentPath);
        if (!hasParent) ImGui.BeginDisabled();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("上一级", "Up"),
                "FolderPickerUp",
                DevToolButtonTone.Subtle))
        {
            NavigateUp();
        }
        if (!hasParent) ImGui.EndDisabled();

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("主目录", "Home"),
                "FolderPickerHome",
                DevToolButtonTone.Subtle))
        {
            string home = NormalizeExistingDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (!string.IsNullOrEmpty(home)) NavigateTo(home);
        }

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("刷新", "Refresh"),
                "FolderPickerRefresh",
                DevToolButtonTone.Subtle))
        {
            refreshPending = true;
            error = string.Empty;
        }
    }

    private static void DrawPathEditor()
    {
        DevToolWidgets.MutedText(DevToolUiSettings.T("路径", "Path"));
        float buttonWidth = DevToolWidgets.ButtonWidth(DevToolUiSettings.T("前往", "Go"));
        float inputWidth = Math.Max(120f, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X);
        ImGui.SetNextItemWidth(inputWidth);
        ImGui.InputText("##DryCycleFolderPath", ref pathEdit, 2048);
        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("前往", "Go"),
                "FolderPickerGo",
                DevToolButtonTone.Normal))
        {
            string path = NormalizeExistingDirectory(pathEdit);
            if (string.IsNullOrEmpty(path))
                error = DevToolUiSettings.T("目录不存在或无法访问。", "Folder does not exist or cannot be accessed.");
            else
                NavigateTo(path);
        }
    }

    private static void DrawRoots()
    {
        if (entries.Length == 0)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有可用的根目录。", "No filesystem roots available."), true);
            return;
        }

        for (int i = 0; i < entries.Length; i++)
        {
            string root = entries[i];
            if (ImGui.Selectable("▣  " + root + "##FolderRoot" + i, false))
                NavigateTo(root);
        }
    }

    private static void DrawDirectories()
    {
        ImGui.TextDisabled(currentPath);
        ImGui.Separator();

        if (entries.Length == 0)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("这个文件夹没有子目录。", "This folder has no subfolders."), true);
            return;
        }

        for (int i = 0; i < entries.Length; i++)
        {
            string path = entries[i];
            string name = SafeFileName(path);
            if (ImGui.Selectable("▸  " + name + "##FolderEntry" + i, false))
                NavigateTo(path);
        }
    }

    private static void DrawCreateFolderRow()
    {
        DevToolWidgets.MutedText(DevToolUiSettings.T("新建文件夹", "New folder"));
        float buttonWidth = DevToolWidgets.ButtonWidth(DevToolUiSettings.T("创建", "Create"));
        float inputWidth = Math.Max(120f, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X);
        ImGui.SetNextItemWidth(inputWidth);
        ImGui.InputText("##DryCycleNewFolderName", ref newFolderName, 512);
        ImGui.SameLine();
        bool canCreate = !string.IsNullOrWhiteSpace(newFolderName) && Directory.Exists(currentPath);
        if (!canCreate) ImGui.BeginDisabled();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("创建", "Create"),
                "FolderPickerCreate",
                DevToolButtonTone.Normal))
        {
            try
            {
                string target = Path.Combine(currentPath, newFolderName.Trim());
                Directory.CreateDirectory(target);
                newFolderName = string.Empty;
                error = string.Empty;
                refreshPending = true;
            }
            catch (Exception ex)
            {
                error = DevToolUiSettings.T("创建文件夹失败：", "Unable to create folder: ") + ex.Message;
            }
        }
        if (!canCreate) ImGui.EndDisabled();
    }

    private static void NavigateUp()
    {
        try
        {
            DirectoryInfo parent = Directory.GetParent(currentPath);
            if (parent == null)
            {
                showRoots = true;
                currentPath = string.Empty;
                pathEdit = string.Empty;
                refreshPending = true;
                error = string.Empty;
                return;
            }
            NavigateTo(parent.FullName);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
    }

    private static void NavigateTo(string path)
    {
        string normalized = NormalizeExistingDirectory(path);
        if (string.IsNullOrEmpty(normalized))
        {
            error = DevToolUiSettings.T("目录不存在或无法访问。", "Folder does not exist or cannot be accessed.");
            return;
        }

        showRoots = false;
        currentPath = normalized;
        pathEdit = normalized;
        error = string.Empty;
        refreshPending = true;
    }

    private static void RefreshEntries()
    {
        refreshPending = false;
        try
        {
            if (showRoots)
            {
                entries = Directory.GetLogicalDrives() ?? Array.Empty<string>();
            }
            else if (Directory.Exists(currentPath))
            {
                entries = Directory.GetDirectories(currentPath, "*", SearchOption.TopDirectoryOnly);
                Array.Sort(entries, CompareDirectoryNames);
            }
            else
            {
                entries = Array.Empty<string>();
                error = DevToolUiSettings.T("当前目录不存在。", "Current folder no longer exists.");
            }
        }
        catch (Exception ex)
        {
            entries = Array.Empty<string>();
            error = DevToolUiSettings.T("读取目录失败：", "Unable to read folder: ") + ex.Message;
        }
    }

    private static string NormalizeExistingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (!Directory.Exists(expanded)) return string.Empty;

            string full = Path.GetFullPath(expanded);
            string root = Path.GetPathRoot(full) ?? string.Empty;
            string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.IsNullOrEmpty(root) &&
                string.Equals(trimmed, trimmedRoot, StringComparison.OrdinalIgnoreCase))
                return root;
            return trimmed;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int CompareDirectoryNames(string a, string b) =>
        string.Compare(SafeFileName(a), SafeFileName(b), StringComparison.OrdinalIgnoreCase);

    private static string SafeFileName(string path)
    {
        try
        {
            string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? path : name;
        }
        catch
        {
            return path ?? string.Empty;
        }
    }
}
