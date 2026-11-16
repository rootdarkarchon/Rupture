using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace Rupture;

/// <summary>
/// Pre-JIT of the kill path: every method of the listed types is compiled a few at a time per warmup step on the main
/// thread, so the first kill, limb cut and head wound run already-compiled code. <c>RuntimeHelpers.PrepareMethod</c>
/// is an empty method in the game's Unity Mono <c>mscorlib</c> (IL body is a single <c>ret</c>, and the runtime
/// registers no such icall), so it compiles nothing. <c>RuntimeMethodHandle.GetFunctionPointer</c> is an InternalCall
/// that the Mono runtime implements by compiling the method, so it is used instead. Compiling can run the static
/// constructors of the types a method touches, which is only safe on the main thread.
/// </summary>
internal static partial class RaidWarmup
{
    private sealed class JitTarget
    {
        internal readonly string AssemblyName;
        internal readonly string TypeName;
        /// <summary>The assembly once found; the backend assembly is loaded lazily, so a miss is not cached.</summary>
        internal Assembly Assembly;
        /// <summary>True once the type was found and every method was attempted; survives raids because compiled code stays.</summary>
        internal bool Done;
        /// <summary>Index of the next method to compile when a raid ended in the middle of this target; survives raids.</summary>
        internal int Resume;

        internal JitTarget(string assemblyName, string typeName)
        {
            AssemblyName = assemblyName;
            TypeName = typeName;
        }
    }

    private const string MainAssemblyName = "Rupture";
    private const string BackendAssemblyName = "Rupture.PhysXBackend";

    /// <summary>
    /// Kill-path types in the order a kill reaches them. Partial declarations, nested types (lambda display classes,
    /// iterator state machines) and closed generic base types are collected with the type. Names are resolved at run
    /// time; a missing type is skipped with one debug line and retried by the next raid's pass.
    /// </summary>
    private static readonly JitTarget[] JitTargets =
    {
        new JitTarget(MainAssemblyName, "Rupture.Combined.Patches.KillPatch"),
        new JitTarget(MainAssemblyName, "Rupture.Impacts.ImpactStateService"),
        new JitTarget(MainAssemblyName, "Rupture.Impacts.ImpactData"),
        new JitTarget(MainAssemblyName, "Rupture.Impacts.LimbImpactPolicy"),
        new JitTarget(MainAssemblyName, "Rupture.Impacts.DeathImpactPolicy"),
        new JitTarget(MainAssemblyName, "Rupture.Ragdolls.Classes.ShadowRagdollWorld"),
        new JitTarget(MainAssemblyName, "Rupture.Ragdolls.Classes.RagdollCoordinator"),
        new JitTarget(MainAssemblyName, "Rupture.Ragdolls.Classes.CorpseWeaponCollisionFilter"),
        new JitTarget(MainAssemblyName, "Rupture.Ragdolls.Classes.WeaponColliderGeometry"),
        new JitTarget(MainAssemblyName, "Rupture.Ragdolls.Classes.BodyHierarchyPath"),
        new JitTarget(MainAssemblyName, "Rupture.Ragdolls.Classes.ShotImpactProcessor"),
        new JitTarget(MainAssemblyName, "Rupture.Ragdolls.Classes.LethalImpulseExperiment"),
        // Its throw sequence is the generic HeadgearThrowSequence<T>, collected as the closed base of the nested PendingDetach.
        new JitTarget(MainAssemblyName, "Rupture.Ragdolls.Classes.LethalHeadgearDetach"),
        // ProceduralLimbCutBatch and ProceduralLimbCutBoundaries are partial declarations of ProceduralLimbCutGeometry.
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.Limbs.LimbDismembermentSystem"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.Limbs.ProceduralLimbCutGeometry"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.Limbs.LimbCutLifetime"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.Limbs.LimbDecalTextureTransfer"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.Limbs.BodyDecalRendering"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.MeshFrameTransform"),
        // The head-wound geometry job runs on the worker thread, so it is not compiled here.
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.HeadWounds.HeadWoundSystem"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.HeadWounds.HeadWoundBallistics"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.HeadWounds.HeadWoundPolicy"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.HeadWounds.HeadWoundOrder"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.HeadWounds.HeadWoundShape"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.HeadWounds.HeadWoundSkinning"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.HeadWounds.HeadWoundLifetime"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.HeadWounds.HeadWoundMaterialProvider"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.BloodStreams.PulsatingBloodStreamSystem"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.BloodStreams.BleedingWoundTracker"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.BloodStreams.BloodDecalBatch"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.BloodStreams.BloodStreamMaterialProvider"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.BloodStreams.BleedEmissionPolicy"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.BloodStreams.ClothingSoakingSystem"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.Limbs.LimbSeverMaterialProvider"),
        // The first fracture of a raid compiled all of these inside FractureSystem.Update (21.9 ms measured); FractureSurface
        // carries its Snapshot, Hit and PoseSample nested types.
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.Fractures.FractureSystem"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.Fractures.FractureSurface"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.Fractures.FractureVisual"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.Fractures.FractureAppearance"),
        // GenerateFractureBone builds the rigid bone ends with the same wound builder as limb cuts. Listing these two
        // types cut the fixture's first fracture visual from 9.6 to 5.9 ms. The generic instantiations the builder
        // reaches (LINQ over value types) cannot be listed: FractureWarmup runs them.
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.Limbs.ProceduralLimbWoundGeometry"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.Limbs.LimbSeverMath"),
        new JitTarget(MainAssemblyName, "Rupture.Dismemberment.Classes.Fractures.FractureWarmup"),
        // Shots fired reach the casing world's main-thread stage before any kill does.
        new JitTarget(MainAssemblyName, "Rupture.Casings.CasingWorld"),
        new JitTarget(MainAssemblyName, "Rupture.Casings.CasingImpactQuery"),
        // Loaded lazily by the physics bootstrap, so a pass may not find them; the next raid's Begin retries.
        new JitTarget(BackendAssemblyName, "Rupture.PhysXBackend.ExternalPhysXRuntime"),
        new JitTarget(BackendAssemblyName, "Rupture.PhysXBackend.NativeScene"),
        new JitTarget(BackendAssemblyName, "Rupture.PhysXBackend.ActorSnapshot"),
        new JitTarget(BackendAssemblyName, "Rupture.PhysXBackend.JointSnapshot"),
        // MeshGeometrySources.cs declares these two; the cooked-shape extraction is nested in the first.
        new JitTarget(BackendAssemblyName, "Rupture.PhysXBackend.UnityPhysXMeshGeometrySource"),
        new JitTarget(BackendAssemblyName, "Rupture.PhysXBackend.UnityPhysXShapeUnavailableException"),
        new JitTarget(BackendAssemblyName, "Rupture.PhysXBackend.MeshGeometry"),
        new JitTarget(BackendAssemblyName, "Rupture.PhysXBackend.MeshGeometryRepository")
    };

    private const BindingFlags JitMemberFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
        BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static int _jitTarget;
    private static List<MethodBase> _jitQueue;
    private static int _jitQueueIndex;
    private static int _jitCompiled;
    private static int _jitFailed;
    private static int _jitMissingTypes;
    private static int _jitSteps;
    private static long _jitTicks;
    private static string _jitFirstFailure;

    private static void BeginPreJit()
    {
        _jitTarget = 0;
        _jitQueue = null;
        _jitQueueIndex = 0;
        ResetPreJitReport();
    }

    /// <summary>Compiles methods until a quarter of the frame allowance is spent. Returns true when the pass over every target is finished.</summary>
    private static bool StepPreJit()
    {
        long started = Stopwatch.GetTimestamp();
        bool finished = CompilePreJitSlice(started);
        _jitTicks += Stopwatch.GetTimestamp() - started;
        _jitSteps++;
        if (finished) LogPreJit();
        return finished;
    }

    private static bool CompilePreJitSlice(long started)
    {
        long softLimit = (long)(FrameWorkBudget.BudgetMilliseconds * Stopwatch.Frequency / 4000d);
        int methods = 0;
        while (_jitTarget < JitTargets.Length)
        {
            JitTarget target = JitTargets[_jitTarget];
            if (target.Done)
            {
                _jitTarget++;
                continue;
            }
            // At least one method per step so the pass always progresses.
            if (methods > 0 && Stopwatch.GetTimestamp() - started >= softLimit) return false;
            if (_jitQueue == null)
            {
                Type type = ResolveType(target);
                if (type == null)
                {
                    _jitMissingTypes++;
                    if (RupturePlugin.Log.IsDebugEnabled)
                        RupturePlugin.Log.LogInfo($"RPT startup JIT: type {target.TypeName} is not loaded; it is retried by the next raid.");
                    _jitTarget++;
                    continue;
                }
                _jitQueue = CollectMethods(type);
                _jitQueueIndex = target.Resume;
                continue;
            }
            while (_jitQueueIndex < _jitQueue.Count)
            {
                if (methods > 0 && Stopwatch.GetTimestamp() - started >= softLimit)
                {
                    target.Resume = _jitQueueIndex;
                    return false;
                }
                MethodBase method = _jitQueue[_jitQueueIndex++];
                methods++;
                try
                {
                    method.MethodHandle.GetFunctionPointer();
                    _jitCompiled++;
                }
                catch (Exception exception)
                {
                    _jitFailed++;
                    _jitFirstFailure ??= method.DeclaringType?.Name + "." + method.Name + ": " + exception.GetType().Name + ": " + exception.Message;
                }
            }
            target.Done = true;
            _jitTarget++;
            _jitQueue = null;
        }
        return true;
    }

    /// <summary>
    /// Every method, constructor and accessor declared by the type and its nested types that the JIT can compile on its
    /// own. A member list that cannot be read counts as one failure and yields what was read so far.
    /// </summary>
    private static List<MethodBase> CollectMethods(Type type)
    {
        List<MethodBase> queue = new List<MethodBase>();
        try
        {
            AddMethods(type, queue);
        }
        catch (Exception exception)
        {
            _jitFailed++;
            _jitFirstFailure ??= type.Name + ": " + exception.GetType().Name + ": " + exception.Message;
        }
        return queue;
    }

    private static void AddMethods(Type type, List<MethodBase> queue)
    {
        // Generic code is compiled per instantiation, so an open declaration has nothing to compile.
        if (type.ContainsGenericParameters) return;
        foreach (ConstructorInfo constructor in type.GetConstructors(JitMemberFlags))
            if (IsCompilable(constructor)) queue.Add(constructor);
        foreach (MethodInfo method in type.GetMethods(JitMemberFlags))
            if (IsCompilable(method)) queue.Add(method);
        foreach (Type nested in type.GetNestedTypes(JitMemberFlags))
            AddMethods(nested, queue);
        // A closed generic base (HeadgearThrowSequence<Item>) owns code the derived type runs but does not declare.
        Type baseType = type.BaseType;
        if (baseType != null && baseType.IsGenericType && !baseType.ContainsGenericParameters && baseType.Assembly == type.Assembly)
            AddMethods(baseType, queue);
    }

    private static bool IsCompilable(MethodBase method)
    {
        if (method.IsAbstract || method.ContainsGenericParameters || (method.Attributes & MethodAttributes.PinvokeImpl) != 0) return false;
        MethodImplAttributes flags = method.GetMethodImplementationFlags();
        // Runtime-implemented members (delegate Invoke) and internal calls have no IL body to compile.
        return (flags & MethodImplAttributes.InternalCall) == 0 && (flags & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.IL;
    }

    private static void LogPreJit()
    {
        if (_jitCompiled + _jitFailed > 0 && RupturePlugin.Log.IsDebugEnabled)
        {
            string failure = _jitFailed > 0 ? ", firstFailure=" + _jitFirstFailure : string.Empty;
            RupturePlugin.Log.LogInfo($"RPT startup JIT: methods={_jitCompiled + _jitFailed}, compiled={_jitCompiled}, failed={_jitFailed}, missingTypes={_jitMissingTypes}, steps={_jitSteps}, mainMs={_jitTicks * 1000d / Stopwatch.Frequency:F1}{failure}.");
        }
        // A later pass after a material invalidation reports only its own work.
        ResetPreJitReport();
    }

    private static void ResetPreJitReport()
    {
        _jitCompiled = 0;
        _jitFailed = 0;
        _jitMissingTypes = 0;
        _jitSteps = 0;
        _jitTicks = 0;
        _jitFirstFailure = null;
    }

    private static Type ResolveType(JitTarget target)
    {
        if (target.Assembly == null)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(assembly.GetName().Name, target.AssemblyName, StringComparison.Ordinal)) continue;
                target.Assembly = assembly;
                break;
            }
        }
        return target.Assembly?.GetType(target.TypeName, false);
    }
}
