using Rupture.Dismemberment.Classes.Limbs;
using UnityEngine;

namespace Rupture.Health;

internal enum BotFallPhase : byte { Prone = 0, Falling = 1, Blending = 2 }

public struct BotSeverData
{
    public string ProfileId;
    public int PlayerId;
    public uint Revision, PoseSequence;
    public byte SeveredMask, TreatedMask, Phase;
    public float Remaining;
    public Vector3 RootPosition;
    public Quaternion RootRotation;
    public LimbSeverLevel LeftLevel, RightLevel;
    public uint LeftEvent, RightEvent, LeftSeed, RightSeed;
    public Vector3 LeftDirection, RightDirection;
    public string[] BonePaths;
    public Vector3[] Positions;
    public Quaternion[] Rotations;
}
