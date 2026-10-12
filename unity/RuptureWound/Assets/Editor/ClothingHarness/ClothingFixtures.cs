using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rupture.ClothingHarness
{
    [Serializable] internal sealed class ClothingCatalog
    {
        public int Schema;
        public string CatalogHash;
        public List<GarmentEntry> Garments;
    }

    [Serializable] internal sealed class GarmentEntry
    {
        public string Id, Part, Bundle, BundleHash, Fixture, FixtureHash, Error;
        public string[] Names, TemplateIds;
        public string Label => Names == null ? Bundle : string.Join(" / ", Names);
    }

    internal sealed class GarmentFixture
    {
        public int Schema;
        public string Id;
        public RendererFixture[] Renderers;
    }

    internal sealed class RendererFixture
    {
        public int LodLevel;
        public string Renderer, PathId, UnityVersion;
        public bool Readable;
        public string[] BonePaths;
        public FixtureTransform[] TransformChain;
        public FixtureMesh Mesh;
    }

    internal sealed class FixtureTransform
    {
        public float[] Position, Rotation, Scale;
    }

    internal sealed class FixtureBlendShape
    {
        public string Name;
        public float Weight;
        public float[][] Positions, Normals, Tangents;
    }

    internal sealed class FixtureMesh
    {
        public string Name;
        public float[][] Positions, Normals, Tangents, Colors, Uv0, Uv1, Uv2, Uv3, Weights, Bindposes;
        public int[][] BoneIndices, Submeshes;
        public FixtureBlendShape[] BlendShapes;

        internal Mesh Create()
        {
            int count = Positions.Length;
            if (Weights.Length != count || BoneIndices.Length != count || Submeshes.Length == 0)
                throw new InvalidDataException(Name + ": inconsistent vertex/skinning/submesh lengths");
            Mesh mesh = new Mesh { name = Name, hideFlags = HideFlags.DontSave,
                indexFormat = count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            try
            {
                mesh.vertices = Vectors(Positions);
                if (Normals.Length != 0) mesh.normals = Vectors(Normals);
                if (Tangents.Length != 0) mesh.tangents = Tangents.Select(v => new Vector4(v[0], v[1], v[2], v[3])).ToArray();
                if (Colors.Length != 0) mesh.colors = Colors.Select(v => new Color(v[0], v[1], v[2], v.Length > 3 ? v[3] : 1)).ToArray();
                mesh.uv = UVs(Uv0); mesh.uv2 = UVs(Uv1); mesh.uv3 = UVs(Uv2); mesh.uv4 = UVs(Uv3);
                mesh.bindposes = Bindposes.Select(Matrix).ToArray();
                mesh.boneWeights = Enumerable.Range(0, count).Select(i => new BoneWeight
                {
                    boneIndex0 = BoneIndices[i][0], boneIndex1 = BoneIndices[i][1],
                    boneIndex2 = BoneIndices[i][2], boneIndex3 = BoneIndices[i][3],
                    weight0 = Weights[i][0], weight1 = Weights[i][1], weight2 = Weights[i][2], weight3 = Weights[i][3]
                }).ToArray();
                mesh.subMeshCount = Submeshes.Length;
                for (int s = 0; s < Submeshes.Length; s++) mesh.SetTriangles(Submeshes[s], s, false);
                foreach (FixtureBlendShape frame in BlendShapes ?? Array.Empty<FixtureBlendShape>())
                    mesh.AddBlendShapeFrame(frame.Name, frame.Weight, Vectors(frame.Positions), Vectors(frame.Normals), Vectors(frame.Tangents));
                mesh.RecalculateBounds();
                return mesh;
            }
            catch { UnityEngine.Object.DestroyImmediate(mesh); throw; }
        }

        internal static Vector3 V(float[] v) => new Vector3(v[0], v[1], v[2]);
        internal static Vector3[] Vectors(float[][] v) => v.Select(V).ToArray();
        private static Vector2[] UVs(float[][] v) => v.Select(p => new Vector2(p[0], p[1])).ToArray();
        internal static Matrix4x4 Matrix(float[] a)
        {
            if (a.Length != 16) throw new InvalidDataException("Expected a column-major 4x4 matrix");
            Matrix4x4 m = new Matrix4x4();
            for (int i = 0; i < 16; i++) m[i] = a[i];
            return m;
        }
    }

    internal static class FixtureIO
    {
        internal static string Hash(string path)
        {
            using (var input = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }

        internal static GarmentFixture Read(string directory, GarmentEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.Error)) throw new InvalidDataException(entry.Error);
            string path = Path.GetFullPath(Path.Combine(directory, entry.Fixture));
            if (!path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Fixture path is outside the selected directory");
            if (!string.Equals(Hash(path), entry.FixtureHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Fixture hash mismatch; rerun extraction");
            using (var file = File.OpenRead(path))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip))
            {
                var fixture = JsonConvert.DeserializeObject<GarmentFixture>(reader.ReadToEnd());
                if (fixture.Schema != 1 || fixture.Id != entry.Id || fixture.Renderers == null || fixture.Renderers.Length == 0)
                    throw new InvalidDataException("Unsupported or incomplete garment fixture");
                return fixture;
            }
        }
    }
}
