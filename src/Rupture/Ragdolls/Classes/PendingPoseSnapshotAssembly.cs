using System;
using Rupture.Ragdolls.Classes.Packets;
using UnityEngine;

namespace Rupture.Ragdolls.Classes;

// Owns one incomplete snapshot's chunks. The coordinator owns session/corpse
// admission, sequence barriers, update-cycle expiry, and publication to playback.
internal sealed class PendingPoseSnapshotAssembly
{
	internal readonly uint SnapshotSequence;
	internal readonly byte ChunkCount;
	internal readonly byte TotalPoseCount;
	internal readonly float AuthorityTime;
	internal readonly float SimulationDeltaTime;
	internal readonly int UpdateCycle;
	private int _receivedChunkCount;
	private readonly RagdollPose[][] _chunks;

	internal PendingPoseSnapshotAssembly(uint snapshotSequence, byte chunkCount, byte totalPoseCount,
		float authorityTime, float simulationDeltaTime, int updateCycle)
	{
		SnapshotSequence = snapshotSequence;
		ChunkCount = chunkCount;
		TotalPoseCount = totalPoseCount;
		AuthorityTime = authorityTime;
		SimulationDeltaTime = simulationDeltaTime;
		UpdateCycle = updateCycle;
		_chunks = new RagdollPose[chunkCount][];
	}

	internal bool Matches(RagdollPoseSnapshotData packet) =>
		ChunkCount == packet.ChunkCount && TotalPoseCount == packet.TotalPoseCount &&
		Mathf.Abs(AuthorityTime - packet.AuthorityTime) <= 0.0001f &&
		Mathf.Abs(SimulationDeltaTime - packet.SimulationDeltaTime) <= 0.0001f;

	// The caller validates the packet before touching this slot. A duplicate is
	// rejected without discarding earlier chunks from the same snapshot.
	internal bool TryAddChunk(byte index, RagdollPose[] poses)
	{
		if (_chunks[index] != null) return false;
		_chunks[index] = poses;
		_receivedChunkCount++;
		return true;
	}

	internal bool IsComplete => _receivedChunkCount == ChunkCount;

	internal bool TryBuild(int maximumBodies, out RagdollPose[] completePoses)
	{
		completePoses = null;
		int totalPoseCount = 0;
		foreach (RagdollPose[] chunk in _chunks)
		{
			if (chunk == null) return false;
			totalPoseCount += chunk.Length;
		}
		if (totalPoseCount != TotalPoseCount || totalPoseCount > maximumBodies) return false;
		completePoses = new RagdollPose[totalPoseCount];
		int destination = 0;
		foreach (RagdollPose[] chunk in _chunks)
		{
			Array.Copy(chunk, 0, completePoses, destination, chunk.Length);
			destination += chunk.Length;
		}
		return true;
	}
}
