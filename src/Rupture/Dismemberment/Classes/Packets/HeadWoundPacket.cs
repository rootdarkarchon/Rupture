using UnityEngine;

namespace Rupture.Dismemberment.Classes.Packets;

public struct HeadWoundData
{
	public int PlayerId;
	public string ProfileId;
	public uint DeathSequence;
	public uint EventSequence;
	public byte WoundOrdinal;
	public bool IsLethal;
	public Vector3 EntryLocal;
	public Vector3 DirectionLocal;
	public float EntryRadius;
	public float ExitRadius;
	public float ConeDepth;
	public uint IrregularitySeed;
}
