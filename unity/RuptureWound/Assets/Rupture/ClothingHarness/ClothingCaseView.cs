using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Rupture.ClothingHarness.Editor")]

namespace Rupture.ClothingHarness
{
    // Temporary scene object: geometry and evidence belong to this exact case,
    // never to another side/level or to an imported source asset.
    public sealed class ClothingCaseView : MonoBehaviour
    {
        [TextArea(4, 18)] public string Summary;
        public bool ShowIntactReference;
        [Min(0)] public int PreviewLod;
        internal readonly Dictionary<GameObject, int> LodSpaces = new Dictionary<GameObject, int>();
        public bool ShowClassification;
        public bool ShowPartialExterior = true;
        public bool ShowBoundaryEdges = true;
        [Tooltip("Yellow: plane edges; magenta: selection interfaces; cyan: pre-existing openings; green: ordered loops.")]
        public bool ShowBonePalette;
        internal readonly List<Object> Owned = new List<Object>();
        internal readonly List<GameObject> Originals = new List<GameObject>();
        internal readonly List<GameObject> Classification = new List<GameObject>();
        internal readonly List<GameObject> Partial = new List<GameObject>();
        internal readonly List<GameObject> Results = new List<GameObject>();
        internal readonly List<EdgeSet> Edges = new List<EdgeSet>();
        internal readonly List<Transform> Bones = new List<Transform>();
        internal readonly List<CutFocus> CutViews = new List<CutFocus>();
        internal bool Failed;

        internal sealed class CutFocus
        {
            internal int LodLevel;
            internal string Face;
            internal Vector3 Center, Normal;
            internal float Radius;
        }

        internal sealed class EdgeSet
        {
            internal Transform Space;
            internal Vector3[] Positions;
            internal int[][] Plane, Selection, Open, Loops;
        }

        private void OnValidate() => ApplyVisibility();
        internal void ApplyVisibility()
        {
            foreach (var entry in LodSpaces) if (entry.Key != null) entry.Key.SetActive(entry.Value == PreviewLod);
            foreach (GameObject go in Originals.Where(o => o != null)) go.SetActive(ShowIntactReference ||
                (Failed && !ShowClassification && (!ShowPartialExterior || Partial.Count == 0)));
            foreach (GameObject go in Classification.Where(o => o != null)) go.SetActive(ShowClassification);
            foreach (GameObject go in Partial.Where(o => o != null)) go.SetActive(ShowPartialExterior && !ShowClassification && !ShowIntactReference);
            foreach (GameObject go in Results.Where(o => o != null)) go.SetActive(!ShowClassification && !ShowIntactReference);
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Handles.Label(transform.position + Vector3.up * 1.9f, name);
            if (ShowBonePalette)
                foreach (Transform bone in Bones.Where(b => b != null))
                {
                    Handles.color = Color.white;
                    Handles.Label(bone.position, bone.name);
                    if (bone.parent != null) Handles.DrawLine(bone.position, bone.parent.position);
                }
            if (!ShowBoundaryEdges) return;
            foreach (EdgeSet set in Edges)
            {
                if (set.Space == null) continue;
                using (new Handles.DrawingScope(set.Space.localToWorldMatrix))
                {
                    Draw(set.Positions, set.Plane, Color.yellow, 1);
                    Draw(set.Positions, set.Selection, Color.magenta, 1);
                    Draw(set.Positions, set.Open, Color.cyan, 0);
                    Handles.color = Color.green;
                    foreach (int[] loop in set.Loops ?? new int[0][])
                        for (int i = 0; i < loop.Length; i++)
                            Handles.DrawLine(set.Positions[loop[i]], set.Positions[loop[(i + 1) % loop.Length]]);
                }
            }
        }

        private static void Draw(Vector3[] p, int[][] edges, Color color, int offset)
        {
            Handles.color = color;
            foreach (int[] edge in edges ?? new int[0][])
                if (edge.Length >= offset + 2 && edge[offset] < p.Length && edge[offset + 1] < p.Length)
                    Handles.DrawLine(p[edge[offset]], p[edge[offset + 1]]);
        }
#endif

        private void OnDestroy()
        {
            foreach (Object item in Owned) if (item != null) DestroyImmediate(item);
            Owned.Clear();
        }
    }
}
