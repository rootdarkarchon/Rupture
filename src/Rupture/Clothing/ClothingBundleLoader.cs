using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Comfort.Common;
using EFT;
using HarmonyLib;

namespace Rupture.Clothing
{
    internal static class ClothingBundleLoader
    {
        private static readonly Dictionary<string, ClothingBundleSource> Sources = new Dictionary<string, ClothingBundleSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Task<byte[]>> Preparing = new Dictionary<string, Task<byte[]>>(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim RewriteSlot = new SemaphoreSlim(1, 1);
        private static EFT.CustomizationSolver _catalog;
        private static ClothingBundleRequests _requests;

        internal static void Install()
        {
            if (MultiplayerRuntime.Current.IsHeadless) return;
            ClothingBundleDecompression.Initialize(WoundResources.PluginRoot, message => RupturePlugin.Log.LogWarning(message));
            var harmony = new Harmony("com.rootdarkarchon.rupture.clothing");
            try
            {
                ClothingBundleHooks.Install(harmony);
                var schedule = AccessTools.Method(typeof(Diz.DependencyManager.DependencyGraph<Diz.Resources.IEasyBundle>), "ProcessPendingLoads",
                    new[] { typeof(List<Diz.DependencyManager.Node<Diz.Resources.IEasyBundle>>) });
                if (schedule == null) throw new MissingMethodException("EFT pending bundle scheduler ABI is unavailable");
                harmony.Patch(schedule, prefix: new HarmonyMethod(AccessTools.Method(typeof(ClothingBundleLoader), nameof(BeforeSchedule))));
                foreach (var constructor in AccessTools.GetDeclaredConstructors(typeof(Diz.Resources.EasyBundle)))
                    harmony.Patch(constructor, postfix: new HarmonyMethod(AccessTools.Method(typeof(ClothingBundleLoader), nameof(Register))) { priority = Priority.Last });
                RupturePlugin.Log.Initialization("Requested clothing readability loader installed; no startup bundle scan; bundle files and download caches are unchanged.");
            }
            catch (Exception e)
            {
                harmony.UnpatchSelf();
                RupturePlugin.Log.LogWarning("Clothing readability loader could not be installed; native loading remains active. " + e);
            }
        }

        private static void Register(Diz.Resources.EasyBundle __instance)
        {
            // Runs after SPT resolves local mod files / downloaded cache paths.
            Sources[__instance.Key] = new ClothingBundleSource { Key = __instance.Key, Path = __instance._path,
                Dependencies = (__instance.DependencyKeys ?? Array.Empty<string>()).ToArray() };
            __instance._bundleCheck = ClothingBundleHooks.WrapValidation(__instance._bundleCheck, () => Prepare(__instance));
        }

        private static ClothingBundleRequests Requests()
        {
            var catalog = Singleton<EFT.CustomizationSolver>.Instance;
            if (catalog == null) return null;
            if (!ReferenceEquals(catalog, _catalog))
            {
                var roots = catalog.ClothingTemplates.Where(t => t.BodyPart == EBodyModelPart.Body || t.BodyPart == EBodyModelPart.Feet)
                    .Select(t => t.Prefab?.path).Where(p => !string.IsNullOrEmpty(p));
                _requests = new ClothingBundleRequests(roots,
                    new Dictionary<string, ClothingBundleSource>(Sources, StringComparer.OrdinalIgnoreCase),
                    message =>
                    {
                        if (message.StartsWith("Requested clothing discovery failed:", StringComparison.Ordinal)) RupturePlugin.Log.LogWarning(message);
                        else RupturePlugin.Log.LogInfo(message);
                    });
                _catalog = catalog;
            }
            return _requests;
        }

        private static void BeforeSchedule(object __0)
        {
            // Generic reference-type instantiations may share Mono code. Ignore other
            // graph types, and observe the list without changing native scheduling.
            if (!(__0 is List<Diz.DependencyManager.Node<Diz.Resources.IEasyBundle>> pending) || pending.Count == 0) return;
            try { Requests()?.Request(pending.Select(n => n.Data.Key)); }
            catch (Exception e) { RupturePlugin.Log.LogWarning("Clothing request registration failed: " + e); }
        }

        private static async Task Prepare(Diz.Resources.EasyBundle instance)
        {
            try
            {
                var requests = Requests();
                if (requests == null) return;
                // Also covers a garment loaded directly outside the dependency scheduler.
                requests.Request(new[] { instance.Key });
                var target = await requests.TargetFor(instance.Key);
                if (target == null) return;
                if (!Preparing.TryGetValue(instance._path, out var task))
                {
                    task = RewriteBounded(target);
                    Preparing.Add(instance._path, task);
                }
                var timer = Stopwatch.StartNew();
                byte[] bytes = await task;
                ClothingBundleHooks.Stage(instance._path, bytes);
                Preparing.Remove(instance._path);
                RupturePlugin.Log.LogInfo($"Clothing readability prepared: {instance.Key}, meshes={target.Meshes.Count}, bytes={bytes.Length}, waitMs={timer.Elapsed.TotalMilliseconds:F1}.");
            }
            catch (Exception e)
            {
                Preparing.Remove(instance._path);
                ClothingBundleHooks.Discard(instance._path);
                RupturePlugin.Log.LogWarning("Clothing readability recovery failed; loading original bundle " + instance.Key + ": " + e);
            }
        }

        private static Task<byte[]> RewriteBounded(ClothingBundleTarget target)
        {
            return ClothingBundleWork.RunAsync(async token =>
            {
                await RewriteSlot.WaitAsync(token);
                try { return await Task.Run(() => ClothingBundlePlan.Rewrite(target, token), token); }
                finally { RewriteSlot.Release(); }
            }, TimeSpan.FromSeconds(30), "Clothing rewrite " + target.Source.Key);
        }

    }
}
