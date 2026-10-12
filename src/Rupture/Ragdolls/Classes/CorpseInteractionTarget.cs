using EFT;
using EFT.Interactive;
using UnityEngine;

namespace Rupture.Ragdolls.Classes;

internal readonly struct CorpseInteractionTarget
{
	internal readonly CorpseLimbContext Limbs;
	internal readonly CorpseHeadContext Head;
	internal readonly Corpse Corpse;
	internal readonly Player Player;
	internal readonly PlayerBody PlayerBody;
	internal readonly Transform VisualRoot;
	internal readonly string ProfileId;
	internal readonly int PlayerId;
	internal readonly uint DeathSequence;
	internal readonly bool IsYourPlayer;

	internal CorpseInteractionTarget(Corpse corpse, Player player, PlayerBody playerBody, Transform visualRoot,
		string profileId, int playerId, uint deathSequence, bool isYourPlayer, CorpseHeadContext head = null, CorpseLimbContext limbs = null)
	{
		Limbs = limbs ?? CorpseLimbContext.Capture(player, playerBody);
		Head = head?.Bone != null ? head : CorpseHeadContext.Capture(player, playerBody);
		Head?.BindIdentity(profileId, playerId, deathSequence);
		Corpse = corpse;
		Player = player;
		PlayerBody = playerBody;
		VisualRoot = visualRoot;
		ProfileId = profileId;
		PlayerId = playerId;
		DeathSequence = deathSequence;
		IsYourPlayer = isYourPlayer;
	}
}

