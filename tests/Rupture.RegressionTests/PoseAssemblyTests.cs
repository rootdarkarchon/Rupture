using System;
using System.Linq;
using System.Reflection;

internal static class PoseAssemblyTests
{
	private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
	internal static void Run(Assembly core)
	{
		Type type = core.GetType("Rupture.Ragdolls.Classes.PendingPoseSnapshotAssembly", true);
		Type poseType = core.GetType("Rupture.Ragdolls.Classes.Packets.RagdollPose", true);
		Type packetType = core.GetType("Rupture.Ragdolls.Classes.Packets.RagdollPoseSnapshotData", true);
		object Create(byte total = 3) => Activator.CreateInstance(type, Flags, null, new object[] { 7u, (byte)3, total, 1f, .03f, 4 }, null);
		Array Chunk(byte index)
		{
			Array result = Array.CreateInstance(poseType, 1);
			object pose = Activator.CreateInstance(poseType);
			poseType.GetField("BodyIndex").SetValue(pose, index);
			result.SetValue(pose, 0);
			return result;
		}
		bool Add(object assembly, byte index, Array chunk) => (bool)type.GetMethod("TryAddChunk", Flags).Invoke(assembly, new object[] { index, chunk });
		bool Build(object assembly, out Array result, int maximum = 64)
		{
			object[] args = { maximum, null };
			bool success = (bool)type.GetMethod("TryBuild", Flags).Invoke(assembly, args);
			result = (Array)args[1]; return success;
		}
		void Check(bool condition, string name)
		{
			if (!condition) throw new Exception(name);
			Console.WriteLine("PASS: " + name);
		}
		foreach (byte[] order in new[] { new byte[] { 0, 1, 2 }, new byte[] { 0, 2, 1 }, new byte[] { 1, 0, 2 }, new byte[] { 1, 2, 0 }, new byte[] { 2, 0, 1 }, new byte[] { 2, 1, 0 } })
		{
			object assembly = Create();
			for (int i = 0; i < order.Length; i++)
			{
				byte index = order[i];
				Check(Add(assembly, index, Chunk(index)), "snapshot accepts unique chunk " + index);
				Check(!Add(assembly, index, Chunk(99)), "duplicate cannot replace a retained chunk");
				if (i < 2) Check(!Build(assembly, out Array partial) && partial == null, "incomplete snapshot publishes no array");
			}
			Check((bool)type.GetProperty("IsComplete", Flags).GetValue(assembly), "all chunks complete snapshot " + string.Join(",", order));
			Check(Build(assembly, out Array result) && result.Length == 3 && Enumerable.Range(0, 3).All(i => (byte)poseType.GetField("BodyIndex").GetValue(result.GetValue(i)) == i), "out-of-order snapshot flattens in wire chunk order");
		}
		foreach (byte total in new byte[] { 2, 4 })
		{
			object assembly = Create(total);
			for (byte i = 0; i < 3; i++) Add(assembly, i, Chunk(i));
			Check(!Build(assembly, out Array result) && result == null, "declared pose count mismatch rejects whole snapshot " + total);
		}
		object bounded = Create();
		for (byte i = 0; i < 3; i++) Add(bounded, i, Chunk(i));
		Check(!Build(bounded, out Array oversized, 2) && oversized == null, "body limit rejects before output allocation");
		object packet = Activator.CreateInstance(packetType);
		void Set(string name, object value) => packetType.GetField(name).SetValue(packet, value);
		Set("ChunkCount", (byte)3); Set("TotalPoseCount", (byte)3); Set("AuthorityTime", 1f); Set("SimulationDeltaTime", .03f);
		bool Matches() => (bool)type.GetMethod("Matches", Flags).Invoke(bounded, new[] { packet });
		Check(Matches(), "snapshot metadata matches original header");
		foreach (var change in new (string Name, object Wrong, object Original)[] { ("ChunkCount", (byte)2, (byte)3), ("TotalPoseCount", (byte)4, (byte)3), ("AuthorityTime", 1.001f, 1f), ("SimulationDeltaTime", .031f, .03f) })
		{
			Set(change.Name, change.Wrong); Check(!Matches(), "inconsistent snapshot " + change.Name + " rejected"); Set(change.Name, change.Original);
		}
		Set("AuthorityTime", 1.00005f); Check(Matches(), "existing authority-time tolerance is preserved");
	}
}
