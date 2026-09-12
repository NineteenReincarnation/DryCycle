using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Small frontend-only bridge to the host operating system's folder chooser.
/// The picker runs off the RWImGui render thread so opening a native dialog never stalls
/// Rain World's UI loop. Consumers only request a picker and poll the completed result.
/// </summary>
internal static class NativeFolderPicker
{
    private const string InitialDirectoryVariable = "DRYCYCLE_FOLDER_PICKER_INITIAL";
    private const string TitleVariable = "DRYCYCLE_FOLDER_PICKER_TITLE";

    private static readonly object Gate = new();
    private static bool pending;
    private static bool completed;
    private static bool cancelled;
    private static string selectedDirectory = string.Empty;
    private static string error = string.Empty;

    internal static bool IsPending
    {
        get
        {
            lock (Gate) return pending;
        }
    }

    internal static bool Request(string initialDirectory, string title)
    {
        RuntimePlatform platform = Application.platform;
        lock (Gate)
        {
            if (pending) return false;
            pending = true;
            completed = false;
            cancelled = false;
            selectedDirectory = string.Empty;
            error = string.Empty;
        }

        string initial = initialDirectory ?? string.Empty;
        string prompt = string.IsNullOrWhiteSpace(title) ? "Select folder" : title.Trim();
        Thread worker = new(() => Run(platform, initial, prompt))
        {
            IsBackground = true,
            Name = "DryCycle Native Folder Picker"
        };
        worker.Start();
        return true;
    }

    internal static bool TryConsume(out string directory, out string pickerError, out bool wasCancelled)
    {
        lock (Gate)
        {
            if (!completed)
            {
                directory = string.Empty;
                pickerError = string.Empty;
                wasCancelled = false;
                return false;
            }

            directory = selectedDirectory;
            pickerError = error;
            wasCancelled = cancelled;
            completed = false;
            selectedDirectory = string.Empty;
            error = string.Empty;
            cancelled = false;
            return true;
        }
    }

    private static void Run(RuntimePlatform platform, string initialDirectory, string title)
    {
        try
        {
            PickerResult result = platform switch
            {
                RuntimePlatform.WindowsPlayer or RuntimePlatform.WindowsEditor => PickWindows(initialDirectory, title),
                RuntimePlatform.OSXPlayer or RuntimePlatform.OSXEditor => PickMac(title),
                RuntimePlatform.LinuxPlayer or RuntimePlatform.LinuxEditor => PickLinux(initialDirectory, title),
                _ => PickerResult.Failure("Unsupported platform: " + platform)
            };
            Complete(result);
        }
        catch (Exception ex)
        {
            Complete(PickerResult.Failure(ex.Message));
        }
    }

    private static PickerResult PickWindows(string initialDirectory, string title)
    {
        const string script =
            "$ErrorActionPreference = 'Stop';" +
            "Add-Type -AssemblyName System.Windows.Forms;" +
            "$d = New-Object System.Windows.Forms.FolderBrowserDialog;" +
            "$t = [Environment]::GetEnvironmentVariable('" + TitleVariable + "');" +
            "if ($t) { $d.Description = $t };" +
            "$d.ShowNewFolderButton = $true;" +
            "$p = [Environment]::GetEnvironmentVariable('" + InitialDirectoryVariable + "');" +
            "if ($p -and (Test-Path -LiteralPath $p)) { $d.SelectedPath = $p };" +
            "if ($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { [Console]::Out.Write($d.SelectedPath) };" +
            "$d.Dispose();";

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        string[] executables = { "powershell.exe", "pwsh.exe", "powershell", "pwsh" };
        Exception lastStartError = null;
        for (int i = 0; i < executables.Length; i++)
        {
            try
            {
                return RunPickerProcess(
                    executables[i],
                    "-NoProfile -STA -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                    initialDirectory,
                    title,
                    nonZeroMeansCancel: false);
            }
            catch (Win32Exception ex)
            {
                lastStartError = ex;
            }
        }

        return PickerResult.Failure(
            "Windows folder picker could not start PowerShell" +
            (lastStartError == null ? "." : ": " + lastStartError.Message));
    }

    private static PickerResult PickMac(string title)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "drycycle-folder-picker-" + Guid.NewGuid().ToString("N") + ".applescript");
        const string script =
            "set pickerTitle to system attribute \"" + TitleVariable + "\"\n" +
            "try\n" +
            "    set pickedFolder to choose folder with prompt pickerTitle\n" +
            "    return POSIX path of pickedFolder\n" +
            "on error number -128\n" +
            "    return \"\"\n" +
            "end try\n";

        try
        {
            File.WriteAllText(tempPath, script, new UTF8Encoding(false));
            return RunPickerProcess(
                "/usr/bin/osascript",
                QuoteArgument(tempPath),
                string.Empty,
                title,
                nonZeroMeansCancel: false);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // Temporary picker scripts are best-effort cleanup only.
            }
        }
    }

    private static PickerResult PickLinux(string initialDirectory, string title)
    {
        LinuxPicker[] pickers =
        {
            new("zenity", BuildZenityArguments(initialDirectory, title)),
            new("kdialog", BuildKDialogArguments(initialDirectory, title)),
            new("yad", BuildYadArguments(initialDirectory, title))
        };

        Exception lastStartError = null;
        for (int i = 0; i < pickers.Length; i++)
        {
            try
            {
                return RunPickerProcess(
                    pickers[i].Executable,
                    pickers[i].Arguments,
                    initialDirectory,
                    title,
                    nonZeroMeansCancel: true);
            }
            catch (Win32Exception ex)
            {
                lastStartError = ex;
            }
        }

        return PickerResult.Failure(
            "No supported Linux folder picker was found (zenity, kdialog or yad)" +
            (lastStartError == null ? "." : ": " + lastStartError.Message));
    }

    private static PickerResult RunPickerProcess(
        string executable,
        string arguments,
        string initialDirectory,
        string title,
        bool nonZeroMeansCancel)
    {
        ProcessStartInfo start = new()
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.EnvironmentVariables[InitialDirectoryVariable] = initialDirectory ?? string.Empty;
        start.EnvironmentVariables[TitleVariable] = title ?? string.Empty;

        using Process process = new() { StartInfo = start };
        if (!process.Start())
            throw new Win32Exception("Unable to start " + executable + ".");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        string path = (stdout ?? string.Empty).Trim();
        if (process.ExitCode == 0)
            return string.IsNullOrWhiteSpace(path) ? PickerResult.Cancelled() : PickerResult.Success(path);

        if (nonZeroMeansCancel)
            return PickerResult.Cancelled();

        string detail = string.IsNullOrWhiteSpace(stderr)
            ? executable + " exited with code " + process.ExitCode + "."
            : stderr.Trim();
        return PickerResult.Failure(detail);
    }

    private static string BuildZenityArguments(string initialDirectory, string title)
    {
        StringBuilder args = new("--file-selection --directory");
        if (!string.IsNullOrWhiteSpace(title))
            args.Append(" --title=").Append(QuoteArgument(title));
        if (Directory.Exists(initialDirectory))
        {
            string path = initialDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            args.Append(" --filename=").Append(QuoteArgument(path));
        }
        return args.ToString();
    }

    private static string BuildKDialogArguments(string initialDirectory, string title)
    {
        StringBuilder args = new("--getexistingdirectory");
        if (Directory.Exists(initialDirectory))
            args.Append(' ').Append(QuoteArgument(initialDirectory));
        if (!string.IsNullOrWhiteSpace(title))
            args.Append(" --title ").Append(QuoteArgument(title));
        return args.ToString();
    }

    private static string BuildYadArguments(string initialDirectory, string title)
    {
        StringBuilder args = new("--file-selection --directory");
        if (!string.IsNullOrWhiteSpace(title))
            args.Append(" --title=").Append(QuoteArgument(title));
        if (Directory.Exists(initialDirectory))
            args.Append(" --filename=").Append(QuoteArgument(initialDirectory));
        return args.ToString();
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static void Complete(PickerResult result)
    {
        lock (Gate)
        {
            selectedDirectory = result.Directory ?? string.Empty;
            error = result.Error ?? string.Empty;
            cancelled = result.WasCancelled;
            pending = false;
            completed = true;
        }
    }

    private readonly struct LinuxPicker
    {
        internal LinuxPicker(string executable, string arguments)
        {
            Executable = executable;
            Arguments = arguments;
        }

        internal string Executable { get; }
        internal string Arguments { get; }
    }

    private readonly struct PickerResult
    {
        private PickerResult(string directory, string error, bool wasCancelled)
        {
            Directory = directory;
            Error = error;
            WasCancelled = wasCancelled;
        }

        internal string Directory { get; }
        internal string Error { get; }
        internal bool WasCancelled { get; }

        internal static PickerResult Success(string directory) => new(directory, string.Empty, false);
        internal static PickerResult Failure(string error) => new(string.Empty, error, false);
        internal static PickerResult Cancelled() => new(string.Empty, string.Empty, true);
    }
}
