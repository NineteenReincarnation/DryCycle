using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using RWIMGUI.API;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Phase 0 verification for the retained World Map renderer.
///
/// This class deliberately does not render the new map yet. It verifies the exact RWImGUI texture
/// contract available in the user's installed build, verifies that a Unity RenderTexture can be
/// created/cleared on the real graphics device, and records the current map CPU baseline so later
/// retained-renderer phases have an objective regression target.
/// </summary>
internal static class WorldMapRetainedV2Phase0
{
    private const int TimingCapacity = 512;
    private const int MinimumSummarySamples = 24;
    private const int BaselineLogInterval = 240;
    private const int MaxLoggedTextureCandidates = 20;

    private readonly struct TimingSummary
    {
        internal TimingSummary(int count, double p50, double p95, double max)
        {
            Count = count;
            P50 = p50;
            P95 = p95;
            Max = max;
        }

        internal int Count { get; }
        internal double P50 { get; }
        internal double P95 { get; }
        internal double Max { get; }
        internal bool Ready => Count >= MinimumSummarySamples;
    }

    private sealed class TimingSeries
    {
        private readonly double[] samples = new double[TimingCapacity];
        private int count;
        private int cursor;
        private int totalSamples;
        private int summarizedAtTotal = -1;
        private TimingSummary cachedSummary;

        internal int TotalSamples => totalSamples;

        internal void Add(double milliseconds)
        {
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0d)
                return;

            samples[cursor] = milliseconds;
            cursor = (cursor + 1) % samples.Length;
            if (count < samples.Length) count++;
            totalSamples++;
        }

        internal TimingSummary GetSummary()
        {
            if (count == 0)
                return default;

            // Sorting a copy every frame would contaminate the benchmark. Refresh summaries only
            // every 16 samples; the displayed values can lag by a fraction of a second.
            if (summarizedAtTotal >= 0 && totalSamples - summarizedAtTotal < 16)
                return cachedSummary;

            double[] copy = new double[count];
            Array.Copy(samples, copy, count);
            Array.Sort(copy);

            int p50Index = Math.Min(count - 1, (int)Math.Floor((count - 1) * 0.50d));
            int p95Index = Math.Min(count - 1, (int)Math.Floor((count - 1) * 0.95d));
            cachedSummary = new TimingSummary(
                count,
                copy[p50Index],
                copy[p95Index],
                copy[count - 1]);
            summarizedAtTotal = totalSamples;
            return cachedSummary;
        }

        internal void Reset()
        {
            Array.Clear(samples, 0, samples.Length);
            count = 0;
            cursor = 0;
            totalSamples = 0;
            summarizedAtTotal = -1;
            cachedSummary = default;
        }
    }

    private sealed class TextureContract
    {
        internal string ApiAssembly = string.Empty;
        internal string ImGuiAssembly = string.Empty;
        internal readonly List<string> DirectUnityTextureCandidates = new();
        internal readonly List<string> NativeImageConsumers = new();
        internal readonly List<string> LifetimeCandidates = new();

        internal string Summary
        {
            get
            {
                if (DirectUnityTextureCandidates.Count > 0)
                    return "Unity texture API " + DirectUnityTextureCandidates.Count;
                if (NativeImageConsumers.Count > 0)
                    return "native-ID image path only";
                return "texture bridge unresolved";
            }
        }
    }

    private static readonly TimingSeries SteadyFrames = new();
    private static readonly TimingSeries NavigationFrames = new();
    private static readonly TimingSeries PanFrames = new();
    private static readonly TimingSeries ZoomFrames = new();
    private static readonly TimingSeries RoomDragFrames = new();

    private static ManualLogSource log;
    private static TextureContract textureContract = new();
    private static bool enabled;
    private static bool renderTextureProbeAttempted;
    private static bool renderTextureProbeSucceeded;
    private static string renderTextureProbeStatus = "RT pending";
    private static int lastLoggedNavigationSamples;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;

        enabled = true;
        log = logger;
        renderTextureProbeAttempted = false;
        renderTextureProbeSucceeded = false;
        renderTextureProbeStatus = "RT pending";
        lastLoggedNavigationSamples = 0;
        SteadyFrames.Reset();
        NavigationFrames.Reset();
        PanFrames.Reset();
        ZoomFrames.Reset();
        RoomDragFrames.Reset();

        ProbeTextureContract();
    }

    internal static void Disable()
    {
        enabled = false;
        log = null;
        textureContract = new TextureContract();
        renderTextureProbeAttempted = false;
        renderTextureProbeSucceeded = false;
        renderTextureProbeStatus = "RT pending";
        lastLoggedNavigationSamples = 0;
        SteadyFrames.Reset();
        NavigationFrames.Reset();
        PanFrames.Reset();
        ZoomFrames.Reset();
        RoomDragFrames.Reset();
    }

    /// <summary>
    /// Runs only from BridgePlugin.Update, never from the RWImGUI Present callback.
    /// </summary>
    internal static void UpdateMainThread()
    {
        if (!enabled || renderTextureProbeAttempted)
            return;

        EditorSession session = DevToolRuntime.ActiveSession;
        if (!DevToolSessionHub.IsCurrentSessionLive ||
            session?.ToolMode != EditorToolMode.Map)
            return;

        renderTextureProbeAttempted = true;
        ProbeRenderTexture();
    }

    internal static long BeginMapFrame() =>
        enabled ? Stopwatch.GetTimestamp() : 0L;

    internal static void EndMapFrame(
        long startedAt,
        bool panning,
        bool zooming,
        bool roomDragging)
    {
        if (!enabled || startedAt == 0L)
            return;

        long elapsedTicks = Stopwatch.GetTimestamp() - startedAt;
        double milliseconds = elapsedTicks * 1000d / Stopwatch.Frequency;

        if (panning || zooming)
            NavigationFrames.Add(milliseconds);
        else if (roomDragging)
            RoomDragFrames.Add(milliseconds);
        else
            SteadyFrames.Add(milliseconds);

        if (panning) PanFrames.Add(milliseconds);
        if (zooming) ZoomFrames.Add(milliseconds);

        MaybeLogBaseline();
    }

    internal static void DrawToolbar()
    {
        if (!enabled) return;

        TimingSummary navigation = NavigationFrames.GetSummary();
        TimingSummary steady = SteadyFrames.GetSummary();

        ImGui.SameLine(0f, 14f);
        if (navigation.Ready)
        {
            ImGui.TextDisabled(
                "· V2 P0 nav " +
                navigation.P50.ToString("0.00") + "/" +
                navigation.P95.ToString("0.00") + "/" +
                navigation.Max.ToString("0.00") + " ms");
        }
        else
        {
            ImGui.TextDisabled(
                "· V2 P0 " +
                navigation.Count + "/" + MinimumSummarySamples);
        }

        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted("World Map Retained V2 · Phase 0");
        DrawTimingLine("steady", steady);
        DrawTimingLine("pan", PanFrames.GetSummary());
        DrawTimingLine("zoom", ZoomFrames.GetSummary());
        DrawTimingLine("room drag", RoomDragFrames.GetSummary());
        ImGui.Separator();
        ImGui.TextUnformatted("RWImGUI: " + textureContract.Summary);
        ImGui.TextUnformatted(renderTextureProbeStatus);
        ImGui.TextDisabled("timings: p50 / p95 / max CPU draw time");
        ImGui.EndTooltip();
    }

    private static void DrawTimingLine(string label, TimingSummary summary)
    {
        if (summary.Count == 0)
        {
            ImGui.TextDisabled(label + ": no samples");
            return;
        }

        ImGui.TextUnformatted(
            label + ": " +
            summary.P50.ToString("0.00") + " / " +
            summary.P95.ToString("0.00") + " / " +
            summary.Max.ToString("0.00") + " ms  n=" + summary.Count);
    }

    private static void ProbeTextureContract()
    {
        TextureContract result = new();

        try
        {
            Assembly apiAssembly = typeof(ImGUIAPI).Assembly;
            Assembly imguiAssembly = typeof(ImGui).Assembly;
            result.ApiAssembly = DescribeAssembly(apiAssembly);
            result.ImGuiAssembly = DescribeAssembly(imguiAssembly);

            HashSet<string> seen = new(StringComparer.Ordinal);
            ProbeAssembly(apiAssembly, result, seen);
            if (!ReferenceEquals(apiAssembly, imguiAssembly))
                ProbeAssembly(imguiAssembly, result, seen);

            textureContract = result;

            log?.LogInfo(
                "World Map Retained V2 Phase 0 texture inventory: api=" +
                result.ApiAssembly + ", imgui=" + result.ImGuiAssembly +
                ", directUnity=" + result.DirectUnityTextureCandidates.Count +
                ", nativeImage=" + result.NativeImageConsumers.Count +
                ", lifetime=" + result.LifetimeCandidates.Count + ".");

            LogCandidates("direct Unity texture", result.DirectUnityTextureCandidates);
            LogCandidates("texture lifetime", result.LifetimeCandidates);

            if (result.DirectUnityTextureCandidates.Count == 0)
            {
                log?.LogWarning(
                    "World Map Retained V2 Phase 0 found no public RWImGUI method that directly accepts " +
                    "UnityEngine.Texture/Texture2D/RenderTexture. The RenderTexture -> ImGui bridge remains " +
                    "unverified; do not guess a native-pointer conversion.");
            }
        }
        catch (Exception error)
        {
            textureContract = result;
            global::DryCycle.StartupDiagnostics.Failure(
                "WorldMapRetainedV2Phase0/TextureContractProbe",
                error);
            log?.LogError(
                "World Map Retained V2 Phase 0 texture contract probe failed: " + error);
        }
    }

    private static void ProbeAssembly(
        Assembly assembly,
        TextureContract result,
        HashSet<string> seen)
    {
        foreach (Type type in GetLoadableTypes(assembly))
        {
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(
                    BindingFlags.Public |
                    BindingFlags.Static |
                    BindingFlags.Instance);
            }
            catch (Exception error)
            {
                log?.LogWarning(
                    "World Map Retained V2 Phase 0 could not inspect " +
                    type.FullName + ": " + error.Message);
                continue;
            }

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                string name = method.Name ?? string.Empty;
                string lower = name.ToLowerInvariant();
                ParameterInfo[] parameters;
                try { parameters = method.GetParameters(); }
                catch { continue; }

                bool acceptsUnityTexture = false;
                bool acceptsNativeTextureId = false;
                for (int p = 0; p < parameters.Length; p++)
                {
                    Type parameterType = parameters[p].ParameterType;
                    if (typeof(Texture).IsAssignableFrom(parameterType))
                        acceptsUnityTexture = true;
                    if (IsNativeTextureIdType(parameterType))
                        acceptsNativeTextureId = true;
                }

                bool textureNamed =
                    lower.Contains("texture") ||
                    lower.Contains("image") ||
                    lower.Contains("shaderresource") ||
                    lower.Contains("srv") ||
                    lower.Contains("native");
                bool lifetimeNamed =
                    lower.Contains("register") ||
                    lower.Contains("unregister") ||
                    lower.Contains("remove") ||
                    lower.Contains("release") ||
                    lower.Contains("destroy");

                if (!textureNamed && !acceptsUnityTexture)
                    continue;

                string signature = DescribeMethod(method);
                if (!seen.Add(signature))
                    continue;

                if (acceptsUnityTexture)
                    result.DirectUnityTextureCandidates.Add(signature);

                if (acceptsNativeTextureId &&
                    (lower.Contains("image") || lower.Contains("texture")))
                    result.NativeImageConsumers.Add(signature);

                if (lifetimeNamed && textureNamed)
                    result.LifetimeCandidates.Add(signature);
            }
        }
    }

    private static Type[] GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            Exception[] loaderErrors = error.LoaderExceptions ?? Array.Empty<Exception>();
            for (int i = 0; i < loaderErrors.Length; i++)
            {
                if (loaderErrors[i] != null)
                    log?.LogWarning(
                        "World Map Retained V2 Phase 0 loader warning: " +
                        loaderErrors[i].Message);
            }

            Type[] source = error.Types ?? Array.Empty<Type>();
            List<Type> valid = new(source.Length);
            for (int i = 0; i < source.Length; i++)
                if (source[i] != null) valid.Add(source[i]);
            return valid.ToArray();
        }
    }

    private static string DescribeAssembly(Assembly assembly)
    {
        AssemblyName name = assembly?.GetName();
        return name == null
            ? "<missing>"
            : name.Name + " " + (name.Version?.ToString() ?? "?");
    }

    private static string DescribeMethod(MethodInfo method) =>
        method.DeclaringType?.Assembly.GetName().Name + "::" +
        method.DeclaringType?.FullName + "." +
        method;

    private static bool IsNativeTextureIdType(Type type) =>
        type == typeof(IntPtr) ||
        type == typeof(UIntPtr) ||
        type == typeof(long) ||
        type == typeof(ulong);

    private static void LogCandidates(string label, List<string> candidates)
    {
        if (candidates == null || candidates.Count == 0)
            return;

        int count = Math.Min(MaxLoggedTextureCandidates, candidates.Count);
        for (int i = 0; i < count; i++)
            log?.LogInfo(
                "World Map Retained V2 Phase 0 " + label + " candidate: " +
                candidates[i]);

        if (candidates.Count > count)
            log?.LogInfo(
                "World Map Retained V2 Phase 0 " + label + ": " +
                (candidates.Count - count) + " additional candidate(s) omitted.");
    }

    private static void ProbeRenderTexture()
    {
        RenderTexture target = null;
        RenderTexture previous = RenderTexture.active;

        try
        {
            target = new RenderTexture(
                32,
                32,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear)
            {
                name = "DryCycle.WorldMapV2.Phase0Probe",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };

            bool createReturned = target.Create();
            if (!createReturned || !target.IsCreated())
                throw new InvalidOperationException(
                    "RenderTexture.Create did not produce a created target.");

            // New RenderTexture contents are not a presentation contract. Explicitly clear the target
            // now because V2 will use the same rule: an uninitialised/undefined surface is never shown.
            RenderTexture.active = target;
            GL.Clear(
                true,
                true,
                new Color(0.16f, 0.18f, 0.22f, 1f));

            IntPtr nativePointer = target.GetNativeTexturePtr();
            renderTextureProbeSucceeded = nativePointer != IntPtr.Zero;
            renderTextureProbeStatus =
                renderTextureProbeSucceeded
                    ? "RT 32x32 OK · native handle non-zero"
                    : "RT created · native handle is zero";

            if (renderTextureProbeSucceeded)
                log?.LogInfo(
                    "World Map Retained V2 Phase 0 RenderTexture probe succeeded: " +
                    target.width + "x" + target.height +
                    ", format=" + target.format +
                    ", native handle is non-zero.");
            else
                log?.LogWarning(
                    "World Map Retained V2 Phase 0 RenderTexture probe created the target but " +
                    "GetNativeTexturePtr returned zero.");
        }
        catch (Exception error)
        {
            renderTextureProbeSucceeded = false;
            renderTextureProbeStatus = "RT probe failed · see log";
            global::DryCycle.StartupDiagnostics.Failure(
                "WorldMapRetainedV2Phase0/RenderTextureProbe",
                error);
            log?.LogError(
                "World Map Retained V2 Phase 0 RenderTexture probe failed: " + error);
        }
        finally
        {
            RenderTexture.active = previous;
            if (target != null)
            {
                try
                {
                    if (target.IsCreated()) target.Release();
                    UnityEngine.Object.Destroy(target);
                }
                catch (Exception cleanupError)
                {
                    log?.LogWarning(
                        "World Map Retained V2 Phase 0 RenderTexture probe cleanup failed: " +
                        cleanupError);
                }
            }
        }
    }

    private static void MaybeLogBaseline()
    {
        int navigationSamples = NavigationFrames.TotalSamples;
        if (navigationSamples < MinimumSummarySamples ||
            navigationSamples - lastLoggedNavigationSamples < BaselineLogInterval)
            return;

        lastLoggedNavigationSamples = navigationSamples;
        TimingSummary navigation = NavigationFrames.GetSummary();
        TimingSummary pan = PanFrames.GetSummary();
        TimingSummary zoom = ZoomFrames.GetSummary();
        TimingSummary steady = SteadyFrames.GetSummary();

        log?.LogInfo(
            "World Map Retained V2 Phase 0 baseline: " +
            "nav=" + FormatSummary(navigation) +
            ", pan=" + FormatSummary(pan) +
            ", zoom=" + FormatSummary(zoom) +
            ", steady=" + FormatSummary(steady) +
            ", texture=" + textureContract.Summary +
            ", " + renderTextureProbeStatus + ".");
    }

    private static string FormatSummary(TimingSummary summary) =>
        summary.Count == 0
            ? "n/a"
            : summary.P50.ToString("0.00") + "/" +
              summary.P95.ToString("0.00") + "/" +
              summary.Max.ToString("0.00") +
              "ms(n=" + summary.Count + ")";
}
