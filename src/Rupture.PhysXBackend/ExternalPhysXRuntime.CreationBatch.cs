using System;
using System.Collections.Generic;
using UnityEngine;
using Rupture.Ragdolls.External;

namespace Rupture.PhysXBackend;

/// <summary>
/// Body and joint creation as typed owner items. A synchronous call runs one item in its own owner round trip; while a
/// creation batch is open the items queue up and the whole batch runs as one owner transaction when the scope is disposed.
/// Item Execute/Rollback run on the owner thread and consume snapshots only; they never touch a Unity object.
/// </summary>
public sealed partial class ExternalPhysXRuntime
{
	private CreationBatchScope _creationBatch;

	public IExternalCreationBatch BeginCreationBatch()
	{
		if (_creationBatch != null) throw new InvalidOperationException("A creation batch is already open.");
		return _creationBatch = new CreationBatchScope(this);
	}

	/// <summary>Runs one creation item now, or queues it while a batch is open.</summary>
	private void SubmitCreation(CreationItem item)
	{
		CreationBatchScope scope = _creationBatch;
		if (scope != null)
		{
			scope.Add(item);
			return;
		}
		ExecuteCreation(new List<CreationItem>(1) { item });
	}

	/// <summary>
	/// One blocking owner transaction for the items, under the 500 ms watchdog. The owner rolls back everything it created
	/// when any item fails, and every wrapper is invalidated, so a failed transaction leaves nothing registered or native.
	/// </summary>
	private void ExecuteCreation(List<CreationItem> items)
	{
		try { InvokeOwnerWork(new CreationBatchWork(items)); }
		catch
		{
			foreach (CreationItem item in items) item.Fail();
			throw;
		}
		// Registration happens here, in creation order, once every native handle exists.
		foreach (CreationItem item in items) item.Finish(this);
	}

	private void CloseCreationBatch(CreationBatchScope scope)
	{
		if (scope.Closed) return;
		scope.Closed = true;
		if (!ReferenceEquals(_creationBatch, scope))
		{
			foreach (CreationItem item in scope.Items) item.Fail();
			throw new InvalidOperationException("The creation batch was abandoned by a raid reset.");
		}
		_creationBatch = null;
		if (scope.Items.Count == 0) return;
		ExecuteCreation(scope.Items);
	}

	/// <summary>
	/// Gives up an open batch: nothing queued runs, every queued wrapper is invalidated and the batch no longer captures
	/// creation calls. Never throws, so a failing build can call it from its failure path without hiding the original error.
	/// </summary>
	private void AbandonCreationBatch(CreationBatchScope scope)
	{
		if (scope.Closed) return;
		scope.Closed = true;
		if (ReferenceEquals(_creationBatch, scope)) _creationBatch = null;
		foreach (CreationItem item in scope.Items) item.Fail();
		scope.Items.Clear();
	}

	/// <summary>The open batch of one build; remembers which colliders belong to bodies that have no native shapes yet.</summary>
	private sealed class CreationBatchScope : IExternalCreationBatch
	{
		private readonly ExternalPhysXRuntime _runtime;
		private readonly HashSet<int> _colliderIds = new HashSet<int>();
		internal readonly List<CreationItem> Items = new List<CreationItem>(48);
		internal bool Closed;

		internal CreationBatchScope(ExternalPhysXRuntime runtime) { _runtime = runtime; }

		internal void Add(CreationItem item)
		{
			Items.Add(item);
			item.CollectColliderIds(_colliderIds);
		}

		internal bool ContainsCollider(int colliderId) => _colliderIds.Contains(colliderId);

		public void Abandon() => _runtime.AbandonCreationBatch(this);

		public void Dispose() => _runtime.CloseCreationBatch(this);
	}

	private abstract class CreationItem
	{
		/// <summary>Owner thread: creates the native objects and publishes their handles into the managed wrappers.</summary>
		internal abstract void Execute(NativeScene scene);

		/// <summary>Owner thread: destroys what a successful <see cref="Execute"/> created, after a later item failed.</summary>
		internal virtual void Rollback(NativeScene scene) { }

		/// <summary>Main thread: the transaction failed; the wrapper must not be used.</summary>
		internal abstract void Fail();

		/// <summary>Main thread: the transaction succeeded; register the wrapper.</summary>
		internal abstract void Finish(ExternalPhysXRuntime runtime);

		internal virtual void CollectColliderIds(HashSet<int> colliderIds) { }
	}

	private enum BodyShapeKind : byte { Mesh, Box, RoundedBox }

	private sealed class BodyCreationItem : CreationItem
	{
		internal readonly ExternalBody Body;
		private readonly ActorSnapshot _snapshot;
		private readonly BodyShapeKind _kind;
		private readonly Vector3 _centerOfMass;
		private readonly IReadOnlyList<Collider> _sourceColliders;
		private NativeActor _native;
		private ulong _shapeHandle;

		internal BodyCreationItem(ExternalPhysXRuntime runtime, ActorSnapshot snapshot, BodyShapeKind kind, Vector3 centerOfMass,
			IReadOnlyList<Collider> sourceColliders)
		{
			_snapshot = snapshot;
			_kind = kind;
			_centerOfMass = centerOfMass;
			_sourceColliders = sourceColliders;
			// Provisional centers: Bind replaces them with the values the created actor reports.
			Body = new ExternalBody(runtime, snapshot.Body, centerOfMass, kind == BodyShapeKind.Box ? snapshot.Body.CenterOfMass.ToUnity() : centerOfMass);
		}

		internal override void Execute(NativeScene scene)
		{
			NativeActor native = scene.CreateActor(_snapshot);
			_native = native;
			try
			{
				switch (_kind)
				{
				case BodyShapeKind.Mesh:
					Body.Bind(native.Handle, _centerOfMass, _centerOfMass);
					for (int index = 0; index < _snapshot.Shapes.Count && index < native.ShapeHandles.Count; index++)
					{
						int colliderId = _snapshot.Shapes[index].ColliderId;
						Body.ShapeHandlesByCollider[colliderId] = native.ShapeHandles[index];
						Body.ShapeEnabledByHandle[native.ShapeHandles[index]] = _snapshot.Shapes[index].Enabled;
					}
					break;
				case BodyShapeKind.Box:
					Body.Bind(native.Handle, _centerOfMass, _snapshot.Body.CenterOfMass.ToUnity());
					_shapeHandle = native.ShapeHandles[0];
					break;
				default:
					Vector3 physicalCenter = native.PhysicalCenterOfMass ?? _snapshot.Body.CenterOfMass.ToUnity();
					Body.Bind(native.Handle, physicalCenter, physicalCenter);
					_shapeHandle = native.ShapeHandles[0];
					break;
				}
			}
			catch
			{
				_native = null;
				scene.DestroyActor(native.Handle);
				throw;
			}
		}

		internal override void Rollback(NativeScene scene)
		{
			if (_native != null) scene.DestroyActor(_native.Handle);
		}

		internal override void Fail() => Body.Invalidate();

		internal override void Finish(ExternalPhysXRuntime runtime)
		{
			// Several native query colliders can represent one standalone weapon shape; their Unity identities are read on main.
			if (_kind != BodyShapeKind.Mesh) BindCompoundColliders(Body, _sourceColliders, _shapeHandle);
			runtime.RegisterBody(Body);
		}

		internal override void CollectColliderIds(HashSet<int> colliderIds)
		{
			if (_kind == BodyShapeKind.Mesh)
			{
				foreach (ShapeSnapshot shape in _snapshot.Shapes) colliderIds.Add(shape.ColliderId);
				return;
			}
			foreach (Collider collider in _sourceColliders)
				if (collider != null) colliderIds.Add(collider.GetInstanceID());
		}
	}

	private sealed class JointCreationItem : CreationItem
	{
		internal readonly ExternalJoint Joint = new ExternalJoint();
		private readonly JointSnapshot _snapshot;
		private readonly ExternalBody _child;
		private readonly ExternalBody _connected;
		private readonly bool _ignoreShapePairs;
		private ulong _handle;

		internal JointCreationItem(JointSnapshot snapshot, ExternalBody child, ExternalBody connected, bool ignoreShapePairs)
		{
			_snapshot = snapshot;
			_child = child;
			_connected = connected;
			_ignoreShapePairs = ignoreShapePairs;
		}

		internal override void Execute(NativeScene scene)
		{
			if (!_child.IsBound || (_connected != null && !_connected.IsBound))
				throw new InvalidOperationException("A joint body has no native actor.");
			_snapshot.BindActors(_child.NativeHandle, _connected?.NativeHandle ?? 0);
			ulong handle = scene.CreateJoint(_snapshot);
			try
			{
				if (_ignoreShapePairs)
					foreach (ulong left in _child.ShapeHandlesByCollider.Values)
						foreach (ulong right in _connected.ShapeHandlesByCollider.Values) scene.IgnoreShapePair(left, right);
			}
			catch
			{
				scene.DestroyJoint(handle);
				throw;
			}
			_handle = handle;
			Joint.Bind(handle);
		}

		internal override void Rollback(NativeScene scene) => scene.DestroyJoint(_handle);

		internal override void Fail() => Joint.Invalidate();

		internal override void Finish(ExternalPhysXRuntime runtime)
		{
			runtime._joints[_handle] = Joint;
			runtime._jointSnapshot = null;
		}
	}

	/// <summary>All queued creation items as one owner transaction: if any item fails, the ones that ran are destroyed again.</summary>
	private sealed class CreationBatchWork : OwnerWork
	{
		private readonly List<CreationItem> _items;

		internal CreationBatchWork(List<CreationItem> items)
		{
			_items = items;
			Name = items.Count == 1 ? "CreateNative" : "CreationBatch";
		}

		internal override void Run(ExternalPhysXRuntime runtime)
		{
			NativeScene scene = runtime._scene ?? throw new InvalidOperationException("External PhysX scene is unavailable.");
			int executed = 0;
			try
			{
				for (; executed < _items.Count; executed++) _items[executed].Execute(scene);
			}
			catch
			{
				for (int index = executed - 1; index >= 0; index--)
				{
					// Best effort: a failed rollback must not mask the creation failure that caused it.
					try { _items[index].Rollback(scene); }
					catch (Exception) { }
				}
				throw;
			}
		}
	}
}
