$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
function Read-Rpt([string]$path) { Get-Content -LiteralPath (Join-Path $repo $path) -Raw }
function Require-Rpt([bool]$condition, [string]$message) { if (!$condition) { throw $message }; Write-Output "PASS: $message" }
$hooks = (Read-Rpt 'src/Rupture/Impacts/ImpactHooks.cs') + (Read-Rpt 'src/Rupture.Fika/Impacts/FikaImpactHooks.cs')
$state = Read-Rpt 'src/Rupture/Impacts/ImpactStateService.cs'
$network = Read-Rpt 'src/Rupture.Fika/Impacts/FikaImpactNetwork.cs'
$kill = Read-Rpt 'src/Rupture/Combined/Patches/KillPatch.cs'
$head = Read-Rpt 'src/Rupture/Dismemberment/Classes/HeadWounds/HeadWoundSystem.cs'
$sample = Read-Rpt 'src/Rupture/Impacts/ImpactSample.cs'
$helmet = Read-Rpt 'src/Rupture/Ragdolls/Patches/HelmetThrowPhysicsPatch.cs'
$coordinator = Read-Rpt 'src/Rupture/Ragdolls/Classes/RagdollCoordinator.cs'
Require-Rpt ($hooks.Contains('nameof(ActiveHealthController.ApplyDamage)') -and $hooks.Contains('float __result') -and $state.Contains('Positive(appliedDamage)')) 'only confirmed health loss contributes momentum'
Require-Rpt ($hooks.Contains('typeof(FikaClientGameWorld)') -and $hooks.Contains('finalizer: Method(nameof(ShotFinally))') -and $hooks.Contains('Current = __state')) 'individual collisions scope and restore the impact context, including exception paths'
Require-Rpt ($sample.Contains('shot.VelocityMagnitude') -and !$sample.Contains('InitialSpeed') -and $sample.Contains('FragmentationHit') -and $sample.Contains('root.RandomSeed')) 'incoming speed and fragment identities are captured without muzzle approximations'
Require-Rpt ($state.Contains('!damage.Blunt') -and $state.Contains('!damage.OverDamageFrom.HasValue') -and $kill.Contains('damageInfo.OverDamageFrom.HasValue')) 'armor-only and redistributed callbacks cannot add buildup or choose a lethal limb'
Require-Rpt ($state.Contains('IsRemoteClient && !player.IsYourPlayer') -and $state.Contains('if (player.IsYourPlayer) ImpactNetwork.SendDeath')) 'living accumulation and death handoff remain victim-owned'
Require-Rpt ($network.Contains('state.Limbs[0].Import') -and $network.Contains('state.Limbs[3].Import') -and $network.Contains('Remember(packet.Sample.Projectile)') -and $network.Contains('ApplyDeath(player, state, damage, packet.Part')) 'death imports every pool but evaluates only the lethal limb without adding the lethal hit twice'
Require-Rpt ($state.Contains('if (state.AwaitingDeathSnapshot) return false') -and $network.Contains('packet.DeathSequence != corpse.DeathSequence') -and $state.Contains('RetainedCorpse')) 'early corpse impacts wait for death state and retain identity after Player retirement'
Require-Rpt ($kill.Contains('BeginSever') -and $kill.Contains('ImpactStateService.Complete') -and $kill.Contains('SeverCompletionStatus.Committed')) 'sever reservation, completion and broadcast follow the cutter transaction'
Require-Rpt ($network.Contains('Pending.Count >= 128') -and $network.Contains('+ 10f') -and $network.Contains('ResetRaid() { Pending.Clear()') -and $coordinator.Contains('ImpactNetwork.ResetRaid()')) 'reconciliation is bounded and cleared at raid teardown'
Require-Rpt ($network.Contains('peer.Disconnect') -and $network.Contains('Compatible(packet.Protocol, packet.Version)') -and $network.Contains('handshake timed out')) 'protocol mismatches and absent peers are explicitly rejected'
$complete = $head.Substring($head.IndexOf('internal static void CompleteImpact'), $head.IndexOf('private static bool CompleteMatched') - $head.IndexOf('internal static void CompleteImpact'))
Require-Rpt ($complete.IndexOf('LethalHeadgearDetach.Queue') -lt $complete.IndexOf('if (!generateWound') -and !$helmet.Contains('Random.Range') -and !$helmet.Contains('Random.value')) 'lethal equipment detachment precedes wound rendering and has no random nonlethal throw path'
Require-Rpt ($state.Contains('part == EBodyPart.Head && HeadWoundSystem.IsAwaitingLethalHeadshot(target)') -and $state.Contains('HeadWoundSystem.CompleteCorpseImpact')) 'post-mortem heads wait for the lethal matcher and then use their own wound route'
Require-Rpt ($state.Contains('InverseTransformPoint(point)') -and $state.Contains('InverseTransformDirection(direction)') -and $state.Contains('shot.PenetrationPower')) 'corpse head collision coordinates and penetration are captured before network delay'
$corpseHead = $head.Substring($head.IndexOf('internal static void CompleteCorpseImpact'), $head.IndexOf('internal static void Enqueue') - $head.IndexOf('internal static void CompleteCorpseImpact'))
Require-Rpt ($corpseHead.IndexOf('LethalHeadgearDetach.Queue') -lt $corpseHead.IndexOf('EnableDismemberment') -and $corpseHead.IndexOf('LethalHeadgearDetach.Queue') -lt $corpseHead.IndexOf('Policy.Reserve')) 'corpse headgear removal remains independent of rendering and the four-wound cap'
Require-Rpt ($head.Contains('if (work.Packet.IsLethal) PulsatingBloodStreamSystem.ResolveHeadExit') -and $head.Contains('if (work.Packet.IsLethal) PulsatingBloodStreamSystem.RejectHeadExit')) 'post-mortem wound application and failure cannot restart the lethal blood stream'
$retention = Read-Rpt 'src/Rupture/Ragdolls/Classes/CorpseHeadContext.cs'
Require-Rpt ($retention.Contains('class CorpseHeadRetention : MonoBehaviour') -and $retention.Contains('private void OnDestroy()') -and
    $retention.Contains('HeadWoundSystem.Remove') -and $retention.Contains('var existing = Find(body)') -and
    $coordinator.Contains('CorpseHeadContext.Capture(state.Player, state.PlayerBody)') -and
    !$coordinator.Contains('HeadWoundSystem.Remove(state.ProfileId')) 'settled physics retirement preserves wounds and admission until the retained skeleton is destroyed'
Write-Output 'Source-wiring checks are not live multiplayer execution tests.'
