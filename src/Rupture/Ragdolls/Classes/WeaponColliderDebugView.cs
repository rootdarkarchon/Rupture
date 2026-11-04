using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Shape = Rupture.Ragdolls.Classes.ShadowRagdollWorld.WeaponColliderDebugShape;

namespace Rupture.Ragdolls.Classes;

internal sealed class WeaponColliderDebugView : MonoBehaviour
{
	private readonly List<Shape> _shapes = new List<Shape>();
	private Material _material;
	private bool _shaderUnavailable;
	private static bool Visible => RuptureConfiguration.WeaponColliderWireframe?.Value == true;

	private void LateUpdate()
	{
		if (!Visible) { _shapes.Clear(); return; }
		Camera camera = Camera.main;
		if (camera == null) { _shapes.Clear(); return; }
		// Cache immutable cooked edges once per body; no geometry readback per frame.
		ShadowRagdollWorld.CollectWeaponColliderDebugShapes(camera.transform.position, _shapes);
	}

	private void OnRenderObject()
	{
		if (!Visible || _shapes.Count == 0 || Camera.current == null || Camera.current != Camera.main) return;
		if (_material == null)
		{
			if (_shaderUnavailable) return;
			Shader shader = Shader.Find("Hidden/Internal-Colored");
			if (shader == null)
			{
				_shaderUnavailable = true;
				RupturePlugin.Log.LogWarning("Weapon collider wireframe shader Hidden/Internal-Colored is unavailable.");
				return;
			}
			_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
			_material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
			_material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
			_material.SetInt("_Cull", (int)CullMode.Off);
			_material.SetInt("_ZWrite", 0);
			_material.SetInt("_ZTest", (int)CompareFunction.Always);
		}
		if (!_material.SetPass(0)) return;
		GL.PushMatrix();
		try
		{
			GL.MultMatrix(Matrix4x4.identity);
			GL.Begin(GL.LINES);
			try
			{
				foreach (Shape shape in _shapes)
				{
					if (shape.Transform == null || !shape.Transform.gameObject.activeInHierarchy) continue;
					GL.Color(shape.Rounded ? Color.cyan : Color.yellow);
					// Native extraction already includes shape offset and baked scale.
					// Apply only the visible actor pose, never the hierarchy scale again.
					Matrix4x4 pose = Matrix4x4.TRS(shape.Transform.position, shape.Transform.rotation, Vector3.one);
					foreach (Vector3 point in shape.Edges) GL.Vertex(pose.MultiplyPoint3x4(point));
				}
			}
			finally { GL.End(); }
		}
		finally { GL.PopMatrix(); }
	}

	private void OnGUI()
	{
		if (!Visible || Camera.main == null) return;
		GUI.Label(new Rect(20, 80, 800, 25), "Rupture weapon colliders (50 m) — cyan: cooked convex hull; yellow: box fallback");
		if (Event.current.type != EventType.Repaint) return;
		Camera camera = Camera.main;
		Color previous = GUI.color;
		foreach (Shape shape in _shapes)
		{
			if (shape.Transform == null || !shape.Transform.gameObject.activeInHierarchy) continue;
			Vector3 screen = camera.WorldToScreenPoint(shape.Transform.position);
			if (screen.z <= camera.nearClipPlane || screen.x < 0 || screen.x > Screen.width || screen.y < 0 || screen.y > Screen.height) continue;
			GUI.color = shape.Rounded ? Color.cyan : Color.yellow;
			GUI.Label(new Rect(screen.x + 8, Screen.height - screen.y, 650, 45),
				$"{shape.Transform.name} ({(shape.Rounded ? "convex" : "box")})\nfit size {shape.Size.x:F3} × {shape.Size.y:F3} × {shape.Size.z:F3} m");
		}
		GUI.color = previous;
	}

	private void OnDisable() => _shapes.Clear();
	private void OnDestroy() { if (_material != null) Destroy(_material); }
}
