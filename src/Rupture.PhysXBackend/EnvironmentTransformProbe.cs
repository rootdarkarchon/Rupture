using UnityEngine;

namespace Rupture.PhysXBackend;

// Poll once on first use, then at most 4 Hz. Long frames do not create a
// catch-up burst. Realtime keeps the cadence independent of simulation rate.
internal struct EnvironmentPollCadence
{
    private float _next;
    internal bool TryBegin(float now)
    {
        if (now < _next) return false;
        _next = now + 0.25f;
        return true;
    }
}

// Do not consume Transform.hasChanged: EFT and other mods also own that flag.
// One native matrix read detects changes anywhere in the parent hierarchy. Only
// changing roots need the more expensive world rotation/scale decomposition.
internal struct EnvironmentTransformProbe
{
    private Matrix4x4 _observed;
    private bool _valid;
    internal bool Changed(Transform root)
    {
        Matrix4x4 current = root.localToWorldMatrix;
        // Matrix4x4.Equals constructs columns on this Unity runtime. Compare the
        // scalar entries directly; exact equality avoids suppressing slow drift.
        if (_valid && current.m00 == _observed.m00 && current.m01 == _observed.m01 && current.m02 == _observed.m02 && current.m03 == _observed.m03 &&
            current.m10 == _observed.m10 && current.m11 == _observed.m11 && current.m12 == _observed.m12 && current.m13 == _observed.m13 &&
            current.m20 == _observed.m20 && current.m21 == _observed.m21 && current.m22 == _observed.m22 && current.m23 == _observed.m23 &&
            current.m30 == _observed.m30 && current.m31 == _observed.m31 && current.m32 == _observed.m32 && current.m33 == _observed.m33) return false;
        _observed = current; _valid = true;
        return true;
    }
}
