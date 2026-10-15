using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Fika.Core.Networking.Packets.Player.Common.SubPackets;

internal static class ImpactAbiTests
{
    private static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    internal static void Run(Assembly plugin)
    {
        using var fika = AssemblyDefinition.ReadAssembly(typeof(DamagePacket).Assembly.Location);
        using var rupture = AssemblyDefinition.ReadAssembly(plugin.Location);
        using var game = AssemblyDefinition.ReadAssembly(typeof(EFT.Ballistics.Shot).Assembly.Location);
        using var bridge = AssemblyDefinition.ReadAssembly(Program.Bridge.Location);
        var network = bridge.MainModule.GetType("Rupture.Impacts.FikaImpactNetwork");
        var send = network.Methods.Single(m => m.Name == "SendDamage");
        string[] types = { "ObservedPlayer", "FikaPlayer", "FikaBot" };
        var sites = fika.MainModule.Types.Where(t => types.Contains(t.Name)).SelectMany(t => t.Methods)
            .Where(m => m.HasBody).SelectMany(m => m.Body.Instructions.Select(i => (Method: m, Instruction: i)))
            .Where(p => p.Instruction.Operand is GenericInstanceMethod method && method.Name == "SendPlayerPacket" &&
                method.DeclaringType.FullName == "Fika.Core.Networking.IFikaNetworkManager" &&
                method.GenericArguments.Count == 1 && method.GenericArguments[0].Name == "DamagePacket").ToArray();
        Check(sites.Length > 0 && sites.Any(p => p.Method.DeclaringType.Name == "ObservedPlayer" && p.Method.Name == "ApplyShot") &&
            sites.Any(p => p.Method.Name == "ApplyClientShot"), "installed Fika ballistic damage send sites are present");
        foreach (var site in sites)
        {
            var original = (GenericInstanceMethod)site.Instruction.Operand;
            Check(original.HasThis && send.IsStatic && original.Parameters.Count + 1 == send.Parameters.Count &&
                send.Parameters[0].ParameterType.FullName == original.DeclaringType.FullName &&
                send.Parameters[1].ParameterType.FullName == "Fika.Core.Networking.Packets.Player.Common.SubPackets.DamagePacket&" &&
                send.Parameters[1].IsIn && send.Parameters[2].ParameterType.FullName == "System.Int32" &&
                original.Parameters.Skip(1).Select(p => p.ParameterType.FullName).SequenceEqual(send.Parameters.Skip(2).Select(p => p.ParameterType.FullName)),
                "stack-compatible Fika envelope hook: " + site.Method.DeclaringType.Name + "." + site.Method.Name);
        }
        foreach (string name in new[] { "EFT.GameWorld", "EFT.ClientGameWorld" })
            Check(game.MainModule.GetType(name).Methods.Any(m => m.Name == "ShotDelegate" && m.Parameters.Count == 1 && m.Parameters[0].Name == "shotResult"), "collision hook ABI: " + name);
        Check(fika.MainModule.Types.Single(t => t.Name == "FikaClientGameWorld").Methods.Any(m => m.Name == "ShotDelegate" && m.Parameters.Count == 1 && m.Parameters[0].Name == "shotResult"), "collision hook ABI: FikaClientGameWorld");
        var health = game.MainModule.GetType("EFT.HealthSystem.ActiveHealthController").Methods.Single(m => m.Name == "ChangeHealth");
        Check(health.Parameters.Any(p => p.Name == "bodyPart") && health.Parameters.Any(p => p.Name == "value"), "positive healing hook parameter ABI");
        var grenade = game.MainModule.GetType("EFT.Grenade");
        Check(grenade.Methods.Single(m => m.Name == "Explosion" && m.IsStatic && m.Parameters.Count == 8).Parameters.Any(p => p.Name == "shift" && p.ParameterType.FullName == "UnityEngine.Vector3"),
            "native grenade hook exposes the explosion offset");
        var damageConstructor = game.MainModule.GetType("EFT.Ballistics.DamageInfo").Methods.Single(m => m.IsConstructor);
        Check(damageConstructor.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference f && f.FullName == "UnityEngine.Vector3 EFT.Ballistics.Shot::MasterOrigin") &&
            damageConstructor.Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.FullName == "UnityEngine.Vector3 EFT.Ballistics.DamageInfo::MasterOrigin"),
            "installed EFT copies projectile master origin into damage metadata");
        var blastFactory = grenade.Methods.Single(m => m.Name == "GetDamageInfo");
        Check(blastFactory.Parameters[2].ParameterType.FullName == "UnityEngine.Vector3" &&
            blastFactory.Body.Instructions.Take(4).Last().OpCode == OpCodes.Ldarg_2 &&
            blastFactory.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "Create" && m.DeclaringType.FullName == "EFT.Ballistics.Shot") &&
            blastFactory.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == ".ctor" && m.DeclaringType.FullName == "EFT.Ballistics.DamageInfo"),
            "installed radial grenade damage uses the explosion center as its projectile origin");
        var receive = network.Methods.Single(m => m.Name == "ReceiveDamage");
        Check(receive.Body.Instructions.Count(i => i.Operand is MethodReference m && m.Name == "Execute" && m.DeclaringType.Name == "DamagePacket") == 1 &&
            !receive.Body.Instructions.Any(i => i.Operand is MethodReference m && (m.Name == "ApplyDamageInfo" || m.Name == "HandleDamagePacket")), "envelope invokes stock packet execution at one site, without direct damage reapplication");
        var identity = rupture.MainModule.GetType("Rupture.RupturePlugin").CustomAttributes.Single(a => a.AttributeType.Name == "BepInPlugin");
        Check((string)identity.ConstructorArguments[0].Value == "com.rootdarkarchon.rupture" && (string)identity.ConstructorArguments[1].Value == "Rupture" &&
            (string)identity.ConstructorArguments[2].Value == "1.2.2", "built plugin GUID, name and version");
        Check(!rupture.MainModule.AssemblyReferences.Any(a => a.Name.IndexOf("bundleloader", StringComparison.OrdinalIgnoreCase) >= 0 || a.Name.IndexOf("visceral", StringComparison.OrdinalIgnoreCase) >= 0), "built plugin has no legacy assembly dependency");
    }
}
