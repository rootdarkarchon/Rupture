#if UNITY_EDITOR
using UnityEngine;
using Rupture.Dismemberment.Classes.BloodStreams;

namespace Rupture.UnityPreview.BloodStreams
{
// Benchmark analogue of the runtime's fixed emission history. Capture launch
// pressure/spread once at 50 Hz, then submit unchanged world-space states.
internal sealed class CapturedParcelHistory
{
    private readonly BloodParcelRenderer.Parcel[] _history;
    private readonly int _emitters;
    private float _next = -1.8f;
    private int _write;
    internal CapturedParcelHistory(int emitters) { _emitters = emitters; _history = new BloodParcelRenderer.Parcel[emitters * 96]; }
    internal void Fill(BloodParcelRenderer renderer, float now)
    {
        Vector3 gravity = Physics.gravity;
        Vector3 direction = new Vector3(1f,0.04f,0.02f).normalized;
        while (_next <= now)
        {
            float pulseAge = _next - Mathf.FloorToInt(_next / 0.72f) * 0.72f;
            float pressure = 1f - Mathf.SmoothStep(0f,1f,Mathf.Clamp01(pulseAge / 0.52f));
            float radius = 0.0052f * 0.8f * Mathf.Lerp(0.58f,1f,pressure);
            for (int emitter = 0; emitter < _emitters; emitter++)
            {
                uint seed = (uint)(emitter * 7919 + 17) ^ (uint)(Mathf.FloorToInt(_next * 50f) + 32768) * 0x85EBCA6Bu;
                Vector3 velocity = StreamPreviewGeometry.ApplyParcelSpread(direction * 2.4f * 0.8f * Mathf.Lerp(0.25f,0.74f,pressure),seed);
                _history[emitter * 96 + _write] = new BloodParcelRenderer.Parcel {
                    OriginTime = new Vector4(emitter % 6,1.4f,emitter / 6,_next),
                    VelocityRadius = new Vector4(velocity.x,velocity.y,velocity.z,radius),
                    GravityStrength = new Vector4(gravity.x,gravity.y,gravity.z,0.8f), Seed = seed };
            }
            _write = (_write + 1) % 96; _next += 1f / 50f;
        }
        foreach (var parcel in _history)
        {
            float age = now - parcel.OriginTime.w;
            if (parcel.VelocityRadius.w <= 0 || age < 0 || age > 1.8f) continue;
            renderer.Add(new Vector3(parcel.OriginTime.x,parcel.OriginTime.y,parcel.OriginTime.z),
                new Vector3(parcel.VelocityRadius.x,parcel.VelocityRadius.y,parcel.VelocityRadius.z),
                new Vector3(parcel.GravityStrength.x,parcel.GravityStrength.y,parcel.GravityStrength.z),
                parcel.OriginTime.w,parcel.VelocityRadius.w,parcel.GravityStrength.w,parcel.Seed);
        }
    }
}
}
#endif
