using System.Threading;

namespace Rupture.PhysXBackend;

// Command versions are captured at submission, not completion: a command queued
// behind an in-flight step must still cause the next simulation to run.
internal sealed class SimulationActivity
{
    private long _version, _completed;
    private bool _awake;
    internal long Version => Interlocked.Read(ref _version);
    internal bool NeedsStep => _awake || Version != _completed;
    internal void Changed() => Interlocked.Increment(ref _version);
    internal void Complete(long version, bool awake) { _completed = version; _awake = awake; }
    internal void Reset() { _version = _completed = 0; _awake = false; }
}
