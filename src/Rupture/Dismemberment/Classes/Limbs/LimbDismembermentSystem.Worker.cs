using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.Limbs;

internal static partial class LimbDismembermentSystem
{
	private sealed class GeometryBuildWork
	{
		internal string RendererName;
		internal string BodyPart;
		internal string RequestIdentity;
		internal string Diagnostic;
		internal string PreparationStage = "pending";
		internal LimbCutCaptureSession CaptureSession;
		internal ProceduralLimbCutGeometry.ClassificationWork Classification;
		internal int LodLevel;
		internal int NewCutIndex;
		internal bool BuildDistal;
		internal bool IsPrimary;
		internal double CaptureMainMilliseconds;
		internal ProceduralLimbCutGeometry.Result Analysis;
		internal ProceduralLimbCutGeometry.Result Built;
	}

	private sealed class GeometryJob : ManagedGeometryJob
	{
		internal GeometryBuildWork[] Work;
		internal bool BuildDebugOverlay;
		internal double ClassificationMilliseconds;
		internal double AnalysisMilliseconds;
		internal double BufferBuildMilliseconds;

		protected override bool TryExecute(out string failure)
		{
			bool succeeded;
			try { succeeded = TryExecuteCore(out failure); }
			catch (Exception exception)
			{
				failure = $"{exception.GetType().Name}: {exception.Message}";
				succeeded = false;
			}
			if (!Cancelled)
				foreach (GeometryBuildWork item in Work)
				{
					if (item.CaptureSession == null) continue;
					string outcome = succeeded ? "prepared" : $"{item.PreparationStage}: {failure}";
					// This work item is no longer modified after the geometry job returns.
					try
					{
						bool queued = LimbCutCaptureQueue.TryEnqueue(() => item.Classification.Capture(item.CaptureSession,
							item.RendererName, item.LodLevel, item.BodyPart, item.NewCutIndex, item.RequestIdentity, outcome));
						item.Diagnostic = $"renderer='{item.RendererName}', request='{item.RequestIdentity}', " +
							$"capture={(queued ? "queued" : "skipped-backlog-limit")}, directory='{item.CaptureSession.DirectoryPath}'";
					}
					catch (Exception exception)
					{
						item.Diagnostic = $"renderer='{item.RendererName}', captureError={exception.GetType().Name}: {exception.Message}";
					}

				}

			return succeeded;
		}

		internal float WinningOffset;
		private bool TryExecuteCore(out string failure)
		{
			var batch = Work.Select(item => new ProceduralLimbCutGeometry.BatchItem
			{
				Label = $"renderer='{item.RendererName}', lod={item.LodLevel}",
				Classification = item.Classification, NewCutIndex = item.NewCutIndex,
				LodLevel = item.LodLevel, BuildDistal = item.BuildDistal
			}).ToArray();
			bool succeeded = ProceduralLimbCutGeometry.TryBuildBatchWithRetries(batch, BuildDebugOverlay,
				() => Cancelled, out WinningOffset, out failure);
			for (int index = 0; index < Work.Length; index++)
			{
				Work[index].PreparationStage = batch[index].Stage;
				Work[index].Analysis = batch[index].Analysis;
				Work[index].Built = batch[index].Built;
				Work[index].IsPrimary = batch[index].IsPrimary;
			}
			ClassificationMilliseconds = batch.Sum(item => item.ClassificationMilliseconds);
			AnalysisMilliseconds = batch.Sum(item => item.AnalysisMilliseconds);
			BufferBuildMilliseconds = batch.Sum(item => item.BuildMilliseconds);
			return succeeded;
		}

	}

	private sealed class ColliderBakeJob : ManagedGeometryJob
	{
		internal int[] MeshInstanceIds;

		protected override bool TryExecute(out string failure)
		{
			failure = null;
			using (new ProfileScope("RPT.LimbCut.UnityColliderBakeWorker"))
			{
				foreach (int meshInstanceId in MeshInstanceIds)
				{
					if (Cancelled)
					{
						failure = "cancelled";
						return false;
					}
					Physics.BakeMesh(meshInstanceId, convex: true, MeshColliderCookingOptions.None);
				}
			}
			return true;
		}
	}

	private static class LimbGeometryWorker
	{
		internal static GeometryJob Enqueue(GeometryBuildWork[] work, bool buildDebugOverlay) =>
			ManagedGeometryWorker.Enqueue(new GeometryJob { Work = work, BuildDebugOverlay = buildDebugOverlay });

		internal static ColliderBakeJob EnqueueColliderBake(Mesh[] meshes)
		{
			int[] ids = new int[meshes.Length];
			for (int index = 0; index < meshes.Length; index++) ids[index] = meshes[index].GetInstanceID();
			return ManagedGeometryWorker.Enqueue(new ColliderBakeJob { MeshInstanceIds = ids });
		}
	}
}
