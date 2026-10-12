using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Mono.Cecil;

internal static class CorpseDragAbiTests
{
	private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
	internal static void Run(Assembly core)
	{
		Type facade = core.GetType("Rupture.Integration.CorpseDragV1", true);
		Type begin = core.GetType("Rupture.Integration.DragBegin", true);
		Type frame = core.GetType("Rupture.Integration.DragFrame", true);
		Type acceleration = core.GetType("Rupture.Integration.DragAcceleration", true);
		Type buffer = core.GetType("Rupture.Integration.DragInputBuffer", true);
		object input = Activator.CreateInstance(buffer, true);
		void Check(bool condition, string label)
		{
			if (!condition) throw new Exception("Corpse drag ABI: " + label);
			Console.WriteLine("PASS: corpse drag " + label);
		}
		Check((int)facade.GetProperty("AbiVersion").GetValue(null) == 1 && facade.IsPublic, "public ABI version is 1");
		foreach (var expected in new[] { ("Inspect", 2), ("Begin", 3), ("Read", 2), ("Submit", 2), ("End", 2) })
			Check(facade.GetMethod(expected.Item1)?.GetParameters().Length == expected.Item2, "entry point " + expected.Item1);
		Check(facade.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name != "get_AbiVersion").Count() == 5, "facade exposes no backend handles or extra control operations");
		Check(facade.GetMethod("Read").GetParameters()[0].ParameterType == typeof(ulong), "opaque token is ulong");
		Check(core.GetType("Rupture.Integration.DragBodyView", true).GetField("NativeTransform")?.FieldType == typeof(Transform),
			"bone identity survives native rigidbody cleanup");
		Check(core.GetType("Rupture.Integration.DragView", true).GetField("SimulationDeltaTime")?.FieldType == typeof(float), "completed step duration is available for consumer damping");
		foreach (string typeName in new[] { "CorpseControlInfo", "DragBegin", "DragView", "DragBodyView", "DragFrame", "DragAcceleration" })
			Check(core.GetType("Rupture.Integration." + typeName, true).IsPublic, "DTO is public: " + typeName);
		foreach (string enumName in new[] { "DragResult", "CorpseControlState", "DragStage", "DragEndReason", "DragCapabilities" })
			Check(core.GetType("Rupture.Integration." + enumName, true).IsPublic, "enum is public: " + enumName);

		void Set(object dto, string field, object value) => dto.GetType().GetField(field).SetValue(dto, value);
		object Get(object dto, string field) => dto.GetType().GetField(field).GetValue(dto);
		object Request(float timeout = 2f)
		{
			object request = Activator.CreateInstance(begin);
			Set(request, "GripSlack", .05f); Set(request, "MaxGripSpeed", 4.5f); Set(request, "LeaseTimeout", timeout);
			return request;
		}
		bool Valid(object request) => (bool)buffer.GetMethod("ValidBegin", Flags).Invoke(null, new[] { request });
		Check(Valid(Request()) && Valid(Request(.5f)) && Valid(Request(10f)), "timeout bounds are inclusive");
		Check(!Valid(null) && !Valid(Request(.499f)) && !Valid(Request(10.001f)) && !Valid(Request(float.NaN)), "bad acquisition cannot create a lease");
		foreach (string field in new[] { "GripSlack", "MaxGripSpeed" })
			foreach (float value in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
			{ object request = Request(); Set(request, field, value); Check(!Valid(request), "rejects " + field + "=" + value); }

		Array Forces(params (int Index, Vector3 Value)[] values)
		{
			Array result = Array.CreateInstance(acceleration, values.Length);
			for (int i = 0; i < values.Length; i++)
			{
				object entry = Activator.CreateInstance(acceleration); Set(entry, "BodyIndex", values[i].Index); Set(entry, "Acceleration", values[i].Value); result.SetValue(entry, i);
			}
			return result;
		}
		object Frame(uint seq, uint topology = 2, uint translation = 0, Vector3 offset = default, Array forces = null)
		{
			object result = Activator.CreateInstance(frame);
			Set(result, "Sequence", seq); Set(result, "TopologyRevision", topology); Set(result, "GripTarget", new Vector3(1, 2, 3));
			Set(result, "TranslationId", translation); Set(result, "RigTranslation", offset); Set(result, "Accelerations", forces);
			return result;
		}
		bool[] eligible = { true, false, true };
		string Accept(object value) => buffer.GetMethod("Accept", Flags).Invoke(input, new object[] { value, 2u, eligible }).ToString();
		object Property(string name) => buffer.GetProperty(name, Flags).GetValue(input);
		Check(Accept(null) == "InvalidArgument" && Accept(Frame(0)) == "InvalidArgument", "missing and zero-sequence frames reject");
		Check(Accept(Frame(1, topology: 1)) == "Stale", "topology mismatch rejects");
		foreach (Array forces in new[] { Forces((1, Vector3.one)), Forces((-1, Vector3.one)), Forces((3, Vector3.one)), Forces((0, Vector3.one), (0, Vector3.one)), Forces((0, new Vector3(float.NaN, 0, 0))) })
			Check(Accept(Frame(1, forces: forces)) == "InvalidArgument", "invalid/disabled/duplicate acceleration rejects the whole frame");
		object invalidTarget = Frame(1); Set(invalidTarget, "GripTarget", new Vector3(0, float.PositiveInfinity, 0));
		Check(Accept(invalidTarget) == "InvalidArgument" && (uint)Property("Sequence") == 0, "rejections do not advance input sequence");
		Check(Accept(Frame(1, offset: Vector3.one)) == "InvalidArgument", "translation requires a request id");

		Array originalForces = Forces((2, new Vector3(0, 8, 0)));
		object first = Frame(1, translation: 4, offset: new Vector3(3, 0, 0), forces: originalForces);
		Check(Accept(first) == "Ok", "accepts initial drive and recovery");
		Set(first, "GripTarget", new Vector3(99, 99, 99)); originalForces.SetValue(Activator.CreateInstance(acceleration), 0);
		object copied = Property("Current");
		Check(((Vector3)Get(copied, "GripTarget")).x == 1f && (int)Get(((Array)Get(copied, "Accelerations")).GetValue(0), "BodyIndex") == 2,
			"caller mutations cannot alter retained target or acceleration array");
		Check(Accept(Frame(1)) == "Stale" && Accept(Frame(0)) == "InvalidArgument", "older input cannot renew a drive");
		Check(Accept(Frame(2, translation: 5, offset: Vector3.one)) == "Busy" && (uint)Property("Sequence") == 1, "second pending translation is rejected without consuming its sequence");
		Check(Accept(Frame(2, translation: 4, offset: Vector3.one)) == "InvalidArgument", "same recovery id cannot change its offset");
		Check(Accept(Frame(2)) == "Ok" && (uint)Property("PendingTranslationId") == 4, "target coalescing cannot erase pending recovery");
		Check(Accept(Frame(3, translation: 4, offset: new Vector3(3, 0, 0))) == "Ok", "retransmitted recovery remains one operation");
		buffer.GetMethod("TranslationApplied", Flags).Invoke(input, null);
		Check((uint)Property("AppliedTranslationId") == 4 && (uint)Property("PendingTranslationId") == 0, "completed recovery exposes its ack");
		Check(Accept(Frame(4, translation: 4, offset: new Vector3(3, 0, 0))) == "Ok" && (uint)Property("PendingTranslationId") == 0,
			"acknowledged recovery is never replayed");
		Check(Accept(Frame(5, translation: 3, offset: Vector3.one)) == "Stale", "old recovery id is rejected");
		Check(Accept(Frame(5, translation: 5, offset: Vector3.one)) == "Ok", "new recovery is permitted after ack");
		buffer.GetMethod("Cancel", Flags).Invoke(input, null);
		Check(Property("Current") == null && (uint)Property("PendingTranslationId") == 0, "release removes both persistent drive and pending recovery");
		bool Expired(float now) => (bool)buffer.GetMethod("Expired", Flags).Invoke(null, new object[] { now, 100f, 2f });
		Check(!Expired(101.999f) && Expired(102f) && Expired(103f), "lease expires exactly at the unscaled deadline");

		// Preserve the motion stream when an old corpse's coordinator is restored.
		Type coordinator = core.GetType("Rupture.Ragdolls.Classes.RagdollCoordinator", true);
		Type stateType = coordinator.GetNestedType("RagdollState", Flags);
		Type tombstoneType = coordinator.GetNestedType("RagdollTombstone", Flags);
		object state = Activator.CreateInstance(stateType, true), tombstone = Activator.CreateInstance(tombstoneType, true);
		void Field(object obj, string name, uint value) => obj.GetType().GetField(name, Flags).SetValue(obj, value);
		uint Number(object obj, string name) => (uint)obj.GetType().GetField(name, Flags).GetValue(obj);
		Field(state, "DeathSequence", 8); Field(tombstone, "DeathSequence", 8);
		Field(tombstone, "NextPoseSnapshotSequence", 700); Field(tombstone, "LastReceivedPoseSnapshotSequence", 650); Field(tombstone, "LastDiscardedPoseSnapshotSequence", 640);
		coordinator.GetMethod("RestoreMotionSequences", Flags).Invoke(null, new[] { state, tombstone });
		Check(Number(state, "NextPoseSnapshotSequence") == 700 && Number(state, "LastReceivedPoseSnapshotSequence") == 650 &&
			Number(state, "AuthorityPlaybackSequenceBarrier") == 650 && Number(state, "LastDiscardedPoseSnapshotSequence") == 640, "retired corpse restores send/receive/playback watermarks");
		Field(state, "DeathSequence", 9); Field(state, "NextPoseSnapshotSequence", 0);
		coordinator.GetMethod("RestoreMotionSequences", Flags).Invoke(null, new[] { state, tombstone });
		Check(Number(state, "NextPoseSnapshotSequence") == 0, "a new death never inherits old motion counters");

		using AssemblyDefinition definition = AssemblyDefinition.ReadAssembly(core.Location);
		TypeDefinition shadow = definition.MainModule.GetType("Rupture.Ragdolls.Classes.ShadowRagdollWorld");
		string[] Calls(string name) => shadow.Methods.Single(m => m.Name == name).Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Select(m => m.Name).ToArray();
		string[] simulation = Calls("Simulate");
		Check(Array.IndexOf(simulation, "CompleteExternalStep") < Array.IndexOf(simulation, "ProcessDragBoundary") &&
			Array.IndexOf(simulation, "DriveDragLeases") < Array.IndexOf(simulation, "SubmitStep"), "grip mutation is ordered after publication and before simulation");
		var completion = shadow.Methods.Single(m => m.Name == "CompleteExternalStep").Body.Instructions.ToList();
		Check(completion.FindIndex(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Stsfld && i.Operand is FieldReference f && f.Name == "_completedExternalSimulationDelta") <
			completion.FindIndex(i => i.Operand is MethodReference m && m.Name == "PublishDragViews"), "drag views publish the current completed step duration");
		Check(Calls("DestroyRig").Contains("InvalidateRigDrag") && Calls("Reset").Contains("ResetDragLeases"), "rig and raid destruction invalidate leases");
		Check(Calls("DisposeDragGrip").SequenceEqual(new[] { "DestroyJoint", "DestroyBody" }), "tether is disposed before its hand actor");
		MethodInfo bindingPolicy = core.GetType("Rupture.Ragdolls.Classes.ShadowRagdollWorld", true).GetMethod("CanBindDragSequence", Flags);
		bool CanBind(uint current, uint incoming) => (bool)bindingPolicy.Invoke(null, new object[] { current, incoming });
		Check(CanBind(0, 5) && !CanBind(0, 0) && !CanBind(5, 6) && !CanBind(5, 5), "delayed authority identity promotes only an unassigned rig");
		TypeDefinition coordinatorDefinition = definition.MainModule.GetType("Rupture.Ragdolls.Classes.RagdollCoordinator");
		Check(new[] { "RegisterNativeRagdoll", "BindStateToCorpse" }.All(name => coordinatorDefinition.Methods.Single(m => m.Name == name).Body.Instructions
			.Select(i => i.Operand).OfType<MethodReference>().Any(m => m.Name == "BindDragDeathIdentity")), "native and retained corpse binding associate the authority death identity");
		string[] Fields(string name) => shadow.Methods.Single(m => m.Name == name).Body.Instructions.Select(i => i.Operand).OfType<FieldReference>().Select(f => f.Name).ToArray();
		Check(!Fields("DragBodyEligible").Contains("Native") && Fields("SelectDragBody").Contains("IsDragChest") &&
			!Fields("SelectDragBody").Contains("_rigidbodySpawners"), "settled external actors remain selectable after native body/spawner cleanup");
	}
}
