using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;

internal static class Program
{
    private const string PhysicsMathTypeName = "Rupture.Ragdolls.Classes.PhysicsMath";
    private const string CorpseSleepPolicyTypeName = "Rupture.Ragdolls.Classes.CorpseSleepPolicy";
    private const string SequencePolicyTypeName = "Rupture.Ragdolls.Classes.RagdollSequencePolicy";
    private const string HeadlessSimulationPolicyTypeName = "Rupture.Ragdolls.Classes.HeadlessSimulationPolicy";
    private const string HeadWoundBallisticsTypeName = "Rupture.Dismemberment.Classes.HeadWounds.HeadWoundBallistics";
    private const string ProceduralLimbGeometryTypeName = "Rupture.Dismemberment.Classes.Limbs.ProceduralLimbCutGeometry";
    private const string ProceduralLimbWoundTypeName = "Rupture.Dismemberment.Classes.Limbs.ProceduralLimbWoundGeometry";

    internal static Assembly Bridge { get; private set; }

    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[1] == "--capture-wire")
        {
            PacketCompatibilityTests.Capture(Assembly.LoadFrom(Path.GetFullPath(args[0])), args[2]);
            return 0;
        }
        if (args.Length != 2 || !File.Exists(args[0]) || !File.Exists(args[1]))
        {
            Console.Error.WriteLine("Pass the rebuilt Rupture assembly path and bridge assembly paths.");
            return 2;
        }

        OptionalFikaTests.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
        var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        Bridge = Assembly.LoadFrom(Path.GetFullPath(args[1]));
        PacketCompatibilityTests.Run(Bridge);
		PoseAssemblyTests.Run(assembly);
		CorpseDragAbiTests.Run(assembly);
		BotSeverTests.Run(assembly, Bridge);
        ImpactPolicyTests.Run(assembly);
        ConfigurationTests.Run(assembly);
        HeadlessConfigurationTests.Run(assembly, Bridge);
            PerformanceTimingTests.Run(assembly);
            HotPathAbiTests.Run();
        CasingAbiTests.Run(assembly);
        DeathImpactLifecycleTests.Run(assembly);
        NaturalDeathTests.Run(assembly);
        DownedDeathTests.Run(assembly, Bridge);
        ImpactReconciliationTests.Run(assembly, Bridge);
        HeadWoundPolicyTests.Run(assembly);
        HeadgearDiagnosticsTests.Run(assembly, Bridge);
        ImpactWireTests.Run(Bridge);
        ImpactAbiTests.Run(assembly);
        ExplosionSeverTests.Run(assembly);
        LivingExplosionSeverTests.Run(assembly);
        StartupAbiTests.Run(assembly);
        BodyDecalLodTests.Run(assembly);
        BleedingTests.Run(assembly, Bridge);
        FractureTests.Run(assembly);
		ReliabilityBehaviorTests.Run(assembly);
        HollywoodFxCompatibilityTests.Run(assembly);
        LimbFailureRetentionTests.Run(assembly);
        ClothingLoaderAbiTests.Run(assembly);

        var physicsMathType = assembly.GetType(PhysicsMathTypeName, throwOnError: false);
        if (physicsMathType == null)
        {
            Console.Error.WriteLine("FAIL: the rebuilt plugin has no projectile momentum calculator.");
            return 1;
        }

        var calculateImpulse = physicsMathType.GetMethod("CalculateProjectileImpulse", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (calculateImpulse == null)
        {
            Console.Error.WriteLine("FAIL: the projectile momentum calculator has no CalculateProjectileImpulse method.");
            return 1;
        }

        var impulseCases = new[]
        {
            new ImpulseCase("zero mass", 0f, 350f, 1f, 0f),
            new ImpulseCase("negative mass", -1f, 350f, 1f, 0f),
            new ImpulseCase("zero velocity", 8f, 0f, 1f, 0f),
            new ImpulseCase("negative velocity", 8f, -1f, 1f, 0f),
            new ImpulseCase("zero multiplier", 8f, 350f, 0f, 0f),
            new ImpulseCase("negative multiplier", 8f, 350f, -1f, 0f),
            new ImpulseCase("NaN mass", float.NaN, 350f, 1f, 0f),
            new ImpulseCase("infinite velocity", 8f, float.PositiveInfinity, 1f, 0f),
			new ImpulseCase("9mm reference", 8f, 350f, 1f, 11.2f),
			new ImpulseCase("5.56 reference", 4f, 900f, 1f, 14.4f),
			new ImpulseCase("12 gauge reference", 28f, 450f, 1f, 50.4f),
			new ImpulseCase("50 BMG reference", 45f, 850f, 1f, 153f),
			new ImpulseCase("multiplier", 8f, 350f, 0.5f, 5.6f),
            new ImpulseCase("exact cap", 500f, 1000f, 1f, 500f),
            new ImpulseCase("outlier cap", 1000f, 1000f, 1f, 500f)
        };

        foreach (var impulseCase in impulseCases)
        {
            var actual = (float)calculateImpulse.Invoke(null, new object[]
            {
                impulseCase.MassGram,
                impulseCase.VelocityMetersPerSecond,
                impulseCase.Multiplier
            });

            if (Math.Abs(actual - impulseCase.Expected) > 0.0001f)
            {
                Console.Error.WriteLine("FAIL: " + impulseCase.Name + " expected " + impulseCase.Expected + " but got " + actual + ".");
                return 1;
            }

            Console.WriteLine("PASS: " + impulseCase.Name);
        }

        var headWoundBallisticsType = assembly.GetType(HeadWoundBallisticsTypeName, throwOnError: false);
        var calculateHeadWound = headWoundBallisticsType?.GetMethod("TryCalculate", BindingFlags.Static | BindingFlags.NonPublic);
        var createHeadWoundSeed = headWoundBallisticsType?.GetMethod("CreateSeed", BindingFlags.Static | BindingFlags.NonPublic);
        if (headWoundBallisticsType == null || calculateHeadWound == null || createHeadWoundSeed == null)
        {
            Console.Error.WriteLine("FAIL: procedural head-wound ballistic scaling is unavailable.");
            return 1;
        }

        if (TryCalculateHeadWound(calculateHeadWound, 0f, 350f, 9f, 20f, "Caliber9x19PARA", out _) ||
            TryCalculateHeadWound(calculateHeadWound, 8f, float.NaN, 9f, 20f, "Caliber9x19PARA", out _) ||
            TryCalculateHeadWound(calculateHeadWound, 8f, 350f, 0f, 20f, "Caliber9x19PARA", out _))
        {
            Console.Error.WriteLine("FAIL: invalid head-wound ballistic inputs were accepted.");
            return 1;
        }

        if (!TryCalculateHeadWound(calculateHeadWound, 8f, 350f, 9f, 25f, "Caliber9x19PARA", out HeadWoundResult handgun) ||
            !TryCalculateHeadWound(calculateHeadWound, 10f, 800f, 7.62f, 55f, "Caliber762x51", out HeadWoundResult rifle) ||
            !TryCalculateHeadWound(calculateHeadWound, 32f, 420f, 18.5f, 35f, "Caliber12g", out HeadWoundResult shotgun))
        {
            Console.Error.WriteLine("FAIL: representative head-wound ballistic inputs were rejected.");
            return 1;
        }
        if (Math.Abs(handgun.EntryRadius - 0.0054f) > 0.0001f ||
            rifle.Energy <= handgun.Energy || rifle.Momentum <= handgun.Momentum || rifle.ExitRadius <= handgun.ExitRadius ||
            shotgun.Momentum <= rifle.Momentum || shotgun.EntryRadius <= rifle.EntryRadius ||
            shotgun.EntryRadius < 0.00925f || shotgun.EntryRadius > 0.01001f || shotgun.ExitRadius > 0.03001f)
        {
            Console.Error.WriteLine("FAIL: handgun/rifle/shotgun head-wound scaling is not monotonic or bounded.");
            return 1;
        }
        if (!TryCalculateHeadWound(calculateHeadWound, 10f, 500f, 9f, 30f, "Caliber9x19PARA", out HeadWoundResult pistolClass) ||
            !TryCalculateHeadWound(calculateHeadWound, 10f, 500f, 9f, 30f, "Caliber12g", out HeadWoundResult shotgunClass) ||
            shotgunClass.ExitRadius <= pistolClass.ExitRadius)
        {
            Console.Error.WriteLine("FAIL: caliber class does not contribute to exit-wound scaling.");
            return 1;
        }
        uint seedA = (uint)createHeadWoundSeed.Invoke(null, new object[] { "profile", 7u, 42 });
        uint seedB = (uint)createHeadWoundSeed.Invoke(null, new object[] { "profile", 7u, 42 });
        uint seedC = (uint)createHeadWoundSeed.Invoke(null, new object[] { "profile", 7u, 43 });
        if (seedA == 0 || seedA != seedB || seedA == seedC)
        {
            Console.Error.WriteLine("FAIL: head-wound irregularity seeds are not deterministic and event-specific.");
            return 1;
        }
        Console.WriteLine("PASS: deterministic handgun/rifle/shotgun head-wound scaling and bounds");

        var sleepPolicyType = assembly.GetType(CorpseSleepPolicyTypeName, throwOnError: false);
        var clampDuration = sleepPolicyType?.GetMethod("ClampDuration", BindingFlags.Static | BindingFlags.NonPublic);
        var shouldForceSleep = sleepPolicyType?.GetMethod("ShouldForceSleep", BindingFlags.Static | BindingFlags.NonPublic);
        if (sleepPolicyType == null || clampDuration == null || shouldForceSleep == null)
        {
            Console.Error.WriteLine("FAIL: bounded corpse sleep policy is unavailable.");
            return 1;
        }

        var clampCases = new[]
        {
            new[] { 0, 1 },
            new[] { 1, 1 },
            new[] { 5, 5 },
            new[] { 15, 15 },
            new[] { 16, 15 }
        };
        foreach (var clampCase in clampCases)
        {
            var actual = (int)clampDuration.Invoke(null, new object[] { clampCase[0] });
            if (actual != clampCase[1])
            {
                Console.Error.WriteLine("FAIL: corpse duration " + clampCase[0] + " clamped to " + actual + " instead of " + clampCase[1] + ".");
                return 1;
            }
        }
        Console.WriteLine("PASS: corpse duration 1/5/15-second boundaries");

        var sleepCases = new[]
        {
            new SleepCase("one second before loop", 0f, 1, true),
            new SleepCase("five seconds just before", 3.999f, 5, false),
            new SleepCase("five seconds at deadline", 4f, 5, true),
            new SleepCase("fifteen seconds just before", 13.999f, 15, false),
            new SleepCase("fifteen seconds at deadline", 14f, 15, true)
        };
        foreach (var sleepCase in sleepCases)
        {
            var actual = (bool)shouldForceSleep.Invoke(null, new object[] { sleepCase.NativeTimePass, sleepCase.TotalDuration });
            if (actual != sleepCase.Expected)
            {
                Console.Error.WriteLine("FAIL: " + sleepCase.Name + " expected " + sleepCase.Expected + " but got " + actual + ".");
                return 1;
            }
        }
        Console.WriteLine("PASS: native one-second delay is included in the death-to-inert deadline");

        var sequencePolicyType = assembly.GetType(SequencePolicyTypeName, throwOnError: false);
        var classifySequence = sequencePolicyType?.GetMethod("Classify", BindingFlags.Static | BindingFlags.NonPublic);
        var tryAcceptEvent = sequencePolicyType?.GetMethod("TryAcceptEvent", BindingFlags.Static | BindingFlags.NonPublic);
        var isExpired = sequencePolicyType?.GetMethod("IsExpired", BindingFlags.Static | BindingFlags.NonPublic);
        if (sequencePolicyType == null || classifySequence == null || tryAcceptEvent == null || isExpired == null)
        {
            Console.Error.WriteLine("FAIL: Fika ragdoll sequence policy is unavailable.");
            return 1;
        }

        var relationCases = new[]
        {
            new RelationCase("zero is invalid", 1u, 0u, "Invalid"),
            new RelationCase("older is stale", 5u, 4u, "Stale"),
            new RelationCase("equal is current", 5u, 5u, "Current"),
            new RelationCase("greater is newer", 5u, 6u, "Newer"),
            new RelationCase("first nonzero is newer", 0u, 1u, "Newer")
        };
        foreach (var relationCase in relationCases)
        {
            var actual = classifySequence.Invoke(null, new object[] { relationCase.Current, relationCase.Incoming }).ToString();
            if (!string.Equals(actual, relationCase.Expected, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("FAIL: " + relationCase.Name + " expected " + relationCase.Expected + " but got " + actual + ".");
                return 1;
            }
        }
        Console.WriteLine("PASS: stale, current, newer and invalid death sequences");

        var receivedEvents = new HashSet<uint>();
        if (!(bool)tryAcceptEvent.Invoke(null, new object[] { 7u, 7u, 11u, receivedEvents }) ||
            (bool)tryAcceptEvent.Invoke(null, new object[] { 7u, 7u, 11u, receivedEvents }) ||
            (bool)tryAcceptEvent.Invoke(null, new object[] { 7u, 6u, 12u, receivedEvents }) ||
            (bool)tryAcceptEvent.Invoke(null, new object[] { 7u, 7u, 0u, receivedEvents }))
        {
            Console.Error.WriteLine("FAIL: Fika event deduplication accepted a duplicate, stale, or zero event.");
            return 1;
        }
        Console.WriteLine("PASS: Fika event sequence deduplication and stale rejection");

        if ((bool)isExpired.Invoke(null, new object[] { 9.999f, 10f }) ||
            !(bool)isExpired.Invoke(null, new object[] { 10f, 10f }) ||
            (bool)isExpired.Invoke(null, new object[] { 100f, 0f }))
        {
            Console.Error.WriteLine("FAIL: pending lifecycle expiry boundaries are incorrect.");
            return 1;
        }
        Console.WriteLine("PASS: ten-second pending lifecycle expiry boundaries");

        var headlessPolicyType = assembly.GetType(HeadlessSimulationPolicyTypeName, throwOnError: false);
        var tryConsumeHeadlessStep = headlessPolicyType?.GetMethod("TryConsume", BindingFlags.Static | BindingFlags.NonPublic);
        if (headlessPolicyType == null || tryConsumeHeadlessStep == null)
        {
            Console.Error.WriteLine("FAIL: variable-rate headless simulation policy is unavailable.");
            return 1;
        }

        float accumulator = 0f;
        if (ConsumeHeadlessStep(tryConsumeHeadlessStep, 1f / 60f, ref accumulator, out _) ||
            !ConsumeHeadlessStep(tryConsumeHeadlessStep, 1f / 60f, ref accumulator, out float thirtyHzDelta) ||
            Math.Abs(thirtyHzDelta - 1f / 30f) > 0.0001f || Math.Abs(accumulator) > 0.0001f)
        {
            Console.Error.WriteLine("FAIL: a 60 Hz authority was not capped to one 30 Hz simulation step.");
            return 1;
        }

        accumulator = 0f;
        if (!ConsumeHeadlessStep(tryConsumeHeadlessStep, 1f / 20f, ref accumulator, out float twentyHzDelta) ||
            Math.Abs(twentyHzDelta - 1f / 20f) > 0.0001f || Math.Abs(accumulator) > 0.0001f)
        {
            Console.Error.WriteLine("FAIL: a 20 Hz authority did not carry its full 50 ms frame delta.");
            return 1;
        }

        accumulator = 0f;
        if (ConsumeHeadlessStep(tryConsumeHeadlessStep, 0.02f, ref accumulator, out _) ||
            !ConsumeHeadlessStep(tryConsumeHeadlessStep, 0.05f, ref accumulator, out float hitchDelta) ||
            Math.Abs(hitchDelta - 0.07f) > 0.0001f || Math.Abs(accumulator) > 0.0001f)
        {
            Console.Error.WriteLine("FAIL: a low-Hz update discarded accumulated simulation time or requested catch-up work.");
            return 1;
        }

        accumulator = 0f;
        int steps = 0;
        float simulatedTime = 0f;
        for (int index = 0; index < 10; index++)
        {
            if (ConsumeHeadlessStep(tryConsumeHeadlessStep, 0.01f, ref accumulator, out float stepDelta))
            {
                steps++;
                simulatedTime += stepDelta;
            }
        }
        if (steps != 3 || Math.Abs(simulatedTime + accumulator - 0.1f) > 0.0001f)
        {
            Console.Error.WriteLine("FAIL: a 100 Hz authority did not preserve time while remaining capped to 30 Hz.");
            return 1;
        }
        Console.WriteLine("PASS: headless simulation follows Update below 30 Hz without time loss or extra submissions");

		if (!RunProceduralLimbGeometryTests(assembly)) return 1;
		if (!RunClothingBoundaryTests(assembly)) return 1;
		if (!RunCaptureQueueTests(assembly)) return 1;

        return 0;
    }

	private static bool RunCaptureQueueTests(Assembly assembly)
	{
		Type type = assembly.GetType("Rupture.Dismemberment.Classes.Limbs.LimbCutCaptureQueue", true);
		MethodInfo enqueue = type.GetMethod("TryEnqueue", BindingFlags.Static | BindingFlags.NonPublic);
		MethodInfo reset = type.GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic);
		MethodInfo dequeue = type.GetMethod("TryDequeueReport", BindingFlags.Static | BindingFlags.NonPublic);
		bool Add(Func<string> work) => (bool)enqueue.Invoke(null, new object[] { work });
		using var entered = new System.Threading.ManualResetEventSlim();
		using var release = new System.Threading.ManualResetEventSlim();
		int staleExecuted = 0;
		reset.Invoke(null, null);
		try
		{
			if (!Add(() => { entered.Set(); if (!release.Wait(10000)) throw new TimeoutException(); return "old raid"; }) || !entered.Wait(5000))
				throw new InvalidOperationException("capture worker did not start");
			for (int i = 0; i < 3; i++)
				if (!Add(() => { System.Threading.Interlocked.Increment(ref staleExecuted); return "stale pending"; }))
					throw new InvalidOperationException("enqueue waited for writer or rejected within capacity");
			if (Add(() => "overflow")) throw new InvalidOperationException("capture backlog was not bounded");
			reset.Invoke(null, null);
			if (!Add(() => "new raid")) throw new InvalidOperationException("reset did not release pending capacity");
			release.Set();
			string report = null;
			bool AwaitReport() => System.Threading.SpinWait.SpinUntil(() => {
				object[] args = { null }; bool found = (bool)dequeue.Invoke(null, args); if (found) report = (string)args[0]; return found;
			}, 5000);
			if (!AwaitReport() || report != "new raid" || staleExecuted != 0)
				throw new InvalidOperationException("old-raid diagnostics leaked across reset: " + report);
			Add(() => throw new System.IO.IOException("intentional capture failure"));
			if (!AwaitReport() || !report.Contains("captureError=IOException")) throw new InvalidOperationException("writer failure was not isolated");
			Add(() => "writer survived");
			if (!AwaitReport() || report != "writer survived") throw new InvalidOperationException("writer stopped after export failure");
			Console.WriteLine("PASS: blocked diagnostic writer never blocks enqueue; backlog is bounded, raid reset drops pending work, and export failures are isolated");
			return true;
		}
		catch (Exception exception) { Console.Error.WriteLine("FAIL: background capture queue: " + exception); return false; }
		finally { release.Set(); reset.Invoke(null, null); }
	}

	private static bool RunClothingBoundaryTests(Assembly assembly)
	{
		Type geometry = assembly.GetType(ProceduralLimbGeometryTypeName, true);
		MethodInfo inspect = geometry.GetMethod("InspectClothingCutForTests", BindingFlags.Static | BindingFlags.NonPublic);
		MethodInfo ownership = geometry.GetMethod("ClassifyCompetingOwnershipForTests", BindingFlags.Static | BindingFlags.NonPublic);
		int[] selected = (int[])ownership.Invoke(null, new object[]
		{
			new[] { -0.05f, 0f, 0.1f, 0.05f, 0f, 0.2f, -0.05f, 0f, 0.2f, 0.05f, 0f, 0.3f },
			new[] { 0, 1, 2, 1, 3, 2 }, new[] { 1f, 0.02f, 0.02f, 0.02f }, new[] { 0f, 0.98f, 0.98f, 0.98f }
		});
		if (!selected.SequenceEqual(new[] { 0 }))
		{
			Console.Error.WriteLine("FAIL: competing torso ownership was admitted through small limb weights, or mixed seam triangles were lost.");
			return false;
		}
		MethodInfo cacheTest = geometry.GetMethod("FailureCacheForTests", BindingFlags.Static | BindingFlags.NonPublic);
		string[] cachedAttempts = (string[])cacheTest.Invoke(null, null);
		string expected = "Proximal:0.00:False;Proximal:-0.05:False;Proximal:0.05:False;Distal:0.00:False;Distal:-0.05:False;Distal:0.05:False";
		if (cachedAttempts[0] != expected || cachedAttempts[1] != expected.Replace("False", "True") ||
			cachedAttempts[2] != expected || cachedAttempts[3] != expected ||
			cachedAttempts[4] != string.Join(";", expected.Split(';').Skip(3).Concat(expected.Split(';').Take(3))).Replace("False", "True"))
		{
			Console.Error.WriteLine("FAIL: six-candidate order, failure reuse across seeds, mesh isolation or reset: " + string.Join(" | ", cachedAttempts));
			return false;
		}
		Console.WriteLine("PASS: six-candidate failures are cached across wound seeds, isolated by source, and invalidated on reset");
		Console.WriteLine("PASS: competing bone ownership blocks minority-weight torso spill and preserves mixed seam triangles");
		// A disconnected sleeve detail outside the fitted capsule must travel
		// with its limb; a nearby torso-owned detail must stay on the body.
		selected = (int[])ownership.Invoke(null, new object[]
		{
			new[] { 0.5f, 0f, 0.3f, 0.55f, 0f, 0.4f, 0.5f, 0.1f, 0.4f,
				0.6f, 0f, 0.3f, 0.65f, 0f, 0.4f, 0.6f, 0.1f, 0.4f },
			new[] { 0, 1, 2, 3, 4, 5 }, new[] { 1f, 1f, 1f, 0.02f, 0.02f, 0.02f },
			new[] { 0f, 0f, 0f, 0.98f, 0.98f, 0.98f }
		});
		if (!selected.SequenceEqual(new[] { 0 }))
		{
			Console.Error.WriteLine("FAIL: loose disconnected limb detail was left floating, or torso detail was removed.");
			return false;
		}
		Console.WriteLine("PASS: disconnected strongly owned sleeve detail outside the capsule follows the limb; torso detail is preserved");
		float[] positions = new float[3 * 8 * 3];
		for (int ring = 0; ring < 3; ring++)
			for (int vertex = 0; vertex < 8; vertex++)
			{
				int offset = (ring * 8 + vertex) * 3;
				positions[offset] = MathF.Cos(vertex * MathF.PI / 4) * 0.065f;
				positions[offset + 1] = MathF.Sin(vertex * MathF.PI / 4) * 0.055f;
				positions[offset + 2] = ring * 0.35f;
			}
		foreach (bool open in new[] { false, true })
		{
			var triangles = new List<int>();
			for (int ring = 0; ring < 2; ring++)
				for (int vertex = 0; vertex < (open ? 7 : 8); vertex++)
				{
					int a = ring * 8 + vertex, b = ring * 8 + (vertex + 1) % 8;
					triangles.AddRange(new[] { a, b, a + 8, b, b + 8, a + 8 });
				}
			foreach (bool gib in new[] { false, true })
			foreach (bool leg in new[] { false, true })
			foreach (bool proximal in new[] { false, true })
			foreach (uint seed in new[] { 1u, 123456u, 0xabcdefu })
			{
				object[] args = { positions, triangles.ToArray(), Enumerable.Range(0, triangles.Count / 3).ToArray(), gib, leg, proximal, seed, null };
				string failure = (string)inspect.Invoke(null, args);
				int[] metrics = (int[])args[7];
				if (failure != null || metrics[0] != 1 || metrics[1] != (leg ? 8 : 6) || metrics[2] != (proximal ? 1 : 2))
				{
					Console.Error.WriteLine($"FAIL: clothing pipeline open={open}, gib={gib}, leg={leg}, proximal={proximal}, seed={seed}: {failure ?? string.Join(",", metrics)}");
					return false;
				}
			}
			if (!open)
			{
				float[] exactPlane = (float[])positions.Clone();
				for (int vertex = 8; vertex < 16; vertex++) exactPlane[vertex * 3 + 2] = 0.3f;
				object[] touching = { exactPlane, triangles.ToArray(), Enumerable.Range(0, triangles.Count / 3).ToArray(), false, false, true, 1u, null };
				string touchingFailure = (string)inspect.Invoke(null, touching);
				if (touchingFailure != null)
				{
					Console.Error.WriteLine("FAIL: cut exactly through source vertices produced duplicate crossing vertices: " + touchingFailure);
					return false;
				}
				int[] degenerate = triangles.Concat(new[] { 0, 1, 0 }).ToArray();
				object[] collapsed = { positions, degenerate, Enumerable.Range(0, degenerate.Length / 3).ToArray(), false, false, true, 1u, null };
				string collapsedFailure = (string)inspect.Invoke(null, collapsed);
				if (collapsedFailure != null)
				{
					Console.Error.WriteLine("FAIL: collapsed source face manufactured overlapping retained owners: " + collapsedFailure);
					return false;
				}

				object[] near = { positions, triangles.ToArray(), Enumerable.Range(0, triangles.Count / 3).Where(i => i != 0).ToArray(), false, false, true, 1u, null };
				string nearFailure = (string)inspect.Invoke(null, near);
				int[] nearMetrics = (int[])near[7];
				if (nearFailure != null || nearMetrics[3] == 0 || nearMetrics[4] == 0)
				{
					Console.Error.WriteLine("FAIL: local classification interface did not stitch and close: " + nearFailure);
					return false;
				}
				object[] torn = { positions, triangles.ToArray(), Enumerable.Range(0, triangles.Count / 3).Where(i => i != 16).ToArray(), false, false, true, 1u, null };
				string failure = (string)inspect.Invoke(null, torn);
				if (failure == null || !failure.Contains("selection tear away from the seam"))
				{
					Console.Error.WriteLine("FAIL: distant classification tear was accepted: " + failure);
					return false;
				}
			}
		}
		Console.WriteLine("PASS: closed and open garments survive clipping, real-edge reconstruction, rim, wounds and blood sampling for every cut type and side");
		Console.WriteLine("PASS: selection interfaces stitch shared source edges; distant tears fail before renderer materialization");
		string directory = Path.Combine(Path.GetTempPath(), "RPT-clothing-capture-test-" + Guid.NewGuid().ToString("N"));
		try
		{
			MethodInfo capture = geometry.GetMethod("CaptureClothingForTests", BindingFlags.Static | BindingFlags.NonPublic);
			string[] reports = (string[])capture.Invoke(null, new object[] { directory });
			if (reports.Any(report => report.Contains("Error=")) || !reports[1].Contains("repeat=2") ||
				Directory.GetFiles(directory, "source-*.json.gz").Length != 1 || Directory.GetFiles(directory, "case-*.json.gz").Length != 2)
			{
				Console.Error.WriteLine("FAIL: source capture/deduplication: " + string.Join(";", reports));
				return false;
			}
			foreach (string file in Directory.GetFiles(directory, "case-*.json.gz"))
			{
				using var stream = File.OpenRead(file);
				using var gzip = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
				using var document = System.Text.Json.JsonDocument.Parse(gzip);
				var root = document.RootElement;
				if (root.GetProperty("Cuts")[0].GetProperty("Selected")[0].GetInt32() != 0 ||
					root.GetProperty("Boundaries")[0].GetProperty("PlaneEdges").GetArrayLength() != 1 ||
					!File.Exists(Path.Combine(directory, root.GetProperty("Source").GetString())))
					throw new InvalidDataException("Clothing capture lost source/selection/boundary evidence.");
			}
		}
		finally
		{
			if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
		}
		Console.WriteLine("PASS: failed clothing captures preserve mesh/selection/boundary data, share immutable sources and deduplicate repeated NPCs independently of seed");
		return true;
	}

	private static bool RunProceduralLimbGeometryTests(Assembly assembly)
	{
		Type geometry = assembly.GetType(ProceduralLimbGeometryTypeName, throwOnError: false);
		MethodInfo classify = geometry?.GetMethod("ClassifyBindSpaceForTests", BindingFlags.Static | BindingFlags.NonPublic);
		MethodInfo classifyDebug = geometry?.GetMethod("ClassifyDebugCountsForTests", BindingFlags.Static | BindingFlags.NonPublic);
		MethodInfo orderLoops = geometry?.GetMethod("OrderClosedLoopsForTests", BindingFlags.Static | BindingFlags.NonPublic);
		MethodInfo copyOuterLoop = geometry?.GetMethod("CopyExactOuterLoopForTests", BindingFlags.Static | BindingFlags.NonPublic);
		MethodInfo disturbOuterRim = geometry?.GetMethod("DisturbOuterRimForTests", BindingFlags.Static | BindingFlags.NonPublic);
		if (geometry == null || classify == null || classifyDebug == null || orderLoops == null ||
			copyOuterLoop == null || disturbOuterRim == null)
		{
			Console.Error.WriteLine("FAIL: procedural limb topology regression hooks are unavailable.");
			return false;
		}

		float[] bindPositions =
		{
			0f, -0.1f, 0f, 0f, 0.1f, 0f,
			1f, -0.1f, 0f, 1f, 0.1f, 0f,
			2f, -0.1f, 0f, 2f, 0.1f, 0f,
			0.55f, -0.3f, 0.05f, 0.55f, 0.3f, 0.05f,
			1.25f, -0.3f, 0.05f, 1.25f, 0.3f, 0.05f
		};
		int[] triangles =
		{
			0, 2, 1, 1, 2, 3, 2, 4, 3, 3, 4, 5,
			6, 8, 7, 7, 8, 9
		};
		// The zero-weight middle ring models upper-hip/shoulder transition vertices.
		// Connectivity and the finite capsule must carry the distal flood through it,
		// while the disconnected zero-weight torso component remains excluded.
		float[] limbWeights = { 1f, 1f, 0f, 0f, 1f, 1f, 0f, 0f, 0f, 0f };
		float[] seedWeights = { 0f, 0f, 0f, 0f, 1f, 1f, 0f, 0f, 0f, 0f };
		float[] unfoldedPose = (float[])bindPositions.Clone();
		float[] foldedAcrossTorsoPose = (float[])bindPositions.Clone();
		for (int vertex = 2; vertex <= 5; vertex++)
		{
			foldedAcrossTorsoPose[vertex * 3] = 0.8f + (vertex - 2) * 0.03f;
			foldedAcrossTorsoPose[vertex * 3 + 1] = 0f;
			foldedAcrossTorsoPose[vertex * 3 + 2] = 0.05f;
		}
		object[] commonArguments =
		{
			triangles, limbWeights, seedWeights,
			new[] { 0f, 0f, 0f, 2f, 0f, 0f }, 0.22f,
			new[] { 0.75f, 0f, 0f }, new[] { 1f, 0f, 0f }, 0.1f
		};
		int[] unfoldedSelection = InvokeClassifier(classify, bindPositions, unfoldedPose, commonArguments);
		int[] foldedSelection = InvokeClassifier(classify, bindPositions, foldedAcrossTorsoPose, commonArguments);
		if (!Equal(unfoldedSelection, foldedSelection) || unfoldedSelection.Length != 4 ||
			unfoldedSelection[0] != 0 || unfoldedSelection[3] != 3)
		{
			Console.Error.WriteLine("FAIL: folding an arm across torso geometry changed bind-space anatomical triangle selection.");
			return false;
		}
		Console.WriteLine("PASS: folded corpse pose cannot change bind-space limb triangle selection");

		int[] debugCounts = (int[])classifyDebug.Invoke(null, new object[]
		{
			new[]
			{
				0f, -0.1f, 0f, 0f, 0.1f, 0f,
				1f, -0.1f, 0f, 1f, 0.1f, 0f,
				1.03f, 0f, 0f
			},
			new[] { 0, 2, 1, 1, 2, 3, 2, 4, 3 },
			new[] { 1f, 1f, 0f, 0f, 0f },
			new[] { 0f, 0f, 0f, 0f, 0f },
			new[] { 0f, 0f, 0f, 2f, 0f, 0f }, 0.25f,
			new[] { 0.5f, 0f, 0f }, new[] { 1f, 0f, 0f }, 0.1f
		});
		if (debugCounts == null || debugCounts.Length != 3 ||
			debugCounts[0] != 3 || debugCounts[1] != 2 || debugCounts[2] != 0)
		{
			Console.Error.WriteLine("FAIL: terminal zero-weight geometry escaped anatomical classification. " +
				$"Actual={string.Join(",", debugCounts ?? Array.Empty<int>())}");
			return false;
		}
		Console.WriteLine("PASS: terminal zero-weight torso geometry is excluded from anatomical selection");

		int[] bridgeCounts = (int[])classifyDebug.Invoke(null, new object[]
		{
			new[]
			{
				0f, -0.1f, 0f, 0.8f, -0.1f, 0f, 0.8f, 0.1f, 0f,
				1.2f, 0.1f, 0f, 2f, -0.1f, 0f
			},
			new[] { 0, 1, 2, 1, 3, 2, 1, 4, 3 },
			new[] { 1f, 0f, 0f, 0f, 1f },
			new[] { 0f, 0f, 0f, 0f, 1f },
			new[] { 0f, 0f, 0f, 2f, 0f, 0f }, 0.25f,
			new[] { 0.5f, 0f, 0f }, new[] { 1f, 0f, 0f }, 0.1f
		});
		if (bridgeCounts == null || bridgeCounts.Length != 3 ||
			bridgeCounts[0] != 3 || bridgeCounts[1] != 2 || bridgeCounts[2] != 1)
		{
			Console.Error.WriteLine("FAIL: a bounded zero-weight seam between limb-weighted regions was not preserved. " +
				$"Actual={string.Join(",", bridgeCounts ?? Array.Empty<int>())}");
			return false;
		}
		Console.WriteLine("PASS: bounded connectivity bridges garment seams without admitting terminal torso branches");

		float[] square = { -1f, -1f, 0f, 1f, -1f, 0f, 1f, 1f, 0f, -1f, 1f, 0f };
		int[][] closed = (int[][])orderLoops.Invoke(null, new object[]
		{
			new[] { 0, 1, 1, 2, 2, 3, 3, 0 }, square, new[] { 0f, 0f, 1f }, 10f
		});
		float[] duplicatedSeam =
		{
			-1f, -1f, 0f, 1f, -1f, 0f,
			1f, -1f, 0f, 1f, 1f, 0f,
			1f, 1f, 0f, -1f, 1f, 0f,
			-1f, 1f, 0f, -1f, -1f, 0f
		};
		int[][] seamClosed = (int[][])orderLoops.Invoke(null, new object[]
		{
			new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, duplicatedSeam, new[] { 0f, 0f, 1f }, 10f
		});
		float[] squareWithSliver =
		{
			-1f, -1f, 0f, 1f, -1f, 0f, 1f, 1f, 0f, -1f, 1f, 0f,
			-0.99999f, -1f, 0f
		};
		int[][] sliverClosed = (int[][])orderLoops.Invoke(null, new object[]
		{
			new[] { 0, 1, 1, 2, 2, 3, 3, 0, 0, 4 }, squareWithSliver, new[] { 0f, 0f, 1f }, 10f
		});
		int[][] repairableOpen = (int[][])orderLoops.Invoke(null, new object[]
		{
			new[] { 0, 1, 1, 2, 2, 3 }, square, new[] { 0f, 0f, 1f }, 3f
		});
		float[] twoOpenSquares =
		{
			-1f, -1f, 0f, 1f, -1f, 0f, 1f, 1f, 0f, -1f, 1f, 0f,
			2f, -1f, 0f, 4f, -1f, 0f, 4f, 1f, 0f, 2f, 1f, 0f
		};
		int[][] multipleOpen = (int[][])orderLoops.Invoke(null, new object[]
		{
			new[] { 0, 1, 1, 2, 2, 3, 4, 5, 5, 6, 6, 7 }, twoOpenSquares, new[] { 0f, 0f, 1f }, 3f
		});
		object oversizedOpen = orderLoops.Invoke(null, new object[]
		{
			new[] { 0, 1, 1, 2, 2, 3 }, square, new[] { 0f, 0f, 1f }, 1f
		});
		object branching = orderLoops.Invoke(null, new object[]
		{
			new[] { 0, 1, 1, 2, 2, 3, 3, 0, 0, 2 }, square, new[] { 0f, 0f, 1f }, 10f
		});
		if (closed == null || closed.Length != 1 || closed[0].Length != 4 ||
			seamClosed == null || seamClosed.Length != 1 || seamClosed[0].Length != 4 ||
			sliverClosed == null || sliverClosed.Length != 1 || sliverClosed[0].Length != 4 ||
			repairableOpen == null || repairableOpen.Length != 1 || repairableOpen[0].Length != 4 ||
			multipleOpen == null || multipleOpen.Length != 2 ||
			multipleOpen[0].Length != 4 || multipleOpen[1].Length != 4 ||
			oversizedOpen != null || branching != null)
		{
			Console.Error.WriteLine("FAIL: procedural cut boundary validation or constrained open-shell closure regressed.");
			return false;
		}
		Console.WriteLine("PASS: closed seams and simple finite-capsule garment openings become validated closed loops");

		float[] exact = (float[])copyOuterLoop.Invoke(null, new object[] { square });
		if (exact.Length != square.Length)
		{
			Console.Error.WriteLine("FAIL: bloody socket outer seam vertex count differs from the clipped boundary.");
			return false;
		}
		for (int index = 0; index < exact.Length; index++)
		{
			if (BitConverter.SingleToInt32Bits(exact[index]) == BitConverter.SingleToInt32Bits(square[index])) continue;
			Console.Error.WriteLine("FAIL: bloody socket outer seam does not bit-match the clipped boundary.");
			return false;
		}
		Console.WriteLine("PASS: bloody socket outer seam exactly matches its supplied post-cut boundary");

		const int rimVertices = 32;
		float[] rimInput = new float[rimVertices * 3];
		for (int vertex = 0; vertex < rimVertices; vertex++)
		{
			float angle = MathF.PI * 2f * vertex / rimVertices;
			rimInput[vertex * 3] = MathF.Cos(angle) * 0.052f;
			rimInput[vertex * 3 + 1] = MathF.Sin(angle) * 0.043f;
		}
		float[] tornBody = (float[])disturbOuterRim.Invoke(null,
			new object[] { rimInput, new[] { 0f, 0f, 1f }, false, 0x12345678u, false });
		float[] tornBodyRepeat = (float[])disturbOuterRim.Invoke(null,
			new object[] { rimInput, new[] { 0f, 0f, 1f }, false, 0x12345678u, false });
		float[] tornGib = (float[])disturbOuterRim.Invoke(null,
			new object[] { rimInput, new[] { 0f, 0f, 1f }, false, 0x12345678u, true });
		bool hasProximal = false;
		bool hasDistal = false;
		float maximumAdjacentStep = 0f;
		for (int vertex = 0; vertex < rimVertices; vertex++)
		{
			if (BitConverter.SingleToInt32Bits(tornBody[vertex * 3]) != BitConverter.SingleToInt32Bits(rimInput[vertex * 3]) ||
				BitConverter.SingleToInt32Bits(tornBody[vertex * 3 + 1]) != BitConverter.SingleToInt32Bits(rimInput[vertex * 3 + 1]) ||
				Math.Abs(tornBody[vertex * 3 + 2]) > 0.01801f)
			{
				Console.Error.WriteLine("FAIL: post-cut rim disturbance changed the cross-section or exceeded its arm bound.");
				return false;
			}
			hasProximal |= tornBody[vertex * 3 + 2] < -0.0001f;
			hasDistal |= tornBody[vertex * 3 + 2] > 0.0001f;
			int next = (vertex + 1) % rimVertices;
			maximumAdjacentStep = Math.Max(maximumAdjacentStep,
				Math.Abs(tornBody[vertex * 3 + 2] - tornBody[next * 3 + 2]));
		}
		if (!hasProximal || !hasDistal || maximumAdjacentStep > 0.0135f ||
			!Equal(tornBody, tornBodyRepeat) || Equal(tornBody, tornGib))
		{
			Console.Error.WriteLine("FAIL: rim curve is not deterministic, bidirectional, or independently seeded per side.");
			return false;
		}
		Console.WriteLine("PASS: validated cut rims receive deterministic smooth axial variation on both sides of the plane");

		Type wound = assembly.GetType(ProceduralLimbWoundTypeName, throwOnError: false);
		MethodInfo seamOffsets = wound?.GetMethod("SeamOffsetsForTests", BindingFlags.Static | BindingFlags.NonPublic);
		MethodInfo generateMetrics = wound?.GetMethod("GenerateMetricsForTests", BindingFlags.Static | BindingFlags.NonPublic);
		MethodInfo boneRoots = wound?.GetMethod("BoneRootsForTests", BindingFlags.Static | BindingFlags.NonPublic);
		MethodInfo boneDiameterBaseline = wound?.GetMethod("BoneDiameterBaselineForTests", BindingFlags.Static | BindingFlags.NonPublic);
		MethodInfo generationFailure = wound?.GetMethod("GenerationFailureForTests", BindingFlags.Static | BindingFlags.NonPublic);
		Type severLevel = assembly.GetType("Rupture.Dismemberment.Classes.Limbs.LimbSeverLevel", throwOnError: false);
		if (wound == null || seamOffsets == null || generateMetrics == null || boneRoots == null ||
			boneDiameterBaseline == null || generationFailure == null || severLevel == null)
		{
			Console.Error.WriteLine("FAIL: procedural fleshy limb-wound regression hooks are unavailable.");
			return false;
		}
		float[] offsets = (float[])seamOffsets.Invoke(null, Array.Empty<object>());
		if (offsets == null || offsets.Length != 2 || Math.Abs(offsets[0]) > 0.000001f || Math.Abs(offsets[1] - 0.10f) > 0.000001f)
		{
			Console.Error.WriteLine("FAIL: body/gib seams do not preserve the body cut and 10 cm distal gap.");
			return false;
		}
		object upper = Enum.ToObject(severLevel, 1);
		object lower = Enum.ToObject(severLevel, 2);
		float[] upperBody = (float[])generateMetrics.Invoke(null, new object[] { false, upper, 0x12345678u, false, true });
		float[] upperGib = (float[])generateMetrics.Invoke(null, new object[] { false, upper, 0x12345678u, true, true });
		float[] lowerArm = (float[])generateMetrics.Invoke(null, new object[] { false, lower, 0x2468ACE0u, false, true });
		float[] upperLeg = (float[])generateMetrics.Invoke(null, new object[] { true, upper, 0x13579BDFu, false, true });
		float[] lowerLeg = (float[])generateMetrics.Invoke(null, new object[] { true, lower, 0x87654321u, false, true });
		float[] secondary = (float[])generateMetrics.Invoke(null, new object[] { true, upper, 0x12345678u, false, false });
		if (!ValidWoundMetrics(upperBody, 1, 6) || !ValidWoundMetrics(upperGib, 1, 6) ||
			!ValidWoundMetrics(lowerArm, 2, 6) || !ValidWoundMetrics(upperLeg, 1, 8) ||
			!ValidWoundMetrics(lowerLeg, 2, 8) || secondary == null || secondary.Length < 21 ||
			secondary[2] != 0f || secondary[4] != 0f || secondary[5] != 0f || secondary[9] != 0f)
		{
			Console.Error.WriteLine("FAIL: fleshy wound topology, anatomical count, primary-loop ownership, or decal bounds regressed. " +
				$"upperBody=[{string.Join(",", upperBody ?? Array.Empty<float>())}] " +
				$"upperGib=[{string.Join(",", upperGib ?? Array.Empty<float>())}] " +
				$"lowerArm=[{string.Join(",", lowerArm ?? Array.Empty<float>())}] " +
				$"upperLeg=[{string.Join(",", upperLeg ?? Array.Empty<float>())}] " +
				$"lowerLeg=[{string.Join(",", lowerLeg ?? Array.Empty<float>())}] " +
				$"secondary=[{string.Join(",", secondary ?? Array.Empty<float>())}] " +
				$"upperFailure={generationFailure.Invoke(null, new object[] { false, upper, 0x12345678u, false, true })} " +
				$"lowerFailure={generationFailure.Invoke(null, new object[] { true, lower, 0x87654321u, false, true })}");
			return false;
		}
		float[] humerus = (float[])boneDiameterBaseline.Invoke(null, new object[] { false, upper, 0 });
		float[] ulna = (float[])boneDiameterBaseline.Invoke(null, new object[] { false, lower, 0 });
		float[] radius = (float[])boneDiameterBaseline.Invoke(null, new object[] { false, lower, 1 });
		float[] femur = (float[])boneDiameterBaseline.Invoke(null, new object[] { true, upper, 0 });
		float[] tibia = (float[])boneDiameterBaseline.Invoke(null, new object[] { true, lower, 0 });
		float[] fibula = (float[])boneDiameterBaseline.Invoke(null, new object[] { true, lower, 1 });
		if (!NearPair(humerus, 0.020f, 0.018f) || !NearPair(ulna, 0.016f, 0.011f) ||
			!NearPair(radius, 0.015f, 0.011f) || !NearPair(femur, 0.029f, 0.026f) ||
			!NearPair(tibia, 0.027f, 0.020f) || !NearPair(fibula, 0.014f, 0.014f) ||
			!ValidScaledBoneDiameters(upperBody, humerus) ||
			!ValidScaledBoneDiameters(lowerArm, ulna, radius) ||
			!ValidScaledBoneDiameters(upperLeg, femur) ||
			!ValidScaledBoneDiameters(lowerLeg, tibia, fibula))
		{
			Console.Error.WriteLine("FAIL: generated anatomical bone diameters or their deterministic +/-15 percent scale bounds regressed.");
			return false;
		}
		if (Math.Abs(upperBody[6] - upperGib[6]) < 0.00001f)
		{
			Console.Error.WriteLine("FAIL: body and gib wound mounds reused the same deterministic shape branch.");
			return false;
		}
		float[] bodyRoots = (float[])boneRoots.Invoke(null, new object[] { 0xCAFEBABEu, 2 });
		float[] gibRoots = (float[])boneRoots.Invoke(null, new object[] { 0xCAFEBABEu, 2 });
		if (!Equal(bodyRoots, gibRoots) || bodyRoots.Length != 4 ||
			Math.Sqrt(bodyRoots[0] * bodyRoots[0] + bodyRoots[1] * bodyRoots[1]) >= 0.5 ||
			Math.Sqrt(bodyRoots[2] * bodyRoots[2] + bodyRoots[3] * bodyRoots[3]) >= 0.5)
		{
			Console.Error.WriteLine("FAIL: paired body/gib bone roots do not share bounded normalized cut-plane positions.");
			return false;
		}
		Console.WriteLine("PASS: 10 cm gap, protruding asymmetric caps, embedded adornments, six validated anatomical bone profiles, primary ownership, and decal rings");
		MethodInfo clothValidation = wound.GetMethod("ValidateClothingForTests", BindingFlags.Static | BindingFlags.NonPublic);
		if (clothValidation == null) { Console.Error.WriteLine("FAIL: cloth geometry checks missing"); return false; }
		string clothFailure = (string)clothValidation.Invoke(null, Array.Empty<object>());
		if (clothFailure != null) { Console.Error.WriteLine("FAIL: " + clothFailure); return false; }
		Console.WriteLine("PASS: calibrated cloth thresholds, unchanged thin cuts, mirrored folds, skeleton/renderer scaling, lip masks, skinning, layer ownership, and source-pinned bare exceptions");
		return true;
	}

	private static bool ValidWoundMetrics(float[] metrics, int expectedBones, int expectedDecals)
	{
		return metrics != null && metrics.Length >= 21 && metrics[0] > 0f && metrics[1] > 0f &&
			metrics[2] == expectedBones && metrics[3] > 0f && metrics[4] >= 3f && metrics[4] <= 7f &&
			metrics[5] >= 2f && metrics[5] <= 4f && metrics[6] > 0f && metrics[7] <= 0.04201f &&
			metrics[8] == 1f && metrics[9] == expectedDecals && metrics[10] == 18f && metrics[11] > 0.0005f &&
			metrics[12] > 0.001f && metrics[12] < metrics[6] && metrics[13] >= 0.00399f &&
			metrics[14] >= 0.00599f && metrics[15] >= 0.00599f && metrics[15] <= 0.03501f && metrics[16] > 0f;
	}

	private static bool NearPair(float[] actual, float first, float second)
	{
		return actual != null && actual.Length == 2 && Math.Abs(actual[0] - first) < 0.000001f &&
			Math.Abs(actual[1] - second) < 0.000001f;
	}

	private static bool ValidScaledBoneDiameters(float[] metrics, params float[][] baselines)
	{
		if (metrics == null || metrics.Length < 21 || baselines == null || baselines.Length < 1 || baselines.Length > 2)
			return false;
		for (int bone = 0; bone < baselines.Length; bone++)
		{
			for (int axis = 0; axis < 2; axis++)
			{
				float actual = metrics[17 + bone * 2 + axis];
				float baseline = baselines[bone][axis];
				if (actual < baseline * 0.8499f || actual > baseline * 1.1501f) return false;
			}
		}
		return baselines.Length == 2 || (metrics[19] == 0f && metrics[20] == 0f);
	}

	private static int[] InvokeClassifier(MethodInfo classify, float[] bindPositions, float[] posedPositions,
		object[] commonArguments)
	{
		return (int[])classify.Invoke(null, new object[]
		{
			bindPositions, posedPositions, commonArguments[0], commonArguments[1], commonArguments[2],
			commonArguments[3], commonArguments[4], commonArguments[5], commonArguments[6], commonArguments[7]
		});
	}

	private static bool Equal(int[] left, int[] right)
	{
		if (left == null || right == null || left.Length != right.Length) return false;
		for (int index = 0; index < left.Length; index++) if (left[index] != right[index]) return false;
		return true;
	}

	private static bool Equal(float[] left, float[] right)
	{
		if (left == null || right == null || left.Length != right.Length) return false;
		for (int index = 0; index < left.Length; index++)
			if (BitConverter.SingleToInt32Bits(left[index]) != BitConverter.SingleToInt32Bits(right[index])) return false;
		return true;
	}

    private static bool ConsumeHeadlessStep(MethodInfo method, float frameDeltaTime, ref float accumulator, out float simulationDeltaTime)
    {
        object[] arguments = { frameDeltaTime, accumulator, 0f };
        bool consumed = (bool)method.Invoke(null, arguments);
        accumulator = (float)arguments[1];
        simulationDeltaTime = (float)arguments[2];
        return consumed;
    }

    private static bool TryCalculateHeadWound(MethodInfo method, float massGrams, float velocity, float diameter,
        float penetration, string caliber, out HeadWoundResult result)
    {
        object[] arguments = { massGrams, velocity, diameter, penetration, caliber, null };
        bool calculated = (bool)method.Invoke(null, arguments);
        result = default;
        if (!calculated)
        {
            return false;
        }
        object shape = arguments[5];
        Type shapeType = shape.GetType();
        result = new HeadWoundResult(
            (float)shapeType.GetField("Momentum", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shape),
            (float)shapeType.GetField("Energy", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shape),
            (float)shapeType.GetField("EntryRadius", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shape),
            (float)shapeType.GetField("ExitRadius", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shape));
        return true;
    }

    private readonly struct HeadWoundResult
    {
        public HeadWoundResult(float momentum, float energy, float entryRadius, float exitRadius)
        {
            Momentum = momentum;
            Energy = energy;
            EntryRadius = entryRadius;
            ExitRadius = exitRadius;
        }

        public float Momentum { get; }
        public float Energy { get; }
        public float EntryRadius { get; }
        public float ExitRadius { get; }
    }

    private sealed class TestCase
    {
        public TestCase(string name, string sourceId, bool expected)
        {
            Name = name;
            SourceId = sourceId;
            Expected = expected;
        }

        public string Name { get; }

        public string SourceId { get; }

        public bool Expected { get; }
    }

    private sealed class ImpulseCase
    {
        public ImpulseCase(string name, float massGram, float velocityMetersPerSecond, float multiplier, float expected)
        {
            Name = name;
            MassGram = massGram;
            VelocityMetersPerSecond = velocityMetersPerSecond;
            Multiplier = multiplier;
            Expected = expected;
        }

        public string Name { get; }

        public float MassGram { get; }

        public float VelocityMetersPerSecond { get; }

        public float Multiplier { get; }

        public float Expected { get; }
    }

    private sealed class SleepCase
    {
        public SleepCase(string name, float nativeTimePass, int totalDuration, bool expected)
        {
            Name = name;
            NativeTimePass = nativeTimePass;
            TotalDuration = totalDuration;
            Expected = expected;
        }

        public string Name { get; }

        public float NativeTimePass { get; }

        public int TotalDuration { get; }

        public bool Expected { get; }
    }

    private sealed class RelationCase
    {
        public RelationCase(string name, uint current, uint incoming, string expected)
        {
            Name = name;
            Current = current;
            Incoming = incoming;
            Expected = expected;
        }

        public string Name { get; }

        public uint Current { get; }

        public uint Incoming { get; }

        public string Expected { get; }
    }
}
