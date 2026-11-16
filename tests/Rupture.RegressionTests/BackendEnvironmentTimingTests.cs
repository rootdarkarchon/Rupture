using System;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;

/// <summary>The per-step timing of the environment update (last/max per step, the breakdown of the longest update, no steady-state allocation) and the lazy hash of a signed-scale mesh bake.</summary>
internal static class BackendEnvironmentTimingTests
{
	private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

	internal static void Run(Assembly backend)
	{
		TestStepAccounting(backend);
		TestNoSteadyStateAllocation(backend);
		TestBakedMeshHashIsLazy(backend);
	}

	/// <summary>Each step keeps its own last/max, the breakdown is the longest update's, and the status text reports both.</summary>
	private static void TestStepAccounting(Assembly backend)
	{
		Type timingsType = backend.GetType("Rupture.PhysXBackend.EnvironmentStepTimings", true);
		Type stepType = backend.GetType("Rupture.PhysXBackend.EnvironmentStep", true);
		object timings = Activator.CreateInstance(timingsType, true);
		object jobs = Enum.Parse(stepType, "Jobs");
		object movable = Enum.Parse(stepType, "Movable");
		object shapeAudit = Enum.Parse(stepType, "ShapeAudit");
		long millisecond = Stopwatch.Frequency / 1000L;

		bool firstIsWorst = Frame(timings, 50 * millisecond, (jobs, 40 * millisecond), (movable, 2 * millisecond), (shapeAudit, millisecond / 100L));
		bool secondIsWorst = Frame(timings, 12 * millisecond, (jobs, 3 * millisecond), (movable, 9 * millisecond));
		Require(firstIsWorst && !secondIsWorst, "only an update longer than every earlier one reports itself as the worst");
		Require((long)timingsType.GetMethod("MaxTicks", Flags).Invoke(timings, new[] { jobs }) == 40 * millisecond &&
			(long)timingsType.GetMethod("MaxTicks", Flags).Invoke(timings, new[] { movable }) == 9 * millisecond, "each step keeps its own longest duration across updates");
		Require((long)timingsType.GetMethod("WorstFrameTicks", Flags).Invoke(timings, new[] { jobs }) == 40 * millisecond &&
			(long)timingsType.GetMethod("WorstFrameTicks", Flags).Invoke(timings, new[] { movable }) == 2 * millisecond, "the breakdown is the one of the longest update, not of the latest");

		string text = (string)timingsType.GetMethod("Describe", Flags).Invoke(timings, null);
		Require(text.Contains("envSteps=calls:2/lastMs:12.00/maxMs:50.00") && text.Contains("/jobs:3.00|40.00") && text.Contains("/movable:9.00|9.00") &&
			text.Contains("envWorstFrame=50.00ms{jobs:40.00,movable:2.00}"),
			"the status text reports last and max per step, and the worst update without its sub-0.05 ms steps: " + text);
		Console.WriteLine("PASS: environment step timings keep per-step last/max and the breakdown of the longest update");

		timingsType.GetMethod("Reset", Flags).Invoke(timings, null);
		Require(((string)timingsType.GetMethod("Describe", Flags).Invoke(timings, null)).Contains("envSteps=calls:0/lastMs:0.00/maxMs:0.00"), "reset clears the timings of a raid");
		Console.WriteLine("PASS: environment step timings reset with the raid");
	}

	/// <summary>Begin and end of an update copy into arrays allocated once; they are bound to typed delegates so the measurement does not count reflection boxing.</summary>
	private static void TestNoSteadyStateAllocation(Assembly backend)
	{
		Type timingsType = backend.GetType("Rupture.PhysXBackend.EnvironmentStepTimings", true);
		object timings = Activator.CreateInstance(timingsType, true);
		Action begin = (Action)Delegate.CreateDelegate(typeof(Action), timings, timingsType.GetMethod("BeginFrame", Flags));
		Func<long, bool> end = (Func<long, bool>)Delegate.CreateDelegate(typeof(Func<long, bool>), timings, timingsType.GetMethod("EndFrame", Flags));
		for (int warm = 0; warm < 8; warm++)
		{
			begin();
			end(warm);
		}
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int index = 0; index < 1000; index++)
		{
			begin();
			end(index);
		}
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Require(allocated == 0L, "an environment step timing update allocates nothing in steady state: " + allocated + " B over 1000 updates");
		Console.WriteLine("PASS: environment step timing allocates 0 B in steady state");
	}

	/// <summary>A signed-scale bake leaves the whole-mesh hash to the cache-key request, so the main-thread snapshot that bakes it pays a vertex copy only.</summary>
	private static void TestBakedMeshHashIsLazy(Assembly backend)
	{
		Type meshType = backend.GetType("Rupture.PhysXBackend.MeshGeometry", true);
		object source = Activator.CreateInstance(meshType, true);
		Vector3[] vertices = { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f) };
		uint[] indices = { 0, 1, 2 };
		MethodInfo hash = meshType.GetMethod("HashGeometry", Flags);
		FieldInfo rawHash = meshType.GetField("RawHash", Flags);
		meshType.GetField("Vertices", Flags).SetValue(source, vertices);
		meshType.GetField("Indices", Flags).SetValue(source, indices);
		meshType.GetField("Layout", Flags).SetValue(source, "layout");
		rawHash.SetValue(source, hash.Invoke(null, new object[] { vertices, indices }));
		MethodInfo bake = meshType.GetMethod("BakeNegativeScale", Flags);

		object positive = bake.Invoke(source, new object[] { new Vector3(2f, 1f, 1f) });
		Require(Equals(rawHash.GetValue(positive), rawHash.GetValue(source)), "a positive-scale clone shares the source hash");

		object mirrored = bake.Invoke(source, new object[] { new Vector3(-1f, 1f, 1f) });
		Require(rawHash.GetValue(mirrored) == null, "a signed-scale bake does not hash the mesh while it is built");
		MethodInfo cacheKey = meshType.GetMethod("CacheKey", Flags);
		string key = (string)cacheKey.Invoke(mirrored, new object[] { "build" });
		Vector3[] bakedVertices = (Vector3[])meshType.GetField("Vertices", Flags).GetValue(mirrored);
		uint[] bakedIndices = (uint[])meshType.GetField("Indices", Flags).GetValue(mirrored);
		Require(!string.IsNullOrEmpty(key) && (string)rawHash.GetValue(mirrored) == (string)hash.Invoke(null, new object[] { bakedVertices, bakedIndices }),
			"the cache-key request hashes the baked arrays and keeps the result");
		Require((string)cacheKey.Invoke(mirrored, new object[] { "build" }) == key, "the cache key is stable once computed");
		Console.WriteLine("PASS: a signed-scale mesh bake hashes lazily on the cache-key request");
	}

	private static bool Frame(object timings, long total, params (object Step, long Ticks)[] steps)
	{
		Type type = timings.GetType();
		MethodInfo add = type.GetMethod("Add", Flags);
		type.GetMethod("BeginFrame", Flags).Invoke(timings, null);
		foreach ((object step, long ticks) in steps) add.Invoke(timings, new[] { step, ticks });
		return (bool)type.GetMethod("EndFrame", Flags).Invoke(timings, new object[] { total });
	}

	private static void Require(bool condition, string message)
	{
		if (!condition) throw new Exception("FAIL: " + message);
	}
}
