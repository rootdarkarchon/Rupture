using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Mono.Cecil;
using UnityEngine;

internal static class BackendThreadingTests
{
	private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

	internal static void Run(Assembly backend)
	{
		TestOwnerCallbacks(backend);
		TestAsyncWorkYieldsToFifo(backend);
		TestSlicedCreationYieldsBetweenSlices(backend);
		TestMovedCreationRunsThrough(backend);
		BackendEnvironmentTimingTests.Run(backend);
	}

	private static bool Derives(TypeDefinition type, params string[] baseNames)
	{
		for (TypeDefinition current = type.BaseType?.Resolve(); current != null; current = current.BaseType?.Resolve())
			if (baseNames.Contains(current.Name)) return true;
		return false;
	}

	/// <summary>Everything that runs on the scene-owner thread consumes snapshots and handles; none of it may touch a Unity object.</summary>
	private static void TestOwnerCallbacks(Assembly backend)
	{
		using var assembly = AssemblyDefinition.ReadAssembly(backend.Location);
		var runtime = assembly.MainModule.GetType("Rupture.PhysXBackend.ExternalPhysXRuntime");
		var ownerTypes = runtime.NestedTypes.Where(t => Derives(t, "OwnerWork", "CreationItem")).ToArray();
		var typed = ownerTypes.SelectMany(t => t.Methods).Where(m => m.HasBody &&
			(m.Name == "Run" || m.Name == "Execute" || m.Name == "Rollback")).ToArray();
		foreach (string required in new[] { "BodyCreationItem", "JointCreationItem", "CreationBatchWork", "ChunkWork", "PromotionWork", "CommandWork", "StepWork" })
			if (!ownerTypes.Any(t => t.Name == required)) throw new Exception("Owner work type is missing from the backend: " + required);
		var closures = runtime.NestedTypes.SelectMany(t => t.Methods).Where(m =>
			m.Name.StartsWith("<TryCreateCorpseGrip>", StringComparison.Ordinal) ||
			m.Name.StartsWith("<TryMoveCorpseGrip>", StringComparison.Ordinal)).ToArray();
		if (closures.Length != 2) throw new Exception("Expected two corpse-grip scene-owner callbacks to audit.");
		var unityObjects = new HashSet<string> { "UnityEngine.Object", "UnityEngine.Component", "UnityEngine.Collider", "UnityEngine.Rigidbody", "UnityEngine.Transform", "UnityEngine.GameObject" };
		foreach (var callback in typed.Concat(closures))
		{
			var unsafeCall = callback.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
				.FirstOrDefault(m => unityObjects.Contains(m.DeclaringType.FullName));
			if (unsafeCall != null) throw new Exception("Owner callback accesses Unity object: " + callback.FullName + " -> " + unsafeCall.FullName);
			Console.WriteLine("PASS: owner-thread work consumes snapshots without Unity object access: " + callback.DeclaringType.Name + "." + callback.Name);
		}
	}

	/// <summary>
	/// A submitted step and every other FIFO operation must run before queued asynchronous environment work, while an item that
	/// already started is allowed to finish.
	/// </summary>
	private static void TestAsyncWorkYieldsToFifo(Assembly backend)
	{
		Type type = backend.GetType("Rupture.PhysXBackend.ExternalPhysXRuntime", true);
		Type delegateWork = backend.GetType("Rupture.PhysXBackend.ExternalPhysXRuntime+DelegateWork", true);
		MethodInfo rent = delegateWork.GetMethod("Rent", Flags);
		MethodInfo enqueueAsync = type.GetMethod("EnqueueAsync", Flags);
		MethodInfo enqueueOwner = type.GetMethod("EnqueueOwner", Flags);
		object runtime = Activator.CreateInstance(type);
		var owner = new Thread(() => type.GetMethod("OwnerLoop", Flags).Invoke(runtime, null)) { IsBackground = true };
		type.GetField("_ownerThread", Flags).SetValue(runtime, owner);
		owner.Start();
		var order = new List<string>();
		using var entered = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		object Work(string name, Action action) => rent.Invoke(null, new object[] { action, name, false, true });
		void Record(string name) { lock (order) order.Add(name); }

		enqueueAsync.Invoke(runtime, new[] { Work("async-started", () => { entered.Set(); release.Wait(); Record("async-started"); }) });
		if (!entered.Wait(5000)) throw new Exception("The first asynchronous owner item never started.");
		enqueueAsync.Invoke(runtime, new[] { Work("async-queued", () => Record("async-queued")) });
		enqueueOwner.Invoke(runtime, new object[] { (Action)(() => Record("fifo")), "fifo" });
		release.Set();
		DateTime deadline = DateTime.UtcNow.AddSeconds(5);
		while (DateTime.UtcNow < deadline)
		{
			lock (order) if (order.Count == 3) break;
			Thread.Sleep(5);
		}
		string observed;
		lock (order) observed = string.Join(",", order);
		((IDisposable)runtime).Dispose();
		if (observed != "async-started,fifo,async-queued")
			throw new Exception("Asynchronous environment work did not yield to the FIFO queue: " + observed);
		Console.WriteLine("PASS: queued asynchronous environment work runs only after every FIFO operation; a started item finishes first.");
	}

	/// <summary>
	/// Environment creation is sliced: a FIFO operation (a step is one) that arrives while a mesh loads waits for that load, not for the
	/// chunk, and runs before the next mesh loads. Drives the production chunk against a scene whose native entry points are stubs.
	/// </summary>
	private static void TestSlicedCreationYieldsBetweenSlices(Assembly backend)
	{
		using var entered = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		var events = new List<string>();
		int meshLoads = 0;
		void Record(string name) { lock (events) events.Add(name); }
		var hooks = new Dictionary<string, Action>
		{
			["CreateMeshFromCooked"] = () =>
			{
				Record("mesh");
				if (Interlocked.Increment(ref meshLoads) != 1) return;
				entered.Set();
				release.Wait(5000);
			},
			["CreateActor"] = () => Record("actor"),
			["AttachShape"] = () => Record("attach")
		};
		using var fixture = new CreationFixture(backend, hooks);
		object chunk = fixture.Chunk("a", "b");
		fixture.EnqueueAsync(chunk);
		if (!entered.Wait(5000)) throw new Exception("The first mesh load of the asynchronous chunk never started.");
		if (!(bool)fixture.RuntimeType.GetField("_asyncSliceRunning", Flags).GetValue(fixture.Runtime))
			throw new Exception("A running asynchronous slice is not visible to step submission.");
		fixture.EnqueueFifo(() => Record("fifo"));
		release.Set();
		fixture.WaitCompleted(chunk);
		string observed;
		lock (events) observed = string.Join(",", events);
		if (observed != "mesh,fifo,actor,attach,mesh,actor,attach")
			throw new Exception("A FIFO operation did not run between the slices of an asynchronous chunk: " + observed);
		if (fixture.FailureOf(chunk) != null || fixture.CreatedCount(chunk) != 2)
			throw new Exception("The sliced chunk did not create both actors: " + fixture.FailureOf(chunk));
		long preloads = (long)fixture.SceneType.GetProperty("GeometryPreloads", Flags).GetValue(fixture.Scene);
		long inline = (long)fixture.SceneType.GetProperty("InlineGeometryLoads", Flags).GetValue(fixture.Scene);
		long items = (long)fixture.RuntimeType.GetField("_asyncItems", Flags).GetValue(fixture.Runtime);
		if (preloads != 2 || inline != 0 || items != 4)
			throw new Exception($"Unexpected slice accounting: preloads={preloads}, inline={inline}, asyncItems={items}.");
		Console.WriteLine("PASS: an asynchronous chunk loads one mesh or creates one actor per owner turn, a FIFO operation runs between turns, and the preloads cover every mesh (no inline loads).");
	}

	/// <summary>A caller that blocks on an asynchronous chunk moves it to the FIFO, where it runs through without yielding to later FIFO work.</summary>
	private static void TestMovedCreationRunsThrough(Assembly backend)
	{
		using var entered = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		var events = new List<string>();
		void Record(string name) { lock (events) events.Add(name); }
		var hooks = new Dictionary<string, Action>
		{
			["CreateMeshFromCooked"] = () => Record("mesh"),
			["CreateActor"] = () => Record("actor")
		};
		using var fixture = new CreationFixture(backend, hooks);
		fixture.EnqueueFifo(() => { entered.Set(); release.Wait(5000); Record("blocker"); });
		if (!entered.Wait(5000)) throw new Exception("The blocking FIFO operation never started.");
		object chunk = fixture.Chunk("c", "d");
		fixture.EnqueueAsync(chunk);
		fixture.MoveAsyncToFifo(chunk);
		fixture.EnqueueFifo(() => Record("fifo-after"));
		if ((bool)fixture.WorkType.GetField("Asynchronous", Flags).GetValue(chunk))
			throw new Exception("A chunk moved to the FIFO is still marked asynchronous.");
		release.Set();
		fixture.WaitCompleted(chunk);
		DateTime deadline = DateTime.UtcNow.AddSeconds(5);
		while (DateTime.UtcNow < deadline)
		{
			lock (events) if (events.Contains("fifo-after")) break;
			Thread.Sleep(5);
		}
		string observed;
		lock (events) observed = string.Join(",", events);
		if (observed != "blocker,mesh,actor,mesh,actor,fifo-after")
			throw new Exception("A chunk moved to the FIFO did not run to completion in one turn: " + observed);
		Console.WriteLine("PASS: a chunk moved to the FIFO runs all its slices before later FIFO work.");
	}

	private sealed class Handles
	{
		private long _next = 1000;
		public ulong Next() => (ulong)Interlocked.Increment(ref _next);
	}

	/// <summary>
	/// A backend runtime with a running owner thread and a <c>NativeScene</c> whose native entry points are stubs, so production
	/// chunk creation runs without the native bridge. Each stub reports to a hook and hands out increasing handles.
	/// </summary>
	private sealed class CreationFixture : IDisposable
	{
		private readonly Assembly _backend;
		private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), "rupture-slice-" + Guid.NewGuid().ToString("N"));
		private readonly MethodInfo _rent;
		internal readonly Type RuntimeType, SceneType, WorkType;
		internal readonly object Runtime, Scene;

		internal CreationFixture(Assembly backend, Dictionary<string, Action> hooks)
		{
			_backend = backend;
			Directory.CreateDirectory(_cacheRoot);
			RuntimeType = backend.GetType("Rupture.PhysXBackend.ExternalPhysXRuntime", true);
			SceneType = backend.GetType("Rupture.PhysXBackend.NativeScene", true);
			WorkType = backend.GetType("Rupture.PhysXBackend.ExternalPhysXRuntime+OwnerWork", true);
			_rent = backend.GetType("Rupture.PhysXBackend.ExternalPhysXRuntime+DelegateWork", true).GetMethod("Rent", Flags);
			Type apiType = backend.GetType("Rupture.PhysXBackend.NativeApi", true);
			var handles = new Handles();
			object api = RuntimeHelpers.GetUninitializedObject(apiType);
			foreach (FieldInfo field in apiType.GetFields(Flags).Where(f => f.DeclaringType == apiType && typeof(Delegate).IsAssignableFrom(f.FieldType)))
				field.SetValue(api, Stub(field.FieldType, handles, hooks.TryGetValue(field.Name, out Action hook) ? hook : () => { }));
			FieldInfo optionsField = SceneType.GetField("_options", Flags);
			object options = Activator.CreateInstance(optionsField.FieldType);
			optionsField.FieldType.GetProperty("CacheRoot").SetValue(options, _cacheRoot);
			optionsField.FieldType.GetProperty("EftBuild").SetValue(options, "test");
			Scene = RuntimeHelpers.GetUninitializedObject(SceneType);
			SceneType.GetField("_api", Flags).SetValue(Scene, api);
			optionsField.SetValue(Scene, options);
			SceneType.GetField("_handle", Flags).SetValue(Scene, (IntPtr)1);
			foreach (string name in new[] { "_materials", "_meshes", "_heightFields", "_transientMeshesByActor" })
			{
				FieldInfo field = SceneType.GetField(name, Flags);
				field.SetValue(Scene, Activator.CreateInstance(field.FieldType));
			}
			Runtime = Activator.CreateInstance(RuntimeType);
			RuntimeType.GetField("_scene", Flags).SetValue(Runtime, Scene);
			var owner = new Thread(() => RuntimeType.GetMethod("OwnerLoop", Flags).Invoke(Runtime, null)) { IsBackground = true };
			RuntimeType.GetField("_ownerThread", Flags).SetValue(Runtime, owner);
			owner.Start();
		}

		/// <summary>A chunk of one single-mesh actor per given mesh identity.</summary>
		internal object Chunk(params string[] meshes)
		{
			Type actorType = _backend.GetType("Rupture.PhysXBackend.ActorSnapshot", true);
			Type shapeType = _backend.GetType("Rupture.PhysXBackend.ShapeSnapshot", true);
			Type meshType = _backend.GetType("Rupture.PhysXBackend.MeshGeometry", true);
			IList snapshots = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(actorType));
			foreach (string identity in meshes)
			{
				object mesh = Activator.CreateInstance(meshType, true);
				meshType.GetField("Vertices", Flags).SetValue(mesh, new[] { Vector3.zero, Vector3.right, Vector3.up });
				meshType.GetField("Indices", Flags).SetValue(mesh, new uint[] { 0, 1, 2 });
				meshType.GetField("RawHash", Flags).SetValue(mesh, identity);
				meshType.GetField("Layout", Flags).SetValue(mesh, "test");
				object shape = Activator.CreateInstance(shapeType, true);
				shapeType.GetField("Mesh", Flags).SetValue(shape, mesh);
				shapeType.GetField("GeometryKind", Flags).SetValue(shape, 4u);
				object actor = Activator.CreateInstance(actorType, true);
				((IList)actorType.GetField("Shapes", Flags).GetValue(actor)).Add(shape);
				snapshots.Add(actor);
			}
			return Activator.CreateInstance(_backend.GetType("Rupture.PhysXBackend.ExternalPhysXRuntime+ChunkWork", true), Flags, null, new object[] { snapshots }, null);
		}

		internal void EnqueueAsync(object work) => RuntimeType.GetMethod("EnqueueAsync", Flags).Invoke(Runtime, new[] { work });
		internal void MoveAsyncToFifo(object work) => RuntimeType.GetMethod("MoveAsyncToFifo", Flags).Invoke(Runtime, new[] { work });

		internal void EnqueueFifo(Action action) =>
			RuntimeType.GetMethod("EnqueueFifo", Flags).Invoke(Runtime, new[] { _rent.Invoke(null, new object[] { action, "fifo", false, true }) });

		internal void WaitCompleted(object work)
		{
			DateTime deadline = DateTime.UtcNow.AddSeconds(5);
			PropertyInfo completed = WorkType.GetProperty("IsCompleted", Flags);
			while (!(bool)completed.GetValue(work))
			{
				if (DateTime.UtcNow > deadline) throw new Exception("The environment chunk did not complete.");
				Thread.Sleep(2);
			}
		}

		internal string FailureOf(object work) => ((Exception)WorkType.GetField("Exception", Flags).GetValue(work))?.Message;

		internal int CreatedCount(object chunk)
		{
			IList created = (IList)chunk.GetType().GetField("Created", Flags).GetValue(chunk);
			return created?.Count ?? 0;
		}

		public void Dispose()
		{
			((IDisposable)Runtime).Dispose();
			try { Directory.Delete(_cacheRoot, true); } catch (IOException) { }
		}

		/// <summary>A delegate of the native signature that runs <paramref name="hook"/>, hands out a handle for every <c>out ulong</c>, and returns success.</summary>
		private static Delegate Stub(Type delegateType, Handles handles, Action hook)
		{
			MethodInfo invoke = delegateType.GetMethod("Invoke");
			ParameterExpression[] parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
			var body = new List<Expression> { Expression.Invoke(Expression.Constant(hook)) };
			foreach (ParameterExpression parameter in parameters)
				if (parameter.IsByRef && parameter.Type == typeof(ulong))
					body.Add(Expression.Assign(parameter, Expression.Call(Expression.Constant(handles), typeof(Handles).GetMethod(nameof(Handles.Next)))));
			body.Add(invoke.ReturnType == typeof(void) ? Expression.Empty() : Expression.Default(invoke.ReturnType));
			return Expression.Lambda(delegateType, Expression.Block(body), parameters).Compile();
		}
	}
}
