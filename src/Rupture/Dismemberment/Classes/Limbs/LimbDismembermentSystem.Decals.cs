using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using Systems.Effects;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.Limbs;

internal static partial class LimbDismembermentSystem
{
	// Native-paint transfer, deferred postcommit painting and its diagnostics.
	// These cosmetics cannot roll back an adopted destructive renderer batch.
	private static void CaptureLimbDecalAudits(List<PreparedRenderer> prepared, EBodyPart bodyPart,
		LimbSeverLevel level)
	{
		foreach (PreparedRenderer item in prepared)
		{
			if (item?.Source?.Native == null) continue;
			LimbMeshLayoutSnapshot immutableLayout = LimbDecalDiagnostics.CaptureMeshLayout(
				item.Source.ImmutableMesh);
			LimbMeshLayoutSnapshot currentLayout = LimbDecalDiagnostics.CaptureMeshLayout(
				item.Source.Native.sharedMesh);
			LimbMeshLayoutSnapshot bodyLayout = LimbDecalDiagnostics.CaptureMeshLayout(item.BodyMesh);
			Mesh bakedGibMesh = item.GibRenderer?.GetComponent<MeshFilter>()?.sharedMesh;
			LimbMeshLayoutSnapshot gibLayout = LimbDecalDiagnostics.CaptureMeshLayout(bakedGibMesh);
			item.DecalAudit = LimbDecalDiagnostics.CaptureBeforeCommit(item.Source.Native, item.BodyMesh,
				item.GibRenderer, item.BodyDecalSamples, item.GibDecalSamples,
				item.Source.LodLevel);
			LimbDecalAudit audit = item.DecalAudit;
			RupturePlugin.Log.LogInfo($"RPT limb decal audit pre: bodyPart={bodyPart}, level={level}, renderer={audit.RendererLabel}, " +
				$"lod={audit.LodLevel}, bodyUv={audit.BodyUvExtraction}, gibUv={audit.GibUvExtraction}, " +
				audit.QueueBefore.Describe("queueBeforeCommit"));
			RupturePlugin.Log.LogInfo($"RPT limb decal audit pre body binding: {LimbDecalDiagnostics.DescribeBinding(item.Source.Native)}");
			RupturePlugin.Log.LogInfo($"RPT limb decal audit pre socket binding: {LimbDecalDiagnostics.DescribeBinding(item.SocketRenderer)}, " +
				$"nativePainterCompatible={LimbDecalTextureTransfer.CanAcceptNativeDecal(item.SocketRenderer)}");
			if (item.GibRenderer != null)
				RupturePlugin.Log.LogInfo($"RPT limb decal audit pre gib binding: {LimbDecalDiagnostics.DescribeBinding(item.GibRenderer)}, " +
					$"nativePainterCompatible={LimbDecalTextureTransfer.CanAcceptNativeDecal(item.GibRenderer)}, " +
					$"woundSubmesh={Mathf.Max(0, item.GibRenderer.sharedMaterials.Length - 1)}, " +
					"eftDrawRendererDefaultSubmesh=0");
			RupturePlugin.Log.LogInfo($"RPT limb decal audit pre texture: {audit.SourceBefore.Describe("sourceBeforeCommit")}; " +
				$"{audit.GibBefore.Describe("gibAfterInheritedCopy")}; " +
				$"{LimbDecalDiagnostics.Compare("sourceToInheritedGib", audit.SourceBefore, audit.GibBefore)}");
			RupturePlugin.Log.LogInfo($"RPT limb decal audit mesh layout immutable: renderer={audit.RendererLabel}, lod={audit.LodLevel}; " +
				immutableLayout.Describe());
			RupturePlugin.Log.LogInfo($"RPT limb decal audit mesh layout current: renderer={audit.RendererLabel}, lod={audit.LodLevel}; " +
				currentLayout.Describe());
			RupturePlugin.Log.LogInfo($"RPT limb decal audit mesh layout prepared-body: renderer={audit.RendererLabel}, lod={audit.LodLevel}; " +
				bodyLayout.Describe());
			RupturePlugin.Log.LogInfo($"RPT limb decal audit mesh layout baked-gib: renderer={audit.RendererLabel}, lod={audit.LodLevel}; " +
				gibLayout.Describe());
			RupturePlugin.Log.LogInfo($"RPT limb decal audit mesh layout comparison: renderer={audit.RendererLabel}, lod={audit.LodLevel}; " +
				$"{LimbDecalDiagnostics.CompareMeshLayouts("immutableToCurrent", immutableLayout, currentLayout)}; " +
				$"{LimbDecalDiagnostics.CompareMeshLayouts("immutableToPreparedBody", immutableLayout, bodyLayout)}; " +
				$"{LimbDecalDiagnostics.CompareMeshLayouts("immutableToBakedGib", immutableLayout, gibLayout)}");
		}
	}

	private static IEnumerator PaintNativeBloodDeferred(PlayerBody playerBody, List<PreparedRenderer> prepared)
	{
		int bodySubmitted = 0;
		int gibSubmitted = 0;
		int bodyEligible = 0;
		int gibEligible = 0;
		bool auditEnabled = prepared.Any(item => item?.DecalAudit != null);
		LimbDecalQueueSnapshot queueBeforeSubmit = null;
		LimbDecalQueueSnapshot bodyQueueAfterSubmit = null;
		LimbDecalQueueSnapshot gibQueueAfterSubmit = null;
		const float ProjectionStartOffset = 0.006f;
		const int MaximumVisibilityWaitFrames = 120;
		const int MaximumAuditQueueDrainFrames = 30;
		// Static gibs still require native renderer visibility. Body skins can
		// receive paint while culled through BodyDecalLodPatch's posed mesh draw.
		yield return null;
		{
			if (RuptureConfiguration.EnableBloodEffects?.Value != true || !Singleton<Effects>.Instantiated ||
				Singleton<Effects>.Instance == null)
			{
				RupturePlugin.Log.LogInfo("RPT limb sever native blood ring skipped: blood effects or EFT Effects singleton unavailable.");
				yield break;
			}
			Effects effects = Singleton<Effects>.Instance;
			if (!effects.UseDecalPainter || effects.TexDecals == null)
			{
				RupturePlugin.Log.LogInfo("RPT limb sever native blood ring skipped: EFT texture decal painter is disabled or unavailable.");
				yield break;
			}
			PreparedRenderer bodySampleSource = prepared
				.Where(item => item?.BodyRenderer != null && item.BodyDecalSamples?.Length > 0)
				.OrderBy(item => item.Source.LodLevel).FirstOrDefault();
			Renderer[] bodyRenderers = prepared.Where(item => item?.BodyRenderer != null)
				.Select(item => (Renderer)item.BodyRenderer).Distinct().ToArray();
			List<BodyRenderer> bodyTargets = NativeBloodTargets(playerBody, bodyRenderers,
				out bool usedNativeBodyGroups);

			PreparedRenderer gibSampleSource = prepared.FirstOrDefault(item =>
				item?.GibRenderer != null && item.GibLocalDecalSamples.Count > 0);
			Renderer[] gibRenderers = prepared.Where(item => item?.GibRenderer != null)
				.Select(item => (Renderer)item.GibRenderer).Distinct().ToArray();
			List<BodyRenderer> gibTargets = GibBloodTargets(prepared, bodyTargets);
			Renderer[] observedRenderers = bodyRenderers.Concat(gibRenderers).Distinct().ToArray();
			if (auditEnabled)
				queueBeforeSubmit = LimbDecalDiagnostics.CaptureQueue(observedRenderers);
			bool bodyComplete = bodySampleSource == null || bodyTargets.Count == 0;
			bool gibComplete = gibSampleSource == null || gibTargets.Count == 0;

			for (int frame = 0; frame < MaximumVisibilityWaitFrames && (!bodyComplete || !gibComplete); frame++)
			{
				if (!bodyComplete)
				{
					bodyEligible = CountEligibleNativeDecalRenderers(bodyTargets);
					if (bodyEligible > 0)
					{
						foreach (ProceduralLimbWoundGeometry.DecalSample sample in bodySampleSource.BodyDecalSamples)
						{
							if (bodySubmitted >= 10) break;
							if (!TrySkinSample(bodySampleSource.BodyRenderer, sample,
								out Vector3 point, out Vector3 normal)) continue;
							effects.PlayerMeshesHit(bodyTargets,
								point + normal * ProjectionStartOffset, -normal);
							bodySubmitted++;
						}
						if (auditEnabled)
							bodyQueueAfterSubmit = LimbDecalDiagnostics.CaptureQueue(observedRenderers);
						bodyComplete = true;
					}
				}

				if (!gibComplete)
				{
					RetryMissingGibDecalTransfers(prepared);
					gibEligible = CountEligibleNativeDecalRenderers(gibTargets);
					if (gibEligible > 0)
					{
						Transform gibTransform = gibSampleSource.GibRenderer.transform;
						foreach (LocalDecalSample sample in gibSampleSource.GibLocalDecalSamples)
						{
							if (gibSubmitted >= 10) break;
							Vector3 normal = gibTransform.TransformDirection(sample.Normal).normalized;
							Vector3 point = gibTransform.TransformPoint(sample.Point);
							if (!IsFinite(point) || !IsFinite(normal)) continue;
							effects.PlayerMeshesHit(gibTargets,
								point + normal * ProjectionStartOffset, -normal);
							gibSubmitted++;
						}
						if (auditEnabled)
							gibQueueAfterSubmit = LimbDecalDiagnostics.CaptureQueue(observedRenderers);
						gibComplete = true;
					}
				}
				if (!bodyComplete || !gibComplete) yield return null;
			}

			int inheritedGibTextures = prepared.Count(item => item != null &&
				(item.GibDecalTransfer == LimbDecalTransferResult.Copied ||
				 item.GibDecalTransfer == LimbDecalTransferResult.AlreadyRegistered));
			RupturePlugin.Log.LogInfo($"RPT limb sever native blood ring: bodySubmissions={bodySubmitted}, gibSubmissions={gibSubmitted}, " +
				$"bodyEligible={bodyEligible}, gibEligible={gibEligible}, bodyLodTargets={bodyRenderers.Length}, " +
				$"gibTargets={gibRenderers.Length}, nativeBodyGroups={usedNativeBodyGroups}, " +
				$"inheritedGibTextures={inheritedGibTextures}, visibilityTimedOut={!bodyComplete || !gibComplete}, perSideCap=10.");

			if (auditEnabled)
			{
				LimbDecalQueueSnapshot queueAfterLoop = LimbDecalDiagnostics.CaptureQueue(observedRenderers);
				LimbDecalQueueSnapshot queueAfterDrain = queueAfterLoop;
				int drainFrames = 0;
				while (queueAfterDrain.Available && queueAfterDrain.Observed > 0 &&
					drainFrames < MaximumAuditQueueDrainFrames)
				{
					drainFrames++;
					yield return null;
					queueAfterDrain = LimbDecalDiagnostics.CaptureQueue(observedRenderers);
				}
				// The painter executes a command buffer. Read back after end-of-frame so a
				// zero queue is not mistaken for completed GPU writes.
				yield return new WaitForEndOfFrame();
				LimbDecalQueueSnapshot queueAfterGpu = LimbDecalDiagnostics.CaptureQueue(observedRenderers);
				LogLimbDecalAuditsAfterPaint(prepared, queueBeforeSubmit, bodyQueueAfterSubmit,
					gibQueueAfterSubmit, queueAfterLoop, queueAfterDrain, queueAfterGpu, drainFrames);
			}
		}
	}

	private static void LogLimbDecalAuditsAfterPaint(List<PreparedRenderer> prepared,
		LimbDecalQueueSnapshot queueBeforeSubmit, LimbDecalQueueSnapshot bodyQueueAfterSubmit,
		LimbDecalQueueSnapshot gibQueueAfterSubmit, LimbDecalQueueSnapshot queueAfterLoop,
		LimbDecalQueueSnapshot queueAfterDrain, LimbDecalQueueSnapshot queueAfterGpu, int drainFrames)
	{
		int bodyQueueDelta = QueueDelta(queueBeforeSubmit, bodyQueueAfterSubmit);
		int gibQueueDelta = QueueDelta(bodyQueueAfterSubmit ?? queueBeforeSubmit, gibQueueAfterSubmit);
		RupturePlugin.Log.LogInfo($"RPT limb decal audit queue: {DescribeQueue(queueBeforeSubmit, "beforeSubmit")}; " +
			$"{DescribeQueue(bodyQueueAfterSubmit, "immediatelyAfterBodySubmit")}; " +
			$"{DescribeQueue(gibQueueAfterSubmit, "immediatelyAfterGibSubmit")}; " +
			$"{DescribeQueue(queueAfterLoop, "afterVisibilityLoop")}; " +
			$"{DescribeQueue(queueAfterDrain, "afterDrain")}; {DescribeQueue(queueAfterGpu, "afterEndOfFrame")}; " +
			$"drainFrames={drainFrames}, bodyQueueDelta={bodyQueueDelta}, gibQueueDelta={gibQueueDelta}");
		foreach (PreparedRenderer item in prepared)
		{
			LimbDecalAudit audit = item?.DecalAudit;
			if (audit == null) continue;
			LimbDecalTextureSnapshot bodyAfter = LimbDecalDiagnostics.CaptureTexture(item.BodyRenderer,
				audit.BodyUvs, audit.GibUvs);
			LimbDecalTextureSnapshot gibAfter = LimbDecalDiagnostics.CaptureTexture(item.GibRenderer,
				Array.Empty<Vector2>(), audit.GibUvs);
			RupturePlugin.Log.LogInfo($"RPT limb decal audit post body binding: renderer={audit.RendererLabel}, lod={audit.LodLevel}, " +
				LimbDecalDiagnostics.DescribeBinding(item.BodyRenderer));
			RupturePlugin.Log.LogInfo($"RPT limb decal audit post socket binding: renderer={audit.RendererLabel}, lod={audit.LodLevel}, " +
				$"{LimbDecalDiagnostics.DescribeBinding(item.SocketRenderer)}, " +
				$"nativePainterCompatible={LimbDecalTextureTransfer.CanAcceptNativeDecal(item.SocketRenderer)}");
			if (item.GibRenderer != null)
				RupturePlugin.Log.LogInfo($"RPT limb decal audit post gib binding: renderer={audit.RendererLabel}, lod={audit.LodLevel}, " +
					LimbDecalDiagnostics.DescribeBinding(item.GibRenderer));
			RupturePlugin.Log.LogInfo($"RPT limb decal audit post texture: renderer={audit.RendererLabel}, lod={audit.LodLevel}; " +
				$"{bodyAfter.Describe("bodyAfterGpu")}; {gibAfter.Describe("gibAfterGpu")}");
			RupturePlugin.Log.LogInfo($"RPT limb decal audit comparison: renderer={audit.RendererLabel}, lod={audit.LodLevel}; " +
				$"{LimbDecalDiagnostics.Compare("sourceBeforeToBodyAfter", audit.SourceBefore, bodyAfter)}; " +
				$"{LimbDecalDiagnostics.Compare("inheritedGibBeforeToGibAfter", audit.GibBefore, gibAfter)}; " +
				$"{LimbDecalDiagnostics.Compare("bodyAfterToGibAfter", bodyAfter, gibAfter)}; " +
				$"{LimbDecalDiagnostics.CompareRegion("bodySeamRegion", audit.SourceBefore, false, bodyAfter, false)}; " +
				$"{LimbDecalDiagnostics.CompareRegion("gibCopyRegion", audit.SourceBefore, true, audit.GibBefore, true)}; " +
				$"{LimbDecalDiagnostics.CompareRegion("gibSeamRegion", audit.GibBefore, true, gibAfter, true)}");
			RupturePlugin.Log.LogInfo($"RPT limb decal audit finding: renderer={audit.RendererLabel}, lod={audit.LodLevel}; " +
				$"{LimbDecalDiagnostics.ClassifySide("body", audit.SourceBefore, false, bodyAfter, false, bodyQueueDelta)}; " +
				$"{LimbDecalDiagnostics.ClassifySide("gib", audit.GibBefore, true, gibAfter, true, gibQueueDelta)}");
		}
	}

	private static string DescribeQueue(LimbDecalQueueSnapshot snapshot, string stage) =>
		snapshot?.Describe(stage) ?? $"{stage}=not-captured";

	private static int QueueDelta(LimbDecalQueueSnapshot before, LimbDecalQueueSnapshot after) =>
		before?.Available == true && after?.Available == true ? after.Observed - before.Observed : -1;

	private static List<BodyRenderer> NativeBloodTargets(PlayerBody playerBody, Renderer[] renderers,
		out bool usedNativeGroups)
	{
		usedNativeGroups = false;
		HashSet<Renderer> allowed = new HashSet<Renderer>(renderers ?? Array.Empty<Renderer>());
		List<BodyRenderer> filtered = new List<BodyRenderer>();
		if (playerBody != null && allowed.Count > 0)
		{
			List<BodyRenderer> native = new List<BodyRenderer>();
			playerBody.GetBodyRenderersNonAlloc(native);
			foreach (BodyRenderer group in native)
			{
				Renderer[] matches = (group.Renderers ?? Array.Empty<Renderer>())
					.Where(renderer => renderer != null && allowed.Contains(renderer)).Distinct().ToArray();
				if (matches.Length == 0) continue;
				filtered.Add(new BodyRenderer { DecalType = group.DecalType, Renderers = matches });
			}
		}
		if (filtered.Count > 0)
		{
			usedNativeGroups = true;
			return filtered;
		}
		return renderers == null || renderers.Length == 0
			? filtered
			: new List<BodyRenderer>
			{
				new BodyRenderer { DecalType = EDecalTextureType.Blood, Renderers = renderers }
			};
	}

	private static List<BodyRenderer> GibBloodTargets(List<PreparedRenderer> prepared,
		List<BodyRenderer> bodyTargets)
	{
		Dictionary<Renderer, EDecalTextureType> sourceTypes = new Dictionary<Renderer, EDecalTextureType>();
		foreach (BodyRenderer group in bodyTargets)
			foreach (Renderer renderer in group.Renderers ?? Array.Empty<Renderer>())
				if (renderer != null && !sourceTypes.ContainsKey(renderer)) sourceTypes.Add(renderer, group.DecalType);
		Dictionary<EDecalTextureType, List<Renderer>> grouped = new Dictionary<EDecalTextureType, List<Renderer>>();
		foreach (PreparedRenderer item in prepared)
		{
			if (item?.GibRenderer == null) continue;
			EDecalTextureType type = sourceTypes.TryGetValue(item.Source.Native, out EDecalTextureType sourceType)
				? sourceType : EDecalTextureType.Blood;
			if (!grouped.TryGetValue(type, out List<Renderer> renderers))
				grouped[type] = renderers = new List<Renderer>();
			if (!renderers.Contains(item.GibRenderer)) renderers.Add(item.GibRenderer);
		}
		return grouped.Select(group => new BodyRenderer
		{
			DecalType = group.Key,
			Renderers = group.Value.ToArray()
		}).ToList();
	}

	private static int CountEligibleNativeDecalRenderers(List<BodyRenderer> targets) =>
		targets.SelectMany(group => group.Renderers ?? Array.Empty<Renderer>()).Distinct()
			.Count(LimbDecalTextureTransfer.CanAcceptNativeDecal);

	private static void RetryMissingGibDecalTransfers(List<PreparedRenderer> prepared)
	{
		foreach (PreparedRenderer item in prepared)
		{
			if (item?.GibRenderer == null || item.GibDecalTransfer != LimbDecalTransferResult.NoSourceTexture)
				continue;
			item.GibDecalTransfer = LimbDecalTextureTransfer.CopyExisting(item.Source.Native, item.GibRenderer,
				out string failure);
			if (item.GibDecalTransfer == LimbDecalTransferResult.Unavailable)
				RupturePlugin.Log.LogWarning($"RPT could not inherit delayed limb decals for gib renderer {item.GibRenderer.name}: {failure}");
		}
	}

}
