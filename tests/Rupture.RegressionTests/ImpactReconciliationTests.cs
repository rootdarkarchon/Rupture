using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

internal static class ImpactReconciliationTests
{
    internal static void Run(Assembly core, Assembly bridge)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        Type network = bridge.GetType("Rupture.Impacts.FikaImpactNetwork", true);
        MethodInfo match = network.GetMethod("MatchIdentity", flags);
        void Check(int id, uint death, string profile, int currentId, uint currentDeath, string expected)
        {
            string result = match.Invoke(null, new object[] { "victim", id, death, profile, currentId, currentDeath }).ToString();
            if (result != expected) throw new Exception($"Identity {id}/{death} -> {profile}/{currentId}/{currentDeath}: {result}, expected {expected}");
        }
        Check(-1, 0, "victim", -1, 0, "WaitingIdentity");
        Check(-1, 0, "victim", 12, 0, "WaitingIdentity");
        Check(-1, 0, "victim", 12, 1, "Applied");
        Check(12, 1, "victim", 12, 1, "Applied");
        Check(12, 1, "victim", 12, 2, "Stale");
        Check(12, 1, "victim", 13, 1, "Stale");
        Check(-1, 0, "other", 12, 1, "Stale");

        using var binary = AssemblyDefinition.ReadAssembly(bridge.Location);
        TypeDefinition type = binary.MainModule.GetType(network.FullName);
        string[] Calls(string name) => type.Methods.Single(m => m.Name == name).Body.Instructions
            .Select(i => i.Operand).OfType<MethodReference>().Select(m => m.Name).ToArray();
        var bound = Calls("TryApplyBound");
        if (Array.IndexOf(bound, "TryGetBoundInteractionTarget") < 0 ||
            Array.IndexOf(bound, "TryGetBoundInteractionTarget") > Array.IndexOf(bound, "SendCorpsePacket"))
            throw new Exception("Pending hits must resolve their exact live corpse before sending.");
        if (type.Methods.Single(m => m.Name == "MakeCorpseState").Body.Instructions.Any(i =>
            i.Operand is FieldReference field && (field.Name == "_sequence" || field.Name == "Sequence")))
            throw new Exception("Provisional hits must not consume wire sequence numbers.");
        var send = type.Methods.Single(m => m.Name == "SendCorpsePacket").Body.Instructions;
        int stamp = send.ToList().FindIndex(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Stfld && i.Operand is FieldReference f && f.Name == "Sequence");
        int transmit = send.ToList().FindIndex(i => i.Operand is MethodReference m && m.Name == "SendData");
        if (stamp < 0 || transmit <= stamp) throw new Exception("Wire sequence must be assigned immediately before sending.");
        // Packet shape and the shared replay gate remain unchanged when death
        // traffic overtakes a provisional hit.
        var received = new Dictionary<string, uint>();
        MethodInfo accept = network.GetMethod("AcceptSequence", flags);
        string session = new string('a', 32);
        foreach (uint sequence in new uint[] { 8, 9 })
            if (!(bool)accept.Invoke(null, new object[] { received, session, sequence })) throw new Exception("Delayed hit rejected after intervening death packet.");
        if ((bool)accept.Invoke(null, new object[] { received, session, 9u })) throw new Exception("Duplicate hit passed replay protection.");
        using var coreBinary = AssemblyDefinition.ReadAssembly(core.Location);
        var lookup = coreBinary.MainModule.GetType("Rupture.Ragdolls.Classes.RagdollCoordinator").Methods.Single(m => m.Name == "TryGetBoundInteractionTarget");
        if (lookup.Body.Instructions.Any(i => i.Operand is MethodReference m && (m.Name == "GetStateForCorpse" || m.Name == "BindStateToCorpse")))
            throw new Exception("Pending hits must not restore or rebind retired corpses.");
        var apply = type.Methods.Single(m => m.Name == "TryApply").Body.Instructions;
        int duplicate = apply.ToList().FindIndex(i => i.Operand is FieldReference f && f.Name == "DeathApplied");
        int players = apply.ToList().FindIndex(i => i.Operand is MethodReference m && m.Name == "get_Players");
        if (duplicate < 0 || players <= duplicate) throw new Exception("Completed death duplicates require the live player dictionary.");
        Console.WriteLine("PASS: provisional impact identity, stale lifetime rejection, send-time sequencing, replay protection and retained death duplicate handling");
    }
}
