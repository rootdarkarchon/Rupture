using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Threading;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Rupture.Clothing
{
    // No Unity calls or file writes: discovery and rewriting run on a loading worker.
    internal sealed class ClothingBundleSource
    {
        internal string Key, Path;
        internal string[] Dependencies = Array.Empty<string>();
    }

    internal sealed class ClothingMeshTarget
    {
        internal string File, Name;
        internal long PathId;
    }

    internal sealed class ClothingBundleTarget
    {
        internal ClothingBundleSource Source;
        internal string Hash;
        internal readonly List<ClothingMeshTarget> Meshes = new List<ClothingMeshTarget>();
    }

    internal sealed class ClothingBundlePlan
    {
        internal readonly Dictionary<string, ClothingBundleTarget> Targets = new Dictionary<string, ClothingBundleTarget>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> Diagnostics = new List<string>();
        internal readonly Dictionary<string, ClothingBundleSource> MeshBundles = new Dictionary<string, ClothingBundleSource>(StringComparer.OrdinalIgnoreCase);
        internal int Garments, ReadableMeshes;

        internal static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        internal static ClothingBundlePlan Discover(IEnumerable<string> roots, IDictionary<string, ClothingBundleSource> sources,
            Action<int, int, string> progress = null, CancellationToken cancellation = default)
        {
            var plan = new ClothingBundlePlan();
            var keys = roots.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            var discoveries = new List<ClothingBundleReader.MeshReference>[keys.Length];
            var failures = new string[keys.Length];
            int completed = 0;
            // Each reader owns its manager/streams. Bound CPU and temporary decoding memory,
            // then merge in catalog order so scheduling cannot change recovery decisions.
            Parallel.For(0, keys.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount), CancellationToken = cancellation }, index =>
            {
                try
                {
                    if (!sources.TryGetValue(keys[index], out var source)) throw new InvalidDataException("clothing bundle is absent from the loader registry");
                    using (var reader = new ClothingBundleReader(source, sources))
                        discoveries[index] = reader.FindClothingMeshes();
                }
                catch (Exception e) { failures[index] = keys[index] + ": " + e.GetType().Name + ": " + e.Message; }
                progress?.Invoke(System.Threading.Interlocked.Increment(ref completed), keys.Length, keys[index]);
            });
            for (int index = 0; index < keys.Length; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (failures[index] != null) { plan.Diagnostics.Add(failures[index]); continue; }
                // Commit discovery per garment, so an unresolved reference cannot leave a partial plan.
                foreach (var mesh in discoveries[index])
                {
                    plan.MeshBundles[mesh.Owner.Key] = mesh.Owner;
                    if (mesh.Readable) { plan.ReadableMeshes++; continue; }
                    string path = mesh.Owner.Path;
                    if (!plan.Targets.TryGetValue(path, out var target))
                        plan.Targets.Add(path, target = new ClothingBundleTarget { Source = mesh.Owner, Hash = HashFile(path) });
                    if (!target.Meshes.Any(m => m.File == mesh.File && m.PathId == mesh.PathId))
                        target.Meshes.Add(new ClothingMeshTarget { File = mesh.File, PathId = mesh.PathId, Name = mesh.Name });
                }
                plan.Garments++;
            }
            return plan;
        }

        internal static byte[] Rewrite(ClothingBundleTarget target, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            if (HashFile(target.Source.Path) != target.Hash) throw new InvalidDataException("bundle changed since clothing discovery");
            var manager = new AssetsManager();
            try
            {
                var bundle = LoadBuffered(manager, target.Source.Path, cancellation);
                foreach (var group in target.Meshes.GroupBy(m => m.File))
                {
                    int index = bundle.file.GetFileIndex(group.Key);
                    if (index < 0) throw new InvalidDataException("owning serialized file is missing");
                    var file = manager.LoadAssetsFileFromBundle(bundle, index, false);
                    foreach (var mesh in group)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var info = file.file.GetAssetInfo(mesh.PathId);
                        if (info == null || info.TypeId != (int)AssetClassID.Mesh) throw new InvalidDataException("mesh identity no longer matches");
                        var field = manager.GetBaseField(file, info);
                        if (field["m_Name"].AsString != mesh.Name) throw new InvalidDataException("mesh name changed at the identified object");
                        if (field["m_IsReadable"].IsDummy) throw new InvalidDataException("mesh has no readability field");
                        if (field["m_IsReadable"].AsBool) continue;
                        file.file.Reader.Position = info.GetAbsoluteByteOffset(file.file);
                        byte[] original = file.file.Reader.ReadBytes(checked((int)info.ByteSize));
                        field["m_IsReadable"].AsBool = true;
                        byte[] updated = field.WriteToByteArray(file.file.Header.Endianness);
                        // Require a literal one-byte false -> true change. Geometry, references and padding
                        // must all survive the serializer unchanged, otherwise use the original bundle.
                        if (original.Length != updated.Length) throw new InvalidDataException("mesh serialization changed size");
                        int changes = 0;
                        for (int i = 0; i < original.Length; i++)
                            if (original[i] != updated[i])
                            {
                                if (original[i] != 0 || updated[i] != 1) throw new InvalidDataException("mesh serialization changed data outside readability");
                                changes++;
                            }
                        if (changes != 1) throw new InvalidDataException("mesh rewrite was not exactly one readability byte");
                        info.SetNewData(updated);
                    }
                    bundle.file.BlockAndDirInfo.DirectoryInfos[index].SetNewData(file.file);
                }
                using (var output = new MemoryStream())
                {
                    cancellation.ThrowIfCancellationRequested();
                    bundle.file.Write(new AssetsFileWriter(output));
                    return output.ToArray();
                }
            }
            finally { manager.UnloadAll(true); }
        }

        internal static BundleFileInstance LoadBuffered(AssetsManager manager, string path, CancellationToken cancellation = default)
        {
            // The LZMA decoder reads compressed input one byte at a time. On Unity
            // Mono, doing that through FileStream dominates decompression time.
            var input = new MemoryStream(System.IO.File.ReadAllBytes(path), false);
            try
            {
                var bundle = manager.LoadBundleFile(input, path, false);
                ClothingBundleDecompression.Unpack(bundle, cancellation);
                return bundle;
            }
            catch { input.Dispose(); throw; }
        }
    }

    internal sealed class ClothingBundleReader : IDisposable
    {
        internal sealed class MeshReference
        {
            internal ClothingBundleSource Owner;
            internal string File, Name;
            internal long PathId;
            internal bool Readable;
        }

        private readonly AssetsManager _manager = new AssetsManager();
        private readonly ClothingBundleSource _root;
        private readonly IDictionary<string, ClothingBundleSource> _sources;
        private readonly Dictionary<string, BundleFileInstance> _bundles = new Dictionary<string, BundleFileInstance>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<AssetsFileInstance, ClothingBundleSource> _owners = new Dictionary<AssetsFileInstance, ClothingBundleSource>();

        internal ClothingBundleReader(ClothingBundleSource root, IDictionary<string, ClothingBundleSource> sources)
        { _root = root; _sources = sources; }

        private BundleFileInstance Bundle(ClothingBundleSource source)
        {
            if (!_bundles.TryGetValue(source.Key, out var bundle))
                // External-reference lookup needs only directory names. In particular,
                // do not LZMA-decompress every texture/shader dependency just to test its name.
                _bundles.Add(source.Key, bundle = _manager.LoadBundleFile(source.Path, false));
            return bundle;
        }

        private AssetsFileInstance FileAt(ClothingBundleSource source, int index)
        {
            var bundle = Bundle(source);
            if (bundle.file.DataIsCompressed)
            {
                _manager.UnloadBundleFile(bundle);
                _bundles[source.Key] = bundle = ClothingBundlePlan.LoadBuffered(_manager, source.Path);
            }
            var file = _manager.LoadAssetsFileFromBundle(bundle, index, false);
            _owners[file] = source;
            return file;
        }

        private IEnumerable<ClothingBundleSource> Dependencies(ClothingBundleSource source, HashSet<string> visited)
        {
            if (!visited.Add(source.Key)) yield break;
            yield return source;
            foreach (string key in source.Dependencies)
                if (_sources.TryGetValue(key, out var dependency))
                    foreach (var nested in Dependencies(dependency, visited)) yield return nested;
        }

        private Tuple<AssetsFileInstance, AssetFileInfo> Resolve(AssetsFileInstance context, AssetTypeValueField pointer)
        {
            if (pointer.IsDummy || pointer["m_FileID"].IsDummy || pointer["m_PathID"].IsDummy)
                throw new InvalidDataException("invalid clothing object reference");
            long pathId = pointer["m_PathID"].AsLong;
            int fileId = pointer["m_FileID"].AsInt;
            if (pathId == 0) throw new InvalidDataException("missing clothing object reference");
            var file = context;
            if (fileId != 0)
            {
                if (fileId < 1 || fileId > context.file.Metadata.Externals.Count) throw new InvalidDataException("external file index is out of range");
                string external = context.file.Metadata.Externals[fileId - 1].PathName.Replace('\\', '/');
                string name = external.Substring(external.LastIndexOf('/') + 1);
                file = null;
                foreach (var dependency in Dependencies(_owners[context], new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                {
                    var bundle = Bundle(dependency);
                    int index = bundle.file.GetFileIndex(name);
                    if (index < 0) continue;
                    if (file != null) throw new InvalidDataException("ambiguous external serialized file " + name);
                    file = FileAt(dependency, index);
                }
                if (file == null) throw new InvalidDataException("unresolved external serialized file " + name);
            }
            var info = file.file.GetAssetInfo(pathId);
            if (info == null) throw new InvalidDataException("referenced clothing object is absent");
            return Tuple.Create(file, info);
        }

        private Tuple<AssetsFileInstance, AssetFileInfo> SkinRenderer(AssetsFileInstance context, AssetTypeValueField pointer,
            List<Tuple<AssetsFileInstance, AssetFileInfo>> variants, int depth = 0)
        {
            if (depth > 4) throw new InvalidDataException("recursive skin reference");
            var skin = Resolve(context, pointer);
            var data = _manager.GetBaseField(skin.Item1, skin.Item2);
            if (!data["_skinnedMeshRenderer"].IsDummy) return Resolve(skin.Item1, data["_skinnedMeshRenderer"]);
            // TorsoSkin swaps meshes as armor/vest/backpack equipment changes. All three
            // are clothing even when the prefab renderer initially references only _base.
            if (!data["_skin"].IsDummy)
            {
                foreach (string variant in new[] { "_base", "_armor", "_vest" })
                    if (!data[variant].IsDummy && data[variant]["m_PathID"].AsLong != 0)
                        variants.Add(Resolve(skin.Item1, data[variant]));
                return SkinRenderer(skin.Item1, data["_skin"], variants, depth + 1);
            }
            if (!data["Skin"].IsDummy) return SkinRenderer(skin.Item1, data["Skin"], variants, depth + 1);
            throw new InvalidDataException("unsupported LoddedSkin entry");
        }

        internal List<MeshReference> FindClothingMeshes()
        {
            var renderers = new List<Tuple<AssetsFileInstance, AssetFileInfo>>();
            var meshes = new List<Tuple<AssetsFileInstance, AssetFileInfo>>();
            var bundle = Bundle(_root);
            for (int index = 0; index < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; index++)
            {
                // UnityFS directory flag 4 denotes a serialized asset file. Sniffing raw
                // .resS bytes can produce false positives for clothing texture streams.
                if ((bundle.file.BlockAndDirInfo.DirectoryInfos[index].Flags & 4) == 0) continue;
                var file = FileAt(_root, index);
                foreach (var info in file.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
                {
                    var data = _manager.GetBaseField(file, info);
                    if (data["_lods"].IsDummy) continue;
                    foreach (var pointer in data["_lods"]["Array"].Children) renderers.Add(SkinRenderer(file, pointer, meshes));
                }
            }
            if (renderers.Count == 0) throw new InvalidDataException("clothing prefab has no LoddedSkin renderer references");
            var result = new List<MeshReference>();
            foreach (var renderer in renderers)
            {
                if (renderer.Item2.TypeId != (int)AssetClassID.SkinnedMeshRenderer) throw new InvalidDataException("skin references a non-skinned renderer");
                var data = _manager.GetBaseField(renderer.Item1, renderer.Item2);
                meshes.Add(Resolve(renderer.Item1, data["m_Mesh"]));
            }
            foreach (var mesh in meshes)
            {
                if (mesh.Item2.TypeId != (int)AssetClassID.Mesh) throw new InvalidDataException("renderer references a non-mesh object");
                if (!result.Any(m => m.File == mesh.Item1.name && m.PathId == mesh.Item2.PathId))
                {
                    // Read only the serialized prefix through m_IsReadable; do not allocate
                    // vertex/UV/index arrays for hundreds of already-readable garments.
                    var template = _manager.GetTemplateBaseField(mesh.Item1, mesh.Item2);
                    mesh.Item1.file.Reader.Position = mesh.Item2.GetAbsoluteByteOffset(mesh.Item1.file);
                    string name = null; bool? readable = null;
                    foreach (var child in template.Children)
                    {
                        var value = child.MakeValue(mesh.Item1.file.Reader);
                        if (child.Name == "m_Name") name = value.AsString;
                        if (child.Name == "m_IsReadable") { readable = value.AsBool; break; }
                    }
                    if (!readable.HasValue || name == null) throw new InvalidDataException("mesh type tree lacks readability or name");
                    result.Add(new MeshReference { Owner = _owners[mesh.Item1], File = mesh.Item1.name,
                        PathId = mesh.Item2.PathId, Name = name, Readable = readable.Value });
                }
            }
            return result;
        }

        public void Dispose() { _manager.UnloadAll(true); }
    }
}

