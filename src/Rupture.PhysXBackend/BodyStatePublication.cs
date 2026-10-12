using System.Collections.Generic;

namespace Rupture.PhysXBackend;

// Owner-thread only. Compact readback in place so the main thread sees active
// bodies and changes to sleeping bodies, including their final sleeping pose.
internal sealed class BodyStatePublication
{
    private readonly Dictionary<ulong, NativeApi.BodyState> _sleeping = new Dictionary<ulong, NativeApi.BodyState>();
    internal int Filter(NativeApi.BodyState[] states, out bool awake)
    {
        int count = 0; awake = false;
        for (int i = 0; i < states.Length; i++)
        {
            var state = states[i];
            if (state.Sleeping == 0) { awake = true; _sleeping.Remove(state.Actor); }
            else
            {
                if (_sleeping.TryGetValue(state.Actor, out var previous) && Same(previous, state)) continue;
                _sleeping[state.Actor] = state;
            }
            states[count++] = state;
        }
        return count;
    }
    internal void Remove(ulong actor) => _sleeping.Remove(actor);
    private static bool Same(NativeApi.BodyState a, NativeApi.BodyState b) =>
        Same(a.Pose.Position, b.Pose.Position) && Same(a.LinearVelocity, b.LinearVelocity) && Same(a.AngularVelocity, b.AngularVelocity) &&
        a.Pose.Rotation.X == b.Pose.Rotation.X && a.Pose.Rotation.Y == b.Pose.Rotation.Y &&
        a.Pose.Rotation.Z == b.Pose.Rotation.Z && a.Pose.Rotation.W == b.Pose.Rotation.W;
    private static bool Same(NativeApi.Vec3 a, NativeApi.Vec3 b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;
}
