using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Windows-native image picker for Cartography authoring. It uses the OS common dialog directly so
/// the editor does not gain a System.Windows.Forms runtime dependency.
/// </summary>
internal static class CartographyNativeFileDialog
{
    private const uint OfnHideReadOnly = 0x00000004;
    private const uint OfnNoChangeDir = 0x00000008;
    private const uint OfnPathMustExist = 0x00000800;
    private const uint OfnFileMustExist = 0x00001000;
    private const uint OfnExplorer = 0x00080000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class OpenFileName
    {
        internal int lStructSize;
        internal IntPtr hwndOwner;
        internal IntPtr hInstance;
        [MarshalAs(UnmanagedType.LPWStr)] internal string lpstrFilter;
        [MarshalAs(UnmanagedType.LPWStr)] internal string lpstrCustomFilter;
        internal int nMaxCustFilter;
        internal int nFilterIndex;
        internal StringBuilder lpstrFile;
        internal int nMaxFile;
        [MarshalAs(UnmanagedType.LPWStr)] internal string lpstrFileTitle;
        internal int nMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] internal string lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] internal string lpstrTitle;
        internal uint Flags;
        internal short nFileOffset;
        internal short nFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] internal string lpstrDefExt;
        internal IntPtr lCustData;
        internal IntPtr lpfnHook;
        [MarshalAs(UnmanagedType.LPWStr)] internal string lpTemplateName;
        internal IntPtr pvReserved;
        internal uint dwReserved;
        internal uint FlagsEx;
    }

    [DllImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName([In, Out] OpenFileName openFileName);

    [DllImport("comdlg32.dll", SetLastError = true)]
    private static extern uint CommDlgExtendedError();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    internal static bool TryPickImage(out string path, out string error)
    {
        path = string.Empty;
        error = string.Empty;

        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            error = "The native Cartography image picker currently requires Windows.";
            return false;
        }

        try
        {
            var buffer = new StringBuilder(32768);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            var dialog = new OpenFileName
            {
                lpstrFile = buffer,
                nMaxFile = buffer.Capacity,
                nFilterIndex = 1,
                lpstrFilter =
                    "PNG / JPG / JPEG / BMP\0*.png;*.jpg;*.jpeg;*.bmp\0\0",
                lpstrInitialDir = desktop,
                lpstrTitle = "Select image / 选择图片",
                lpstrDefExt = "png",
                Flags =
                    OfnExplorer |
                    OfnFileMustExist |
                    OfnPathMustExist |
                    OfnNoChangeDir |
                    OfnHideReadOnly,
                hwndOwner = GetForegroundWindow()
            };
            dialog.lStructSize = Marshal.SizeOf(dialog);

            if (!GetOpenFileName(dialog))
            {
                uint code = CommDlgExtendedError();
                if (code != 0)
                    error = "Image file dialog failed (0x" + code.ToString("X") + ").";
                return false; // code == 0 is an ordinary user cancellation.
            }

            string selected = buffer.ToString();
            if (string.IsNullOrWhiteSpace(selected))
                return false;

            string extension = Path.GetExtension(selected)?.ToLowerInvariant() ?? string.Empty;
            if (extension != ".png" &&
                extension != ".jpg" &&
                extension != ".jpeg" &&
                extension != ".bmp")
            {
                error = "Only PNG, JPG/JPEG, and BMP images can be imported.";
                return false;
            }

            path = selected;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.GetType().Name + ": " + exception.Message;
            return false;
        }
    }
}
