using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;

namespace Rupture.Dismemberment.Classes.Limbs;

internal static partial class ProceduralLimbCutGeometry
{
	internal sealed class CutAttempt
	{
		public float FractionOffset;
		public string Level, Stage, Failure;
		public bool Cached;
		public float[] Center;
		public int SelectedTriangles;
		public int[] Selected;
		public List<BoundaryCapture> Boundaries;
	}

	internal sealed class BatchItem
	{
		internal string Label, Stage;
		internal ClassificationWork Classification;
		internal int NewCutIndex, LodLevel;
		internal bool BuildDistal, IsPrimary;
		internal Result Analysis, Built;
		internal double ClassificationMilliseconds, AnalysisMilliseconds, BuildMilliseconds;
	}

	private sealed class FailedCandidate
	{
		internal string Failure;
		internal string[] Stages;
	}
	private static ConcurrentDictionary<string, FailedCandidate> FailedCandidates = new ConcurrentDictionary<string, FailedCandidate>();
	private static void ResetFailureCache() => FailedCandidates = new ConcurrentDictionary<string, FailedCandidate>();

	// Retry complete transactions. Cache geometry failures independently of wound
	// seed; seed-dependent wound failures include the seed in their cache key.
	internal static bool TryBuildBatchWithRetries(BatchItem[] items, bool debug, Func<bool> cancelled,
		out float winningOffset, out string failure, bool allowLevelFallback = true, float? singleCandidateOffset = null)
	{
		winningOffset = 0f;
		failure = "no affected readable renderer was found";
		if (items == null || items.Length == 0) return false;
		var cache = FailedCandidates;
		LimbSeverLevel requested = items[0].Classification.RequestedLevel(items[0].NewCutIndex);
		LimbSeverLevel alternate = requested == LimbSeverLevel.Proximal ? LimbSeverLevel.Distal : LimbSeverLevel.Proximal;
		foreach (LimbSeverLevel level in allowLevelFallback ? new[] { requested, alternate } : new[] { requested })
		foreach (float offset in singleCandidateOffset.HasValue ? new[] { singleCandidateOffset.Value } : new[] { 0f, -0.05f, 0.05f })
		{
			if (cancelled?.Invoke() == true) { failure = "cancelled"; return false; }
			bool available = true;
			var unavailable = new List<string>();
			foreach (BatchItem item in items)
			{
				if (!item.Classification.SetCandidate(item.NewCutIndex, level, offset))
				{
					available = false;
					unavailable.Add(item.Label);
				}
				item.Analysis = item.Built = null;
				item.IsPrimary = false;
				item.Classification.SetClothingCores(item.NewCutIndex, null, null);
				item.Stage = "pending";
			}
			string Key(bool seeds) => string.Join("|", items.Select(item => item.Classification.FailureKey(item.NewCutIndex, seeds) + ":" + item.LodLevel + ":" + item.BuildDistal));
			string geometryKey = Key(false), woundKey = Key(true);
			bool cached = cache.TryGetValue(geometryKey, out FailedCandidate previous) || cache.TryGetValue(woundKey, out previous);
			bool succeeded = false;
			if (cached)
			{
				failure = previous.Failure;
				for (int i = 0; i < items.Length; i++) items[i].Stage = previous.Stages[i];
			}
			else if (!available)
			{
				failure = $"renderer(s) [{string.Join(", ", unavailable)}] lack {level} bind endpoints or weighted limb bones";
				foreach (BatchItem item in items) item.Stage = "eligibility";
			}
			else succeeded = TryBuildBatchCandidate(items, debug, cancelled, out failure);
			foreach (BatchItem item in items)
				item.Classification.RecordAttempt(item.NewCutIndex, item.Stage, failure, cached);
			if (succeeded) { winningOffset = offset; return true; }
			if (cancelled?.Invoke() == true) return false;
			if (!cached)
				cache.TryAdd(items.Any(item => item.Stage == "geometry-build" || item.Stage == "prepared") ? woundKey : geometryKey,
					new FailedCandidate { Failure = failure, Stages = items.Select(item => item.Stage).ToArray() });
		}
		return false;
	}

	private static bool TryBuildBatchCandidate(BatchItem[] items, bool debug, Func<bool> cancelled, out string failure)
	{
		failure = null;
		foreach (BatchItem item in items)
		{
			if (cancelled?.Invoke() == true) { failure = "cancelled"; return false; }
			item.Stage = "classification";
			long started = Stopwatch.GetTimestamp();
			bool ok = item.Classification.TryPrime(out failure);
			item.ClassificationMilliseconds += ElapsedMilliseconds(started);
			if (!ok) { failure = item.Label + ": " + item.Stage + ": " + failure; return false; }
		}
		foreach (BatchItem item in items)
		{
			if (cancelled?.Invoke() == true) { failure = "cancelled"; return false; }
			item.Stage = "boundary-analysis";
			long started = Stopwatch.GetTimestamp();
			bool ok = item.Classification.TryBuild(item.NewCutIndex, false, true, item.BuildDistal, out item.Analysis, out failure);
			item.AnalysisMilliseconds += ElapsedMilliseconds(started);
			if (!ok) { failure = item.Label + ": " + item.Stage + ": " + failure; return false; }
		}
		if (items.Sum(item => item.Analysis.NewBodyLoopCount) == 0 ||
			!items.Any(item => item.BuildDistal && item.Analysis.GibLoopCount > 0))
		{
			foreach (BatchItem item in items) item.Stage = "aggregate-boundaries";
			failure = "the anatomical cut did not produce matching closed body and gib boundaries";
			return false;
		}
		foreach (IGrouping<int, BatchItem> lod in items.GroupBy(item => item.LodLevel))
		{
			BatchItem primary = lod.OrderByDescending(item => item.Analysis.PrimaryLoopScore).First();
			primary.IsPrimary = true;
			primary.Classification.SetIncludeAnatomicalDetails(primary.NewCutIndex, true);
		}
		LimbClothingProfiles.Core sharedBody = null, sharedGib = null;
		foreach (BatchItem item in items.OrderByDescending(i => i.IsPrimary).ThenBy(i => i.LodLevel))
		{
			if (cancelled?.Invoke() == true) { failure = "cancelled"; return false; }
			item.Classification.SetClothingCores(item.NewCutIndex, sharedBody, sharedGib);
			item.Stage = "geometry-build";
			long started = Stopwatch.GetTimestamp();
			bool ok = item.Classification.TryBuild(item.NewCutIndex, debug, false, item.BuildDistal, out item.Built, out failure);
			item.BuildMilliseconds += ElapsedMilliseconds(started);
			if (!ok) { failure = item.Label + ": " + item.Stage + ": " + failure; return false; }
			if (item.IsPrimary) { sharedBody ??= item.Built.BodyClothCore; sharedGib ??= item.Built.GibClothCore; }
			item.Stage = "prepared";
		}
		return true;
	}
}
