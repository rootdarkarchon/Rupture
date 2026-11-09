using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

internal static class CorpseHandoffAbiTests
{
	private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
	internal static void Run(Assembly core)
	{
		void Check(bool condition, string label)
		{
			if (!condition) throw new Exception("Corpse handoff ABI: " + label);
			Console.WriteLine("PASS: corpse handoff " + label);
		}

		// Public surface: the consumer contract in docs/CORPSE-HANDOFF-API.md.
		Type facade = core.GetType("Rupture.Integration.CorpseHandoffV1", true);
		Type result = core.GetType("Rupture.Integration.HandoffResult", true);
		Type state = core.GetType("Rupture.Integration.HandoffState", true);
		Type info = core.GetType("Rupture.Integration.CorpseHandoffInfo", true);
		Type release = core.GetType("Rupture.Integration.HandoffRelease", true);
		Check(facade.IsPublic && facade.IsAbstract && facade.IsSealed, "facade is a public static class");
		Check((int)facade.GetProperty("AbiVersion").GetValue(null) == 1, "public ABI version is 1");
		string[] methods = facade.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
			.Where(m => m.Name != "get_AbiVersion").Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
		Check(methods.SequenceEqual(new[] { "Adopt", "Inspect", "Release", "Renew" }), "facade exposes exactly Inspect, Release, Renew and Adopt");
		Check(facade.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).Length == 0 &&
			facade.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).Select(p => p.Name).SequenceEqual(new[] { "AbiVersion" }),
			"facade exposes no public state besides the ABI version");

		MethodInfo Entry(string name) => facade.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
		bool Shape(MethodInfo method, params (Type Type, bool Out)[] parameters)
		{
			ParameterInfo[] actual = method.GetParameters();
			if (method.ReturnType != result || actual.Length != parameters.Length) return false;
			for (int i = 0; i < actual.Length; i++)
			{
				Type expected = parameters[i].Type;
				if (parameters[i].Out)
				{
					if (!actual[i].ParameterType.IsByRef || !actual[i].IsOut || actual[i].ParameterType.GetElementType() != expected) return false;
				}
				else if (actual[i].ParameterType != expected) return false;
			}
			return true;
		}
		Type corpse = Entry("Inspect").GetParameters()[0].ParameterType;
		Check(corpse.FullName == "EFT.Interactive.Corpse", "entry points take the EFT corpse");
		Check(Shape(Entry("Inspect"), (corpse, false), (info, true)), "Inspect(Corpse, out CorpseHandoffInfo) returns HandoffResult");
		Check(Shape(Entry("Release"), (corpse, false), (release, false), (typeof(ulong), true)), "Release(Corpse, HandoffRelease, out ulong) returns HandoffResult");
		Check(Shape(Entry("Renew"), (typeof(ulong), false)), "opaque Renew token is ulong");
		Check(Shape(Entry("Adopt"), (typeof(ulong), false)), "opaque Adopt token is ulong");

		string Values(Type enumType) => string.Join(",", Enum.GetValues(enumType).Cast<object>()
			.Select(v => Convert.ToInt32(v) + "=" + v).OrderBy(s => int.Parse(s.Substring(0, s.IndexOf('=')))).ToArray());
		Check(result.IsPublic && result.IsEnum &&
			Values(result) == "0=Ok,1=Pending,2=NotManaged,3=Busy,4=Unavailable,5=InvalidArgument,6=InvalidToken,7=Failed,8=Stale", "HandoffResult values are frozen");
		Check(state.IsPublic && state.IsEnum &&
			Values(state) == "0=Unmanaged,1=Preparing,2=Managed,3=Released,4=Unavailable", "HandoffState values are frozen");

		bool Fields(Type dto, params (string Name, Type Type)[] expected)
		{
			FieldInfo[] actual = dto.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
			return actual.Length == expected.Length && expected.All(e => actual.Any(f => f.Name == e.Name && f.FieldType == e.Type && f.IsPublic && !f.IsStatic));
		}
		Check(info.IsPublic && info.IsClass && info.IsSealed && Fields(info,
			("State", state), ("Simulating", typeof(bool)), ("ProfileId", typeof(string)), ("DeathSequence", typeof(uint)),
			("OwnershipEpoch", typeof(uint)), ("SeveredRoots", typeof(Transform[])), ("WeaponIndependent", typeof(bool))), "CorpseHandoffInfo fields");
		Check(release.IsPublic && release.IsClass && release.IsSealed && Fields(release, ("LeaseTimeout", typeof(float))), "HandoffRelease carries only the lease timeout");

		// Shared pure policy.
		Type policy = core.GetType("Rupture.Integration.CorpseHandoffPolicy", true);
		Type decision = core.GetType("Rupture.Integration.OwnershipEpochDecision", true);
		Type clockType = core.GetType("Rupture.Integration.HandoffLeaseClock", true);
		Check(Values(decision) == "0=Stale,1=Current,2=NewerRetain,3=NewerAdopt", "epoch decision values");

		object Constant(string name) => policy.GetField(name, Flags).GetRawConstantValue();
		Check((float)Constant("MinimumLeaseTimeout") == 1f && (float)Constant("MaximumLeaseTimeout") == 120f &&
			(int)Constant("ReleaseBoundaryFrames") == 2 && (float)Constant("ClientMotionSilenceSeconds") == 2f &&
			(float)Constant("TerminalRecordSeconds") == 30f, "policy constants match the spec");

		MethodInfo validTimeout = policy.GetMethod("IsValidLeaseTimeout", Flags);
		bool Valid(float seconds) => (bool)validTimeout.Invoke(null, new object[] { seconds });
		Check(Valid(1f) && Valid(30f) && Valid(120f), "lease timeout bounds are inclusive");
		Check(!Valid(.999f) && !Valid(120.001f) && !Valid(0f) && !Valid(-5f), "lease timeout outside [1, 120] is rejected");
		Check(!Valid(float.NaN) && !Valid(float.PositiveInfinity) && !Valid(float.NegativeInfinity), "non-finite lease timeout is rejected");

		MethodInfo classify = policy.GetMethod("ClassifyEpoch", Flags);
		string Epoch(uint local, uint incoming, bool released) => classify.Invoke(null, new object[] { local, incoming, released }).ToString();
		foreach (bool released in new[] { false, true })
		{
			string label = released ? " while released" : " while managed";
			Check(Epoch(5, 4, released) == "Stale" && Epoch(5, 0, released) == "Stale", "lower epoch is stale" + label);
			Check(Epoch(5, 5, released) == "Current" && Epoch(0, 0, released) == "Current", "equal epoch is current" + label);
		}
		Check(Epoch(5, 6, true) == "NewerRetain" && Epoch(0, 1, true) == "NewerRetain", "newer epoch is retained, never adopted, while released");
		Check(Epoch(5, 6, false) == "NewerAdopt" && Epoch(0, 1, false) == "NewerAdopt", "newer epoch is adopted while managed");
		Check(Epoch(uint.MaxValue, uint.MaxValue, true) == "Current" && Epoch(uint.MaxValue, uint.MaxValue, false) == "Current", "maximum epoch equals itself");
		Check(Epoch(uint.MaxValue, uint.MaxValue - 1, false) == "Stale" && Epoch(uint.MaxValue - 1, uint.MaxValue, true) == "NewerRetain" &&
			Epoch(uint.MaxValue - 1, uint.MaxValue, false) == "NewerAdopt" && Epoch(0, uint.MaxValue, false) == "NewerAdopt", "maximum epoch compares without wrapping");

		// Hitch-safe lease clock.
		object Clock(float now, float timeout) => Activator.CreateInstance(clockType, Flags, null, new object[] { now, timeout }, null);
		bool Observe(object clock, float now, int frame) => (bool)clockType.GetMethod("ObserveExpired", Flags).Invoke(clock, new object[] { now, frame });
		void Renew(object clock, float now) => clockType.GetMethod("Renew", Flags).Invoke(clock, new object[] { now });
		float Timeout(object clock) => (float)clockType.GetProperty("Timeout", Flags).GetValue(clock);
		float Deadline(object clock) => (float)clockType.GetProperty("Deadline", Flags).GetValue(clock);

		object lease = Clock(10f, 30f);
		Check(Timeout(lease) == 30f && Deadline(lease) == 40f, "deadline is now plus timeout");
		Renew(lease, 25f);
		Check(Timeout(lease) == 30f && Deadline(lease) == 55f, "renew moves the deadline and keeps the timeout");

		lease = Clock(10f, 30f);
		Check(!Observe(lease, 20f, 100) && !Observe(lease, 39.9f, 101), "a live lease never expires before its deadline");
		Check(!Observe(lease, 40f, 102), "first past-due boundary only records the observation");
		Check(!Observe(lease, 41f, 102), "second past-due boundary in the same frame does not expire");
		Check(Observe(lease, 41f, 103), "a later frame at a second past-due boundary expires");
		Check(Observe(lease, 50f, 104), "expiry stays true once reached");

		lease = Clock(10f, 30f);
		Check(!Observe(lease, 45f, 200) && !Observe(lease, 46f, 199), "an earlier frame number cannot confirm expiry");
		Check(Observe(lease, 47f, 201), "a strictly later frame confirms expiry");

		lease = Clock(0f, 30f);
		Check(!Observe(lease, 100f, 10), "a main-thread hitch past the deadline does not expire on its first boundary");
		Renew(lease, 100f);
		Check(Deadline(lease) == 130f && !Observe(lease, 101f, 11), "renew after the hitch keeps the lease");
		Check(!Observe(lease, 131f, 12) && !Observe(lease, 132f, 12), "after renew a first past-due boundary and a same-frame repeat do not expire");
		Check(Observe(lease, 133f, 13), "after renew two new past-due boundaries across frames expire");

		lease = Clock(0f, 30f);
		Check(!Observe(lease, 100f, 10), "hitch observation recorded");
		Renew(lease, 100f);
		Check(!Observe(lease, 131f, 20) && Observe(lease, 132f, 21), "renew discards the earlier past-due observation");
	}
}
