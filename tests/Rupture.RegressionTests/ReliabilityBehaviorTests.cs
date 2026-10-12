using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using UnityEngine;

internal static class ReliabilityBehaviorTests
{
	private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
	private static void Check(bool condition, string message)
	{
		if (!condition) throw new Exception(message);
		Console.WriteLine("PASS: " + message);
	}

	internal static void Run(Assembly rupture)
	{
		TestCommit(rupture);
		TestEvents(rupture);
		TestEffectLifetimes(rupture);
		TestElapsedSimulation(rupture);
		string sourceRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(rupture.Location), "../../../.."));
		string configuration = new DirectoryInfo(Path.GetDirectoryName(rupture.Location)).Parent.Name;
		Assembly backend = Assembly.LoadFrom(Path.Combine(sourceRoot, "Rupture.PhysXBackend/bin", configuration, "netstandard2.1/Rupture.PhysXBackend.dll"));
		TestOwnership(backend);
		BackendThreadingTests.Run(backend);
		TestOwnerWatchdog(backend);
		TestHash(backend);
	}

	private static void TestElapsedSimulation(Assembly assembly)
	{
		MethodInfo consume = assembly.GetType("Rupture.Ragdolls.Classes.GraphicalSimulationPolicy", true).GetMethod("TryConsume", Flags);
		foreach (int fps in new[] { 15, 30, 60 })
		{
			double simulated = 0;
			for (int frame = 0; frame < fps * 10; frame++)
			{
				object[] args = { 1f / fps, 0f };
				if ((bool)consume.Invoke(null, args)) simulated += (float)args[1];
			}
			Check(Math.Abs(simulated - 10d) < .0001d, "graphical simulation preserves ten elapsed seconds at " + fps + " FPS");
		}
		object[] stall = { 1.75f, 0f };
		Check((bool)consume.Invoke(null, stall) && (float)stall[1] == 1.75f, "graphical stall delta is represented before the next wall-clock deadline");
		foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
			Check(!(bool)consume.Invoke(null, new object[] { invalid, 0f }), "invalid graphical step is rejected: " + invalid);
	}

	private static void TestEvents(Assembly assembly)
	{
		MethodInfo apply = assembly.GetType("Rupture.EventAdmission", true).GetMethod("TryApply", Flags);
		var received = new HashSet<uint>();
		int attempts = 0;
		bool Ready() { attempts++; return attempts >= 3; }
		bool Try() => (bool)apply.Invoke(null, new object[] { received, 123u, (Func<bool>)Ready });
		Check(!Try() && !Try() && received.Count == 0, "missing observer geometry leaves the event available for retry");
		Check(Try() && Try() && received.SetEquals(new[] { 123u }) && attempts == 3,
			"admitted observer sever commits deduplication exactly once");
		bool threw = false;
		try { apply.Invoke(null, new object[] { received, 124u, (Func<bool>)(() => throw new Exception("admission failure")) }); }
		catch (TargetInvocationException) { threw = true; }
		Check(threw && !received.Contains(124u), "throwing admission cannot consume an unapplied event");
	}

	private sealed class Effect { internal bool Alive = true; internal bool Bleeding = true; }
	private static void TestEffectLifetimes(Assembly assembly)
	{
		Type type = assembly.GetType("Rupture.EffectLifetimes`1", true).MakeGenericType(typeof(Effect));
		object registry = Activator.CreateInstance(type, true);
		var effects = new List<Effect>();
		for (uint index = 1; index <= 256; index++)
		{
			var effect = new Effect(); effects.Add(effect);
			type.GetMethod("Start", Flags).Invoke(registry, new object[] { index, effect });
		}
		int stops = 0;
		Action<Effect> stop = _ => stops++;
		Func<Effect, bool> ended = effect => !effect.Alive || !effect.Bleeding;
		void Poll() => type.GetMethod("StopWhere", Flags).Invoke(registry, new object[] { ended, stop });
		// No visual emitter is created. All 256 logical effects must still terminate.
		for (int index = 0; index < effects.Count; index++)
			if (index % 2 == 0) effects[index].Alive = false; else effects[index].Bleeding = false;
		Poll(); Poll();
		Check(stops == 256, "headless or visually evicted effects stop once on death/despawn or healing");
		type.GetMethod("Start", Flags).Invoke(registry, new object[] { 1u, new Effect { Alive = false } });
		type.GetMethod("Remove", Flags).Invoke(registry, new object[] { 1u });
		Poll();
		Check(stops == 256, "remote stop echo removes authority lifecycle without publishing another stop");
		type.GetMethod("Start", Flags).Invoke(registry, new object[] { 2u, new Effect { Alive = false } });
		type.GetMethod("Clear", Flags).Invoke(registry, null);
		Poll();
		Check(stops == 256, "raid reset drops logical effects without leaking stops into the next raid");
	}

	private static void TestCommit(Assembly assembly)
	{
		Type type = assembly.GetType("Rupture.CommitTransaction", true);
		for (int failing = 0; failing < 6; failing++)
		{
			object transaction = Activator.CreateInstance(type, true);
			int[] meshes = new int[6];
			for (int index = 0; index < meshes.Length; index++)
			{
				int slot = index;
				Action apply = () => { meshes[slot] = 1; if (slot == failing) throw new InvalidOperationException("injected after swap"); };
				Action undo = () => meshes[slot] = 0;
				type.GetMethod("Add", Flags).Invoke(transaction, new object[] { apply, undo });
			}
			bool rejected = false;
			try { type.GetMethod("Commit", Flags).Invoke(transaction, null); }
			catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { rejected = true; }
			Check(rejected && meshes.All(value => value == 0), "partial renderer/LOD transaction rolls back at stage " + failing);
		}
		object success = Activator.CreateInstance(type, true);
		bool committed = false, undone = false;
		type.GetMethod("Add", Flags).Invoke(success, new object[] { (Action)(() => committed = true), (Action)(() => undone = true) });
		type.GetMethod("Commit", Flags).Invoke(success, null);
		try { throw new Exception("cosmetic failure after commit"); } catch { }
		Check(committed && !undone, "completed transaction retains transferred ownership after cosmetic failure");
	}

	private static void TestOwnership(Assembly assembly)
	{
		Type type = assembly.GetType("Rupture.PhysXBackend.OwnedResult`1", true).MakeGenericType(typeof(object));
		MethodInfo publish = type.GetMethod("TryPublish", Flags), take = type.GetMethod("Take", Flags);
		for (int order = 0; order < 3; order++)
		{
			object result = Activator.CreateInstance(type, true), actor = new object();
			int releases = 0;
			using var created = new ManualResetEventSlim();
			using var resume = new ManualResetEventSlim();
			var worker = new Thread(() =>
			{
				if (order == 0) { created.Set(); resume.Wait(); }
				if (!(bool)publish.Invoke(result, new[] { actor })) Interlocked.Increment(ref releases);
				if (order != 0) { created.Set(); resume.Wait(); }
			});
			worker.Start();
			Check(created.Wait(5000), "native publication barrier reached " + order);
			object owned = take.Invoke(result, null);
			if (owned != null) releases++;
			resume.Set();
			Check(worker.Join(5000), "native publication worker finished " + order);
			Check(releases == 1 && take.Invoke(result, null) == null,
				"cancellation before publication, before completion, or handoff releases exactly once: " + order);
		}
	}

	private static void TestOwnerWatchdog(Assembly assembly)
	{
		Type type = assembly.GetType("Rupture.PhysXBackend.ExternalPhysXRuntime", true);
		foreach (bool queuedBehindOtherWork in new[] { false, true })
		{
		object runtime = Activator.CreateInstance(type);
		using var release = new ManualResetEventSlim();
		using var entered = new ManualResetEventSlim();
		var owner = new Thread(() => type.GetMethod("OwnerLoop", Flags).Invoke(runtime, null)) { IsBackground = true };
		type.GetField("_ownerThread", Flags).SetValue(runtime, owner);
		owner.Start();
		if (queuedBehindOtherWork)
		{
			type.GetMethod("EnqueueOwner", Flags).Invoke(runtime, new object[] {
				(Action)(() => { entered.Set(); release.Wait(); }), "blocked-preceding-work" });
			Check(entered.Wait(5000), "preceding owner operation entered before waiting caller");
		}
		Stopwatch elapsed = Stopwatch.StartNew();
		bool timedOut = false;
		string timeoutMessage = null;
		try
		{
			type.GetMethod("InvokeOwner", Flags).Invoke(runtime, new object[] { (Action)(() => { entered.Set(); release.Wait(); }), "waiting-operation" });
		}
		catch (TargetInvocationException error) when (error.InnerException is TimeoutException) { timedOut = true; timeoutMessage = error.InnerException.Message; }
		finally { release.Set(); }
		Check(entered.IsSet && timedOut && elapsed.ElapsedMilliseconds < 3000 &&
			(bool)type.GetField("_ownerThreadUnresponsive", Flags).GetValue(runtime),
			"non-step owner operation times out and quarantines the backend");
		Check(timeoutMessage.Contains("waiting=waiting-operation") &&
			timeoutMessage.Contains(queuedBehindOtherWork ? "executing=blocked-preceding-work" : "executing=waiting-operation") &&
			timeoutMessage.Contains("waitingStarted=" + (!queuedBehindOtherWork)),
			"watchdog distinguishes queued wait from the executing operation: " + queuedBehindOtherWork);
		Check(owner.Join(5000), "quarantined owner exits safely when stalled work returns");
		bool rejected = false;
		try { type.GetMethod("InvokeOwner", Flags).Invoke(runtime, new object[] { (Action)(() => throw new Exception("must never execute")), "rejected-operation" }); }
		catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { rejected = true; }
		Check(rejected, "quarantine rejects later work instead of waiting on a stopped owner");
		((IDisposable)runtime).Dispose();
		}
	}

	private static void TestHash(Assembly assembly)
	{
		MethodInfo hash = assembly.GetType("Rupture.PhysXBackend.MeshGeometry", true).GetMethod("HashGeometry", Flags);
		Vector3[] fixture = { new Vector3(-0f, 1.25f, -9.5f), new Vector3(float.Epsilon, float.MaxValue, float.NaN) };
		uint[] indices = { 0, 1, uint.MaxValue };
		List<byte> bytes = new List<byte>();
		foreach (Vector3 vertex in fixture) { bytes.AddRange(BitConverter.GetBytes(vertex.x)); bytes.AddRange(BitConverter.GetBytes(vertex.y)); bytes.AddRange(BitConverter.GetBytes(vertex.z)); }
		foreach (uint index in indices) bytes.AddRange(BitConverter.GetBytes(index));
		string expected = Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
		Check((string)hash.Invoke(null, new object[] { fixture, indices }) == expected, "allocation-free float writes preserve mesh hash bits and cache identity");
		Vector3[] large = new Vector3[100000];
		long before = GC.GetAllocatedBytesForCurrentThread();
		hash.Invoke(null, new object[] { large, Array.Empty<uint>() });
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Check(allocated < 1500000, "100k vertex hashing allocates only the packed input and digest, bytes=" + allocated);
	}
}
