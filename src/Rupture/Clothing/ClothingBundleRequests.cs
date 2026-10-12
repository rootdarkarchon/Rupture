using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Rupture.Clothing
{
    // Catalog indexing is metadata-only. Bundle IO starts only for requested garments.
    internal sealed class ClothingBundleRequests
    {
        private readonly IDictionary<string, ClothingBundleSource> _sources;
        private readonly Dictionary<string, HashSet<string>> _dependents = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[]> _closures = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task<ClothingBundlePlan>> _requests = new Dictionary<string, Task<ClothingBundlePlan>>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Lazy<Task<ClothingBundlePlan>>> _discoveries = new ConcurrentDictionary<string, Lazy<Task<ClothingBundlePlan>>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Task<ClothingBundlePlan>>> _waiting = new Dictionary<string, List<Task<ClothingBundlePlan>>>(StringComparer.OrdinalIgnoreCase);
        private readonly Action<string> _report;
        private readonly Func<string, CancellationToken, ClothingBundlePlan> _discover;
        private readonly SemaphoreSlim _workers = new SemaphoreSlim(2, 2);

        internal int RequestedGarments => _requests.Count;

        internal ClothingBundleRequests(IEnumerable<string> roots, IDictionary<string, ClothingBundleSource> sources, Action<string> report,
            Func<string, CancellationToken, ClothingBundlePlan> discover = null)
        {
            _sources = sources; _report = report;
            _discover = discover ?? ((key, token) => ClothingBundlePlan.Discover(new[] { key }, sources, cancellation: token));
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AddDependencies(root, closure);
                _closures[root] = closure.ToArray();
                foreach (string key in closure)
                {
                    if (!_dependents.TryGetValue(key, out var dependents))
                        _dependents.Add(key, dependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    dependents.Add(root);
                }
            }
        }

        private void AddDependencies(string key, HashSet<string> closure)
        {
            if (!closure.Add(key) || !_sources.TryGetValue(key, out var source)) return;
            foreach (string dependency in source.Dependencies) AddDependencies(dependency, closure);
        }

        internal void Request(IEnumerable<string> keys)
        {
            foreach (string key in keys)
            {
                if (!_closures.TryGetValue(key, out var closure)) continue;
                if (_requests.TryGetValue(key, out var previous) && !previous.IsFaulted && !previous.IsCanceled) continue;
                var task = ClothingBundleWork.RunAsync(token => DiscoverRequested(key, token),
                    TimeSpan.FromSeconds(30), "Requested clothing discovery " + key);
                _requests[key] = task;
                // Register dependency waits synchronously, before native scheduling starts.
                foreach (string dependency in closure)
                {
                    if (!_waiting.TryGetValue(dependency, out var tasks)) _waiting.Add(dependency, tasks = new List<Task<ClothingBundlePlan>>());
                    tasks.Add(task);
                }
                _ = task.ContinueWith(t => _report?.Invoke("Requested clothing discovery failed: " + key + ": " + t.Exception.GetBaseException().Message),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        private async Task<ClothingBundlePlan> DiscoverOnce(string key)
        {
            // Cache immutable metadata, including readable-only results. A request's
            // cancellation must not cancel discovery shared with another request.
            var entry = _discoveries.GetOrAdd(key, k => new Lazy<Task<ClothingBundlePlan>>(() =>
                ClothingBundleWork.RunAsync(async cancellation =>
                {
                    await _workers.WaitAsync(cancellation).ConfigureAwait(false);
                    try
                    {
                        var plan = await Task.Run(() => _discover(k, cancellation), cancellation).ConfigureAwait(false);
                        if (plan.Diagnostics.Count != 0) throw new InvalidDataException(string.Join("; ", plan.Diagnostics));
                        return plan;
                    }
                    finally { _workers.Release(); }
                }, TimeSpan.FromSeconds(30), "Clothing discovery " + k), LazyThreadSafetyMode.ExecutionAndPublication));
            try { return await entry.Value.ConfigureAwait(false); }
            catch
            {
                // Remove only this failed attempt; an older waiter must not evict a retry.
                ((ICollection<KeyValuePair<string, Lazy<Task<ClothingBundlePlan>>>>)_discoveries)
                    .Remove(new KeyValuePair<string, Lazy<Task<ClothingBundlePlan>>>(key, entry));
                throw;
            }
        }

        private async Task<ClothingBundlePlan> DiscoverRequested(string root, CancellationToken token)
        {
            var result = new ClothingBundlePlan();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var relevantBundles = new HashSet<string>(_closures[root], StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>(); pending.Enqueue(root);
            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                string key = pending.Dequeue();
                if (!visited.Add(key)) continue;
                var plan = await DiscoverOnce(key).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                result.Garments += plan.Garments; result.ReadableMeshes += plan.ReadableMeshes;
                result.Diagnostics.AddRange(plan.Diagnostics);
                foreach (var target in plan.Targets.Values)
                    Merge(result, target);
                foreach (var ownerBundle in plan.MeshBundles.Values)
                {
                    // A shared owning bundle is loaded only once. Find its other clothing
                    // references now, even when those garments will be requested later.
                    if (relevantBundles.Contains(ownerBundle.Key) && _dependents.TryGetValue(ownerBundle.Key, out var owners))
                        foreach (string owner in owners) if (!visited.Contains(owner)) pending.Enqueue(owner);
                }
            }
            if (result.Diagnostics.Count != 0) throw new InvalidDataException(string.Join("; ", result.Diagnostics));
            _report?.Invoke($"Requested clothing discovery: {root}, garments={result.Garments}, recoveryBundles={result.Targets.Count}.");
            return result;
        }

        internal async Task<ClothingBundleTarget> TargetFor(string key)
        {
            if (!_waiting.TryGetValue(key, out var waiting)) return null;
            var plans = waiting.ToArray();
            var result = new ClothingBundlePlan();
            foreach (var task in plans)
            {
                // Faults are reported once by Request. Native validation/loading continues.
                ClothingBundlePlan plan;
                try { plan = await task; } catch { continue; }
                if (_sources.TryGetValue(key, out var source) && plan.Targets.TryGetValue(source.Path, out var target)) Merge(result, target);
            }
            return result.Targets.Values.SingleOrDefault();
        }

        private static void Merge(ClothingBundlePlan plan, ClothingBundleTarget source)
        {
            if (!plan.Targets.TryGetValue(source.Source.Path, out var target))
                plan.Targets.Add(source.Source.Path, target = new ClothingBundleTarget { Source = source.Source, Hash = source.Hash });
            if (target.Hash != source.Hash) throw new InvalidDataException("clothing bundle changed between requests");
            foreach (var mesh in source.Meshes)
                if (!target.Meshes.Any(m => m.File == mesh.File && m.PathId == mesh.PathId)) target.Meshes.Add(mesh);
        }
    }
}
