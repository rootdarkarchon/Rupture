using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class OptionalFikaTests
{
    private sealed class WithoutFika : AssemblyLoadContext
    {
        internal int ForbiddenResolutions;
        internal WithoutFika() : base(isCollectible: true) { }
        protected override Assembly Load(AssemblyName name)
        {
            if (name.Name.StartsWith("Fika", StringComparison.Ordinal) || name.Name == "Rupture.Fika")
            {
                ForbiddenResolutions++;
                throw new FileNotFoundException("Fika deliberately unavailable: " + name.Name);
            }
            string path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }

    internal static void Run(string corePath, string bridgePath)
    {
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(AppContext.BaseDirectory);
        using var core = AssemblyDefinition.ReadAssembly(corePath, new ReaderParameters { AssemblyResolver = resolver });
        using var bridge = AssemblyDefinition.ReadAssembly(bridgePath);
        Check(!core.MainModule.AssemblyReferences.Any(a => a.Name.Contains("Fika")), "core has no Fika or bridge assembly references");
        Check(bridge.MainModule.AssemblyReferences.Any(a => a.Name == "Fika.Core") &&
            !bridge.MainModule.Types.Any(t => t.CustomAttributes.Any(a => a.AttributeType.Name == "BepInPlugin")),
            "Fika bridge is explicitly loaded and is not another BepInEx plugin");
        var plugin = core.MainModule.GetType("Rupture.RupturePlugin");
        var dependency = plugin.CustomAttributes.Single(a => a.AttributeType.Name == "BepInDependency" &&
            (string)a.ConstructorArguments[0].Value == "com.fika.core");
        Check(Convert.ToInt32(dependency.ConstructorArguments[1].Value) == 2, "main plugin declares Fika as a soft dependency");
        var awake = plugin.Methods.Single(m => m.Name == "Awake");
        var calls = awake.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        int init = Array.FindIndex(calls, m => m.DeclaringType.Name == "MultiplayerRuntime" && m.Name == "Initialize");
        Check(init >= 0 && init < Array.FindIndex(calls, m => m.DeclaringType.Name == "WoundResources" && m.Name == "Load") &&
            init < Array.FindIndex(calls, m => m.DeclaringType.Name == "ImpactHooks" && m.Name == "Install") &&
            awake.Body.ExceptionHandlers.Any(h => h.HandlerType == ExceptionHandlerType.Catch),
            "bridge initializes before resources and native hooks with an initialization failure boundary");
        var runtime = bridge.MainModule.GetType("Rupture.Fika.FikaRuntime");
        string[] RuntimeCalls(string method) => runtime.Methods.Single(m => m.Name == method).Body.Instructions
            .Select(i => i.Operand).OfType<MethodReference>().Select(m => m.Name).ToArray();
        Check(RuntimeCalls("Initialize").Contains("SubscribeEvent") && RuntimeCalls("Dispose").Contains("UnsubscribeEvent") &&
            RuntimeCalls("Dispose").Count(n => n == "Shutdown") == 3,
            "bridge teardown removes manager subscriptions, Fika hooks and impact network state");
        var hooks = bridge.MainModule.GetType("Rupture.Impacts.FikaImpactHooks").Methods.Single(m => m.Name == "Install");
        Check(hooks.Body.Instructions.Count(i => i.OpCode == OpCodes.Ldtoken && i.Operand is TypeReference t &&
            t.FullName == "Rupture.Impacts.ImpactHooks") == 2,
            "Fika collision hooks use the same core impact prefix and finalizer");

        var isolated = new WithoutFika();
        try
        {
            var assembly = isolated.LoadFromAssemblyPath(corePath);
            // Type enumeration catches missing interface/base/signature dependencies, not just a lazy assembly load.
            Check(assembly.GetTypes().Length > 100, "all core types load with Fika resolution forbidden");
            var owner = assembly.GetType("Rupture.MultiplayerRuntime", true);
            var current = owner.GetProperty("Current", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var type = current.GetType();
            foreach (string name in new[] { "IsHeadless", "IsRemoteClient", "IsNetworkAuthority", "HasServer", "HasClient" })
                Check(!(bool)type.GetProperty(name).GetValue(current), "standalone " + name + " is false");
            Check((int)type.GetMethod("PlayerId").Invoke(current, new object[] { null }) == -1 &&
                !(bool)type.GetMethod("IsObservedPlayer").Invoke(current, new object[] { null }) &&
                !(bool)type.GetMethod("IsDowned").Invoke(current, new object[] { null }) &&
                !(bool)type.GetMethod("IsRemoteVictim").Invoke(current, new object[] { "victim" }),
                "standalone identities cannot create remote ownership or await a remote death snapshot");
            foreach (string name in new[] { "Initialize", "Update", "ConfigurationChanged", "ResetRaid", "Dispose" })
                type.GetMethod(name).Invoke(current, null);
            foreach (var send in type.GetMethods().Where(m => m.Name == "Send"))
            {
                var dataType = send.GetParameters()[0].ParameterType.GetElementType();
                send.Invoke(current, new[] { Activator.CreateInstance(dataType), (object)false });
            }
            Check(isolated.ForbiddenResolutions == 0 && !isolated.Assemblies.Any(a => a.GetName().Name.Contains("Fika")),
                "standalone lifecycle and every effect transport perform no Fika assembly resolution");
        }
        finally { isolated.Unload(); }
    }
}
