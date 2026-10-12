#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
namespace Rupture.Dismemberment.Classes.BloodStreams
{
    internal static partial class PulsatingBloodStreamSystem
    {
        internal static void VerifyClosestHits()
        {
            var objects = new List<GameObject>();
            Vector3 origin = new Vector3(1000, 20, 0);
            try
            {
                // More hits than the old eight-entry buffer could hold, created
                // farthest first, plus a closer trigger which must be ignored.
                for (int i = 12; i >= 0; i--)
                {
                    var slab = GameObject.CreatePrimitive(PrimitiveType.Cube); objects.Add(slab);
                    slab.transform.position = origin + Vector3.right * (i + 1) * .1f;
                    slab.transform.localScale = new Vector3(.02f, 4, 4);
                    if (i == 0) slab.GetComponent<Collider>().isTrigger = true;
                }
                Physics.SyncTransforms();
                PredictImpact(origin, Vector3.right * 100, Vector3.zero, 1, out bool hit, out float age, out Vector3 point, out Vector3 normal);
                if (!hit || Math.Abs(point.x - (origin.x + .19f)) > .0001f || Math.Abs(age - .0019f) > .00001f || normal.x > -.999f)
                    throw new Exception("Dense impact prediction must return the closest non-trigger surface");
                Debug.Log("PASS: closest impact with twelve blocking surfaces and an ignored trigger");
            }
            finally { foreach (var obj in objects) UnityEngine.Object.DestroyImmediate(obj); }
        }
    }
}
#endif
