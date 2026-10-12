using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

internal static class BackendThreadingTests
{
	internal static void Run(Assembly backend)
	{
		using var assembly = AssemblyDefinition.ReadAssembly(backend.Location);
		var runtime = assembly.MainModule.GetType("Rupture.PhysXBackend.ExternalPhysXRuntime");
		var callbacks = runtime.NestedTypes.SelectMany(t => t.Methods).Where(m =>
			m.Name.StartsWith("<TryCreateBody>", StringComparison.Ordinal) ||
			m.Name.StartsWith("<TryCreateBoxBody>", StringComparison.Ordinal) ||
			m.Name.StartsWith("<TryCreateRoundedBoxBody>", StringComparison.Ordinal) ||
			m.Name.StartsWith("<TryCreateCorpseGrip>", StringComparison.Ordinal) ||
			m.Name.StartsWith("<TryMoveCorpseGrip>", StringComparison.Ordinal)).ToArray();
		if (callbacks.Length != 5) throw new Exception("Expected five body/grip scene-owner callbacks to audit.");
		var unityObjects = new HashSet<string> { "UnityEngine.Object", "UnityEngine.Component", "UnityEngine.Collider", "UnityEngine.Rigidbody", "UnityEngine.Transform", "UnityEngine.GameObject" };
		foreach (var callback in callbacks)
		{
			var unsafeCall = callback.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
				.FirstOrDefault(m => unityObjects.Contains(m.DeclaringType.FullName));
			if (unsafeCall != null) throw new Exception("Owner callback accesses Unity object: " + callback.FullName + " -> " + unsafeCall.FullName);
			Console.WriteLine("PASS: synchronous body owner callback consumes snapshots without Unity object access: " + callback.Name);
		}
	}
}
