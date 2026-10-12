#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Rupture;
using Rupture.Dismemberment.Classes;
using Rupture.Ragdolls.Classes;
using UnityEngine;

[UnityEditor.InitializeOnLoad]
public static class ReliabilityRuntimeTests
{
	private const string PendingRun = "RuptureReliabilityRuntimeTests";
	static ReliabilityRuntimeTests()
	{
		UnityEditor.EditorApplication.playModeStateChanged += state =>
		{
			if (state != UnityEditor.PlayModeStateChange.EnteredPlayMode || !UnityEditor.SessionState.GetBool(PendingRun, false)) return;
			UnityEditor.SessionState.SetBool(PendingRun, false);
			try { Execute(); UnityEditor.EditorApplication.Exit(0); }
			catch (Exception exception) { Debug.LogException(exception); UnityEditor.EditorApplication.Exit(1); }
		};
	}
	private static int _cases;
	private static void Check(bool condition, string name)
	{
		if (!condition) throw new Exception(name);
		_cases++;
		Debug.Log("PASS: " + name);
	}

	public static void RunBatch()
	{
		UnityEditor.SessionState.SetBool(PendingRun, true);
		UnityEditor.EditorApplication.EnterPlaymode();
	}

	private static void Execute()
	{
		LimbRetentionRuntimeTests.Run(Check);
		WeaponColliderGeometryTests.Run(Check);
		RendererSourceTests.Run(Check);
		TestFrames();
		TestContacts();
		TestOwnerRemoval();
		TestRendererRollback();
		string report = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "verification.json");
		File.WriteAllText(report, "{\"passed\":true,\"cases\":" + _cases + ",\"unityVersion\":\"" + Application.unityVersion + "\"}");
	}

	private static void TestFrames()
	{
		foreach (Vector3 scale in new[] { Vector3.one, new Vector3(2f, .4f, 1.7f), new Vector3(-2f, .4f, 1.7f) })
		{
			var source = new GameObject("source renderer");
			var target = new GameObject("gib or head frame");
			source.transform.SetPositionAndRotation(new Vector3(2, 3, 4), Quaternion.Euler(23, 41, 9));
			source.transform.localScale = scale;
			target.transform.SetPositionAndRotation(new Vector3(-1, 1, 2), Quaternion.Euler(14, -27, 10));
			target.transform.localScale = new Vector3(1.1f, .9f, 1.3f);
			Vector3[] vertices = { new Vector3(0, 0, 0), new Vector3(1, .2f, 0), new Vector3(.1f, 1, .3f) };
			Vector3 normal = Vector3.Cross(vertices[1] - vertices[0], vertices[2] - vertices[0]).normalized;
			Vector3 tangent = (vertices[1] - vertices[0]).normalized;
			var normals = new[] { normal, normal, normal };
			var tangents = new[] { new Vector4(tangent.x, tangent.y, tangent.z, 1), new Vector4(tangent.x, tangent.y, tangent.z, 1), new Vector4(tangent.x, tangent.y, tangent.z, 1) };
			var mesh = new Mesh { vertices = vertices, normals = normals, tangents = tangents };
			mesh.subMeshCount = 2;
			mesh.SetTriangles(new[] { 0, 1, 2 }, 0); mesh.SetTriangles(new[] { 0, 1, 2 }, 1);
			Vector3[] expectedWorld = Array.ConvertAll(vertices, v => source.transform.TransformPoint(v));
			Matrix4x4 matrix = target.transform.worldToLocalMatrix * source.transform.localToWorldMatrix;
			MeshFrameTransform.Apply(mesh, matrix, vertices, normals, tangents);
			for (int index = 0; index < vertices.Length; index++)
			{
				Check(Vector3.Distance(target.transform.TransformPoint(mesh.vertices[index]), expectedWorld[index]) < 1e-5f, "scaled frame preserves world vertex " + scale);
				Check(Mathf.Abs(Vector3.Dot(mesh.normals[index], (Vector3)mesh.tangents[index])) < 1e-5f, "transformed tangent stays perpendicular to normal");
			}
			for (int submesh = 0; submesh < 2; submesh++)
			{
				int[] triangle = mesh.GetTriangles(submesh);
				Vector3 face = Vector3.Cross(vertices[triangle[1]] - vertices[triangle[0]], vertices[triangle[2]] - vertices[triangle[0]]).normalized;
				Check(Vector3.Dot(face, mesh.normals[0]) > .999f, "inverse-transpose normals and winding agree in submesh " + submesh);
			}
			Check(mesh.tangents[0].w == (scale.x < 0 ? -1 : 1), "mirror changes tangent handedness");
			UnityEngine.Object.DestroyImmediate(mesh);
			UnityEngine.Object.DestroyImmediate(source); UnityEngine.Object.DestroyImmediate(target);
		}
		var singular = new Mesh { vertices = new[] { Vector3.one } };
		bool rejected = false;
		try { MeshFrameTransform.Apply(singular, Matrix4x4.Scale(Vector3.zero), singular.vertices, null, null); }
		catch (InvalidOperationException) { rejected = true; }
		Check(rejected && singular.vertices[0] == Vector3.one, "singular frame fails before changing mesh");
		UnityEngine.Object.DestroyImmediate(singular);
	}

	private static void TestContacts()
	{
		bool automatic = Physics.autoSimulation;
		Physics.autoSimulation = false;
		var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
		floor.transform.position = new Vector3(0, -.5f, 0); floor.transform.localScale = new Vector3(10, 1, 10);
		var gib = GameObject.CreatePrimitive(PrimitiveType.Cube); gib.transform.position = new Vector3(0, 2, 0);
		var body = gib.AddComponent<Rigidbody>();
		Collider collider = gib.GetComponent<Collider>();
		collider.includeLayers = 1; collider.excludeLayers = 1 << 12;
		var contacts = new ColliderContactState(collider);
		collider.includeLayers = 0; collider.excludeLayers = ~0; gib.layer = 2; body.isKinematic = true;
		contacts.Restore();
		body.isKinematic = false; body.useGravity = true; body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
		Physics.SyncTransforms();
		for (int frame = 0; frame < 180; frame++) Physics.Simulate(1f / 60f);
		Check(gib.layer == 0 && collider.includeLayers == 1 && collider.excludeLayers == (1 << 12) && collider.enabled && !collider.isTrigger,
			"fallback restores layer, both contact masks, enablement and trigger state");
		Check(body.position.y > .45f && body.position.y < .6f, "restored gib collides with the Unity floor");
		UnityEngine.Object.DestroyImmediate(gib); UnityEngine.Object.DestroyImmediate(floor);
		Physics.autoSimulation = automatic;
	}

	private static void TestOwnerRemoval()
	{
		var retained = new Dictionary<int, object>();
		for (int cycle = 0; cycle < 100; cycle++)
		{
			var owner = new GameObject("corpse");
			var detached = new GameObject("detached or restored native gib");
			int id = owner.GetInstanceID(); object current = new object(); retained[id] = current;
			ObjectLifetime.Bind(owner, "state", () => throw new Exception("obsolete callback must be replaced"));
			ObjectLifetime.Bind(owner, "state", () => { if (ReferenceEquals(retained[id], current)) retained.Remove(id); });
			ObjectLifetime.Bind(owner, "gib", () => UnityEngine.Object.DestroyImmediate(detached));
			UnityEngine.Object.DestroyImmediate(owner);
			Check(detached == null && !retained.ContainsKey(id), "corpse removal releases detached root and current registry identity " + cycle);
		}
		Check(retained.Count == 0, "repeated corpse cycles retain no registry graphs");
	}

	private static void TestRendererRollback()
	{
		for (int failAt = 0; failAt < 4; failAt++)
		{
			var owner = new GameObject("LOD corpse"); var group = owner.AddComponent<LODGroup>();
			var first = owner.AddComponent<SkinnedMeshRenderer>();
			var child = new GameObject("second renderer"); child.transform.SetParent(owner.transform);
			var second = child.AddComponent<SkinnedMeshRenderer>();
			var oldMesh = new Mesh(); var nextMesh = new Mesh(); first.sharedMesh = second.sharedMesh = oldMesh;
			group.SetLODs(new[] { new LOD(.5f, new Renderer[] { first }), new LOD(.1f, new Renderer[] { second }) });
			LOD[] before = group.GetLODs();
			var transaction = new CommitTransaction();
			int operation = 0;
			foreach (SkinnedMeshRenderer renderer in new[] { first, second })
			{
				transaction.Add(() => { renderer.sharedMesh = nextMesh; if (operation++ == failAt) throw new Exception("mesh swap"); }, () => renderer.sharedMesh = oldMesh);
				transaction.Add(() => { group.SetLODs(new[] { new LOD(.2f, new Renderer[] { first, second }) }); if (operation++ == failAt) throw new Exception("LOD swap"); }, () => group.SetLODs(before));
			}
			try { transaction.Commit(); } catch { }
			UnityEngine.Object.DestroyImmediate(nextMesh);
			Check(first.sharedMesh == oldMesh && second.sharedMesh == oldMesh && group.lodCount == 2 && oldMesh != null,
				"failed mesh/LOD transaction leaves both previous meshes alive " + failAt);
			UnityEngine.Object.DestroyImmediate(owner); UnityEngine.Object.DestroyImmediate(oldMesh);
		}
	}
}

// Only the logging sink is substituted; tests execute the production lifetime,
// collider restoration, frame conversion, and transaction implementations.
namespace Rupture
{
	internal static class RupturePlugin { internal static readonly TestLog Log = new TestLog(); }
	internal sealed class TestLog { internal void LogError(object message) => Debug.LogError(message); }
}
#endif
