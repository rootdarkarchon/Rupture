using UnityEngine;
using Rupture.Dismemberment.Classes.Limbs;

namespace Rupture.Dismemberment.Classes.Packets;

public struct DismembermentData
{
	public int PlayerId;
	public uint DeathSequence;
	public uint EventSequence;
	public Vector3 Direction;
	public EBodyPart BodyPart;
	public LimbSeverLevel SeverLevel;
	public uint IrregularitySeed;
	public bool InitialDeathSpawn;
}
