using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace MangosSuperUI_Extractor;

using VERTEX = VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>;

/// <summary>
/// Converts a parsed M2Model + BLP textures into a GLB (glTF Binary) file.
/// Uses SharpGLTF Toolkit (MeshBuilder + SceneBuilder) API.
/// 
/// Each submesh becomes a separate mesh in the scene (like wow.export's "Geoset0", "Geoset1")
/// with its own material/texture. This prevents SharpGLTF from merging primitives that
/// share the same material.
/// 
/// Triangle winding: M2 indices are emitted in native order (i0, i1, i2) — no swap needed.
/// The Z-up → Y-up coordinate transform in M2Reader already flips handedness, so the
/// indices come out in glTF's expected counter-clockwise front-face convention as-is.
/// </summary>
public static class GlbWriter
{
    public static bool SaveGlb(M2Model m2, Dictionary<int, byte[]> textures, string outputPath)
    {
        if (!m2.IsValid) return false;

        try
        {
            // ── Convert BLP textures to PNG and build materials ──
            var materialsByTexIdx = new Dictionary<int, MaterialBuilder>();

            foreach (var (texIdx, blpData) in textures)
            {
                var pngBytes = ConvertBlpToPngBytes(blpData);
                if (pngBytes != null)
                {
                    var img = new SharpGLTF.Memory.MemoryImage(pngBytes);
                    var mat = new MaterialBuilder($"mat_{texIdx}")
                        .WithUnlitShader()
                        .WithBaseColor(img);
                    materialsByTexIdx[texIdx] = mat;
                }
            }

            var fallbackMat = new MaterialBuilder("default")
                .WithUnlitShader()
                .WithChannelParam(KnownChannel.BaseColor, KnownProperty.RGBA, new Vector4(0.7f, 0.7f, 0.7f, 1f));

            var scene = new SceneBuilder("scene");
            var vertices = m2.Vertices;
            var indices = m2.Indices;

            if (m2.Submeshes.Count > 1)
            {
                // ── Multi-submesh: build a SEPARATE MeshBuilder per submesh ──
                // This is how wow.export does it (Geoset0, Geoset1, etc.)
                // Using separate meshes prevents SharpGLTF from merging primitives.

                // Build submesh → texture index mapping from batches
                var submeshTexture = BuildSubmeshTextureMap(m2);

                for (int subIdx = 0; subIdx < m2.Submeshes.Count; subIdx++)
                {
                    var submesh = m2.Submeshes[subIdx];
                    if (submesh.IndexCount == 0 || submesh.IndexCount % 3 != 0) continue;

                    // Determine material for this submesh
                    int texIdx = submeshTexture.ContainsKey(subIdx) ? submeshTexture[subIdx] : subIdx;
                    var mat = materialsByTexIdx.ContainsKey(texIdx) ? materialsByTexIdx[texIdx] :
                              materialsByTexIdx.Count > 0 ? materialsByTexIdx.Values.First() : fallbackMat;

                    var meshBuilder = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>($"Geoset{subIdx}");
                    var prim = meshBuilder.UsePrimitive(mat);

                    for (int i = submesh.IndexStart; i + 2 < submesh.IndexStart + submesh.IndexCount; i += 3)
                    {
                        if (i + 2 >= indices.Count) break;
                        int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                        if (i0 >= vertices.Count || i1 >= vertices.Count || i2 >= vertices.Count) continue;
                        prim.AddTriangle(MakeVertex(vertices[i0]), MakeVertex(vertices[i1]), MakeVertex(vertices[i2]));
                    }

                    scene.AddRigidMesh(meshBuilder, Matrix4x4.Identity);
                }
            }
            else
            {
                // ── Single submesh or no submesh info: one mesh ──
                var mat = materialsByTexIdx.Count > 0 ? materialsByTexIdx.Values.First() : fallbackMat;
                var meshBuilder = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>("mesh");
                var prim = meshBuilder.UsePrimitive(mat);

                if (m2.Submeshes.Count == 1)
                {
                    var sub = m2.Submeshes[0];
                    for (int i = sub.IndexStart; i + 2 < sub.IndexStart + sub.IndexCount; i += 3)
                    {
                        if (i + 2 >= indices.Count) break;
                        int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                        if (i0 >= vertices.Count || i1 >= vertices.Count || i2 >= vertices.Count) continue;
                        prim.AddTriangle(MakeVertex(vertices[i0]), MakeVertex(vertices[i1]), MakeVertex(vertices[i2]));
                    }
                }
                else
                {
                    // No submesh info — dump all indices
                    for (int i = 0; i + 2 < indices.Count; i += 3)
                    {
                        int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                        if (i0 >= vertices.Count || i1 >= vertices.Count || i2 >= vertices.Count) continue;
                        prim.AddTriangle(MakeVertex(vertices[i0]), MakeVertex(vertices[i1]), MakeVertex(vertices[i2]));
                    }
                }

                scene.AddRigidMesh(meshBuilder, Matrix4x4.Identity);
            }

            // ── Save ──
            var model = scene.ToGltf2();
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            model.SaveGLB(outputPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Build a mapping of submeshIndex → textureIndex using the batch chain:
    ///   batch.SubmeshIndex → batch.TextureIndex → TextureLookup[idx] → texture index
    /// </summary>
    private static Dictionary<int, int> BuildSubmeshTextureMap(M2Model m2)
    {
        var map = new Dictionary<int, int>();

        foreach (var batch in m2.Batches)
        {
            int subIdx = batch.SubmeshIndex;
            if (map.ContainsKey(subIdx)) continue; // first batch wins for each submesh

            int texIdx = 0;
            if (batch.TextureIndex < m2.TextureLookup.Count)
            {
                texIdx = m2.TextureLookup[batch.TextureIndex];
            }

            map[subIdx] = texIdx;
        }

        return map;
    }

    /// <summary>Simplified overload for single-texture models.</summary>
    public static bool SaveGlb(M2Model m2, byte[]? singleTexture, string outputPath)
    {
        var textures = new Dictionary<int, byte[]>();
        if (singleTexture != null) textures[0] = singleTexture;
        return SaveGlb(m2, textures, outputPath);
    }

    private static VERTEX MakeVertex(M2Vertex v)
    {
        return new VERTEX(
            new VertexPositionNormal(new Vector3(v.PosX, v.PosY, v.PosZ), new Vector3(v.NormX, v.NormY, v.NormZ)),
            new VertexTexture1(new Vector2(v.TexU, v.TexV))
        );
    }

    private static byte[]? ConvertBlpToPngBytes(byte[] blpData)
    {
        try
        {
            using var blpStream = new MemoryStream(blpData);
            var blpFile = new War3Net.Drawing.Blp.BlpFile(blpStream);
            var pixels = blpFile.GetPixels(0, out int w, out int h);
            if (w == 0 || h == 0 || pixels.Length == 0) return null;

            using var bitmap = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bmpData = bitmap.LockBits(new Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmpData.Scan0, pixels.Length);
            bitmap.UnlockBits(bmpData);

            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
            return pngStream.ToArray();
        }
        catch { return null; }
    }
}