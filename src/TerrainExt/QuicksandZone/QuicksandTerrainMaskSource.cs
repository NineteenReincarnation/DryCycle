using System;
using UnityEngine;
using Watcher;

namespace DryCycle.TerrainExt.QuicksandZone;

/// <summary>
/// Watcher terrain shaders are only half of the terrain rendering pipeline.
/// TerrainCurve also publishes a SlopedTerrainMask mesh every frame; sunlight,
/// depth/edge lighting and the terrain compositing pass expect that mask to exist.
/// Quicksand keeps its own non-solid physics, but mirrors that visual mask path.
/// </summary>
internal sealed class QuicksandTerrainMaskSource : UpdatableAndDeletable, IDrawable, INotifyWhenRoomUnloaded
{
    private const int SampleCount = 64;
    private const float TerrainBackOffset = 50f;
    private const float TerrainMaxDepth = 35f;

    private readonly PlacedObject _placedObject;
    private readonly Vector2[] _surface = new Vector2[SampleCount];
    private readonly Vector2[] _bottom = new Vector2[SampleCount];
    private readonly Vector2[] _back = new Vector2[SampleCount];

    private Mesh _mesh;
    private Vector3[] _vertices = Array.Empty<Vector3>();
    private Vector2[] _uvs = Array.Empty<Vector2>();
    private int[] _indices = Array.Empty<int>();
    private bool _disposed;

    internal PlacedObject PlacedObject => _placedObject;

    internal QuicksandTerrainMaskSource(PlacedObject placedObject)
    {
        _placedObject = placedObject;
        _mesh = new Mesh
        {
            name = "DryCycle Quicksand Terrain Mask"
        };

        RefreshMesh();
    }

    public override void Update(bool eu)
    {
        base.Update(eu);

        if (_disposed ||
            room == null ||
            _placedObject == null ||
            !_placedObject.active ||
            _placedObject.data is not QuicksandZoneData)
        {
            Destroy();
            return;
        }

        RefreshMesh();
    }

    public override void Destroy()
    {
        base.Destroy();
        Dispose();
    }

    public void RoomUnloaded()
    {
        Dispose();
    }

    private void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_mesh != null)
        {
            UnityEngine.Object.Destroy(_mesh);
            _mesh = null;
        }
    }

    private void RefreshMesh()
    {
        if (_disposed || _mesh == null || _placedObject?.data is not QuicksandZoneData data)
        {
            return;
        }

        QuicksandSurface.SampleZone(_placedObject, data, _surface, _bottom);

        for (int i = 0; i < SampleCount; i++)
        {
            // LocalTerrainCurve creates its far edge by moving the same Bezier
            // curve 50 px upward. Keep exactly that front/back relationship here.
            _back[i] = _surface[i] + Vector2.up * TerrainBackOffset;
        }

        int vertexCount = SampleCount * 3;
        int indexCount = (SampleCount - 1) * 4 * 3;
        if (_vertices.Length != vertexCount)
        {
            _vertices = new Vector3[vertexCount];
            _uvs = new Vector2[vertexCount];
        }

        if (_indices.Length != indexCount)
        {
            _indices = new int[indexCount];
        }

        int vertex = 0;
        int index = 0;
        for (int i = 0; i < SampleCount; i++)
        {
            if (i < SampleCount - 1)
            {
                AddQuad(_indices, ref index, vertex);
                AddQuad(_indices, ref index, vertex + 1);
            }

            // TerrainCurveMaskSource normally uses one flat bottom. Quicksand has a
            // freely editable BottomSpline, so use its matching sample instead. This
            // preserves the same three-layer mask topology without masking space that
            // is outside the actual quicksand volume.
            _vertices[vertex] = To3D(_bottom[i], 0f);
            _vertices[vertex + 1] = To3D(_surface[i], 0f);
            _vertices[vertex + 2] = To3D(_back[i], 1f);

            float depthToBottom = Mathf.Max(0f, _surface[i].y - _bottom[i].y);
            _uvs[vertex] = new Vector2(0f, depthToBottom);
            _uvs[vertex + 1] = Vector2.zero;
            _uvs[vertex + 2] = Vector2.zero;
            vertex += 3;
        }

        _mesh.Clear();
        _mesh.SetVertices(_vertices, 0, _vertices.Length);
        _mesh.SetUVs(0, _uvs, 0, _uvs.Length);
        _mesh.SetIndices(_indices, MeshTopology.Triangles, 0);
    }

    private static Vector3 To3D(Vector2 pos, float z)
    {
        return new Vector3(pos.x, pos.y, z);
    }

    private static void AddQuad(int[] indices, ref int index, int vertex)
    {
        indices[index++] = vertex;
        indices[index++] = vertex + 1;
        indices[index++] = vertex + 3;
        indices[index++] = vertex + 3;
        indices[index++] = vertex + 1;
        indices[index++] = vertex + 4;
    }

    public void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        sLeaser.sprites = Array.Empty<FSprite>();
        sLeaser.maskSources = new MaskSource[1];
        sLeaser.maskSources[0] = MaskMaker.MakeSource(
            "SlopedTerrainMaskGrab",
            "SlopedTerrainMask",
            isSecondLayer: false,
            _mesh);
    }

    public void DrawSprites(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        if (_disposed || _mesh == null)
        {
            if (sLeaser.maskSources != null && sLeaser.maskSources.Length != 0)
            {
                sLeaser.maskSources = Array.Empty<MaskSource>();
            }

            return;
        }

        if (sLeaser.maskSources == null || sLeaser.maskSources.Length == 0)
        {
            return;
        }

        float minDepth = room.roomSettings.TerrainDepth;
        MaskSource source = sLeaser.maskSources[0];
        source.setGameObjectPos =
            new Vector3(0f, 0f, minDepth / 30f) - (Vector3)camPos;
        source.setGameObjectRotation = Vector3.zero;
        source.setGameObjectScale =
            new Vector3(1f, 1f, TerrainMaxDepth / 30f - minDepth / 30f) / 16f;
        source.ApplyVertexColor();

        if (slatedForDeletetion || room != rCam.room)
        {
            sLeaser.CleanSpritesAndRemove();
        }
    }

    public void ApplyPalette(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        RoomPalette palette)
    {
    }

    public void AddToContainer(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        FContainer newContainer)
    {
    }
}
