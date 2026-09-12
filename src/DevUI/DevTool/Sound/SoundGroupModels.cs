using System;
using System.Collections.Generic;

namespace DryCycle.DevUI.DevTool.Sound;

public enum EditorSoundSourceKind
{
    Vanilla,
    Downpour,
    Watcher,
    Dlc,
    Mod,
    Missing
}

public sealed class EditorSoundSampleSnapshot
{
    public string Sample { get; init; } = string.Empty;
    public EditorSoundSourceKind SourceKind { get; init; }
    public string SourceName { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
    public bool Available { get; init; }
}

public enum DevToolProblemSeverity
{
    Warning,
    Error
}

public sealed class DevToolProblemSnapshot
{
    public DevToolProblemSeverity Severity { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string GroupId { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
}

public sealed class SoundGroupEntrySnapshot
{
    public string Type { get; init; } = string.Empty;
    public string Sample { get; init; } = string.Empty;
    public float Volume { get; init; }
    public float Pitch { get; init; }
    public float Doppler { get; init; }
    public float Taper { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Radius { get; init; }
    public float DirectionX { get; init; }
    public float DirectionY { get; init; }
    public bool Available { get; init; }
    public EditorSoundSourceKind SourceKind { get; init; }
    public string SourceName { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
}

public sealed class SoundGroupSnapshot
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string SourceName { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public bool IsLocal { get; init; }
    public bool HasMissingResources { get; init; }
    public SoundGroupEntrySnapshot[] Sounds { get; init; } = Array.Empty<SoundGroupEntrySnapshot>();
}

public sealed class SoundGroupLibrarySnapshot
{
    public static readonly SoundGroupLibrarySnapshot Empty = new();

    public string LocalDirectory { get; init; } = string.Empty;
    public string LocalFilePath { get; init; } = string.Empty;
    public SoundGroupSnapshot[] Groups { get; init; } = Array.Empty<SoundGroupSnapshot>();
    public DevToolProblemSnapshot[] Problems { get; init; } = Array.Empty<DevToolProblemSnapshot>();
}

internal sealed class SoundGroupSoundDefinition
{
    internal string Type = string.Empty;
    internal string Sample = string.Empty;
    internal float Volume = 0.5f;
    internal float Pitch = 1f;
    internal float Doppler;
    internal float Taper = 0.1f;
    internal float X = 0.5f;
    internal float Y = 0.5f;
    internal float Radius = 50f;
    internal float DirectionX;
    internal float DirectionY = -1f;
}

internal sealed class SoundGroupDefinition
{
    internal string Id = string.Empty;
    internal string Name = string.Empty;
    internal string SourceName = string.Empty;
    internal string SourcePath = string.Empty;
    internal bool IsLocal;
    internal readonly List<SoundGroupSoundDefinition> Sounds = new();
}
