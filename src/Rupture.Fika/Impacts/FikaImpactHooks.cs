using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using EFT;
using EFT.HealthSystem;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Players;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.Packets.Backend;
using Fika.Core.Networking.Packets.Player.Common.SubPackets;
using HarmonyLib;

namespace Rupture.Impacts;

internal static class FikaImpactHooks
{
    private static readonly Harmony Patches = new Harmony("com.rootdarkarchon.rupture.fika.impacts");
    private static readonly MethodInfo InformationCallback = typeof(FikaServer).GetMethod("OnInformationPacketReceived",
        BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(InformationPacket), typeof(NetPeer) }, null) ??
        throw new MissingMethodException(typeof(FikaServer).FullName, "OnInformationPacketReceived");
    internal static void Install()
    {
        MethodInfo shot = typeof(FikaClientGameWorld).GetMethod(nameof(GameWorld.ShotDelegate), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            null, new[] { typeof(EFT.Ballistics.Shot) }, null);
        if (shot == null) throw new MissingMethodException(typeof(FikaClientGameWorld).FullName, nameof(GameWorld.ShotDelegate));
        Patches.Patch(shot, prefix: new HarmonyMethod(typeof(ImpactHooks), nameof(ImpactHooks.ShotPrefix)),
            finalizer: new HarmonyMethod(typeof(ImpactHooks), nameof(ImpactHooks.ShotFinally)));
        int sites = 0;
        // Patch concrete DamagePacket call sites, leaving every other player packet
        // and the shared generic network sender untouched.
        foreach (Type type in new[] { typeof(ObservedPlayer), typeof(FikaPlayer), typeof(FikaBot) })
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (method.GetMethodBody() == null || method.ContainsGenericParameters) continue;
            if (!OriginalInstructions(method).Any(IsDamageSend)) continue;
            Patches.Patch(method, transpiler: Method(nameof(WrapDamageSend)));
            sites++;
        }
        if (sites == 0) throw new MissingMethodException("Fika DamagePacket send sites are unavailable.");
        Patches.Patch(InformationCallback, prefix: Method(nameof(BeforeInformation)));
        RupturePlugin.Log.LogInfo($"RPT impact hooks installed; Fika damage packet send methods={sites}.");
    }
    private static IEnumerable<CodeInstruction> OriginalInstructions(MethodInfo method)
    {
        return (IEnumerable<CodeInstruction>)ResolveInstructionReader().Invoke(null, new object[] { method, null });
    }
    private static MethodInfo ResolveInstructionReader()
    {
        // The installed Harmony overload exposes a framework ILGenerator optional argument.
        // Reflection avoids a netstandard/framework facade mismatch at compile time.
        return typeof(PatchProcessor).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(candidate => candidate.Name == "GetOriginalInstructions" &&
                candidate.GetParameters().Length == 2 &&
                candidate.GetParameters()[0].ParameterType == typeof(MethodBase) &&
                candidate.GetParameters()[1].ParameterType.FullName == "System.Reflection.Emit.ILGenerator");
    }
    internal static void Shutdown() => Patches.UnpatchSelf();
    private static HarmonyMethod Method(string name) => new HarmonyMethod(typeof(FikaImpactHooks), name);
    private static void BeforeInformation(FikaServer __instance, ref InformationPacket packet, NetPeer peer) =>
        FikaImpactNetwork.BeforeInformation(__instance, ref packet, peer);
    // Replay only the start flag. Readiness counts and peer identity were already
    // processed by the original callback when the start request was deferred.
    internal static void ReleaseRaidStart(FikaServer server, NetPeer peer) =>
        InformationCallback.Invoke(server, new object[] { new InformationPacket { RequestStart = true }, peer });
    internal static bool IsDamageSend(CodeInstruction instruction) => instruction.operand is MethodInfo method &&
        method.DeclaringType == typeof(IFikaNetworkManager) && method.Name == nameof(IFikaNetworkManager.SendPlayerPacket) && method.IsGenericMethod &&
        method.GetGenericArguments().Length == 1 && method.GetGenericArguments()[0] == typeof(DamagePacket);
    internal static IEnumerable<CodeInstruction> WrapDamageSend(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (IsDamageSend(instruction))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = typeof(FikaImpactNetwork).GetMethod(nameof(FikaImpactNetwork.SendDamage), BindingFlags.Static | BindingFlags.NonPublic);
            }
            yield return instruction;
        }
    }
}
