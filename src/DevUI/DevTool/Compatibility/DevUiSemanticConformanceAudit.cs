using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Compatibility;

public sealed class DevUiSemanticFailure
{
    public string PageType { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string RuntimeType { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class DevUiSemanticConformanceSnapshot
{
    public static readonly DevUiSemanticConformanceSnapshot Empty = new();

    public string PageType { get; init; } = string.Empty;
    public int ControlCount { get; init; }
    public int ExecutableRouteCount { get; init; }
    public DevUiSemanticFailure[] Failures { get; init; } = Array.Empty<DevUiSemanticFailure>();
    public int FailureCount => Failures?.Length ?? 0;
    public bool Passed => FailureCount == 0;
}

/// <summary>
/// Non-mutating protocol conformance test for the universal mirror. Runtime coverage answers
/// "did we recognize this node?"; this audit additionally proves that every published semantic
/// control has a valid action route and internally consistent detached data.
/// </summary>
public static class DevUiSemanticConformanceAudit
{
    private const int LoggedFailureLimit = 32;
    private static volatile DevUiSemanticConformanceSnapshot current = DevUiSemanticConformanceSnapshot.Empty;
    private static Page lastPage;
    private static int lastFrame = int.MinValue / 2;
    private static string lastFingerprint = string.Empty;

    public static DevUiSemanticConformanceSnapshot Current => current;

    public static DevUiSemanticConformanceSnapshot Evaluate(UniversalDevUiPresentationSnapshot mirror)
    {
        EditorSession session = DevToolSessionHub.Current;
        Page page = session?.Owner?.activePage;
        if (page == null || mirror == null || !mirror.Available)
        {
            current = DevUiSemanticConformanceSnapshot.Empty;
            lastPage = null;
            return current;
        }

        int frame = Time.frameCount;
        if (ReferenceEquals(page, lastPage) && frame == lastFrame)
            return current;

        lastPage = page;
        lastFrame = frame;

        LegacyControlSnapshot[] controls = mirror.Controls ?? Array.Empty<LegacyControlSnapshot>();
        List<DevUiSemanticFailure> failures = new();
        HashSet<string> paths = new(StringComparer.Ordinal);
        int executable = 0;

        for (int i = 0; i < controls.Length; i++)
        {
            LegacyControlSnapshot control = controls[i];
            if (control == null)
            {
                AddFailure(failures, mirror.PageType, string.Empty, string.Empty, "Unknown", "Null semantic control snapshot");
                continue;
            }

            if (string.IsNullOrWhiteSpace(control.Path))
            {
                AddFailure(failures, mirror.PageType, control.Path, control.RuntimeType, control.Kind.ToString(), "Missing tree path");
                continue;
            }

            if (!paths.Add(control.Path))
            {
                AddFailure(failures, mirror.PageType, control.Path, control.RuntimeType, control.Kind.ToString(), "Duplicate semantic path");
                continue;
            }

            if (!UniversalDevUiActionBridge.CanExecute(page, control))
            {
                AddFailure(failures, mirror.PageType, control.Path, control.RuntimeType, control.Kind.ToString(), "No generic action route for published control");
            }
            else
            {
                executable++;
            }

            ValidateDetachedValue(mirror.PageType, control, failures);
        }

        current = new DevUiSemanticConformanceSnapshot
        {
            PageType = mirror.PageType ?? string.Empty,
            ControlCount = controls.Length,
            ExecutableRouteCount = executable,
            Failures = failures.ToArray()
        };

        LogIfChanged(current);
        return current;
    }

    internal static void Reset()
    {
        current = DevUiSemanticConformanceSnapshot.Empty;
        lastPage = null;
        lastFrame = int.MinValue / 2;
        lastFingerprint = string.Empty;
    }

    private static void ValidateDetachedValue(
        string pageType,
        LegacyControlSnapshot control,
        List<DevUiSemanticFailure> failures)
    {
        switch (control.Kind)
        {
            case LegacyControlKind.Slider:
                if (!Finite(control.Factor) || control.Factor < -0.0001f || control.Factor > 1.0001f)
                    AddFailure(failures, pageType, control.Path, control.RuntimeType, control.Kind.ToString(), "Slider factor is not finite/in [0,1]");
                break;

            case LegacyControlKind.Cycler:
            case LegacyControlKind.ExtEnum:
            case LegacyControlKind.Select:
            case LegacyControlKind.PanelSelect:
            {
                string[] options = control.Options ?? Array.Empty<string>();
                if (options.Length == 0)
                    AddFailure(failures, pageType, control.Path, control.RuntimeType, control.Kind.ToString(), "Choice protocol published without options");
                if (control.SelectedIndex < -1 || control.SelectedIndex >= options.Length)
                    AddFailure(failures, pageType, control.Path, control.RuntimeType, control.Kind.ToString(), "Choice selected index is outside option range");
                break;
            }

            case LegacyControlKind.Direction:
                if (!Finite(control.X) || !Finite(control.Y))
                    AddFailure(failures, pageType, control.Path, control.RuntimeType, control.Kind.ToString(), "Direction contains a non-finite component");
                break;

            case LegacyControlKind.Color:
                if (!Finite(control.X) || !Finite(control.Y) || !Finite(control.Z) || !Finite(control.W))
                    AddFailure(failures, pageType, control.Path, control.RuntimeType, control.Kind.ToString(), "Color contains a non-finite component");
                break;
        }
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static void AddFailure(
        List<DevUiSemanticFailure> failures,
        string pageType,
        string path,
        string runtimeType,
        string protocol,
        string reason)
    {
        failures.Add(new DevUiSemanticFailure
        {
            PageType = pageType ?? string.Empty,
            Path = path ?? string.Empty,
            RuntimeType = runtimeType ?? string.Empty,
            Protocol = protocol ?? string.Empty,
            Reason = reason ?? string.Empty
        });
    }

    private static void LogIfChanged(DevUiSemanticConformanceSnapshot snapshot)
    {
        DevUiSemanticFailure[] failures = snapshot?.Failures ?? Array.Empty<DevUiSemanticFailure>();
        List<string> lines = new(failures.Length);
        for (int i = 0; i < failures.Length; i++)
        {
            DevUiSemanticFailure failure = failures[i];
            lines.Add(failure.Path + "|" + failure.RuntimeType + "|" + failure.Protocol + "|" + failure.Reason);
        }
        lines.Sort(StringComparer.Ordinal);
        string fingerprint = (snapshot?.PageType ?? string.Empty) + "\n" + string.Join("\n", lines);
        if (string.Equals(fingerprint, lastFingerprint, StringComparison.Ordinal)) return;
        lastFingerprint = fingerprint;

        Plugin.Logger?.LogInfo(
            "DevTool semantic conformance: " + (snapshot?.PageType ?? "<none>") + " · " +
            (snapshot?.ControlCount ?? 0) + " mirrored control(s), " +
            (snapshot?.ExecutableRouteCount ?? 0) + " executable route(s), " + failures.Length + " failure(s).");

        int shown = Math.Min(LoggedFailureLimit, failures.Length);
        for (int i = 0; i < shown; i++)
        {
            DevUiSemanticFailure failure = failures[i];
            Plugin.Logger?.LogWarning(
                "[DevUI semantic gap] " + failure.PageType + "|" + failure.Path + "|" +
                failure.RuntimeType + "|" + failure.Protocol + "|" + failure.Reason);
        }
        if (failures.Length > shown)
            Plugin.Logger?.LogWarning(
                "[DevUI semantic gap] " + (failures.Length - shown) + " additional failure(s) omitted from this log batch.");
    }
}
