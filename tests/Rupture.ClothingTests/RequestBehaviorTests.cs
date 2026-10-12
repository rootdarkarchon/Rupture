using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Rupture.Clothing;

internal static class RequestBehaviorTests
{
    internal static async Task Run()
    {
        var shared = new ClothingBundleSource { Key = "shared", Path = "shared" };
        var sources = new Dictionary<string, ClothingBundleSource>
        {
            ["first"] = new ClothingBundleSource { Key = "first", Path = "first", Dependencies = new[] { "shared" } },
            ["later"] = new ClothingBundleSource { Key = "later", Path = "later", Dependencies = new[] { "shared" } },
            ["unused"] = new ClothingBundleSource { Key = "unused", Path = "does-not-exist" },
            ["shared"] = shared
        };
        var reads = new ConcurrentDictionary<string, int>();
        using (var gate = new ManualResetEventSlim(false))
        {
            var requests = new ClothingBundleRequests(new[] { "first", "later", "unused" }, sources, null, (key, token) =>
            {
                reads.AddOrUpdate(key, 1, (_, n) => n + 1);
                if (key == "unused") throw new Exception("Unrequested garment was read");
                if (key == "first") gate.Wait(token);
                var plan = new ClothingBundlePlan { Garments = 1 };
                plan.MeshBundles.Add("shared", shared);
                if (key == "later")
                {
                    var target = new ClothingBundleTarget { Source = shared, Hash = "same-source" };
                    target.Meshes.Add(new ClothingMeshTarget { File = "mesh-file", Name = "later-unreadable", PathId = 7 });
                    plan.Targets.Add(shared.Path, target);
                }
                else plan.ReadableMeshes = 1;
                return plan;
            });
            if (reads.Count != 0) throw new Exception("Index construction read bundles");
            requests.Request(new[] { "first", "first" });
            var dependency = requests.TargetFor("shared");
            if (dependency.IsCompleted || requests.RequestedGarments != 1) throw new Exception("Dependency escaped discovery or duplicate request started work");
            var unrelated = requests.TargetFor("unused");
            if (!unrelated.IsCompleted || await unrelated != null) throw new Exception("Unrelated load was blocked");
            // A different root joins while discovery of the shared owner is in flight.
            requests.Request(new[] { "later" });
            var overlap = requests.TargetFor("shared");
            gate.Set();
            var recovered = await dependency;
            var overlapping = await overlap;
            if (recovered == null || recovered.Meshes.Count != 1 || recovered.Meshes[0].PathId != 7 ||
                overlapping == null || overlapping.Meshes.Count != 1 ||
                reads["first"] != 1 || reads["later"] != 1 || reads.ContainsKey("unused"))
                throw new Exception("Readable first garment did not recover later garment's shared mesh before dependency load");
        }
        // Failed discovery must not permanently poison the shared result or root request.
        int attempts = 0;
        var retry = new ClothingBundleRequests(new[] { "first" }, sources, null, (key, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("transient discovery failure");
            return new ClothingBundlePlan { Garments = 1, ReadableMeshes = 1 };
        });
        retry.Request(new[] { "first" });
        await retry.TargetFor("first");
        retry.Request(new[] { "first" });
        await retry.TargetFor("first");
        retry.Request(new[] { "first" });
        await retry.TargetFor("first");
        if (attempts != 2) throw new Exception("Failed discovery did not retry or readable result was rescanned");
        Console.WriteLine("PASS: overlapping roots share discovery, preserve later shared meshes, bypass unrelated loads and retry failures");
    }
}
