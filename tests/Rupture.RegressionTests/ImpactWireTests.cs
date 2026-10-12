using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using HarmonyLib;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib.Utils;
using Fika.Core.Networking.Packets.Player.Common.SubPackets;
using EFT;
using UnityEngine;

internal static class ImpactWireTests
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Set(object value, string field, object data) => value.GetType().GetField(field).SetValue(value,data);
    private static object Get(object value, string field) => value.GetType().GetField(field).GetValue(value);
    private static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    private static object Roundtrip(object value)
    {
        NetDataWriter writer = new NetDataWriter(); ((INetSerializable)value).Serialize(writer);
        object result = Activator.CreateInstance(value.GetType());
        NetDataReader reader = new NetDataReader(writer.CopyData()); ((INetSerializable)result).Deserialize(reader);
        Check(reader.AvailableBytes == 0, "packet consumes its exact wire payload: " + value.GetType().Name);
        return result;
    }
    internal static void Run(Assembly assembly)
    {
        // Exercise the actual startup reader: Harmony also exposes a ref ILGenerator
        // overload, which must not make the runtime lookup ambiguous.
        Type hooks = assembly.GetType("Rupture.Impacts.FikaImpactHooks", true);
        var reader = (MethodInfo)hooks.GetMethod("ResolveInstructionReader", Flags).Invoke(null, null);
        Check(reader.GetParameters()[0].ParameterType == typeof(MethodBase) &&
            !reader.GetParameters()[1].ParameterType.IsByRef &&
            reader.GetParameters()[1].ParameterType.FullName == "System.Reflection.Emit.ILGenerator",
            "startup Harmony instruction reader unambiguously selects the non-ref overload");
        var genericSend = typeof(IFikaNetworkManager).GetMethod("SendPlayerPacket");
        var damageCall = new CodeInstruction(System.Reflection.Emit.OpCodes.Callvirt, genericSend.MakeGenericMethod(typeof(DamagePacket)));
        damageCall.labels.Add(default);
        damageCall.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
        var otherCall = new CodeInstruction(System.Reflection.Emit.OpCodes.Callvirt, genericSend.MakeGenericMethod(typeof(DropPacket)));
        var transformed = ((IEnumerable<CodeInstruction>)hooks.GetMethod("WrapDamageSend", Flags)
            .Invoke(null, new object[] { new[] { damageCall, otherCall } })).ToArray();
        Check(transformed[0].opcode == System.Reflection.Emit.OpCodes.Call &&
            ((MethodInfo)transformed[0].operand).Name == "SendDamage" &&
            transformed[0].labels.Count == 1 && transformed[0].blocks.Count == 1 &&
            transformed[1].opcode == System.Reflection.Emit.OpCodes.Callvirt &&
            ((MethodInfo)transformed[1].operand).GetGenericArguments()[0] == typeof(DropPacket),
            "damage transpiler preserves branch/exception metadata and leaves other player packets untouched");
        string session = new string('a',32);
        Type network = assembly.GetType("Rupture.Impacts.FikaImpactNetwork",true);
        MethodInfo compatible = network.GetMethod("Compatible",Flags);
        Check((bool)compatible.Invoke(null,new object[] {14,"1.2.1"}) && !(bool)compatible.Invoke(null,new object[] {13,"1.2.1"}) && !(bool)compatible.Invoke(null,new object[] {12,"1.2.1"}) && !(bool)compatible.Invoke(null,new object[] {11,"1.2.1"}) && !(bool)compatible.Invoke(null,new object[] {10,"1.2.0"}) &&
            !(bool)compatible.Invoke(null,new object[] {9,"1.1.6"}) && !(bool)compatible.Invoke(null,new object[] {4,"1.1.6"}) &&
            !(bool)compatible.Invoke(null,new object[] {5,"1.1.6"}) && !(bool)compatible.Invoke(null,new object[] {6,"1.1.0"}) &&
            !(bool)compatible.Invoke(null,new object[] {7,"1.1.2"}) && !(bool)compatible.Invoke(null,new object[] {8,"1.1.2"}) &&
            !(bool)compatible.Invoke(null,new object[] {7,"1.1.6"}) && !(bool)compatible.Invoke(null,new object[] {8,"1.1.3"}),
            "protocol and release mismatches are rejected, including the former dismemberment configuration");
        MethodInfo accept = network.GetMethod("AcceptSequence",Flags);
        MethodInfo totalValid = network.GetMethod("TotalValid",Flags);
        Check((bool)totalValid.Invoke(null,new object[] {24f}) && (bool)totalValid.Invoke(null,new object[] {120f}) &&
            !(bool)totalValid.Invoke(null,new object[] {121f}) && !(bool)totalValid.Invoke(null,new object[] {float.NaN}),
            "death snapshots accept configurable momentum totals within the supported bound");
        var seen = new Dictionary<string,uint>();
        Check((bool)accept.Invoke(null,new object[] {seen,session,1u}) && !(bool)accept.Invoke(null,new object[] {seen,session,1u}) &&
            (bool)accept.Invoke(null,new object[] {seen,session,3u}) && !(bool)accept.Invoke(null,new object[] {seen,session,2u}), "retransmitted/stale envelopes cannot execute damage twice");
        object sample = Activator.CreateInstance(assembly.GetType("Rupture.Impacts.ImpactSample",true));
        Set(sample,"Projectile",77UL); Set(sample,"MassGrams",4f); Set(sample,"Speed",900f); Set(sample,"Diameter",5.56f); Set(sample,"FireIndex",12); Set(sample,"Caliber","Caliber556x45NATO");
        Set(sample,"MasterOrigin",new Vector3(-12,0.25f,23));
        Set(sample,"HasMasterOrigin",true);
        object envelope = Activator.CreateInstance(assembly.GetType("Rupture.Impacts.RuptureDamageEnvelope",true));
        // Stock payload roundtrip does not apply damage or require a live network manager.
        DamagePacket damage = new DamagePacket(17, new EFT.Ballistics.DamageInfo {
            Damage = 42f, DamageType = EDamageType.Bullet, Direction = new Vector3(1,0,0),
            HitPoint = new Vector3(2,3,4), SourceId = "54527a984bdc2d4e668b4567"
        }, EBodyPart.LeftArm, EBodyPartColliderType.LeftUpperArm);
        NetDataWriter stockWriter = new NetDataWriter(); stockWriter.Put(17); damage.Serialize(stockWriter); byte[] bytes = stockWriter.CopyData();
        Set(envelope,"Session",session); Set(envelope,"Sequence",5u); Set(envelope,"Sample",sample); Set(envelope,"Damage",bytes);
        object copy = Roundtrip(envelope);
        Vector3 fragmentOrigin = (Vector3)Get(Get(copy,"Sample"),"MasterOrigin");
        Check(fragmentOrigin.x == -12f && fragmentOrigin.y == 0.25f && fragmentOrigin.z == 23f && (bool)Get(Get(copy,"Sample"),"HasMasterOrigin"),
            "fragment damage envelope preserves launch origin before owner-side Fika damage reconstruction");
        MethodInfo wraps = network.GetMethod("UsesDamageEnvelope",Flags);
        Check((bool)wraps.Invoke(null,new object[] {EDamageType.Bullet}) && (bool)wraps.Invoke(null,new object[] {EDamageType.GrenadeFragment}) &&
            !(bool)wraps.Invoke(null,new object[] {EDamageType.Explosion}), "bullet and grenade fragment packets use the envelope without changing native radial damage");
        MethodInfo deathOrigin = network.GetMethod("DeathMasterOrigin",Flags);
        Vector3 restoredOrigin = (Vector3)deathOrigin.Invoke(null,new object[] {new EFT.Ballistics.DamageInfo {DamageType = EDamageType.GrenadeFragment}, sample});
        Check(restoredOrigin.x == -12f && restoredOrigin.y == 0.25f && restoredOrigin.z == 23f,
            "remote fragment death uses the enveloped origin instead of stock Fika's missing origin");
        object originOnly = Activator.CreateInstance(sample.GetType());
        Set(originOnly,"MasterOrigin",new Vector3(3,4,5)); Set(originOnly,"HasMasterOrigin",true);
        Vector3 originWithoutMomentum = (Vector3)deathOrigin.Invoke(null,new object[] {new EFT.Ballistics.DamageInfo {DamageType = EDamageType.GrenadeFragment}, originOnly});
        Check(originWithoutMomentum.x == 3f && originWithoutMomentum.y == 4f && originWithoutMomentum.z == 5f,
            "fragment origin transport does not depend on projectile momentum eligibility");
        Vector3 radialOrigin = (Vector3)deathOrigin.Invoke(null,new object[] {new EFT.Ballistics.DamageInfo {DamageType = EDamageType.Explosion, MasterOrigin = new Vector3(8,9,10)}, sample});
        Check(radialOrigin.x == 8f && radialOrigin.y == 9f && radialOrigin.z == 10f,
            "native radial death retains its own center independently of an enclosing projectile sample");
        byte[] recovered = (byte[])Get(copy,"Damage");
        Check(Convert.ToBase64String(bytes) == Convert.ToBase64String(recovered), "damage envelope retains stock payload byte-for-byte");
        // Protocol 9 carries the separately supplied victim ID followed by the
        // readonly DamagePacket payload, decoded by Fika's stock constructor.
        NetDataReader recoveredReader = new NetDataReader(recovered);
        NetDataReader baselineReader = new NetDataReader(bytes);
        Check(recoveredReader.GetInt() == 17 && baselineReader.GetInt() == 17,
            "separate victim network ID is preserved with the stock damage payload");
        DamagePacket restored = new DamagePacket(recoveredReader);
        DamagePacket baseline = new DamagePacket(baselineReader);
        Check(recoveredReader.AvailableBytes == 0 && baselineReader.AvailableBytes == 0, "stock damage decoder consumes the complete enveloped payload");
        // Installed Fika quantizes damage on the wire; compare with its own roundtrip.
        Check(restored.Damage == baseline.Damage && restored.SourceId == damage.SourceId && restored.BodyPartType == EBodyPart.LeftArm &&
            (float)Get(Get(copy,"Sample"),"Speed") == 900f, $"stock damage and actual impact sample remain paired (damage={restored.Damage}, part={restored.BodyPartType}, speed={Get(Get(copy,"Sample"),"Speed")})");
        object head = Activator.CreateInstance(assembly.GetType("Rupture.Dismemberment.Classes.Packets.HeadWoundPacket", true));
        Set(head, "PlayerId", 17); Set(head, "ProfileId", "victim"); Set(head, "DeathSequence", 9u);
        Set(head, "EventSequence", 11u); Set(head, "WoundOrdinal", (byte)16); Set(head, "IsLethal", false);
        Set(head, "EntryLocal", new Vector3(0.01f, 0.02f, -0.1f)); Set(head, "DirectionLocal", new Vector3(0, 0, 1));
        object headCopy = Roundtrip(head);
        Check((byte)Get(headCopy, "WoundOrdinal") == 16 && !(bool)Get(headCopy, "IsLethal") &&
            (uint)Get(headCopy, "DeathSequence") == 9u && ((Vector3)Get(headCopy, "EntryLocal")).x == 0.01f,
            "post-mortem packet preserves ordinal, local trajectory and corpse generation");
        Set(head, "IsLethal", true); Set(head, "WoundOrdinal", (byte)1);
        Check((bool)Get(Roundtrip(head), "IsLethal"), "lethal packet retains blood-stream ownership");
        object state = Activator.CreateInstance(assembly.GetType("Rupture.Impacts.RuptureImpactStatePacket",true));
        Set(state,"Session",session); Set(state,"Sequence",6u); Set(state,"ProfileId","victim"); Set(state,"PlayerId",17);
        Set(state,"Sample",sample); Set(state,"LeftArm",10.8f); Set(state,"RightLeg",3.6f); Set(state,"DeathSequence",9u);
        Set(state,"Penetration",37f); Set(state,"Part",EBodyPart.Head);
        Set(state,"Point",new Vector3(4,5,6)); Set(state,"MasterOrigin",new Vector3(-12,0.25f,23));
        object stateCopy = Roundtrip(state);
        Check((float)Get(stateCopy,"Penetration") == 37f && (EBodyPart)Get(stateCopy,"Part") == EBodyPart.Head, "corpse head requests preserve collision penetration");
        Check((float)Get(stateCopy,"LeftArm") == 10.8f && (float)Get(stateCopy,"RightLeg") == 3.6f && (uint)Get(stateCopy,"DeathSequence") == 9u,
            "death handoff preserves independent limb totals and corpse generation");
        Vector3 origin = (Vector3)Get(stateCopy,"MasterOrigin");
        Vector3 hit = (Vector3)Get(stateCopy,"Point");
        Check(origin.x == -12f && origin.y == 0.25f && origin.z == 23f && hit.x == 4f && hit.y == 5f && hit.z == 6f,
            "death handoff preserves the blast center separately from the victim hit point");
    }
}
