using System;
using System.IO;
using DryCycle.DevUI.DevTool.Map.Cartography;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Cartography-owned file browser. It deliberately stays inside RWImGui so image import keeps the
/// same visual language as the rest of the developer UI and does not depend on an OS-native dialog.
/// </summary>
internal static partial class CartographyView
{
    private static bool imageBrowserOpen;
    private static bool imageBrowserJustOpened;
    private static bool imageBrowserNeedsRefresh;
    private static string imageBrowserDirectory = string.Empty;
    private static string imageBrowserSelectedPath = string.Empty;
    private static string imageBrowserSearch = string.Empty;
    private static string imageBrowserError = string.Empty;
    private static string[] imageBrowserDirectories = Array.Empty<string>();
    private static string[] imageBrowserFiles = Array.Empty<string>();
    private static DriveInfo[] imageBrowserDrives = Array.Empty<DriveInfo>();

    private static void OpenImageBrowser()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
            desktop = Directory.GetCurrentDirectory();

        imageBrowserDirectory = desktop;
        imageBrowserSelectedPath = string.Empty;
        imageBrowserSearch = string.Empty;
        imageBrowserError = string.Empty;
        imageBrowserNeedsRefresh = true;
        imageBrowserJustOpened = true;
        imageBrowserOpen = true;
    }

    private static void DrawImageBrowser(CartographyPresentation snapshot)
    {
        if (!imageBrowserOpen)
            return;

        if (imageBrowserNeedsRefresh)
            RefreshImageBrowser();

        Num.Vector2 display = ImGui.GetIO().DisplaySize;
        float width = Math.Min(820f, Math.Max(520f, display.X - 48f));
        float height = Math.Min(580f, Math.Max(380f, display.Y - 48f));

        if (imageBrowserJustOpened)
        {
            ImGui.SetNextWindowPos(
                new Num.Vector2(
                    Math.Max(16f, (display.X - width) * 0.5f),
                    Math.Max(16f, (display.Y - height) * 0.5f)),
                ImGuiCond.Appearing);
            ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.Appearing);
            imageBrowserJustOpened = false;
        }
        else
        {
            ImGui.SetNextWindowSizeConstraints(
                new Num.Vector2(520f, 380f),
                new Num.Vector2(Math.Max(520f, display.X - 24f), Math.Max(380f, display.Y - 24f)));
        }

        bool open = imageBrowserOpen;
        if (!ImGui.Begin(
                T("图片资源管理器###CartographyImageBrowser", "Image Browser###CartographyImageBrowser"),
                ref open,
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            imageBrowserOpen = open;
            return;
        }

        DrawImageBrowserToolbar();

        if (!string.IsNullOrEmpty(imageBrowserError))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(1f, .48f, .36f, 1f));
            ImGui.TextWrapped(imageBrowserError);
            ImGui.PopStyleColor();
        }

        ImGui.Separator();

        float footerHeight = ImGui.GetFrameHeightWithSpacing() * 2.4f;
        float contentHeight = Math.Max(180f, ImGui.GetContentRegionAvail().Y - footerHeight);
        float sidebarWidth = Math.Min(190f, Math.Max(150f, ImGui.GetContentRegionAvail().X * .22f));

        if (ImGui.BeginChild(
                "##CartographyImageBrowserPlaces",
                new Num.Vector2(sidebarWidth, contentHeight),
                ImGuiChildFlags.Borders))
        {
            DevToolWidgets.PaneTitle(T("位置", "LOCATIONS"));

            if (ImGui.Selectable(T("桌面", "Desktop")))
                NavigateImageBrowser(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            if (ImGui.Selectable(T("用户目录", "User folder")))
                NavigateImageBrowser(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            if (imageBrowserDrives.Length > 0)
            {
                ImGui.Separator();
                DevToolWidgets.MutedText(T("磁盘", "Drives"));
                for (int i = 0; i < imageBrowserDrives.Length; i++)
                {
                    DriveInfo drive = imageBrowserDrives[i];
                    string label;
                    try
                    {
                        label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                            ? drive.Name
                            : drive.Name + "  " + drive.VolumeLabel;
                    }
                    catch
                    {
                        label = drive.Name;
                    }

                    if (ImGui.Selectable(label + "##Drive" + i))
                        NavigateImageBrowser(drive.RootDirectory.FullName);
                }
            }
        }
        ImGui.EndChild();

        ImGui.SameLine(0f, 8f);

        if (ImGui.BeginChild(
                "##CartographyImageBrowserFiles",
                new Num.Vector2(0f, contentHeight),
                ImGuiChildFlags.Borders))
        {
            DrawImageBrowserEntries(snapshot);
        }
        ImGui.EndChild();

        ImGui.Separator();

        string selectedName = string.IsNullOrEmpty(imageBrowserSelectedPath)
            ? T("未选择图片", "No image selected")
            : Path.GetFileName(imageBrowserSelectedPath);
        ImGui.TextUnformatted(selectedName);

        string importLabel = T("导入所选图片", "Import selected image");
        if (string.IsNullOrEmpty(imageBrowserSelectedPath))
            ImGui.BeginDisabled();
        if (ImGui.Button(importLabel))
            ImportImageBrowserSelection(snapshot);
        if (string.IsNullOrEmpty(imageBrowserSelectedPath))
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button(T("取消", "Cancel")))
            open = false;

        ImGui.End();

        imageBrowserOpen = open;
        if (!imageBrowserOpen)
            imageBrowserSelectedPath = string.Empty;
    }

    private static void DrawImageBrowserToolbar()
    {
        if (ImGui.Button(T("桌面", "Desktop")))
            NavigateImageBrowser(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

        ImGui.SameLine();
        if (ImGui.Button(T("上一级", "Up")))
        {
            try
            {
                DirectoryInfo parent = Directory.GetParent(imageBrowserDirectory);
                if (parent != null)
                    NavigateImageBrowser(parent.FullName);
            }
            catch (Exception exception)
            {
                imageBrowserError = exception.Message;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button(T("刷新", "Refresh")))
            imageBrowserNeedsRefresh = true;

        ImGui.SameLine();
        DevToolWidgets.MutedText(imageBrowserDirectory);

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint(
                "##CartographyImageBrowserSearch",
                T("搜索当前文件夹中的图片", "Search images in this folder"),
                ref imageBrowserSearch,
                256))
        {
            imageBrowserSelectedPath = string.Empty;
        }
    }

    private static void DrawImageBrowserEntries(CartographyPresentation snapshot)
    {
        string search = imageBrowserSearch?.Trim() ?? string.Empty;

        for (int i = 0; i < imageBrowserDirectories.Length; i++)
        {
            string directory = imageBrowserDirectories[i];
            string name = SafeFileName(directory);
            if (search.Length > 0 &&
                name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            string label = T("[文件夹] ", "[Folder] ") + name + "##Dir" + i;
            bool clicked = ImGui.Selectable(label, false);
            bool openFolder =
                ImGui.IsItemHovered() &&
                ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
            if (clicked && openFolder)
            {
                NavigateImageBrowser(directory);
                return;
            }
        }

        for (int i = 0; i < imageBrowserFiles.Length; i++)
        {
            string file = imageBrowserFiles[i];
            string name = Path.GetFileName(file);
            if (search.Length > 0 &&
                name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            bool selected = string.Equals(
                imageBrowserSelectedPath,
                file,
                StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(name + "##File" + i, selected))
                imageBrowserSelectedPath = file;

            if (ImGui.IsItemHovered() &&
                ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                imageBrowserSelectedPath = file;
                ImportImageBrowserSelection(snapshot);
                return;
            }
        }

        if (imageBrowserDirectories.Length == 0 &&
            imageBrowserFiles.Length == 0 &&
            string.IsNullOrEmpty(imageBrowserError))
        {
            DevToolWidgets.MutedText(T("这个文件夹里没有可导入的图片。", "No importable images in this folder."), true);
        }
    }

    private static void NavigateImageBrowser(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;

        try
        {
            string fullPath = Path.GetFullPath(directory);
            if (!Directory.Exists(fullPath))
                return;

            imageBrowserDirectory = fullPath;
            imageBrowserSelectedPath = string.Empty;
            imageBrowserSearch = string.Empty;
            imageBrowserError = string.Empty;
            imageBrowserNeedsRefresh = true;
        }
        catch (Exception exception)
        {
            imageBrowserError = exception.Message;
        }
    }

    private static void RefreshImageBrowser()
    {
        imageBrowserNeedsRefresh = false;
        imageBrowserError = string.Empty;

        try
        {
            string[] directories = Directory.GetDirectories(imageBrowserDirectory);
            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
            imageBrowserDirectories = directories;

            string[] files = Directory.GetFiles(imageBrowserDirectory);
            int count = 0;
            for (int i = 0; i < files.Length; i++)
            {
                if (IsSupportedImage(files[i]))
                    count++;
            }

            string[] images = new string[count];
            int at = 0;
            for (int i = 0; i < files.Length; i++)
            {
                if (IsSupportedImage(files[i]))
                    images[at++] = files[i];
            }

            Array.Sort(images, StringComparer.OrdinalIgnoreCase);
            imageBrowserFiles = images;

            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                imageBrowserDrives = drives ?? Array.Empty<DriveInfo>();
            }
            catch
            {
                imageBrowserDrives = Array.Empty<DriveInfo>();
            }
        }
        catch (Exception exception)
        {
            imageBrowserDirectories = Array.Empty<string>();
            imageBrowserFiles = Array.Empty<string>();
            imageBrowserError = exception.Message;
        }
    }

    private static bool IsSupportedImage(string path)
    {
        string extension = Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
        return extension == ".png" ||
               extension == ".jpg" ||
               extension == ".jpeg" ||
               extension == ".bmp";
    }

    private static string SafeFileName(string path)
    {
        try
        {
            string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? path : name;
        }
        catch
        {
            return path;
        }
    }

    private static void ImportImageBrowserSelection(CartographyPresentation snapshot)
    {
        if (snapshot?.Document == null ||
            snapshot.Scene == null ||
            string.IsNullOrEmpty(imageBrowserSelectedPath) ||
            !File.Exists(imageBrowserSelectedPath))
            return;

        string selected = imageBrowserSelectedPath;
        Send(
            CartographyCommandKind.AddImage,
            command =>
            {
                command.Path = selected;
                command.Item = new CartographyItem
                {
                    Kind = CartographyItemKind.Image,
                    LayerId = activeLayer,
                    X = snapshot.Scene.Bounds.X,
                    Y = snapshot.Scene.Bounds.Y,
                    Color = 0xFFFFFFFF
                };
            });

        imageBrowserOpen = false;
        imageBrowserSelectedPath = string.Empty;
    }
}
