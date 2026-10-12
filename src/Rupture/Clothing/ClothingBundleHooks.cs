using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace Rupture.Clothing
{
    // Keep interception outside EFT's struct async state machine. Patching MoveNext
    // caused repeated continuations on the game's Mono runtime during startup.
    internal static class ClothingBundleHooks
    {
        private static readonly Dictionary<string, byte[]> Prepared = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        internal static void Install(Harmony harmony)
        {
            var load = AccessTools.Method(typeof(AssetBundle), nameof(AssetBundle.LoadFromFileAsync), new[] { typeof(string) });
            if (load?.GetMethodBody() == null) throw new MissingMethodException("Unity managed bundle-load overload is unavailable");
            harmony.Patch(load, prefix: new HarmonyMethod(AccessTools.Method(typeof(ClothingBundleHooks), nameof(BeforeFileLoad))));
        }

        internal static Func<string, Task> WrapValidation(Func<string, Task> validate, Func<Task> prepare)
        {
            return async key =>
            {
                if (validate != null) await validate(key);
                await prepare();
            };
        }

        internal static void Stage(string path, byte[] bytes) { lock (Prepared) Prepared[path] = bytes; }
        internal static void Discard(string path) { lock (Prepared) Prepared.Remove(path); }

        private static bool BeforeFileLoad(string path, ref AssetBundleCreateRequest __result)
        {
            byte[] bytes;
            lock (Prepared)
            {
                if (!Prepared.TryGetValue(path, out bytes)) return true;
                Prepared.Remove(path);
            }
            __result = AssetBundle.LoadFromMemoryAsync(bytes);
            return false;
        }
    }
}
