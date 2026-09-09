using UnityEngine;

namespace DryCycle.Creatures.MantleCrab.Rendering;

internal enum MantleCrabMaterial { Shell, Leg, Joint, Foot, Pincer, Eye, Fringe }

internal static class MantleCrabMeshBuilder
{
    internal const int TileSize = 128, MaterialCount = 7;
    internal static TriangleMesh Grid(string atlas, int columns, int rows, MantleCrabMaterial material)
    {
        TriangleMesh.Triangle[] triangles = new TriangleMesh.Triangle[columns * rows * 2];
        int triangle = 0;
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < columns; x++)
        {
            int i = y * (columns + 1) + x;
            triangles[triangle++] = new TriangleMesh.Triangle(i, i + 1, i + columns + 1);
            triangles[triangle++] = new TriangleMesh.Triangle(i + 1, i + columns + 2, i + columns + 1);
        }
        TriangleMesh mesh = new(atlas, triangles, true);
        for (int y = 0; y <= rows; y++)
        for (int x = 0; x <= columns; x++)
            mesh.UVvertices[y * (columns + 1) + x] = AtlasUV(material, new Vector2(x / (float)columns, y / (float)rows));
        return mesh;
    }

    // Half-texel gutters: derivative height taps are clamped to the same anatomical tile.
    internal static Vector2 AtlasUV(MantleCrabMaterial material, Vector2 uv) => new(
        ((int)material * TileSize + .5f + uv.x * (TileSize - 1)) / (TileSize * MaterialCount),
        (.5f + uv.y * (TileSize - 1)) / (TileSize * 2));

    internal static void Segment(TriangleMesh mesh, int columns, int rows, Vector2 start, Vector2 end,
        float width, float taper, Vector2 camera)
    {
        Vector2 cross = MantleCrabRenderingMath.Perpendicular((end - start).normalized);
        for (int y = 0; y <= rows; y++)
        for (int x = 0; x <= columns; x++)
        {
            float u = x / (float)columns, v = y / (float)rows * 2f - 1f;
            float profile = Mathf.Lerp(1f, taper, u) * (.76f + .24f * Mathf.Sin(u * Mathf.PI));
            mesh.MoveVertice(y * (columns + 1) + x, Vector2.Lerp(start, end, u) + cross * (v * width * profile) - camera);
        }
    }

    internal static void Rounded(TriangleMesh mesh, int columns, int rows, Vector2 center,
        Vector2 axis, float width, float height, Vector2 camera, bool block = false)
    {
        Vector2 cross = MantleCrabRenderingMath.Perpendicular(axis);
        for (int y = 0; y <= rows; y++)
        for (int x = 0; x <= columns; x++)
        {
            float u = x / (float)columns * 2 - 1, v = y / (float)rows * 2 - 1;
            Vector2 p = block ? new Vector2(u * (.9f - .25f * v) * (1f - .1f * Mathf.Abs(v)), v * (1f - .08f * Mathf.Abs(u))) :
                new Vector2(u * Mathf.Sqrt(1f - v * v * .5f), v * Mathf.Sqrt(1f - u * u * .5f));
            mesh.MoveVertice(y * (columns + 1) + x, center + axis * (p.x * width) + cross * (p.y * height) - camera);
        }
    }

    internal static void Chitin(TriangleMesh mesh, int columns, int rows, Vector2 start, Vector2 end, float width, int variant, Vector2 camera)
    {
        Vector2 cross = MantleCrabRenderingMath.Perpendicular((end-start).normalized);
        for(int x=0;x<=columns;x++) for(int y=0;y<=rows;y++)
        {
            float u=x/(float)columns, v=y/(float)rows*2-1;
            float profile = u < .12f ? Mathf.Lerp(.7f,1f,u/.12f) : u < .86f ? Mathf.Lerp(.92f,.66f,(u-.12f)/.74f) : Mathf.Lerp(1.05f,.45f,(u-.86f)/.14f);
            float bend = Mathf.Sin(u*Mathf.PI)*width*(variant%2==0?.18f:-.12f);
            mesh.MoveVertice(y*(columns+1)+x,Vector2.Lerp(start,end,u)+cross*(bend+v*width*profile)-camera);
        }
    }

    internal static void Foot(TriangleMesh mesh,int columns,int rows,Vector2 ankle,Vector2 tip,float width,float side,Vector2 groundNormal,Vector2 camera)
    {
        Vector2 cross = MantleCrabRenderingMath.Perpendicular((tip-ankle).normalized);
        for(int x=0;x<=columns;x++) for(int y=0;y<=rows;y++)
        {
            float u=x/(float)columns,v=y/(float)rows*2-1;
            float bulk=u<.58f?Mathf.Lerp(.3f,1f,Mathf.Pow(u/.58f,1.25f)):Mathf.Lerp(1f,.86f,(u-.58f)/.42f);
            Vector2 center=Vector2.Lerp(ankle,tip,u)+cross*(side*2.4f*Mathf.Sin(u*Mathf.PI));
            Vector2 point = center+cross*v*width*bulk;
            point -= groundNormal*Vector2.Dot(point-tip,groundNormal)*Mathf.SmoothStep(0,1,Mathf.InverseLerp(.82f,1f,u));
            mesh.MoveVertice(y*(columns+1)+x,point-camera);
        }
    }
}
