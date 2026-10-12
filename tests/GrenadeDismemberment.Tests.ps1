[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
function Read-Repo([string]$relative) { Get-Content -LiteralPath (Join-Path $repoRoot $relative) -Raw }
$kill = Read-Repo "src\Rupture\Combined\Patches\KillPatch.cs"
$limbs = Read-Repo "src\Rupture\Dismemberment\Classes\Limbs\LimbDismembermentSystem.cs"
$limbs += Read-Repo "src/Rupture/Dismemberment/Classes/Limbs/LimbDismembermentSystem.Worker.cs"
$limbs += Read-Repo "src/Rupture/Dismemberment/Classes/ManagedGeometryWorker.cs"
$limbs += Read-Repo "src/Rupture/Dismemberment/Classes/Limbs/LimbDismembermentSystem.Decals.cs"
$limbs += Get-Content -LiteralPath (Join-Path $repoRoot "src/Rupture/Dismemberment/Classes/Limbs/LimbCutLifetime.cs") -Raw
$math = Read-Repo "unity\RuptureWound\Assets\Rupture\Limbs\LimbSeverLevel.cs"
$geometry = Read-Repo "src\Rupture\Dismemberment\Classes\Limbs\ProceduralLimbCutGeometry.cs"
$batch = Read-Repo "src\Rupture\Dismemberment\Classes\Limbs\ProceduralLimbCutBatch.cs"
$wound = Read-Repo "unity\RuptureWound\Assets\Rupture\Limbs\ProceduralLimbWoundGeometry.cs"
$woundShader = Read-Repo "unity\RuptureWound\Assets\Rupture\LimbSeverSurface.shader"
$woundProvider = Read-Repo "src\Rupture\Dismemberment\Classes\Limbs\LimbSeverMaterialProvider.cs"
$decalTransfer = Read-Repo "src\Rupture\Dismemberment\Classes\Limbs\LimbDecalTextureTransfer.cs"
$decalDiagnostics = Read-Repo "src\Rupture\Dismemberment\Classes\Limbs\LimbDecalDiagnostics.cs"
$bundleBuild = Read-Repo "unity\RuptureWound\Assets\Editor\BuildRuptureWoundBundle.cs"
$packet = Read-Repo "src\Rupture.Fika\Dismemberment\Classes\Packets\DismembermentPacket.cs"
$shadow = Read-Repo "src\Rupture\Ragdolls\Classes\ShadowRagdollWorld.cs"
$shot = Read-Repo "src\Rupture\Ragdolls\Classes\ShotImpactProcessor.cs"
$contracts = Read-Repo "src\Rupture\Ragdolls\External\ExternalPhysicsContracts.cs"
$coordinator = Read-Repo "src\Rupture\Ragdolls\Classes\RagdollCoordinator.cs"
$streams = Read-Repo "src\Rupture\Dismemberment\Classes\BloodStreams\PulsatingBloodStreamSystem.cs"
$entry = Read-Repo "src\Rupture\RupturePlugin.cs"
$entry += Read-Repo "src\Rupture\RuptureConfiguration.cs"
$failures = [System.Collections.Generic.List[string]]::new()
function Need([string]$source,[string]$needle,[string]$message) { if(-not $source.Contains($needle)){$failures.Add($message)} }

Need (Read-Repo 'src/Rupture/Impacts/DeathImpactPolicy.cs') 'EDamageType.GrenadeFragment' "Grenade fragments do not use the explosion sever path."
Need $kill 'DeathImpactPolicy.HasSpatialExplosion(damageInfo, ImpactStateService.Current)' "Lethal explosion effects must require a known blast origin."
foreach($part in @('LeftLeg','RightLeg','LeftArm','RightArm')) { Need $kill "TrySeverExplosionLimb(target, EBodyPart.$part" "Explosion sever roll is missing $part." }
foreach($needle in @('explosion ? damageInfo.MasterOrigin : damageInfo.HitPoint','TryGetExplosionDistance','ExplosionSeverPolicy.Select','ExplosionSeverPolicy.Level(UnityEngine.Random.value)','forcedLevel: level','AwaitingDeathSnapshot')) { Need $kill $needle "Distance-based authoritative explosion selection is missing: $needle" }
if($kill.Contains('Random.Range(0, 3)')) { $failures.Add('Flat explosion limb lottery remains.') }
Need $kill 'if (bodyPart == EBodyPart.Head || !ImpactStateService.BeginSever' "Post-mortem head severing is not rejected."
Need $kill 'Whole-head dismemberment is disabled' "The temporary whole-head rejection is not documented at the lethal branch."
foreach($needle in @('ProximalSegmentFraction = 0.50f','DistalSegmentFraction = 0.15f','proximalDistance < distalDistance','LimbSeverLevel.Distal')) { Need $math $needle "Two-level closest-cut math is missing: $needle" }
foreach($needle in @('TryBuild(','ClassifyTriangles(','BuildAnatomicalMask(','MaximumWeakBridgeTriangles','BuildTriangleAdjacency(','BuildCanonicalPositionMap(','BuildCapsuleSegments(','StrongSeedWeight','MissingBandWidthWorld = ProceduralLimbWoundGeometry.MissingBandWorld','bodyPlane = selected.Center','gibPlane = selected.Center + selected.Axis * selected.DistalGap','EdgeKey','builder.Crossing(','TryOrderClosedLoops(','TryWeldBoundarySegments(','maximumOpenClosure','if (a == b) continue;','FindOrCreateCanonicalBoundaryVertex(','BodySocket','AddWound(','FindPrimaryLoop(','CopyExactOuterLoop(','DisturbBoundaryLoop(','BuildDebugOverlay(')) { Need $geometry $needle "Bind-space indexed procedural limb cutting is missing: $needle" }
foreach($needle in @('MissingBandWorld = 0.10f','MaximumAdornmentReachWorld = 0.042f','RimCurveControlCount = 7','RimDisturbanceRadiusScale = 0.25f','MinimumArmRimDisturbanceWorld = 0.008f','MaximumArmRimDisturbanceWorld = 0.018f','MinimumLegRimDisturbanceWorld = 0.010f','MaximumLegRimDisturbanceWorld = 0.024f','DisturbBoundary(','ExactSeam','AddAnatomicalDetails','BuildSharedBoneRoots','ResolveBoneRadiiWorld(','anatomicalScale = Mathf.Lerp(0.85f, 1.15f','new Vector2(0.0145f, 0.0130f)','new Vector2(0.0100f, 0.0090f)','new Vector2(0.0135f, 0.0100f)','new Vector2(0.0070f, 0.0070f)','new Vector2(0.0080f, 0.0055f)','new Vector2(0.0075f, 0.0055f)','EstimateMoundSurfaceHeight(','MinimumAdornmentEmbedWorld','MinimumBoneEmbedWorld','MinimumBoneExposedReachWorld','AddBone(','AddFlap(','AddCord(','BuildDecalSamples(','OnRetainedTriangle','MeshVertexC','MeshBarycentric','DecorationsClosed','MeanPlateauProtrusionWorld','MinimumBoneFractureSpanWorld','EmissionAnchor','StreamAnchor','Vector3.Lerp(center, plateauCenter, 0.58f)')) { Need $wound $needle "Procedural fleshy wound generation is missing: $needle" }
foreach($needle in @('Rupture/LimbSeverSurface','IN.color.r','IN.color.g','IN.color.b','IN.color.a','_VariationOffset','uvA','uvB','uvC')) { Need $woundShader $needle "Dedicated masked limb-sever shader is missing: $needle" }
foreach($needle in @('LimbSeverMaterialProvider.TryGet','ApplyVariation','MaterialPropertyBlock','_VariationOffset')) { Need ($limbs + $woundProvider) $needle "Dedicated shared limb-sever material/property-block path is missing: $needle" }
foreach($needle in @('LimbSeverSurface.shader','ClothingSoak.shader','ClothingSoak.compute','assetNames.Length != 5','bundledShaders.Length != 4','hasTransport')) { Need $bundleBuild $needle "Wound-bundle shader/compute validation is missing: $needle" }
Need $entry 'Procedural Limb Cut Overlay' "The opt-in procedural limb classification overlay setting is missing."
Need $limbs 'selectedConnectivityRed' "The procedural limb overlay log does not expose connectivity-only selected triangles."
foreach($rejected in @('TrySlice(','AddFleshyCap(','MinimumLimbWeight = 0.55f','CandidateLimbWeight','SetCutDistance')) { if($geometry.Contains($rejected)){$failures.Add("The replaced pose-space/threshold cutter remains: $rejected")} }
foreach($needle in @('LeftUpperArm','LeftForearm','RightUpperArm','RightForearm','LeftThigh','LeftCalf','RightThigh','RightCalf')) { Need $coordinator $needle "Collider removal mapping is missing: $needle" }
foreach($needle in @('SeverLevel','IrregularitySeed','ReliableOrdered')) { Need ($packet + $kill + (Read-Repo 'src/Rupture.Fika/FikaRuntime.cs')) $needle "Compact reliable sever replication is missing: $needle" }
foreach($rejected in @('capAssetName','assetNames','gorecaps')) { if($packet.Contains($rejected) -or $kill.Contains($rejected) -or $limbs.Contains($rejected)){$failures.Add("Legacy asset-based dismemberment remains: $rejected")} }
Need $contracts 'EnvironmentOnly = 1' "External environment-only collision profile is missing."
foreach($needle in @('RegisterLocalDetachedGib','NetworkPoseEligible = !localEnvironmentOnly','ConfigureLocalGibQueryCollider','collider.excludeLayers = new LayerMask { value = ~0 }','ExternalCollisionProfile.EnvironmentOnly')) { Need $shadow $needle "Local shadow-only gib ownership is missing: $needle" }
foreach($needle in @('TryCreateGeneratedLimbMeshColliders','MeshCollider','meshCollider.convex = true','RPT_GeneratedLimb','mesh-derived gib compound bounds are implausible','ResetCenterOfMass','ResetInertiaTensor')) { Need $limbs $needle "Mesh-derived generated gib collider validation is missing: $needle" }
foreach($needle in @('OriginalSubmeshCount','exteriorSubmeshCount','submesh < exteriorSubmeshCount')) { Need $limbs $needle "Decorative wound submeshes can still leak into gib collider cooking: $needle" }
foreach($needle in @('ImmutableMesh','state.Cuts','TryBuildBatchWithRetries','CreateSkinnedRenderer','CopyBlendShapeWeights','MoveBakedMeshIntoGibFrame','_RPT_GibBakedOnce')) { Need ($limbs + $geometry) $needle "Immutable source rebuild / one-time posed gib bake is missing: $needle" }
foreach($needle in @('BuildDistal = sourceState.LodLevel == 0','request.Sever.PrimarySources.Add','TryBuildBatchWithRetries')) { Need $limbs $needle "Per-LOD worker transaction is missing: $needle" }
foreach($needle in @('items.GroupBy(item => item.LodLevel)','OrderByDescending(item => item.Analysis.PrimaryLoopScore)','false, true, item.BuildDistal, out item.Analysis','debug, false, item.BuildDistal, out item.Built','FailedCandidates','0f, -0.05f, 0.05f','requested, alternate')) { Need $batch $needle "Shared per-LOD analysis/build and retry transaction is missing: $needle" }
foreach ($needle in @('item.Classification.TryPrime(out failure)', 'item.Classification.TryBuild(item.NewCutIndex', 'primary.Classification.SetIncludeAnatomicalDetails')) {
    Need $batch $needle "Captured classification work must be primed and built through the production batch: $needle"
}
Need $limbs 'request.Level = request.Sever.Level = request.GeometryJob.Work[0].Built.EffectiveLevel' 'Fallback level does not propagate to collider ownership and completion.'
Need $limbs 'request.Sever.FractionOffset = request.GeometryJob.WinningOffset' 'Successful offset is not retained for subsequent source rebuilds.'
Need $limbs 'ProceduralLimbCutGeometry.IsSourceReadable(immutable' 'Unreadable sources are not classified before weight reads and retries.'

foreach($needle in @('BodyDecalSamples','GibLocalDecalSamples','TryTransformBakedSample','PaintNativeBloodDeferred','yield return null','UseDecalPainter','NativeBloodTargets','GetBodyRenderersNonAlloc','GibBloodTargets','CountEligibleNativeDecalRenderers','ProjectionStartOffset','point + normal * ProjectionStartOffset, -normal','bodySubmitted >= 10','gibSubmitted >= 10','bodyLodTargets=','bodySubmissions=','gibSubmissions=','inheritedGibTextures=','visibilityTimedOut=')) { Need $limbs $needle "LOD-consistent retained-surface body/gib decal rings are missing: $needle" }
foreach($needle in @('Dictionary<Renderer, RenderTexture>','CopyExisting','RebindExisting','CanAcceptNativeDecal','sourceTexture.descriptor','Graphics.Blit','_DecalsTex','_DecalPower','field.FieldType == expected')) { Need $decalTransfer $needle "Existing EFT decal textures are not preserved across the body/gib renderer transition: $needle" }
foreach($needle in @('CaptureLimbDecalAudits','CaptureBeforeCommit','LogLimbDecalAuditsAfterPaint','WaitForEndOfFrame','queueAfterDrain','sourceBeforeToBodyAfter','inheritedGibBeforeToGibAfter')) { Need $limbs $needle "The debug cut path does not run the one-shot pre/post decal audit: $needle" }
foreach($needle in @('List<TextureDecalsPainter.DecalDescription>','TryGetRegisteredTexture','RenderTexture.active','previousActive','GetPixels32','ExtractUvs','MeshBarycentric','sampleSubmeshes:','eftDrawRendererDefaultSubmesh:0','hash=','rgbaSum=','bodyRegion=','gibRegion=','DescribeBinding','dictionaryRt:','globalBlock:','immediatelyAfterBodySubmit','bodyQueueDelta=','gibQueueDelta=','sameRt:','sameHash:','ClassifySide','GPU-texture-changed-globally-but-seam-UV-region-remained-empty')) { Need ($decalDiagnostics + $limbs) $needle "The decal audit cannot distinguish queue, UV, GPU texture, or material-binding failures: $needle" }
foreach($needle in @('CaptureMeshLayout','GetVertexAttributes','GetVertexBufferStride','GetUVs(channel, values)','GetBonesPerVertex','GetAllBoneWeights','new LimbMeshUvLayoutSnapshot[8]','attributesExact:','immutableToCurrent','immutableToPreparedBody','immutableToBakedGib','RPT limb decal audit mesh layout immutable:','RPT limb decal audit mesh layout prepared-body:','RPT limb decal audit mesh layout baked-gib:')) { Need ($decalDiagnostics + $limbs) $needle "The decal audit does not compare the exact source, prepared-body, and baked-gib vertex layouts: $needle" }
foreach($needle in @('_requiresGeneratedSurfaceAttributes || SourceHasUv(_source.Uv0)','HasUv1 = SourceHasUv(_source.Uv1)','HasUv2 = SourceHasUv(_source.Uv2)','HasUv3 = SourceHasUv(_source.Uv3)','_requiresGeneratedSurfaceAttributes || SourceHasColors','requiresGeneratedSurfaceAttributes: true')) { Need $geometry $needle "Generated limb meshes do not preserve absent EFT vertex channels: $needle" }
Need $limbs 'item.GibRenderer?.GetComponent<MeshFilter>()?.sharedMesh' "The decal layout audit does not inspect the renderer-owned baked gib mesh."
Need $entry 'one-shot EFT decal audit' "The expensive decal readback diagnostic is not disclosed on the opt-in debug setting."
if(-not ($limbs -match 'if \(DebugOverlayEnabled\) CaptureLimbDecalAudits')) { $failures.Add('The decal audit is not gated by the existing opt-in limb debug setting.') }
if(-not ($decalDiagnostics -match 'finally[\s\S]{0,180}RenderTexture\.active = previousActive')) { $failures.Add('The debug GPU decal readback does not restore RenderTexture.active in a finally block.') }
if($limbs.Contains('PaintNativeBlood(prepared)')) { $failures.Add('The native sever ring is still submitted before a new gib can satisfy EFT renderer visibility.') }
foreach($needle in @('TryMoveDecalsOntoRetainedTriangles','TryFindRetainedTriangle','interiorWeight = 0.22f','BlendWeights(','retainedSubmeshCount')) { Need $geometry $needle "Decal samples are not derived from retained triangles adjacent to the seam: $needle" }
Need $limbs 'RendererSupportsEitherCut(source, newCut)' "Partial renderer bone palettes can still reject otherwise valid limb cuts."
foreach($needle in @('RendererSource.TryGetLodLevel(renderer, out int level) ? Mathf.Max(0, level) : 0','TryEnsureImmutableSource','BuildDistal = sourceState.LodLevel == 0','IncludeInGib = work.BuildDistal','source.Native.sharedMesh = item.BodyMesh','ReplaceLodAttachments','CloneLods')) { Need $limbs $needle "LOD-aware native renderer mesh replacement is missing: $needle" }
if($limbs.Contains('IsLodZeroRenderer(source)')) { $failures.Add('Lower body LODs are still excluded from procedural limb cutting.') }
if($limbs.Contains('state.Lifetime.Disable(source.Native)')) { $failures.Add('Cut renderers are still disabled instead of retaining their EFT decal/LOD identity.') }
if($limbs -match 'foreach\s*\(Renderer native in state\.NativeRenderers\)[\s\S]{0,260}Lifetime\.Disable') { $failures.Add('Commit still broadly hides weighted native renderers that have no prepared replacement.') }
if(([regex]::Matches($limbs,'\.DistalRenderer\.BakeMesh\(')).Count -ne 1) { $failures.Add('Detached procedural gibs must have exactly one SkinnedMeshRenderer BakeMesh call site.') }
if($limbs.Contains('TryCloneCollider(')) { $failures.Add('Generated gibs still clone pre-cut native limb colliders instead of fitting isolated geometry.') }
foreach($needle in @('LocalGibPendingLifetime = 10f','LocalGibMaximumInitialLaunchSpeed = 2.75f','LocalGibMinimumAngularSpeed = 0.35f','LocalGibMaximumAngularSpeed = 1.5f','LocalGibMaximumDepenetrationSpeed = 8f','simulationBody.velocity = launchVelocity','external gib pose diverged during capture')) { Need $shadow $needle "Local gib spawn/launch validation is missing: $needle" }
foreach($needle in @('TryApplyLocalDetachedGibPointImpulse','ParkSeveredBody','RemoveSeveredBodyJoints','ParkedAnchor','PermanentlyDisabledBodies.Contains')) { Need ($shadow + $shot) $needle "Local gib bullet interaction or severed-body parking is missing: $needle" }
foreach($needle in @('ParkLocalDetachedEffectShell','EnableLocalDetachedEffectShapesForCapture','body.isKinematic = true','collider.enabled = false')) { Need $shadow $needle "Pending local gib EFT-physics isolation is missing: $needle" }
if($shadow -match 'if\s*\(detached\.LocalEnvironmentOnly\)\s*\{\s*DestroyObject\(detached\.Root\)') { $failures.Add('Presentation sync still destroys local environment-only gibs.') }
foreach($needle in @('PendingColliderDisables','ApplyPendingColliderDisable(rig)','ApplyPendingColliderDisableToNative','PermanentlyDisabledBodies')) { Need $shadow $needle "Pre-build sever collider handoff is missing: $needle" }
Need $coordinator 'if (ShadowRagdollWorld.DisableCorpseColliders(target.Corpse, target.ProfileId, colliders)) return;' "Native limb colliders are disabled before the shadow rig can capture them."
foreach($needle in @('StartProceduralDismembermentStreams','float gibDuration = bodyDuration * 0.5f','-axis')) { Need $streams $needle "Body/gib stump stream pairing is missing: $needle" }
foreach($needle in @('ResolveProceduralStreamAnchors','request.GibRoot.transform.TransformPoint(gibStreamPointLocal)','cutPoint + axis * ProceduralLimbCutGeometry.MissingBandWidthWorld','TrySkinWoundAnchor')) { Need $limbs $needle "Generated body/gib stream origins are not resolved inside their wound caps: $needle" }
foreach($needle in @('RequestSever','AlreadyPendingOrSevered','Queue<PendingSever>','Reservations','SeverStage.Prepare','SeverStage.Materialize','SeverStage.BakeAndColliders','SeverStage.WaitForUnityColliderBake','SeverStage.RegisterAndCommit','LimbDismembermentSystem.Update()','Procedural Limb Cut Main Thread Budget (ms)','No additional cut stage runs this frame','completion.Status == LimbDismembermentSystem.SeverCompletionStatus.Committed')) { Need ($limbs + $kill + $entry) $needle "Budgeted sever scheduler is missing: $needle" }
foreach($needle in @('ManagedGeometryWorker','RPT Geometry','System.Threading.ThreadPriority.BelowNormal','TryCaptureClassificationWork','TryPrimeClassificationsCore','TryBuildBatchWithRetries','ManagedMeshBuffersAndBlendShapes','MapBlendShapes','geometryWorkerMs=','maxGeometryWorkerMs=','cumulativeGeometryWorkerMs=')) { Need ($limbs + $geometry) $needle "The serial value-only managed geometry worker is missing: $needle" }
if($geometry -notmatch 'ClassificationWork contains managed arrays, strings and value[\s\S]{0,220}must never dereference Mesh, Transform') { $failures.Add('The worker Unity-object boundary is not explicit at classification capture.') }
foreach($needle in @('Stations = 5','Directions = 8','vertices.Length > 42','indices.Length / 3 > 80','ValidateClosedProxy','Physics.BakeMesh','MeshColliderCookingOptions.None')) { Need $limbs $needle "Bounded mesh-derived collider proxy is missing: $needle" }
foreach($needle in @('EnqueueColliderBake','UnityColliderBakeWorker','TryAttachBakedGibColliders','collider.sharedMesh = mesh','DeferredGibCleanups','colliderBakeWorkerMs=')) { Need $limbs $needle "Off-main-thread Unity convex cooking or safe attachment is missing: $needle" }
Need $limbs 'existing.Severed.Contains(bodyPart)' "A limb can be severed more than once."
Need $limbs 'visible limb and colliders preserved' "Observer visual failure must preserve the intact limb's colliders."

if ($failures.Count) { $failures | ForEach-Object { [Console]::Error.WriteLine("FAIL: $_") }; exit 1 }
Write-Output "PASS: procedural two-level limb severing, local shadow gibs, collider removal, blood streams, and head rejection are wired."
