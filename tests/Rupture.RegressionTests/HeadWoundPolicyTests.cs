using System;
using System.Collections.Generic;
using System.Reflection;

internal static class HeadWoundPolicyTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }

    internal static void Run(Assembly assembly)
    {
        Type type = assembly.GetType("Rupture.Dismemberment.Classes.HeadWounds.HeadWoundPolicy", true);
        object state = Activator.CreateInstance(type, true);
        var remember = type.GetMethod("Remember", Flags); var reserve = type.GetMethod("Reserve", Flags);
        bool Remember(ulong id) => (bool)remember.Invoke(state, new object[] { id });
        int Reserve(bool enabled = true) => (int)reserve.Invoke(state, new object[] { enabled });
        Check(!Remember(0) && Remember(17) && !Remember(17), "lethal/corpse callbacks share projectile deduplication");
        for (int attempt = 1; attempt <= 16; attempt++)
            Check(Reserve() == attempt, "headless publishes ordered attempts independently of local geometry: " + attempt);
        Check(Remember(18) && Reserve() == 0, "attempt budget remains bounded and does not block headgear eligibility");
        state = Activator.CreateInstance(type, true);
        Check(Remember(17) && Reserve(false) == 0 && Reserve() == 1,
            "corpse generations reset admission; disabled geometry does not reserve a slot");

        Type orderType = assembly.GetType("Rupture.Dismemberment.Classes.HeadWounds.HeadWoundOrder", true);
        object order = Activator.CreateInstance(orderType, true);
        bool Queue(int ordinal) => (bool)orderType.GetMethod("TryQueue", Flags).Invoke(order, new object[] { ordinal });
        int Next() => (int)orderType.GetProperty("Next", Flags).GetValue(order);
        void Complete(int ordinal) => orderType.GetMethod("Complete", Flags).Invoke(order, new object[] { ordinal });
		void Discard(int ordinal) => orderType.GetMethod("Discard", Flags).Invoke(order, new object[] { ordinal });
        Check(Queue(4) && Queue(2) && !Queue(4) && Next() == 1 && !Queue(17) && !Queue(0),
            "late corpse creation preserves bounded out-of-order wound packets without admitting duplicates");
        Complete(2);
        Check(Next() == 1 && Queue(1), "later wound work cannot overtake the pending lethal ordinal");
        Complete(1); Complete(2);
        Check(Next() == 3 && !Queue(1) && Queue(3), "applied and rejected ordinals advance once and cannot replay");
        Complete(3); Complete(4);
        Check(Next() == 5, "ordered receive window drains all four rapid impacts");
        for (int attempt = 5; attempt <= 16; attempt++)
        { Check(Queue(attempt), "failed early geometry does not exhaust receive window: " + attempt); Complete(attempt); }
        Check(Next() == 17 && !Queue(16), "sixteen-attempt window drains without reusing an event ordinal");
        order = Activator.CreateInstance(orderType, true);
        Check(Queue(1) && Next() == 1, "new corpse lifecycle has a fresh receive window");
		Check(Queue(2) && Queue(3), "later wounds can wait behind an unresolved lethal wound");
		Discard(2);
		Check(Next() == 1, "discarding a later wound cannot skip its unresolved predecessor");
		Complete(1);
		Check(Next() == 3 && !Queue(2), "finishing the predecessor skips an already discarded ordinal without replay");
		Discard(3);
		Check(Next() == 4, "failed current geometry releases the next wound ordinal");
		order = Activator.CreateInstance(orderType, true);
		Queue(3); Discard(3); Queue(1); Complete(1);
		Check(Next() == 2, "discarding a later wound never skips a predecessor that has not arrived");

        Type sequenceType = assembly.GetType("Rupture.Ragdolls.Classes.HeadgearThrowSequence`1", true).MakeGenericType(typeof(string));
        object sequence = Activator.CreateInstance(sequenceType, true);
        var items = (List<string>)sequenceType.GetField("Items", Flags).GetValue(sequence);
        items.AddRange(new[] { "helmet", "earpiece", "mask", "glasses" });
        var equipped = new HashSet<string> { "helmet", "mask", "glasses" };
        var throws = new List<string>(); var timeouts = new List<string>();
        Func<string, bool> isEquipped = equipped.Contains;
        var completed = new HashSet<string>();
        var failed = new HashSet<string>();
        Func<string, bool> isCompleted = completed.Contains, isFailed = failed.Contains;
        Action<string> start = throws.Add, timeout = timeouts.Add;
        var advance = sequenceType.GetMethod("Advance", Flags);
        bool Advance(float now) => (bool)advance.Invoke(sequence, new object[] { now, isEquipped, start, timeout, isCompleted, isFailed });
        Check(!Advance(0) && !Advance(0.01f) && throws.Count == 1 && throws[0] == "helmet",
            "rapid updates cannot start overlapping inventory throws");
        equipped.Remove("helmet"); equipped.Remove("glasses");
        Check(!Advance(0.1f) && throws.Count == 1, "slot removal alone cannot advance an unfinished throw");
        completed.Add("helmet");
        Check(!Advance(0.2f) && throws.Count == 2 && throws[1] == "mask",
            "completed removal advances past missing equipment and rechecks remaining slots");
        equipped.Remove("mask");
        completed.Add("mask");
        Check(Advance(0.3f) && throws.Count == 2 && timeouts.Count == 0, "already removed items are never thrown again");
        sequence = Activator.CreateInstance(sequenceType, true);
        ((List<string>)sequenceType.GetField("Items", Flags).GetValue(sequence)).AddRange(new[] { "helmet", "mask" });
        equipped.Add("helmet"); equipped.Add("mask"); throws.Clear();
        Check(!Advance(1) && Advance(2) && throws.Count == 1 && timeouts.Count == 1,
            "stalled EFT inventory removal terminates the sequence without throwing the next item");
        sequence = Activator.CreateInstance(sequenceType, true);
        ((List<string>)sequenceType.GetField("Items", Flags).GetValue(sequence)).AddRange(new[] { "helmet", "mask" });
        throws.Clear(); failed.Add("helmet");
        Check(!Advance(3) && Advance(3.1f) && throws.Count == 1, "failed operation stops subsequent throws without automatic retry");
    }
}
