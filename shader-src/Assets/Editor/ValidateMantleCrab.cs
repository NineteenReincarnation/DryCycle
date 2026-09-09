#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DryCycle.Editor
{
    /// <summary>Consumes exported production geometry and CPU atlas; no hand-authored creature art.</summary>
    public static class ValidateMantleCrab
    {
        [Serializable]
        public class MeshData
        {
            public int columns, rows, kind;
            public float depth;
            public Vector2[] vertices;
        }

        [Serializable]
        public class Fixture
        {
            public int tileSize, materialCount, atlasWidth, atlasHeight;
            public float[] motif, pattern, material, eye, noise;
            public MeshData[] meshes;
        }

        private static Vector4 V(float[] a) => new Vector4(a[0], a[1], a[2], a[3]);

        public static void Run()
        {
            string root = Directory.GetParent(Application.dataPath).Parent.FullName;
            string folder = Path.Combine(root, "artifacts", "mantlecrab");
            Fixture fixture = JsonUtility.FromJson<Fixture>(File.ReadAllText(Path.Combine(folder, "fixture.json")));
            if (fixture == null || fixture.tileSize <= 1 || fixture.materialCount <= 0 ||
                fixture.atlasWidth != fixture.tileSize * fixture.materialCount ||
                fixture.atlasHeight != fixture.tileSize * 2)
                throw new Exception("MantleCrab fixture contains an invalid atlas layout");

            ComputeShader compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/DryCycle/Creatures/MantleCrab/MantleCrabMaterialBake.compute");
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(
                "Assets/DryCycle/Creatures/MantleCrab/MantleCrabSurface.shader");
            if (ShaderUtil.ShaderHasError(shader) || !shader.isSupported)
                throw new Exception("Surface shader unsupported or has compiler errors");
            if (!SystemInfo.supportsComputeShaders)
                throw new Exception("GPU verification requires compute support; CPU fallback still supported in game");

            RenderTexture atlas = new RenderTexture(
                fixture.atlasWidth,
                fixture.atlasHeight,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            atlas.enableRandomWrite = true;
            atlas.filterMode = FilterMode.Bilinear;
            atlas.wrapMode = TextureWrapMode.Clamp;
            atlas.Create();

            Texture2D cpu = new Texture2D(
                fixture.atlasWidth,
                fixture.atlasHeight,
                TextureFormat.RGBA32,
                false,
                true);
            Texture2D readback = new Texture2D(
                fixture.atlasWidth,
                fixture.atlasHeight,
                TextureFormat.RGBA32,
                false,
                true);
            Material material = new Material(shader);
            List<Mesh> meshes = new List<Mesh>();
            Texture2D palette = new Texture2D(32, 16, TextureFormat.RGBA32, false, true);
            RenderTexture previous = RenderTexture.active;

            try
            {
                int kernel = compute.FindKernel("Bake");
                compute.SetVector("_Motif", V(fixture.motif));
                compute.SetVector("_Pattern", V(fixture.pattern));
                compute.SetVector("_Material", V(fixture.material));
                compute.SetVector("_Eye", V(fixture.eye));
                compute.SetVector("_NoiseSeed", V(fixture.noise));
                compute.SetTexture(kernel, "_Atlas", atlas);
                compute.Dispatch(
                    kernel,
                    Mathf.CeilToInt(fixture.atlasWidth / 8f),
                    Mathf.CeilToInt(fixture.tileSize / 8f),
                    1);

                RenderTexture.active = atlas;
                readback.ReadPixels(new Rect(0, 0, fixture.atlasWidth, fixture.atlasHeight), 0, 0);
                readback.Apply();

                byte[] expected = File.ReadAllBytes(Path.Combine(folder, "cpu-atlas.rgba"));
                int expectedBytes = fixture.atlasWidth * fixture.atlasHeight * 4;
                if (expected.Length != expectedBytes)
                    throw new Exception("CPU atlas byte count mismatch: expected " + expectedBytes + ", got " + expected.Length);
                cpu.LoadRawTextureData(expected);
                cpu.Apply();
                cpu.filterMode = FilterMode.Bilinear;
                cpu.wrapMode = TextureWrapMode.Clamp;

                byte[] actual = readback.GetRawTextureData();
                int max = 0;
                long total = 0;
                for (int i = 0; i < actual.Length; i++)
                {
                    int error = Math.Abs(actual[i] - expected[i]);
                    max = Math.Max(max, error);
                    total += error;
                }
                double mean = total / (double)actual.Length;
                if (max > 3 || mean > .2)
                    throw new Exception("CPU/GPU atlas mismatch: max=" + max + ", mean=" + mean);
                File.WriteAllBytes(Path.Combine(folder, "baked-atlas.png"), readback.EncodeToPNG());

                Color[] colors = new Color[512];
                for (int i = 0; i < colors.Length; i++)
                    colors[i] = new Color(.22f, .32f, .36f);
                palette.SetPixels(colors);
                palette.SetPixel(0, 7, new Color(.3f, .45f, .5f));
                palette.SetPixel(1, 7, new Color(.24f, .39f, .43f));
                palette.SetPixel(2, 7, new Color(.018f, .021f, .035f));
                palette.SetPixel(9, 7, Color.white);
                palette.Apply();
                Shader.SetGlobalTexture("_PalTex", palette);

                foreach (MeshData data in fixture.meshes)
                {
                    Mesh mesh = new Mesh();
                    Vector3[] vertices = new Vector3[data.vertices.Length];
                    Vector2[] uv = new Vector2[vertices.Length];
                    for (int y = 0; y <= data.rows; y++)
                    for (int x = 0; x <= data.columns; x++)
                    {
                        int i = y * (data.columns + 1) + x;
                        vertices[i] = data.vertices[i];
                        uv[i] = new Vector2(
                            (data.kind * fixture.tileSize + .5f +
                                x / (float)data.columns * (fixture.tileSize - 1)) / fixture.atlasWidth,
                            (.5f + y / (float)data.rows * (fixture.tileSize - 1)) / fixture.atlasHeight);
                    }

                    int[] triangles = new int[data.columns * data.rows * 6];
                    int t = 0;
                    for (int y = 0; y < data.rows; y++)
                    for (int x = 0; x < data.columns; x++)
                    {
                        int i = y * (data.columns + 1) + x;
                        triangles[t++] = i;
                        triangles[t++] = i + 1;
                        triangles[t++] = i + data.columns + 1;
                        triangles[t++] = i + 1;
                        triangles[t++] = i + data.columns + 2;
                        triangles[t++] = i + data.columns + 1;
                    }

                    mesh.vertices = vertices;
                    mesh.uv = uv;
                    mesh.triangles = triangles;
                    mesh.RecalculateBounds();
                    meshes.Add(mesh);
                }

                material.mainTexture = atlas;
                byte[] left = Render(folder, "preview-left.png", fixture, meshes, material, new Vector2(.9f, -.35f), false);
                byte[] right = Render(folder, "preview-right.png", fixture, meshes, material, new Vector2(-.9f, -.35f), false);
                int different = 0;
                for (int i = 0; i < left.Length; i++)
                    if (left[i] != right[i])
                        different++;
                if (different < 1000)
                    throw new Exception("Directional lighting did not change enough rendered pixels");

                Render(folder, "preview-dark-local.png", fixture, meshes, material, new Vector2(.9f, -.35f), true);
                material.mainTexture = cpu;
                Render(folder, "preview-cpu-bake.png", fixture, meshes, material, new Vector2(.9f, -.35f), false);

                string report = "PASS. Unity " + Application.unityVersion + "; GPU " + SystemInfo.graphicsDeviceName +
                    "; atlas=" + fixture.atlasWidth + "x" + fixture.atlasHeight +
                    "; CPU/GPU atlas max byte error=" + max + ", mean=" + mean +
                    "; changed light-direction channels=" + different + "; production geometry meshes=" + meshes.Count +
                    ". Offscreen render validation, not in-game collision/standing acceptance.";
                File.WriteAllText(Path.Combine(folder, "unity-validation.txt"), report);
                Debug.Log(report);
            }
            finally
            {
                RenderTexture.active = previous;
                foreach (Mesh mesh in meshes)
                    UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(cpu);
                UnityEngine.Object.DestroyImmediate(palette);
                UnityEngine.Object.DestroyImmediate(readback);
                atlas.Release();
                UnityEngine.Object.DestroyImmediate(atlas);
            }
        }

        private static byte[] Render(
            string folder,
            string name,
            Fixture fixture,
            List<Mesh> meshes,
            Material material,
            Vector2 direction,
            bool dark)
        {
            RenderTexture target = new RenderTexture(960, 1080, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            target.Create();
            Texture2D image = new Texture2D(960, 1080, TextureFormat.RGBA32, false, true);
            CommandBuffer commands = new CommandBuffer();
            RenderTexture previous = RenderTexture.active;

            try
            {
                Shader.SetGlobalVector("_lightDirAndPixelSize", new Vector4(direction.x, direction.y, 1f / 960, 1f / 1080));
                commands.SetRenderTarget(target);
                commands.ClearRenderTarget(true, true, dark ? new Color(.02f, .035f, .045f) : new Color(.43f, .72f, .78f));
                commands.SetViewProjectionMatrices(
                    Matrix4x4.identity,
                    GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(-160, 160, -20, 340, -1, 1), false));

                for (int i = 0; i < meshes.Count; i++)
                {
                    Color[] colors = new Color[meshes[i].vertexCount];
                    for (int v = 0; v < colors.Length; v++)
                    {
                        float light = dark
                            ? .12f + .5f * Mathf.Clamp01(1f - Vector2.Distance(
                                fixture.meshes[i].vertices[v],
                                new Vector2(70, 150)) / 110f)
                            : .9f;
                        colors[v] = new Color(light, light, light, 1 - fixture.meshes[i].depth * .5f);
                    }
                    meshes[i].colors = colors;
                    commands.DrawMesh(meshes[i], Matrix4x4.identity, material);
                }

                Graphics.ExecuteCommandBuffer(commands);
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 960, 1080), 0, 0);
                image.Apply();
                File.WriteAllBytes(Path.Combine(folder, name), image.EncodeToPNG());
                return image.GetRawTextureData();
            }
            finally
            {
                RenderTexture.active = previous;
                commands.Release();
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(image);
            }
        }
    }
}
#endif
