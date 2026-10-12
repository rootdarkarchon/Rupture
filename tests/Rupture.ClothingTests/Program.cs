using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Rupture.Clothing;

internal static class Program
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
    private static int Main(string[] args)
    {
        try
        {
            string game = args[0], output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(output);
            var sources = new Dictionary<string, ClothingBundleSource>(StringComparer.OrdinalIgnoreCase);
            string nativeRoot = Path.Combine(game, "EscapeFromTarkov_Data/StreamingAssets/Windows");
            using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(nativeRoot, "Windows.json"))))
                foreach (var entry in manifest.RootElement.EnumerateObject())
                    sources[entry.Name] = new ClothingBundleSource { Key = entry.Name, Path = Path.Combine(nativeRoot, entry.Name),
                        Dependencies = entry.Value.GetProperty("Dependencies").EnumerateArray().Select(v => v.GetString()).ToArray() };
            var roots = new List<string>();
            string server = Directory.Exists(Path.Combine(game, "SPT_Runtime/SPT_Data"))
                ? Path.Combine(game, "SPT_Runtime") : Path.Combine(game, "SPT");
            using (var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(server, "SPT_Data/database/templates/customization.json"))))
                foreach (var entry in catalog.RootElement.EnumerateObject())
                {
                    var props = entry.Value.GetProperty("_props");
                    if (!props.TryGetProperty("BodyPart", out var part) || (part.GetString() != "Body" && part.GetString() != "Feet")) continue;
                    if (props.TryGetProperty("Prefab", out var prefab) && prefab.TryGetProperty("path", out var path)) roots.Add(path.GetString());
                }
            string wtt = Path.Combine(server, "user/mods/WTT-ContentBackport");
            if (Directory.Exists(wtt))
            {
                using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(wtt, "bundles.json"))))
                    foreach (var entry in manifest.RootElement.GetProperty("manifest").EnumerateArray())
                    {
                        string key = entry.GetProperty("key").GetString();
                        sources[key] = new ClothingBundleSource { Key = key, Path = Path.Combine(wtt, "bundles", key),
                            Dependencies = entry.GetProperty("dependencyKeys").EnumerateArray().Select(v => v.GetString()).ToArray() };
                    }
                foreach (string config in Directory.GetFiles(Path.Combine(wtt, "db/CustomClothing"), "*.json"))
                    using (var document = JsonDocument.Parse(File.ReadAllText(config)))
                        foreach (var item in document.RootElement.EnumerateArray())
                            foreach (string property in new[] { "topBundlePath", "bottomBundlePath" })
                                if (item.TryGetProperty(property, out var path) && path.ValueKind == JsonValueKind.String) roots.Add(path.GetString());
            }
            File.WriteAllText(Path.Combine(output, "discovery.json"), JsonSerializer.Serialize(new { roots,
                sources = sources.Values.Select(s => new { key = s.Key, path = s.Path, dependencies = s.Dependencies }) }));
            if (args.Contains("--export-only")) return 0;
            string decoderRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/rupture-physx-native-build/release"));
            Check(ClothingBundleDecompression.Initialize(decoderRoot, Console.WriteLine), "built native LZMA decoder loads from the explicit artifact directory");
            RequestBehaviorTests.Run().GetAwaiter().GetResult();
            var timer = Stopwatch.StartNew();
            var plan = ClothingBundlePlan.Discover(roots, sources);
            Console.WriteLine($"Discovery: garments={plan.Garments}, readable={plan.ReadableMeshes}, targets={plan.Targets.Count}, ms={timer.Elapsed.TotalMilliseconds:F1}");
            foreach (var diagnostic in plan.Diagnostics) Console.WriteLine("DIAGNOSTIC: " + diagnostic);
            Check(plan.Targets.Count > 0, "native unreadable clothing discovered");
            if (Directory.Exists(wtt)) Check(plan.Targets.Values.Any(t => t.Meshes.Any(m => m.Name == "Pants_Civilian_Jeans_LOD0")), "WTT civilian jeans discovered by prefab references");
            var report = new List<object>();
            var requests = new ClothingBundleRequests(roots, sources, null);
            requests.Request(new[] { "unrelated.bundle" });
            Check(requests.RequestedGarments == 0 && requests.TargetFor("unrelated.bundle").IsCompleted,
                "catalog indexing and unrelated loads start no clothing scan");
            int count = 0;
            foreach (var target in plan.Targets.Values)
            {
                VerifyDecoders(target.Source.Path);
                // Fresh request state: earlier tests must not supply missing shared meshes.
                requests = new ClothingBundleRequests(roots, sources, null);
                string request = roots.First(r => Uses(r, target.Source.Key, sources, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                requests.Request(new[] { request });
                var requested = requests.TargetFor(target.Source.Key).GetAwaiter().GetResult();
                Check(requested != null && requested.Meshes.Select(m => m.File + ":" + m.PathId).OrderBy(v => v)
                    .SequenceEqual(target.Meshes.Select(m => m.File + ":" + m.PathId).OrderBy(v => v)),
                    "requested load recovers every shared mesh: " + target.Source.Key);
                int requestedCount = requests.RequestedGarments;
                requests.Request(new[] { request });
                Check(requests.RequestedGarments == requestedCount, "repeat request reuses discovery");
                byte[] rewritten = ClothingBundlePlan.Rewrite(target);
                string patched = Path.Combine(output, (++count).ToString("D3") + ".bundle");
                File.WriteAllBytes(patched, rewritten); // Private test artifact, never an installed file.
                VerifyObjects(target, rewritten);
                Check(ClothingBundlePlan.HashFile(target.Source.Path) == target.Hash, "original unchanged: " + target.Source.Key);
                report.Add(new { original = target.Source.Path, patched, request, key = target.Source.Key, meshes = target.Meshes.Select(m => m.Name).ToArray(), bytes = rewritten.Length });
            }
            File.WriteAllText(Path.Combine(output, "bundles.json"), JsonSerializer.Serialize(new { bundles = report }, new JsonSerializerOptions { WriteIndented = true }));
            // A changed source and unresolved reference must not be silently rewritten.
            var first = plan.Targets.Values.First(); string hash = first.Hash; first.Hash = "wrong";
            bool rejected = false; try { ClothingBundlePlan.Rewrite(first); } catch (InvalidDataException) { rejected = true; }
            first.Hash = hash; Check(rejected, "changed source bundle rejected");
            var missing = ClothingBundlePlan.Discover(new[] { "missing.bundle" }, sources);
            Check(missing.Targets.Count == 0 && missing.Diagnostics.Count == 1, "missing garment diagnosed without unrelated edits");
            Console.WriteLine($"PASS: {count} rewritten bundles validated; elapsed={timer.Elapsed.TotalSeconds:F1}s");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static bool Uses(string key, string target, IDictionary<string, ClothingBundleSource> sources, HashSet<string> visited)
    {
        if (key == target) return true;
        if (!visited.Add(key) || !sources.TryGetValue(key, out var source)) return false;
        return source.Dependencies.Any(d => Uses(d, target, sources, visited));
    }

    private static void VerifyDecoders(string path)
    {
        byte[] input = File.ReadAllBytes(path);
        byte[] Load(int mode)
        {
            var manager = new AssetsManager();
            try
            {
                var bundle = manager.LoadBundleFile(new MemoryStream(input, false), path, mode == 0);
                if (mode != 0) ClothingBundleDecompression.Unpack(bundle, allowNative: mode == 2);
                bundle.file.Reader.Position = 0;
                return bundle.file.Reader.ReadBytes(checked((int)bundle.file.Reader.BaseStream.Length));
            }
            finally { manager.UnloadAll(true); }
        }
        byte[] original = Load(0);
        Check(original.SequenceEqual(Load(1)) && original.SequenceEqual(Load(2)),
            "buffered managed and native decompression preserve complete bundle bytes: " + Path.GetFileName(path));
    }

    private static void VerifyObjects(ClothingBundleTarget target, byte[] rewritten)
    {
        var originalManager = new AssetsManager(); var updatedManager = new AssetsManager();
        try
        {
            var a = originalManager.LoadBundleFile(target.Source.Path, true);
            var b = updatedManager.LoadBundleFile(new MemoryStream(rewritten, false), "patched", true);
            Check(a.file.GetAllFileNames().SequenceEqual(b.file.GetAllFileNames()), "bundle entry identities preserved");
            for (int i = 0; i < a.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
            {
                if ((a.file.BlockAndDirInfo.DirectoryInfos[i].Flags & 4) == 0)
                {
                    a.file.GetFileRange(i, out long ao, out long al); b.file.GetFileRange(i, out long bo, out long bl);
                    a.file.DataReader.Position = ao; b.file.DataReader.Position = bo;
                    Check(al == bl && a.file.DataReader.ReadBytes((int)al).SequenceEqual(b.file.DataReader.ReadBytes((int)bl)), "non-asset bundle data preserved");
                    continue;
                }
                var af = originalManager.LoadAssetsFileFromBundle(a, i, false); var bf = updatedManager.LoadAssetsFileFromBundle(b, i, false);
                Check(af.file.AssetInfos.Count == bf.file.AssetInfos.Count, "asset object count preserved");
                foreach (var ai in af.file.AssetInfos)
                {
                    var bi = bf.file.GetAssetInfo(ai.PathId);
                    if (bi == null || ai.TypeId != bi.TypeId || ai.ByteSize != bi.ByteSize) throw new Exception("asset identity changed");
                    af.file.Reader.Position = ai.GetAbsoluteByteOffset(af.file); bf.file.Reader.Position = bi.GetAbsoluteByteOffset(bf.file);
                    byte[] ab = af.file.Reader.ReadBytes((int)ai.ByteSize), bb = bf.file.Reader.ReadBytes((int)bi.ByteSize);
                    bool selected = target.Meshes.Any(m => m.File == af.name && m.PathId == ai.PathId);
                    int changes = ab.Zip(bb, (x, y) => x == y ? 0 : 1).Sum();
                    if (changes != (selected ? 1 : 0)) throw new Exception("unexpected object changes " + ai.PathId);
                    if (selected && !updatedManager.GetBaseField(bf, bi)["m_IsReadable"].AsBool) throw new Exception("mesh remains unreadable");
                }
            }
        }
        finally { originalManager.UnloadAll(true); updatedManager.UnloadAll(true); }
    }
}
