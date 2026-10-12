[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$failures = [System.Collections.Generic.List[string]]::new()

function Read-RepoFile([string] $relativePath) {
    $path = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required source file not found: $path"
    }
    return Get-Content -LiteralPath $path -Raw
}

function Require-Text([string] $source, [string] $needle, [string] $failure) {
    if (-not $source.Contains($needle)) {
        $failures.Add($failure)
    }
}

$kill = Read-RepoFile "src\Rupture\Combined\Patches\KillPatch.cs"
$shot = Read-RepoFile "src\Rupture\Ragdolls\Classes\ShotImpactProcessor.cs"
$system = Read-RepoFile "src\Rupture\Dismemberment\Classes\HeadWounds\HeadWoundSystem.cs"
$system += Read-RepoFile "src/Rupture/Dismemberment/Classes/RendererSource.cs"
$system += Read-RepoFile "src/Rupture/Dismemberment/Classes/HeadWounds/HeadWoundSystem.Capture.cs"
$job = Read-RepoFile "src/Rupture/Dismemberment/Classes/HeadWounds/HeadWoundGeometryJob.cs"
$job += Read-RepoFile "src/Rupture/Dismemberment/Classes/HeadWounds/HeadWoundGeometryJob.Fallback.cs"
$pipeline = $system + $job
$ballistics = Read-RepoFile "src\Rupture\Dismemberment\Classes\HeadWounds\HeadWoundBallistics.cs"
$lifetime = Read-RepoFile "src\Rupture\Dismemberment\Classes\HeadWounds\HeadWoundLifetime.cs"
$material = Read-RepoFile "src\Rupture\Dismemberment\Classes\HeadWounds\HeadWoundMaterialProvider.cs"
$headgear = (Read-RepoFile "src\Rupture\Ragdolls\Classes\LethalHeadgearDetach.cs") + (Read-RepoFile "src\Rupture\Ragdolls\Classes\CorpseHeadContext.cs") + (Read-RepoFile "src\Rupture\Ragdolls\Classes\HeadgearThrowSequence.cs")
$helmetPatch = Read-RepoFile "src\Rupture\Ragdolls\Patches\HelmetThrowPhysicsPatch.cs"
$coordinator = Read-RepoFile "src\Rupture\Ragdolls\Classes\RagdollCoordinator.cs"
$entry = Read-RepoFile "src\Rupture\RupturePlugin.cs"
$entry += Read-RepoFile "src\Rupture\RuptureConfiguration.cs"
$bleed = ''
$decalCapacity = Read-RepoFile "src\Rupture\Dismemberment\Patches\DecalCapacityPatch.cs"
$packet = Read-RepoFile "src\Rupture.Fika\Dismemberment\Classes\Packets\HeadWoundPacket.cs"
$geometry = Read-RepoFile "unity\RuptureWound\Assets\Rupture\HeadWounds\ProceduralHeadWoundGeometry.cs"
$shader = Read-RepoFile "unity\RuptureWound\Assets\Rupture\WoundInterior.shader"
$benchmark = Read-RepoFile "unity\RuptureWound\Assets\Rupture\HeadWounds\BenchmarkAndPreviewProceduralHeadWound.cs"
$fullPackage = Read-RepoFile "scripts\Build-Package.ps1"


Require-Text $kill 'DismemberForImpact(target, explosion ? damageInfo.MasterOrigin : damageInfo.HitPoint, damageInfo.Direction' "Explosion severing must use the blast origin while projectile severing retains the hit point and direction."
$headBranch = $kill.IndexOf('if (projectile && bodyPartType == EBodyPart.Head)', [StringComparison]::Ordinal)
$chanceBranch = $kill.IndexOf('bool dismembered = false;', $headBranch + 1, [StringComparison]::Ordinal)
if ($headBranch -lt 0 -or $chanceBranch -le $headBranch -or -not $kill.Substring($headBranch, $chanceBranch - $headBranch).Contains('return;')) {
    $failures.Add("Lethal projectile headshots do not bypass the legacy chance-gated whole-head cap path.")
}
Require-Text $kill 'HeadWoundSystem.RegisterLethalHeadshot' "Kill patch does not arm lethal projectile headshots."
Require-Text $shot 'HeadWoundSystem.TryCompleteLethalHeadshot' "Completed EFT.Ballistics.Shot data is not routed to the lethal descriptor."
Require-Text $kill 'ShotImpactProcessor.TryCompleteLethalHeadshotNow()' "The lethal callback does not consume the exact live bullet impact state."
foreach ($needle in @('TryCompleteHeadWound(ref pending)', 'HeadWoundCompleted', 'pending.Shot?.HitCollider == null')) {
    Require-Text $shot $needle "Live bullet head-wound completion fallback is missing: $needle"
}
Require-Text $entry 'LethalHeadgearDetach.Update()' "Lethal headgear requests are not advanced from the Unity update loop."
Require-Text $entry 'HeadWoundSystem.Update()' "Procedural wound generation is not advanced from the Unity update loop."
Require-Text $entry 'RegisterCommand("RPTHeadWoundStatus", HeadWoundSystem.LogStatus)' "The in-raid procedural head-wound readiness diagnostic is not registered."
Require-Text $entry 'LethalHeadgearDetach.Reset()' "Lethal headgear state is not reset at raid/plugin teardown."
Require-Text $entry 'HeadWoundSystem.Reset()' "Procedural wound state is not reset at raid/plugin teardown."
Require-Text (Read-RepoFile 'src/Rupture.Fika/FikaRuntime.cs') 'RegisterPacket<HeadWoundPacket>' "Fika clients do not register HeadWoundPacket."
Require-Text $helmetPatch 'LethalHeadgearDetach.TryConsume' "Canonical GameWorld.ThrowItem physics patch does not consume lethal launch requests."
Require-Text $entry 'Freeze Corpse Ragdolls For Wound Debug' "The wound-inspection ragdoll freeze toggle has no stable configuration name."
Require-Text $coordinator 'FreezeNativeCorpseRagdoll' "The wound-inspection toggle is not wired to native corpse ragdoll bodies."
Require-Text $coordinator 'ShadowRagdollWorld.Schedule(ragdoll)' "Normal localized ragdoll scheduling was removed instead of bypassed only by the debug toggle."
foreach ($needle in @('PaintVanillaBloodDecals(state.Lifetime,', 'GetBodyRenderersNonAlloc(renderers)',
	'QueueBloodDecals', 'StartPostMortemHeadWound', 'LimbDecalTextureTransfer.CopyExisting',
	'queuedBloodDecals=')) {
	Require-Text $system $needle "Resolved head wounds do not use EFT's untouched entry/exit decal path: $needle"
}
$headLifetime = Read-RepoFile "src\Rupture\Dismemberment\Classes\HeadWounds\HeadWoundLifetime.cs"
foreach ($needle in @('CanAcceptNativeDecal(skin)', 'frame < 120', 'EDecalTextureType.Blood',
	'transform.TransformPoint(entryLocal)', 'transform.TransformPoint(exitLocal)', 'PlayerMeshesHit(renderers',
	'Use EFT''s untouched painter, default decal textures and default size.')) {
	Require-Text $headLifetime $needle "Head decals do not target the visible replacement with bounded moving-corpse projection: $needle"
}
foreach ($rejected in @('EnableCharacterBloodCoating', 'Blood Coating on Characters',
	'BloodSplatterSize', 'HitSprayMax', 'UseOldBloodDecal')) {
	if ($entry.Contains($rejected)) {
		$failures.Add("Removed RPT character-paint configuration remains wired: $rejected")
	}
}
foreach ($rejected in @('PlayerMeshesHit', 'HitEffect(', 'BleedEffect(', 'EnsureFloorPainter')) {
	if ($bleed.Contains($rejected)) {
		$failures.Add("Generic per-hit RPT character blood path remains wired: $rejected")
	}
}
foreach ($rejected in @('TextureDecalsPainter', 'ConfigureCharacterTexturePainter', '_decalSize',
	'_bloodDecalTexture', '_vestDecalTexture', '_backDecalTexture')) {
	if ($decalCapacity.Contains($rejected)) {
		$failures.Add("RPT still mutates EFT's native character texture painter: $rejected")
	}
}
foreach ($rejected in @('SpawnOldVolumetricBlood', 'bloodSFX')) {
	if ($kill.Contains($rejected)) {
		$failures.Add("Legacy RPT character-overlap death volume remains wired: $rejected")
	}
}
if ($shot.Contains('BleedPatch.ProcessShot')) {
	$failures.Add("Generic projectile impacts still invoke RPT's removed per-hit blood path.")
}
if (Test-Path -LiteralPath (Join-Path $repoRoot "src\Rupture\Dismemberment\Classes\EffectContainer.cs")) {
	$failures.Add("The retired gore-cap EffectContainer still exists.")
}

foreach ($needle in @('pending.FireIndex != shot.FireIndex', 'pending.SourceId', 'HitColliderMatches', 'MaximumHitPointDifference', 'hitPlayer != pending.Player')) {
    Require-Text $system $needle "Completed bullets are not matched on all required lethal-hit identifiers: $needle"
}
foreach ($needle in @('internal static void LogStatus()', 'materialReady=', 'pendingShotMatches=', 'queuedWounds=', 'activeWounds=')) {
    Require-Text $system $needle "The in-raid head-wound diagnostic omits: $needle"
}
foreach ($needle in @('RendererSource.TryGetLodLevel(r, out int level) && level == 0', 'TryParseLodName', 'sources.Count == 0',
	'lower head LODs are intentionally excluded', '.OrderByDescending(r => r.enabled)')) {
	Require-Text $system $needle "The runtime head renderer selection does not require the readable LOD0 mesh: $needle"
}
foreach ($needle in @('bulletDiameter=', 'entryRadius=', 'entryDiameter=', 'exitRadius=', 'exitDiameter=')) {
    Require-Text $system $needle "The queued head-wound diagnostic does not label ballistic and wound dimensions explicitly: $needle"
}
foreach ($needle in @('DirectionLocal = pending.DirectionLocal', 'Vector3 launchDirection = pending.DirectionWorld',
    'MaximumVisualEntrySnapDistance = 0.015f', 'completedHitDeltaMm=', 'completedDirectionDot=', 'entrySnapMm=',
    'suppliedEntryWorld=', 'resolvedEntryWorld=', 'resolvedExitWorld=', 'ClosestPointOnTriangle',
    'no terminal surface or bounded cone-approached LOD0 geometry was found from the visual entry', 'a.Snap.CompareTo(b.Snap)',
    'furthestExitDistance', 'candidateDistance > furthestExitDistance', 'geometry intersecting that full canal',
    'TryProjectEntryAlongTrajectory', 'trajectory-extended', 'extended trajectory fallback found no LOD0 surface',
    'TryResolveConeTerminalExtent', 'terminalResolution=', 'boundaryComponents=', 'solidCanalSpans=',
    'head geometry was cancelled', 'entryResolution=')) {
    Require-Text $pipeline $needle "Lethal-hit coordinate preservation or placement diagnostics are missing: $needle"
}
if ($system.Contains('WorldToHeadRotation') -or $system.Contains('completedDirectionLocal')) {
    $failures.Add("The wound descriptor can still replace the lethal-hit direction with a later completed-projectile direction.")
}
foreach ($needle in @('BulletMassGram', 'VelocityMagnitude', 'BulletDiameterMilimeters', 'PenetrationPower', 'Caliber')) {
    if (-not ($shot.Contains($needle) -or $system.Contains($needle) -or $ballistics.Contains($needle))) {
        $failures.Add("Head-wound scaling omits ballistic input $needle.")
    }
}

foreach ($needle in @('AxialRingCount = 8', 'EntryAngularSegmentCount = 16', 'ExitAngularSegmentCount = 24',
	'TriangleBoundsOverlap', 'TriangleApproachesWoundAxis', 'TriangleIntersectsVolume', 'ApplyFilteredIndicesOnMainThread',
	'MaximumGeneratedHeadVertices = 4096', 'GetOrCreateIntersectionVertex', 'AddInterpolatedVertex',
	'ClipIntersectedTriangle', 'ClipLeafTriangle', 'TryCreateBoundaryVertex', 'EdgeRootIterations',
	'CutBoundaryEdge', 'CutBoundaryEdges', 'AddCutBoundaryEdge', 'BuildCutBoundaryComponents',
	'BoundaryVertexKey', 'GetCanonicalBoundaryVertex', 'AddCanonicalCutBoundaryEdge',
	'BridgeBoundaryGraphGaps', 'BoundaryGraphBridgeMaximum',
	'WoundBoundaryComponent', 'BoundaryComponents', 'BoundaryTraversalEdges', 'BuildComponentDrivenWound',
	'BuildCanalSpan', 'BuildIndependentBoundaryLip', 'BuildExitBurstCollarAndFlaps',
	'WoundCanalBoundaryAttachment', 'CanalBoundaryAttachments',
	'ExitFlapCount = 8', 'ExitFlapRowCount = 4',
	'ExitCollarMinimumOverlap', 'ExitCollarMaximumOverlap',
	'ExitFlapMinimumLength', 'ExitFlapMaximumLength',
	'ExitFlapMinimumThickness', 'ExitFlapMaximumThickness',
	'ExitFlapInnerVertices', 'ExitFlapOuterVertices',
	'AddExitFlapQuad', 'AddExitFlapTriangle', 'CanalRoughnessScale = 0.18f',
	'AddDisplacedInterpolatedVertex', 'AddGeneratedHeadTriangle', 'AddRimTriangleIfUsable',
	'actual / Mathf.Sqrt(areaSquared)', 'desired / Mathf.Sqrt(desiredSquared)', 'AddRimTriangle',
	'MaximumCavityVertices = 4096',
	'Retained failed spike below: whole-leaf omission plus independently sampled oversized overlap rims')) {
	Require-Text $geometry $needle "Component-driven local wound clipper contract is missing: $needle"
}
foreach ($needle in @('MaximumBackwardEntryProjectionDistance',
	'MaximumBackwardEntryProjectionDistance + MaximumForwardEntryProjectionDistance',
	'choose the visual intersection closest to the supplied hit',
	'extendedProjectionDistance', 'trajectory-extended')) {
	Require-Text $job $needle "Bidirectional lethal-trajectory entry projection is missing: $needle"
}
foreach ($needle in @('SourceBoneWeights', 'mesh.boneWeights = SourceBoneWeights.ToArray()',
	'BlendBoneWeights(a, 0.5f, b, 0.5f)', 'BlendBoneWeights(a, 1f - t, b, t)',
	'BlendBoneWeights(a, 1f / 3f, b, 1f / 3f, c, 1f / 3f)')) {
	Require-Text $geometry $needle "Generated head vertices do not preserve/interpolate skinning weights: $needle"
}
$generateStart = $geometry.IndexOf('internal static bool TryGenerate(', [StringComparison]::Ordinal)
$clipStart = $geometry.IndexOf('private static bool ClipIntersectedTriangle(', [StringComparison]::Ordinal)
if ($generateStart -lt 0 -or $clipStart -le $generateStart) {
	$failures.Add("The active procedural generation method could not be isolated for contract validation.")
} else {
	$activeGenerate = $geometry.Substring($generateStart, $clipStart - $generateStart)
	foreach ($needle in @('BuildCutBoundaryComponents(workspace, profile, out failure)',
		'BuildComponentDrivenWound(workspace, profile')) {
		Require-Text $activeGenerate $needle "The active generator does not use its retained clipped boundary: $needle"
	}
	foreach ($rejected in @('BuildBoundaryDrivenCavity(workspace, profile', 'TrySampleClippedBoundaryLoop(workspace, profile')) {
		if ($activeGenerate.Contains($rejected)) {
			$failures.Add("The active generator still uses the rejected single entry/exit reconstruction: $rejected")
		}
	}
}
foreach ($needle in @('sourceVertexCount', 'workspace.SourceVertices.Count != sourceVertexCount + metrics.GeneratedHeadVertexCount', 'procedural cavity violated',
	'locally clipped head violated its bounded source-plus-generated vertex contract',
	'component-driven canal, independent lips, or the eight thick terminal exit flaps are missing or unbounded',
	'central entry/exit channel is not open after triangle filtering')) {
    Require-Text $job $needle "Strict specialized-result validation is missing: $needle"
}
$validationIndex = $system.IndexOf('if (!job.Succeeded || job.Cancelled)', [StringComparison]::Ordinal)
$disableIndex = $system.IndexOf('headRenderer.enabled = false', [StringComparison]::Ordinal)
if ($validationIndex -lt 0 -or $disableIndex -le $validationIndex) {
    $failures.Add("The original head renderer can be disabled before specialized geometry validation succeeds.")
}
foreach ($needle in @('BakeMesh', 'TryResolveEntryExit', 'PendingWounds.Dequeue()', 'queueMs=', 'bakeMs=', 'rayMs=',
    'acquisitionMs=', 'classificationMs=', 'cavityGenerationMs=', 'meshApplicationMs=', 'managedDeltaBytes=')) {
	Require-Text $pipeline $needle "Geometry/performance pipeline is missing: $needle"
}
foreach ($needle in @('data.SourceBoneWeights.AddRange(native.boneWeights)', 'TryBuildCurrentBindposes', 'bakedHead.bindposes = state.Bindposes',
	'SkinnedMeshRenderer renderer = replacement.AddComponent<SkinnedMeshRenderer>()',
	'renderer.bones = sourceBones', 'HeadWoundSkinning.SetMesh(renderer, bakedHead, headBone)', 'skinningBones=',
	'RendererSource.CopyPropertyBlocks', 'source.GetPropertyBlock(properties)',
	'source.GetPropertyBlock(properties, materialIndex)', 'destination.SetPropertyBlock(properties)',
	'destination.SetPropertyBlock(properties, materialIndex)', 'rendererPropertyBlocks=')) {
	Require-Text $system $needle "The procedural replacement head does not retain the native skinning contract: $needle"
}
Require-Text $lifetime 'HeadWoundSkinning.SetMesh(skin, head, transform.parent)' 'Cumulative head mesh swaps must retain head-local culling bounds.'
$skinning = Read-RepoFile 'src/Rupture/Dismemberment/Classes/HeadWounds/HeadWoundSkinning.cs'
foreach ($needle in @('renderer.rootBone = headBone', 'renderer.sharedMesh = mesh', 'renderer.localBounds = mesh.bounds')) {
    Require-Text $skinning $needle "Head-local vertices and culling bounds must use the same root: $needle"
}
$replacementStart = $system.IndexOf('replacement = new GameObject("RPT Procedural Head Wound")', [StringComparison]::Ordinal)
$cavityStart = $system.IndexOf('GameObject cavityObject = new GameObject("RPT Procedural Wound Cavity")', [StringComparison]::Ordinal)
if ($replacementStart -lt 0 -or $cavityStart -le $replacementStart) {
	$failures.Add("The active replacement-head renderer block could not be isolated.")
} else {
	$replacementBlock = $system.Substring($replacementStart, $cavityStart - $replacementStart)
	if ($replacementBlock.Contains('AddComponent<MeshFilter>()') -or $replacementBlock.Contains('AddComponent<MeshRenderer>()')) {
		$failures.Add("The active replacement head can still be rendered as a rigid MeshRenderer.")
	}
}
Require-Text $lifetime 'originalRendererStates[index]' "Generated-head cleanup does not restore native renderer states."
Require-Text $lifetime 'Mesh[] _ownedMeshes' "Generated head and cavity meshes are not jointly corpse-owned."
Require-Text $lifetime 'Destroy(mesh)' "Generated meshes are not destroyed with their corpse-owned object."
foreach ($needle in @('private void LateUpdate()', 'renderer.enabled = false', 'renderer.enabled = true',
    'renderer.forceRenderingOff = false', 'Renderer[] _replacementRenderers')) {
    Require-Text $lifetime $needle "Generated-head renderer persistence is missing: $needle"
}

foreach ($slot in @('EquipmentSlot.Headwear', 'EquipmentSlot.Earpiece', 'EquipmentSlot.FaceCover', 'EquipmentSlot.Eyewear')) {
    Require-Text $headgear $slot "Lethal headgear sequence omits $slot."
}
foreach ($needle in @('WaitingItem', 'controller().ThrowItem(item, false, callback)', 'item.TotalWeight', 'PendingVelocities[item.Id]', 'request.Evidence?.Complete', 'ValidateHeadgearOwner')) {
    Require-Text $headgear $needle "Ordered canonical headgear throw contract is missing: $needle"
}
Require-Text $headgear 'TryConsume(Item item' "Lethal headgear launch requests cannot be consumed by the canonical throw patch after activation."
Require-Text $helmetPatch 'HeadgearPhysicsLifetime.Track' "Detached headgear does not receive bounded native loot cleanup."
Require-Text $helmetPatch 'Physics.IgnoreCollision' "Detached headgear no longer ignores its source corpse."

foreach ($needle in @('DeathSequence', 'EventSequence', 'EntryLocal', 'DirectionLocal', 'EntryRadius', 'ExitRadius', 'ConeDepth', 'IrregularitySeed')) {
    Require-Text $packet $needle "HeadWoundPacket omits $needle."
}
Require-Text (Read-RepoFile 'src/Rupture.Fika/FikaRuntime.cs') 'DeliveryMethod.ReliableOrdered' "Authority does not send head wounds reliably and in order."
foreach ($needle in @('PendingHeadWounds', 'RejectRetiredSequence(packet.ProfileId', 'RagdollSequencePolicy.TryAcceptEvent', 'HeadWoundSystem.Enqueue')) {
    Require-Text $coordinator $needle "Fika head-wound lifecycle contract is missing: $needle"
}
Require-Text $system 'MultiplayerRuntime.Current.IsHeadless' "Dedicated headless authorities are not excluded from local mesh generation."

foreach ($textureName in @('decal_flesh2', 'decal_flesh2_nrm', 'blood_splash_d', 'blood_splash_n')) {
    Require-Text $material $textureName "Runtime material does not require EFT texture $textureName."
}
Require-Text $material 'rupture-wound' "Runtime material does not resolve the project-owned wound shader bundle."
Require-Text $shader '#pragma surface surf Standard' "Wound shader does not use the built-in opaque Standard lighting path."
Require-Text $shader 'output.Smoothness = lerp' "Wound shader does not layer wet-region smoothness."
foreach ($needle in @('Benchmark Head Wound Cutter', 'bakeOrAcquisitionMilliseconds', 'triangleClassificationMilliseconds',
	'cavityGenerationMilliseconds', 'unityMeshApplicationMilliseconds', 'candidateTriangleCount', 'removedTriangleCount',
	'intersectedSourceTriangleCount', 'clippedTriangleCount', 'generatedHeadVertexCount',
	'GetAllocatedBytesForCurrentThread',
	'entryOpeningCoverageRadiusMillimeters', 'exitOpeningCoverageRadiusMillimeters',
	'entryRimOuterRadiusMillimeters', 'exitRimOuterRadiusMillimeters', 'minimumRimCoverageMarginMillimeters',
	'rimVertexCount', 'rimTriangleCount', 'generatedWoundVertexCount', 'generatedWoundTriangleCount',
	'minimumEntryRimFacingDot', 'minimumExitRimFacingDot',
	'entryRimSurfaceHitCount', 'exitRimSurfaceHitCount',
	'boundaryComponentCount', 'openBoundaryComponentCount', 'solidCanalSpanCount',
	'entryLipCount', 'intermediateLipCount', 'exitFlapCount',
	'Common Through-Shot', 'Common Non-Through', 'High-Vertex Oblique Through-Shot',
	'Separated Surface Crossings', 'separated-shell-crossings',
	'Cavity Only', 'Exterior Lip Only (Debug)')) {
    Require-Text $benchmark $needle "Unity specialized benchmark/preview contract is missing: $needle"
}
if ($system.Contains('CSG.Subtract') -or $system.Contains('Parabox.CSG') -or $geometry.Contains('Parabox.CSG')) {
    $failures.Add("Runtime geometry still depends on general-purpose CSG/BSP code.")
}
foreach ($package in @($fullPackage)) {
    Require-Text $package 'rupture-wound.bundle' "A package workflow omits the wound shader bundle."
    if ($package.Contains('pb_CSG')) {
        $failures.Add("A package workflow still ships the rejected pb_CSG implementation.")
    }
}
foreach ($needle in @('$OutputFileName = "Rupture-$version-$workingTreeHash.zip"', 'ls-files -z --cached --others --exclude-standard', '$outputArchive = Join-Path $artifactRoot $OutputFileName')) {
    Require-Text $fullPackage $needle "Full package output is not constrained to the repository artifacts directory: $needle"
}
if ($fullPackage.Contains('[IO.Path]::GetTempPath()') -or $fullPackage.Contains('--artifacts-path')) {
	$failures.Add("The full package build still redirects managed outputs through an extra temporary build directory.")
}
foreach ($needle in @('src/Rupture/bin/$Configuration/netstandard2.1/Rupture.dll',
    'src/Rupture.PhysXBackend/bin/$Configuration/netstandard2.1/Rupture.PhysXBackend.dll')) {
    Require-Text $fullPackage $needle "The package omits a standard managed build output: $needle"
}
if ($fullPackage -match 'SourceArchive|payloadRoot|BundleLoader|VolumetricBloodFX') {
    $failures.Add("The package still consumes legacy payload dependencies.")
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("FAIL: $failure")
    }
    exit 1
}

Write-Output "PASS: lethal headshot, Fika, lifecycle and headgear runtime entry points remain source-wired."
Write-Output "PASS: deterministic local triangle clipping, retained-cut-boundary cavity generation, strict validation and renderer transaction are present."
Write-Output "PASS: replacement heads retain/interpolate source weights and rebind to the corpse's current skinned pose."
Write-Output "PASS: RPT character paint overrides and overlap volumes are absent; successful head wounds use only EFT's untouched entry/exit decal painter."
Write-Output "PASS: replacement heads retain global and per-material EFT renderer property blocks."
Write-Output "PASS: headwear/earpiece/face-cover/eyewear throws retain EFT inventory, rigidbody and collision paths."
Write-Output "PASS: component-driven lips/spans, the generated overlap collar and eight thick closed exit flaps, the corpse-only debug freeze, Unity previews, native EFT textures, and CSG-free package inputs are wired."
