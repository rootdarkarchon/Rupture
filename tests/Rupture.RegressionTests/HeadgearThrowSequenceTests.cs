using System;
using System.Collections.Generic;
using Rupture.Ragdolls.Classes;

// HeadgearThrowSequence.cs is compiled into this assembly: its callbacks are
// virtual members, which a reflection-only test could not implement.
internal static class HeadgearThrowSequenceTests
{
    private sealed class Harness : HeadgearThrowSequence<string>
    {
        internal readonly HashSet<string> Equipped = new HashSet<string>();
        internal readonly HashSet<string> Completed = new HashSet<string>();
        internal readonly HashSet<string> Failed = new HashSet<string>();
        internal readonly List<string> Throws = new List<string>();
        internal readonly List<string> Timeouts = new List<string>();

        internal Harness(params string[] items)
        {
            Items.AddRange(items);
            foreach (string item in items) Equipped.Add(item);
        }

        protected override bool ItemEquipped(string item) => Equipped.Contains(item);
        protected override void BeginThrow(string item) => Throws.Add(item);
        protected override void ThrowTimedOut(string item) => Timeouts.Add(item);
        protected override bool ThrowCompleted(string item) => Completed.Contains(item);
        protected override bool ThrowFailed(string item) => Failed.Contains(item);

        internal bool Step(float now, bool allowStart = true) => Advance(now, allowStart, out _);
        internal bool Started(float now, bool allowStart = true) { Advance(now, allowStart, out bool started); return started; }
        internal bool Overdue(float now) => StartOverdue(now);
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }

    internal static void Run()
    {
        var sequence = new Harness("helmet", "earpiece", "mask", "glasses");
        sequence.Equipped.Remove("earpiece");
        Check(!sequence.Step(0) && !sequence.Step(0.01f) && sequence.Throws.Count == 1 && sequence.Throws[0] == "helmet",
            "rapid updates cannot start overlapping inventory throws");
        sequence.Equipped.Remove("helmet"); sequence.Equipped.Remove("glasses");
        Check(!sequence.Step(0.1f) && sequence.Throws.Count == 1, "slot removal alone cannot advance an unfinished throw");
        sequence.Completed.Add("helmet");
        Check(!sequence.Step(0.2f) && sequence.Throws.Count == 2 && sequence.Throws[1] == "mask",
            "completed removal advances past missing equipment and rechecks remaining slots");
        sequence.Equipped.Remove("mask");
        sequence.Completed.Add("mask");
        Check(sequence.Step(0.3f) && sequence.Throws.Count == 2 && sequence.Timeouts.Count == 0, "already removed items are never thrown again");

        sequence = new Harness("helmet", "mask");
        Check(!sequence.Step(1) && sequence.Step(2) && sequence.Throws.Count == 1 && sequence.Timeouts.Count == 1,
            "stalled EFT inventory removal terminates the sequence without throwing the next item");

        sequence = new Harness("helmet", "mask");
        sequence.Failed.Add("helmet");
        Check(!sequence.Step(3) && sequence.Step(3.1f) && sequence.Throws.Count == 1, "failed operation stops subsequent throws without automatic retry");

        sequence = new Harness("helmet", "mask");
        Check(!sequence.Step(0, allowStart: false) && sequence.Throws.Count == 0 && sequence.WaitingItem == null,
            "a frame without room starts no throw and arms no deadline");
        Check(sequence.Started(5) && sequence.Throws.Count == 1 && sequence.Throws[0] == "helmet",
            "the throw starts in the first frame that has room");
        Check(!sequence.Step(5.9f) && sequence.Timeouts.Count == 0 && sequence.Step(6.1f) && sequence.Timeouts.Count == 1,
            "the one second deadline counts from the throw start, not from queuing");

        sequence = new Harness("helmet", "mask");
        sequence.Started(0);
        sequence.Completed.Add("helmet"); sequence.Equipped.Remove("helmet");
        Check(!sequence.Started(0.1f, allowStart: false) && sequence.Throws.Count == 1 && sequence.WaitingItem == null,
            "completion is still observed in a frame that cannot start the next throw");
        Check(sequence.Started(0.2f) && sequence.Throws.Count == 2 && sequence.Throws[1] == "mask",
            "the next item starts once the frame has room");

        sequence = new Harness("helmet");
        sequence.Equipped.Clear();
        Check(sequence.Step(0, allowStart: false) == false && sequence.Step(0.1f) && sequence.Throws.Count == 0,
            "items that are no longer equipped finish the sequence without a throw");

        sequence = new Harness("helmet", "mask");
        Check(!sequence.Overdue(0), "a sequence that was never polled has no start wait yet");
        sequence.Step(0, allowStart: false);
        Check(!sequence.Overdue(0.49f) && sequence.Overdue(0.5f),
            "a throw that cannot get a frame becomes overdue after half a second of realtime");
        Check(sequence.Started(0.5f) && sequence.Throws.Count == 1 && sequence.Throws[0] == "helmet",
            "an overdue start is honoured by the caller and begins the oldest item");
        Check(!sequence.Overdue(10f), "a throw in flight is never overdue");
        sequence.Completed.Add("helmet"); sequence.Equipped.Remove("helmet");
        sequence.Step(1f, allowStart: false);
        Check(sequence.Throws.Count == 1 && !sequence.Overdue(1.49f) && sequence.Overdue(1.5f),
            "the wait clock restarts when the previous throw completes");
    }
}
