using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Last-known-good room thumbnail ownership.
///
/// The texture is owned by Rain World/Futile; this resource only retains a reference/UV descriptor.
/// A failed or missing replacement never clears the committed thumbnail.
/// </summary>
internal sealed class RoomThumbnailResource
{
    internal readonly struct Descriptor
    {
        internal Descriptor(
            Texture2D texture,
            Rect uv,
            float pixelWidth,
            float pixelHeight,
            int signature,
            string persistentElementName)
        {
            Texture = texture;
            Uv = uv;
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
            Signature = signature;
            PersistentElementName = persistentElementName ?? string.Empty;
        }

        internal Texture2D Texture { get; }
        internal Rect Uv { get; }
        internal float PixelWidth { get; }
        internal float PixelHeight { get; }
        internal int Signature { get; }
        internal string PersistentElementName { get; }

        internal bool IsValid =>
            Texture != null &&
            PixelWidth > 0f &&
            PixelHeight > 0f &&
            Uv.width != 0f &&
            Uv.height != 0f;
    }

    private Descriptor committed;
    private Descriptor pending;
    private bool hasCommitted;
    private bool hasPending;

    internal bool HasCommitted => hasCommitted && committed.IsValid;
    internal Descriptor Committed => committed;
    internal long Generation { get; private set; }

    internal bool Stage(WorldMapLegacyRoomSourceService.RoomTextureSource source)
    {
        Descriptor descriptor = new(
            source.Texture,
            source.Uv,
            source.Width,
            source.Height,
            source.Signature,
            source.PersistentElementName);

        if (!descriptor.IsValid)
        {
            hasPending = false;
            pending = default;
            return false;
        }

        if (HasCommitted && committed.Signature == descriptor.Signature)
        {
            hasPending = false;
            pending = default;
            return false;
        }

        pending = descriptor;
        hasPending = true;
        return true;
    }

    internal bool CommitPending()
    {
        if (!hasPending || !pending.IsValid)
            return false;

        // Atomic at the resource-object level: the old committed descriptor is retained until the
        // replacement is fully valid, preventing black/missing frames during source changes.
        committed = pending;
        hasCommitted = true;
        pending = default;
        hasPending = false;
        unchecked { Generation++; }
        return true;
    }

    internal void RejectPending()
    {
        pending = default;
        hasPending = false;
    }

    internal void Reset()
    {
        committed = default;
        pending = default;
        hasCommitted = false;
        hasPending = false;
        unchecked { Generation++; }
    }
}
