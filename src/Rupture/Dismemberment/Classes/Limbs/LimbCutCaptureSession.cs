using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.Limbs;

// Accessed by the serial capture writer (synchronously in editor tests). A cancelled raid retains its own
// session object; it cannot populate a subsequent raid's diagnostic directory.
internal sealed class LimbCutCaptureSession
{
	internal readonly string DirectoryPath;
	private readonly Dictionary<string, int> _attempts = new Dictionary<string, int>();
	private readonly HashSet<string> _sources = new HashSet<string>();
	private int _successes, _failures;
	private long _bytes;
	private const long MaximumBytes = 128L * 1024 * 1024;

	internal LimbCutCaptureSession(string path) => DirectoryPath = path;

	internal string Record(string sourceId, Func<object> source, string group, bool success, object detail)
	{
		string id = Hash(Encoding.UTF8.GetBytes(group));
		int repeat = _attempts.TryGetValue(id, out int previous) ? previous + 1 : 1;
		_attempts[id] = repeat;
		if (repeat > 1) return $"case={id}, repeat={repeat}";
		if ((success ? _successes : _failures) >= 64 || _bytes >= MaximumBytes)
			return $"case={id}, repeat=1, capture=raid-limit";
		try
		{
			Directory.CreateDirectory(DirectoryPath);
			if (!_sources.Contains(sourceId))
			{
				Write("source-" + sourceId, source());
				_sources.Add(sourceId);
			}
			Write("case-" + id, detail);
			if (success) _successes++; else _failures++;
			return $"case={id}, repeat=1, capture='{Path.Combine(DirectoryPath, "case-" + id + ".json.gz")}'";
		}
		catch (Exception exception)
		{
			// Diagnostics must never reject otherwise valid geometry.
			return $"case={id}, repeat=1, captureError={exception.GetType().Name}: {exception.Message}";
		}
	}

	private void Write(string name, object value)
	{
		string path = Path.Combine(DirectoryPath, name + ".json.gz");
		using (FileStream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
		using (GZipStream gzip = new GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
		using (StreamWriter writer = new StreamWriter(gzip, new UTF8Encoding(false)))
		using (JsonTextWriter json = new JsonTextWriter(writer))
			JsonSerializer.CreateDefault().Serialize(json, value);
		_bytes += new FileInfo(path).Length;
	}

	internal static string Hash(byte[] bytes)
	{
		using (SHA256 algorithm = SHA256.Create())
			return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
	}
}

internal static partial class ProceduralLimbCutGeometry
{
	private static string CaptureClothingCase(LimbCutCaptureSession session, SourceData source,
		BoneSnapshot[] bones, CapturedCut[] cuts, CutBoundaryTrace trace, List<CutAttempt> attempts, string renderer, int lod,
		string bodyPart, int newCutIndex, string request, string outcome)
	{
		try
		{
			// Content identity remains stable across NPCs, raids and instance IDs.
			EnsureSourceIdentity(source);
			string anatomy = string.Join(";", cuts.Select(cut =>
				$"{cut.StartIndex}/{cut.EndIndex}/{cut.Level}/{cut.FractionOffset:R}/{cut.WorldScale:R}/{string.Join(",", cut.LimbBones)}" +
				BindFrameKey(cut.StartBind) + BindFrameKey(cut.EndBind)));
			string palette = string.Join(";", bones.Select(bone => $"{bone.Name}/{bone.ParentIndex}"));
			string group = $"v2:{source.DiagnosticIdentity}:{palette}:{lod}:{bodyPart}:{newCutIndex}:{anatomy}:{outcome}";
			string capture = session == null ? "capture=disabled" : session.Record(source.DiagnosticIdentity,
				() => CaptureSource(source), group, outcome == "prepared", new
				{
					Schema = 2, Source = "source-" + source.DiagnosticIdentity + ".json.gz", Renderer = renderer,
					Lod = lod, BodyPart = bodyPart, Request = request, Outcome = outcome, NewCutIndex = newCutIndex,
					Bones = bones.Select(bone => new { bone.Name, bone.ParentIndex, Position = Point(bone.BindPosition) }).ToArray(),
					Cuts = cuts.Select(cut =>
					{
						BindCut classified = ReadCapturedBindCut(source, cut.CacheKey);
						return new
						{
							cut.StartIndex, cut.EndIndex, cut.LimbBones, Level = cut.Level.ToString(), cut.Seed, cut.IsLeg,
							StartPosition = cut.StartBind.HasValue ? Point(cut.StartBind.Value.inverse.MultiplyPoint3x4(Vector3.zero)) : null,
							EndPosition = cut.EndBind.HasValue ? Point(cut.EndBind.Value.inverse.MultiplyPoint3x4(Vector3.zero)) : null,
							cut.WorldScale, cut.IncludeAnatomicalDetails, cut.FractionOffset,
							Center = classified == null ? null : Point(classified.Center),
							Axis = classified == null ? null : Point(classified.Axis),
							Radius = classified?.CapsuleRadius, Gap = classified?.DistalGap,
							Selected = classified?.SelectedTriangles.OrderBy(value => value).ToArray(),
							Candidate = classified?.Classification.Candidate,
							TargetWeight = classified?.Classification.LimbWeight,
							SeedWeight = classified?.Classification.SeedWeight,
							Protected = classified?.Classification.ProtectedByOtherBones
						};
					}).ToArray(),
					Attempts = attempts,
					Boundaries = trace?.Sides, Clothing = trace?.Clothing
				});
			int selected = 0, protectedCount = 0, candidates = 0;
			BindCut latest = newCutIndex >= 0 && newCutIndex < cuts.Length ? ReadCapturedBindCut(source, cuts[newCutIndex].CacheKey) : null;
			if (latest != null)
			{
				selected = latest.SelectedTriangles.Count;
				candidates = latest.Classification.Candidate.Count(value => value);
				protectedCount = latest.Classification.ProtectedByOtherBones.Count(value => value);
			}
			return $"renderer='{renderer}', mesh='{source.Name}', meshId={source.DiagnosticIdentity}, lod={lod}, " +
				$"part={bodyPart}, level={cuts[newCutIndex].Level}, seed={cuts[newCutIndex].Seed}, request='{request}', " +
				$"sourceTriangles={source.Triangles.Count}, candidates={candidates}, selected={selected}, protected={protectedCount}, " +
				$"selectionEdges={trace?.Sides.Sum(side => side.SelectionEdges.Count) ?? 0}, cloth=[{string.Join(";", trace?.Clothing.Select(f => f.Face + ":" + f.Status + (f.Reason == null ? "" : ":" + f.Reason)) ?? System.Array.Empty<string>())}], outcome='{outcome}', {capture}";
		}
		catch (Exception exception)
		{
			return $"renderer='{renderer}', lod={lod}, outcome='{outcome}', diagnosticError={exception.GetType().Name}: {exception.Message}";
		}
	}

	private static BindCut ReadCapturedBindCut(SourceData source, string key)
	{
		// A later cut can populate the shared cache while diagnostics serialize.
		// Hold its lock only for lookup; BindCut and source arrays are immutable.
		lock (source.CacheGate) return source.BindCutCache.TryGetValue(key, out BindCut cut) ? cut : null;
	}

	private static object CaptureSource(SourceData source) => new
	{
		Schema = 2, source.Name, source.SubmeshCount,
		Positions = source.Positions.Select(Point).ToArray(), Normals = source.Normals.Select(Point).ToArray(),
		Tangents = source.Tangents.Select(v => new[] { v.x, v.y, v.z, v.w }).ToArray(),
		Uv0 = source.Uv0.Select(v => new[] { v.x, v.y }).ToArray(),
		Uv1 = source.Uv1.Select(v => new[] { v.x, v.y }).ToArray(),
		Uv2 = source.Uv2.Select(v => new[] { v.x, v.y }).ToArray(),
		Uv3 = source.Uv3.Select(v => new[] { v.x, v.y }).ToArray(),
		Colors = source.Colors.Select(v => new[] { v.r, v.g, v.b, v.a }).ToArray(),
		Triangles = source.Triangles.Select(t => new[] { t.A, t.B, t.C, t.Submesh }).ToArray(),
		Weights = source.Weights.Select(w => new
		{
			Bones = new[] { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3 },
			Values = new[] { w.weight0, w.weight1, w.weight2, w.weight3 }
		}).ToArray(),
		Bindposes = source.Bindposes.Select(pose => Enumerable.Range(0, 16).Select(i => pose[i]).ToArray()).ToArray(),
		BlendShapes = source.BlendShapes.Select(frame => new
		{
			frame.Name, frame.Weight, Positions = frame.DeltaVertices.Select(Point).ToArray(),
			Normals = frame.DeltaNormals.Select(Point).ToArray(), Tangents = frame.DeltaTangents.Select(Point).ToArray()
		}).ToArray()
	};
}
