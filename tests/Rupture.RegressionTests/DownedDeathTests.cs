using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class DownedDeathTests
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Check(bool value, string name)
    { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }

    internal static void Run(Assembly core, Assembly bridge)
    {
        Type service = core.GetType("Rupture.Impacts.ImpactStateService", true);
        Type victim = service.GetNestedType("VictimState", Flags);
        object New() => Activator.CreateInstance(victim, true);
        void Set(object state, string name, object value) => victim.GetField(name, Flags).SetValue(state, value);
        bool Flag(object state, string name) => (bool)victim.GetField(name, Flags).GetValue(state);
        bool Begin(object state, bool downed) => (bool)service.GetMethod("TryBeginDeath", Flags).Invoke(null, new[] { state, (object)downed });

        object living = New();
        var limbs = (Array)victim.GetField("Limbs", Flags).GetValue(living);
        object limb = limbs.GetValue(0);
        var add = limb.GetType().GetMethod("Add", Flags);
        add.Invoke(limb, new object[] { 1UL, 6f, 12f });
        for (int cycle = 0; cycle < 3; cycle++)
        {
            Check(!Begin(living, true) && !Flag(living, "DeathSent") && !Flag(living, "DeathApplied") && !Flag(living, "DeathQueued"),
                "recoverable downing consumes no death state, cycle " + cycle);
            Check(ReferenceEquals(limbs.GetValue(0), limb), "downing retains accumulated limb state");
            // Revival does not call the death path; another downing uses the same lifetime.
        }
        Check(!(bool)add.Invoke(limb, new object[] { 1UL, 6f, 12f }), "down/revive retains projectile deduplication");
        Set(living, "TerminalDeathObserved", true);
        Check(Begin(living, false) && !Begin(living, false), "death after repeated revives publishes once");

        object bleedout = New();
        Check(!Begin(bleedout, true), "initial downing is not bleedout");
        Set(bleedout, "TerminalDeathObserved", true);
        Check(Begin(bleedout, true) && !Begin(bleedout, true), "terminal bleedout overrides lingering Downed and publishes once");
        foreach (string cause in new[] { "headshot", "grenade", "natural", "standalone" })
        {
            object state = New();
            Set(state, "TerminalDeathObserved", cause != "standalone");
            Check(Begin(state, false) && !Begin(state, false), cause + " death preserves one-time publication");
        }
        foreach (string flag in new[] { "DeathSent", "DeathApplied", "AwaitingDeathSnapshot" })
        {
            object state = New(); Set(state, flag, true); Set(state, "TerminalDeathObserved", true);
            Check(!Begin(state, true), "terminal downed death respects existing ownership/deduplication: " + flag);
        }

        using var binary = AssemblyDefinition.ReadAssembly(core.Location);
        var impact = binary.MainModule.GetType(service.FullName);
        var queue = impact.Methods.Single(m => m.Name == "QueueDeath").Body.Instructions;
        Check(queue.Any(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "TerminalDeathObserved"),
            "actual OnDead queue records terminal lifetime before publication");
        var death = impact.Methods.Single(m => m.Name == "Death").Body.Instructions;
        Check(death.Any(i => i.Operand is MethodReference m && m.Name == "IsDowned") &&
            death.Any(i => i.Operand is MethodReference m && m.Name == "TryBeginDeath"), "production death entry uses downed policy");
        var standalone = binary.MainModule.GetType("Rupture.StandaloneRuntime").Methods.Single(m => m.Name == "IsDowned");
        Check(standalone.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4_0), "standalone has no downed state");
        using var adapter = AssemblyDefinition.ReadAssembly(bridge.Location);
        Check(adapter.MainModule.GetType("Rupture.Fika.FikaRuntime").Methods.Single(m => m.Name == "IsDowned").Body.Instructions
            .Any(i => i.Operand is MethodReference m && m.Name == "get_Downed" && m.DeclaringType.FullName == "Fika.Core.Main.Players.FikaPlayer"),
            "adapter reads baseline Fika downed state");
        var dependencies = binary.MainModule.GetType("Rupture.RupturePlugin").CustomAttributes
            .Where(a => a.AttributeType.FullName == "BepInEx.BepInDependency").ToArray();
        Check(dependencies.All(a => a.Constructor.Parameters.Count == 2 && a.Constructor.Parameters[1].ParameterType.FullName != "System.String"),
            "plugin dependencies contain no SPT or Fika version requirement");
        Check(typeof(Fika.Core.Main.Players.FikaPlayer).GetProperty("Downed")?.PropertyType == typeof(bool), "Fika downed ABI exists");
    }
}
