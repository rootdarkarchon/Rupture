using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Rupture.Ragdolls.External;

namespace Rupture.PhysXBackend;

/// <summary>
/// Scene-owner thread scheduling. Two queues feed the single owner thread:
/// <list type="bullet">
/// <item><c>_ownerQueue</c>: strict FIFO for simulation steps, synchronous <c>InvokeOwner</c> work, body/joint creation and every command.</item>
/// <item><c>_asyncQueue</c>: asynchronous environment work (corridor actor creation, static-to-kinematic replacement) in sliced items, drained only while <c>_ownerQueue</c> is empty.</item>
/// </list>
/// </summary>
/// <remarks>
/// Ordering invariants:
/// 1. <c>_ownerQueue</c> order is main-thread submission order. A command queued after a creation therefore always runs after it,
///    and a step always sees every body, joint and command queued before it.
/// 2. Asynchronous items only create NEW native actors. Nothing on the main thread names such an actor until it observes the
///    item complete (<c>IsCompleted</c>/<c>Wait</c> is the happens-before edge) and commits it, so no FIFO item can reference a
///    handle an asynchronous item has not produced yet. Running asynchronous items after later FIFO items is invisible to the main thread.
/// 3. A submitted step is never queued behind more than one asynchronous slice: asynchronous environment work is sliced (see
///    <c>RunSlice</c>), each slice loads at most one mesh or heightfield or creates one actor shell with a bounded number of
///    shapes (see <c>NativeScene.CreateSlice</c>), and the owner serves the FIFO between slices. An item that is mid-way waits in
///    <c>_asyncActive</c> and continues before any other asynchronous item starts.
/// 4. A newly committed actor bumps <c>_activity</c> when the main thread commits it, so a step submitted after the commit
///    includes it even if every body was asleep before.
/// 5. Asynchronous items are tagged with the raid generation and skipped when <c>ResetRaid</c> moved it on, so a stale item can
///    never create actors in a later raid's scene.
/// 6. Asynchronous items never latch <c>_ownerFailure</c>; their exception is reported through the item to its transaction,
///    and only a real scene failure (a FIFO command or step) latches.
/// 7. A caller that must have an asynchronous item finished (<c>MoveAsyncToFifo</c>) takes it out of <c>_asyncActive</c> or
///    <c>_asyncQueue</c> and appends it to <c>_ownerQueue</c>, where it runs all its remaining slices in one turn, so it waits behind
///    submitted FIFO work only, never behind unrelated asynchronous backlog. A slice already running continues without yielding.
/// </remarks>
public sealed partial class ExternalPhysXRuntime
{
	private readonly Queue<OwnerWork> _asyncQueue = new Queue<OwnerWork>();
	/// <summary>The asynchronous item that ran a slice and has slices left; owner and main thread access it under <c>_ownerGate</c>. It runs before <c>_asyncQueue</c>.</summary>
	private OwnerWork _asyncActive;
	/// <summary>True while the owner runs a slice of asynchronous work; a step queued then is counted as waiting behind it.</summary>
	private volatile bool _asyncSliceRunning;
	/// <summary>Asynchronous slices run, the longest one, and the steps that were queued behind a running slice with the longest of their waits; written by the owner thread.</summary>
	private long _asyncItems, _stepAsyncWaits;
	private double _asyncItemMaxMilliseconds, _stepAsyncWaitMaxMilliseconds;
	private volatile int _raidGeneration;
	private StepWork _stepWorkItem;

	#region Owner thread

	private void OwnerLoop()
	{
		while (!_disposed && !_ownerThreadUnresponsive)
		{
			OwnerWork work = null;
			lock (_ownerGate)
			{
				if (_ownerQueue.Count != 0) work = _ownerQueue.Dequeue();
				else if (_asyncActive != null)
				{
					work = _asyncActive;
					_asyncActive = null;
				}
				else if (_asyncQueue.Count != 0) work = _asyncQueue.Dequeue();
				// Published under the gate, so MoveAsyncToFifo finds an item that is queued, resumable or executing.
				_executingWork = work;
			}
			if (work == null)
			{
				_ownerSignal.WaitOne(100);
				continue;
			}
			ExecuteOwnerWork(work);
		}
		// A quarantined owner may eventually return from native code. It alone can
		// then release its scene; the main thread never frees resources beneath it.
		try { _scene?.Dispose(); _scene = null; }
		catch (Exception exception) { Log(ExternalLogLevel.Error, "External PhysX owner cleanup failed: " + exception.Message); }
		lock (_ownerGate)
		{
			AbandonQueue(_ownerQueue);
			AbandonQueue(_asyncQueue);
			if (_asyncActive != null) Abandon(_asyncActive);
			_asyncActive = null;
		}
	}

	private static void AbandonQueue(Queue<OwnerWork> queue)
	{
		while (queue.Count > 0) Abandon(queue.Dequeue());
	}

	private static void Abandon(OwnerWork abandoned)
	{
		abandoned.Exception = new InvalidOperationException("External PhysX owner stopped before this operation.");
		abandoned.Complete();
	}

	/// <summary>
	/// Runs one item. An asynchronous sliced item runs a single slice and goes back to <c>_asyncActive</c> when it has more, so the
	/// loop serves the FIFO before its next slice. Everything else, including an asynchronous item a caller moved to the FIFO, runs to completion.
	/// </summary>
	private void ExecuteOwnerWork(OwnerWork work)
	{
		while (!ExecuteSlice(work))
		{
			lock (_ownerGate)
			{
				if (!work.Asynchronous) continue;
				_asyncActive = work;
				_executingWork = null;
				return;
			}
		}
		_executingWork = null;
		bool recycle = work.FireAndForget;
		work.Complete();
		// Nobody waits on a fire-and-forget item, so the owner returns it to its pool.
		if (recycle) work.Recycle();
	}

	/// <summary>Runs one slice and accounts it; false when the item has slices left. A failed item is finished.</summary>
	private bool ExecuteSlice(OwnerWork work)
	{
		bool asynchronous = work.Asynchronous;
		long started = Stopwatch.GetTimestamp();
		work.Stopwatch.Start();
		_asyncSliceRunning = asynchronous;
		bool finished = true;
		try
		{
			if (work.Generation >= 0 && work.Generation != _raidGeneration)
				throw new InvalidOperationException("The raid ended before this asynchronous owner operation ran.");
			finished = work.RunSlice(this);
		}
		catch (Exception exception)
		{
			work.Exception = exception;
			if (work.LatchException) _ownerFailure = exception.GetType().Name + ": " + exception.Message;
		}
		finally
		{
			work.Stopwatch.Stop();
			_asyncSliceRunning = false;
			double sliceMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
			if (asynchronous)
			{
				_asyncItems++;
				if (sliceMilliseconds > _asyncItemMaxMilliseconds) _asyncItemMaxMilliseconds = sliceMilliseconds;
			}
			if (sliceMilliseconds >= 100)
				Log(ExternalLogLevel.Warning, $"External PhysX slow owner operation: name={work.Name}, phase={work.Phase}, executionMs={sliceMilliseconds:F3}, dt={work.DeltaTime:F6}, bodies={work.BodyCount}, joints={work.JointCount}, completed={finished}, quarantined={_ownerThreadUnresponsive}.");
		}
		return finished;
	}

	/// <summary>Owner thread: a step that was queued behind a running asynchronous slice has started; <paramref name="waitTicks"/> is its queue time.</summary>
	private void RecordStepWaitBehindAsync(long waitTicks)
	{
		_stepAsyncWaits++;
		double milliseconds = waitTicks * 1000d / Stopwatch.Frequency;
		if (milliseconds > _stepAsyncWaitMaxMilliseconds) _stepAsyncWaitMaxMilliseconds = milliseconds;
	}

	/// <summary>Status counters of the asynchronous owner lane; slices are the owner turns of sliced environment work.</summary>
	private string DescribeOwnerAsync()
	{
		NativeScene scene = _scene;
		return $"ownerAsync=items:{_asyncItems}/maxMs:{_asyncItemMaxMilliseconds:F1}/stepWaits:{_stepAsyncWaits}/stepWaitMaxMs:{_stepAsyncWaitMaxMilliseconds:F1}" +
			$"/preloads:{scene?.GeometryPreloads ?? 0L}/inlineLoads:{scene?.InlineGeometryLoads ?? 0L}, ";
	}

	#endregion

	#region Submission

	private void InvokeOwner(Action action, [CallerMemberName] string name = null)
	{
		if (_disposed || _ownerThreadUnresponsive)
			throw new InvalidOperationException("External PhysX owner is unavailable.");
		InvokeOwnerWork(DelegateWork.Rent(action, name, latchException: false, fireAndForget: false));
	}

	/// <summary>Queues work on the strict FIFO and blocks until it completes, under the 500 ms watchdog.</summary>
	private void InvokeOwnerWork(OwnerWork work)
	{
		if (_disposed || _ownerThreadUnresponsive)
			throw new InvalidOperationException("External PhysX owner is unavailable.");
		lock (_ownerGate) { _activity.Changed(); _ownerQueue.Enqueue(work); }
		_ownerSignal.Set();
		AwaitOwner(work);
		Exception failure = work.Exception;
		work.Recycle();
		if (failure != null)
			throw new InvalidOperationException($"External PhysX owner operation failed: {DescribeException(failure)}", failure);
	}

	/// <summary>
	/// Waits for queued owner work. Past the 500 ms watchdog the owner is declared unresponsive and the backend is
	/// quarantined until process exit, exactly as for a synchronous operation.
	/// </summary>
	private void AwaitOwner(OwnerWork work)
	{
		if (work.Wait(500)) return;
		_ownerThreadUnresponsive = true;
		_ready = false;
		_raidStarted = false;
		_failure = "External PhysX scene-owner operation exceeded the 500 ms watchdog. The backend is quarantined until process exit. " + DescribeOwner(work);
		Log(ExternalLogLevel.Error, _failure);
		throw new TimeoutException(_failure);
	}

	private void EnqueueOwner(Action action, [CallerMemberName] string name = null)
	{
		if (_disposed || _ownerThreadUnresponsive) return;
		EnqueueFifo(DelegateWork.Rent(action, name, latchException: true, fireAndForget: true));
	}

	/// <summary>Queues work on the strict FIFO without waiting; the work wakes the simulation like any command.</summary>
	private void EnqueueFifo(OwnerWork work)
	{
		lock (_ownerGate) { _activity.Changed(); _ownerQueue.Enqueue(work); }
		_ownerSignal.Set();
	}

	/// <summary>
	/// Queues asynchronous environment work on the low-priority queue (see the ordering invariants above). The caller commits
	/// the result when it observes completion and then bumps the simulation activity.
	/// </summary>
	private void EnqueueAsync(OwnerWork work)
	{
		work.Generation = _raidGeneration;
		work.Asynchronous = true;
		lock (_ownerGate) _asyncQueue.Enqueue(work);
		_ownerSignal.Set();
	}

	/// <summary>
	/// Takes a still-queued or mid-way asynchronous item out of the low-priority lane and appends it to the FIFO, for a caller that
	/// is about to block on it; it then runs to completion there. A slice the owner is running right now is told not to yield and
	/// continues; an item that already completed needs nothing.
	/// </summary>
	private void MoveAsyncToFifo(OwnerWork work)
	{
		bool moved = false;
		lock (_ownerGate)
		{
			if (ReferenceEquals(_asyncActive, work))
			{
				_asyncActive = null;
				moved = true;
			}
			else
			{
				for (int remaining = _asyncQueue.Count; remaining > 0; remaining--)
				{
					OwnerWork queued = _asyncQueue.Dequeue();
					if (!moved && ReferenceEquals(queued, work)) moved = true;
					else _asyncQueue.Enqueue(queued);
				}
			}
			if (moved || ReferenceEquals(_executingWork, work)) work.Asynchronous = false;
			if (moved)
			{
				_activity.Changed();
				_ownerQueue.Enqueue(work);
			}
		}
		if (moved) _ownerSignal.Set();
	}

	private void EnqueueCommand(CommandWork work, string name)
	{
		if (_disposed || _ownerThreadUnresponsive)
		{
			work.Recycle();
			return;
		}
		work.Name = name;
		work.LatchException = true;
		work.FireAndForget = true;
		EnqueueFifo(work);
	}

	private void QueueShapeEnabled(ulong shape, bool enabled)
	{
		CommandWork work = CommandWork.Rent(CommandKind.SetShapeEnabled);
		work.HandleA = shape;
		work.FlagA = enabled;
		EnqueueCommand(work, nameof(QueueShapeEnabled));
	}

	/// <summary>The shape handle is read on the owner, so a scale rebuild or kinematic replacement queued earlier is honoured.</summary>
	private void QueueEnvironmentShapeEnabled(EnvironmentShape shape, bool enabled)
	{
		CommandWork work = CommandWork.Rent(CommandKind.SetEnvironmentShapeEnabled);
		work.Shape = shape;
		work.FlagA = enabled;
		EnqueueCommand(work, nameof(QueueEnvironmentShapeEnabled));
	}

	private void QueueDestroyActor(ulong actor)
	{
		CommandWork work = CommandWork.Rent(CommandKind.DestroyActor);
		work.HandleA = actor;
		EnqueueCommand(work, nameof(QueueDestroyActor));
	}

	private void QueueDestroyEnvironmentActor(EnvironmentActor actor)
	{
		CommandWork work = CommandWork.Rent(CommandKind.DestroyEnvironmentActor);
		work.Actor = actor;
		EnqueueCommand(work, nameof(QueueDestroyEnvironmentActor));
	}

	private void QueueDestroyJoint(ulong joint)
	{
		CommandWork work = CommandWork.Rent(CommandKind.DestroyJoint);
		work.HandleA = joint;
		EnqueueCommand(work, nameof(QueueDestroyJoint));
	}

	private void QueueSetBodyState(ulong actor, NativeApi.BodyState state, bool kinematic, bool wake)
	{
		CommandWork work = CommandWork.Rent(CommandKind.SetBodyState);
		work.HandleA = actor;
		work.State = state;
		work.FlagA = kinematic;
		work.FlagB = wake;
		EnqueueCommand(work, nameof(QueueSetBodyState));
	}

	private void QueueActorPose(ulong actor, NativeApi.Pose pose, bool kinematicTarget)
	{
		CommandWork work = CommandWork.Rent(CommandKind.SetActorPose);
		work.HandleA = actor;
		work.Pose = pose;
		work.FlagA = kinematicTarget;
		EnqueueCommand(work, nameof(QueueActorPose));
	}

	/// <summary>Kinematic target for an environment actor; its native handle is read on the owner.</summary>
	private void QueueEnvironmentActorPose(EnvironmentActor actor, NativeApi.Pose pose)
	{
		CommandWork work = CommandWork.Rent(CommandKind.SetEnvironmentActorPose);
		work.Actor = actor;
		work.Pose = pose;
		EnqueueCommand(work, nameof(QueueEnvironmentActorPose));
	}

	private void QueueApplyForce(NativeApi.ForceCommand command)
	{
		CommandWork work = CommandWork.Rent(CommandKind.ApplyForce);
		work.Force = command;
		EnqueueCommand(work, nameof(QueueApplyForce));
	}

	private void QueueIgnoreShapePair(ulong left, ulong right)
	{
		CommandWork work = CommandWork.Rent(CommandKind.IgnoreShapePair);
		work.HandleA = left;
		work.HandleB = right;
		EnqueueCommand(work, nameof(QueueIgnoreShapePair));
	}

	#endregion

	#region Diagnostics

	private string DescribeOwner(OwnerWork waiting)
	{
		OwnerWork executing = _executingWork;
		int queued;
		int queuedAsync;
		lock (_ownerGate)
		{
			queued = _ownerQueue.Count;
			queuedAsync = _asyncQueue.Count + (_asyncActive != null ? 1 : 0);
		}
		return $"waiting={waiting?.Name}, waitingStarted={waiting?.Stopwatch.IsRunning}, executing={executing?.Name ?? "none"}, " +
			$"phase={executing?.Phase ?? "none"}, executionMs={executing?.Stopwatch.Elapsed.TotalMilliseconds ?? 0:F3}, " +
			$"dt={executing?.DeltaTime ?? 0:F6}, bodies={executing?.BodyCount ?? 0}, joints={executing?.JointCount ?? 0}, queued={queued}, queuedAsync={queuedAsync}";
	}

	private static string DescribeException(Exception exception)
	{
		if (exception == null) return "unknown failure";
		System.Text.StringBuilder description = new System.Text.StringBuilder();
		int depth = 0;
		for (Exception current = exception; current != null && depth < 8; current = current.InnerException, depth++)
		{
			if (description.Length != 0) description.Append(" -> ");
			description.Append(current.GetType().Name).Append(": ").Append(current.Message);
		}
		return description.ToString();
	}

	#endregion

	#region Owner work items

	/// <summary>
	/// One unit of owner-thread work. Completion is a <see cref="ManualResetEventSlim"/> instead of a task so pooled items
	/// are reused without allocating. Only the owner thread runs <see cref="Run"/>; the submitter observes completion
	/// through <see cref="IsCompleted"/>/<see cref="Wait"/>, which publishes everything <see cref="Run"/> wrote.
	/// </summary>
	private abstract class OwnerWork
	{
		internal readonly Stopwatch Stopwatch = new Stopwatch();
		private readonly ManualResetEventSlim _completed = new ManualResetEventSlim(false);
		internal Exception Exception;
		internal bool LatchException;
		/// <summary>Nobody waits for or reads the item: the owner recycles it after running it.</summary>
		internal bool FireAndForget;
		/// <summary>A value of zero or more marks asynchronous environment work that is skipped when the raid generation moved on.</summary>
		internal int Generation = -1;
		/// <summary>The item sits in the low-priority lane and yields between slices; cleared when a caller moves it to the FIFO.</summary>
		internal bool Asynchronous;
		internal string Name;
		internal volatile string Phase = "operation";
		internal long Sequence;
		internal float DeltaTime;
		internal int BodyCount, JointCount;

		internal bool IsCompleted => _completed.IsSet;
		internal bool Wait(int timeoutMilliseconds) => _completed.IsSet || _completed.Wait(Math.Max(0, timeoutMilliseconds));
		internal void Complete() => _completed.Set();
		internal abstract void Run(ExternalPhysXRuntime runtime);

		/// <summary>Runs one slice and reports whether the item is finished; only sliced items override it, the default is one slice.</summary>
		internal virtual bool RunSlice(ExternalPhysXRuntime runtime)
		{
			Run(runtime);
			return true;
		}

		/// <summary>Returns a pooled item to its pool; the default does nothing for items that are not pooled.</summary>
		internal virtual void Recycle() { }

		internal virtual void Reset()
		{
			Exception = null;
			LatchException = false;
			FireAndForget = false;
			Generation = -1;
			Asynchronous = false;
			Name = null;
			Phase = "operation";
			Sequence = 0;
			DeltaTime = 0f;
			BodyCount = JointCount = 0;
			Stopwatch.Reset();
			_completed.Reset();
		}
	}

	/// <summary>A small lock-protected pool; owner and main thread both return items.</summary>
	private static class WorkPool<T> where T : OwnerWork, new()
	{
		private const int MaximumPooled = 64;
		private static readonly Stack<T> Items = new Stack<T>();

		internal static T Rent()
		{
			lock (Items)
				if (Items.Count != 0) return Items.Pop();
			return new T();
		}

		internal static void Return(T item)
		{
			lock (Items)
				if (Items.Count < MaximumPooled) Items.Push(item);
		}
	}

	/// <summary>Closure-based work for operations that are rare or hold a result for their submitter.</summary>
	private sealed class DelegateWork : OwnerWork
	{
		private Action _action;
		private bool _pooled;

		public DelegateWork() { }

		/// <summary>An item that is never pooled, for submitters that keep it (transient body tickets, the full bake).</summary>
		internal DelegateWork(Action action, string name)
		{
			_action = action;
			Name = name;
		}

		internal static DelegateWork Rent(Action action, string name, bool latchException, bool fireAndForget)
		{
			DelegateWork work = WorkPool<DelegateWork>.Rent();
			work._pooled = true;
			work._action = action;
			work.Name = name;
			work.LatchException = latchException;
			work.FireAndForget = fireAndForget;
			return work;
		}

		internal override void Run(ExternalPhysXRuntime runtime) => _action();

		internal override void Recycle()
		{
			if (!_pooled) return;
			_action = null;
			Reset();
			WorkPool<DelegateWork>.Return(this);
		}
	}

	private enum CommandKind : byte
	{
		SetShapeEnabled,
		SetEnvironmentShapeEnabled,
		DestroyActor,
		DestroyEnvironmentActor,
		DestroyJoint,
		SetBodyState,
		SetActorPose,
		SetEnvironmentActorPose,
		ApplyForce,
		IgnoreShapePair
	}

	/// <summary>
	/// The frequent fire-and-forget commands as one pooled typed item, so queueing one allocates neither a closure nor a
	/// completion object. A missing scene (raid reset) makes every command a no-op, like the null-conditional calls it replaces.
	/// </summary>
	private sealed class CommandWork : OwnerWork
	{
		internal CommandKind Kind;
		internal ulong HandleA, HandleB;
		internal bool FlagA, FlagB;
		internal NativeApi.BodyState State;
		internal NativeApi.Pose Pose;
		internal NativeApi.ForceCommand Force;
		internal EnvironmentShape Shape;
		internal EnvironmentActor Actor;

		internal static CommandWork Rent(CommandKind kind)
		{
			CommandWork work = WorkPool<CommandWork>.Rent();
			work.Kind = kind;
			return work;
		}

		internal override void Run(ExternalPhysXRuntime runtime)
		{
			NativeScene scene = runtime._scene;
			if (scene == null) return;
			switch (Kind)
			{
			case CommandKind.SetShapeEnabled: scene.SetShapeEnabled(HandleA, FlagA); break;
			case CommandKind.SetEnvironmentShapeEnabled: scene.SetShapeEnabled(Shape.NativeHandle, FlagA); break;
			case CommandKind.DestroyActor: scene.DestroyActor(HandleA); break;
			case CommandKind.DestroyEnvironmentActor: scene.DestroyActor(Actor.Native.Handle); break;
			case CommandKind.DestroyJoint: scene.DestroyJoint(HandleA); break;
			case CommandKind.SetBodyState: scene.SetBodyState(HandleA, State, FlagA, FlagB); break;
			case CommandKind.SetActorPose: scene.SetActorPose(HandleA, Pose, FlagA); break;
			case CommandKind.SetEnvironmentActorPose: scene.SetActorPose(Actor.Native.Handle, Pose, kinematicTarget: true); break;
			case CommandKind.ApplyForce: scene.ApplyForce(Force); break;
			case CommandKind.IgnoreShapePair: scene.IgnoreShapePair(HandleA, HandleB); break;
			}
		}

		internal override void Recycle()
		{
			Shape = null;
			Actor = null;
			Reset();
			WorkPool<CommandWork>.Return(this);
		}
	}

	/// <summary>
	/// The simulation step. One instance is reused for the whole session: a step is submitted only after the previous one was
	/// consumed by <see cref="CompleteStep"/>, so steady-state submission allocates nothing.
	/// </summary>
	private sealed class StepWork : OwnerWork
	{
		internal ExternalJoint[] Joints;
		internal ExternalCasingFrame CasingFrame;
		internal NativeApi.BodyState[] StepStates;
		internal int PublishedBodies;
		internal bool AwakeBodies;
		internal long CommandVersion;
		/// <summary>The step was queued while the owner ran an asynchronous slice, so its start is delayed by at most that slice.</summary>
		internal bool QueuedBehindAsync;
		internal long QueuedTimestamp;

		internal override void Run(ExternalPhysXRuntime runtime)
		{
			if (QueuedBehindAsync) runtime.RecordStepWaitBehindAsync(System.Diagnostics.Stopwatch.GetTimestamp() - QueuedTimestamp);
			NativeScene scene = runtime._scene;
			ExternalCasingFrame casingFrame = CasingFrame;
			if (casingFrame != null && runtime._ownerFailure != null) throw new InvalidOperationException(runtime._ownerFailure);
			if (casingFrame != null) Phase = "casing commands";
			if (casingFrame != null) scene.CasingCommands(casingFrame);
			long simulationStarted = casingFrame != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
			Phase = "native simulation and body readback";
			StepStates = scene.Step(DeltaTime, out PublishedBodies, out AwakeBodies);
			if (casingFrame != null) casingFrame.SimulationMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - simulationStarted) * 1000d / System.Diagnostics.Stopwatch.Frequency;
			if (casingFrame != null) Phase = "casing readback";
			if (casingFrame != null) scene.ReadCasings(casingFrame);
			Phase = "joint publication";
			ExternalJoint[] joints = Joints;
			for (int index = 0; index < joints.Length; index++)
			{
				ExternalJoint joint = joints[index];
				if (joint.IsValid) joint.PublishBroken(scene.IsJointBroken(joint.Handle));
			}
		}

		internal override void Reset()
		{
			base.Reset();
			Joints = null;
			CasingFrame = null;
			StepStates = null;
			PublishedBodies = 0;
			AwakeBodies = false;
			CommandVersion = 0;
			QueuedBehindAsync = false;
			QueuedTimestamp = 0L;
		}
	}

	#endregion
}
