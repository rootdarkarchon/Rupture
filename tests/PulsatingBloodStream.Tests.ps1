[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$failures = [System.Collections.Generic.List[string]]::new()

function Read-Repo([string] $path) {
    $full = Join-Path $repoRoot $path
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Required file missing: $full" }
    Get-Content -LiteralPath $full -Raw
}
function Need([string] $source, [string] $text, [string] $message) {
    if (-not $source.Contains($text)) { $failures.Add($message) }
}

$system = Read-Repo "src\Rupture\Dismemberment\Classes\BloodStreams\PulsatingBloodStreamSystem.cs"
$system += Read-Repo "src/Rupture/Dismemberment/Classes/BloodStreams/PulsatingBloodStreamSystem.Rendering.cs"
$system = $system.Replace("`r`n", "`n")
$material = Read-Repo "src\Rupture\Dismemberment\Classes\BloodStreams\BloodStreamMaterialProvider.cs"
$heavy = Read-Repo "src\Rupture\Dismemberment\Patches\HeavyBleedingPatch.cs"
$packet = Read-Repo "src\Rupture.Fika\Dismemberment\Classes\Packets\BloodStreamPacket.cs"
$kill = Read-Repo "src\Rupture\Combined\Patches\KillPatch.cs"
$head = Read-Repo "src\Rupture\Dismemberment\Classes\HeadWounds\HeadWoundSystem.cs"
$entry = Read-Repo "src\Rupture\RupturePlugin.cs"
$entry += Read-Repo "src\Rupture\RuptureConfiguration.cs"
$shader = Read-Repo "unity\RuptureWound\Assets\Rupture\BloodStream.shader"
$preview = Read-Repo "unity\RuptureWound\Assets\Rupture\BloodStreams\BenchmarkAndPreviewPulsatingBloodStream.cs"
$bundleBuild = Read-Repo "unity\RuptureWound\Assets\Editor\BuildRuptureWoundBundle.cs"
$package = Read-Repo "scripts\Build-Package.ps1"

foreach ($needle in @('MaximumVisualEmitters = 64', 'MaximumRenderedEmitters = 24', 'Physics.Raycast(', 'EFTHardSettings.Instance.ENVIRONMENT_HIT_MASK', '_mesh.MarkDynamic();',
    'MaximumDecalsPerFrame = 3', 'MaximumDecalsPerSecond = 12', 'SpatialDecalCooldown',
    'EmitBloodOnEnvironment', 'EmitBleeding', 'PendingDescriptors', 'AcceptSequence', 'BleedingWoundTracker.ActiveEffect',
	'HeadResolutionWaitSeconds', 'ResolveHeadExit', 'RejectHeadExit', 'Vector3 outward = ResolveEntryDirection(damageInfo)',
	'PostMortemStumpFlow', 'StartProceduralDismembermentStreams', 'PeakSpeed = postMortem ? 1.35f : 3.4f',
	'Radius = postMortem ? 0.0105f : 0.0115f', 'PulsePeriod = postMortem ? 0.15f', 'Mathf.Min(bodyDuration, 3f)',
	'distalAxisWorld.normalized', 'bodyPointWorld, axis', 'gibPointWorld, -axis',
	'anchor.InverseTransformDirection(directionWorld)')) {
    Need $system $needle "Blood-stream runtime contract missing: $needle"
}
foreach ($needle in @('MaximumStreamSamplesPerEmitter = 96',
	'StreamSampleInterval = 1f / 50f', 'Pulsating blood stream started: kind=')) {
	Need $system $needle "Emission-history stream geometry missing: $needle"
}
foreach ($rejected in @('ParticleSystem', 'OnParticleCollision', 'GetCollisionEvents', 'StartCoroutine', 'IEnumerator')) {
    if ($system.Contains($rejected)) { $failures.Add("Blood-stream runtime still uses forbidden particle/coroutine path: $rejected") }
}
foreach ($needle in @('FindExistingEffect<EFT.HealthSystem.IHeavyBleeding>', 'FindExistingEffect<EFT.HealthSystem.ILightBleeding>', 'BleedingWoundTracker.Capture', '[PatchPrefix]', '[PatchPostfix]')) {
	Need $heavy $needle "Heavy-bleed creation detection missing: $needle"
}
Need $heavy 'EBodyPartColliderType colliderType' "Heavy-bleed Harmony prefix does not bind the installed ApplyDamageInfo colliderType parameter."
if ($heavy.Contains('bodyPartColliderType')) {
	$failures.Add("Heavy-bleed Harmony prefix still requests a nonexistent ApplyDamageInfo parameter name.")
}
Need $system 'kind != BloodStreamTriggerKind.LightWound' 'Light bleeding must not emit arterial heartbeat pulses.'
$entry += Read-Repo 'src/Rupture.Fika/FikaRuntime.cs'
foreach ($needle in @('BloodStreamRequestPacket', 'ReliableOrdered', 'RegisterPacket<BloodStreamPacket>',
	'RegisterPacket<BloodStreamRequestPacket, NetPeer>', 'Killshot Stream Duration (Seconds)',
	'Dismemberment Stream Duration (Seconds)', 'AnchorBone', 'Publish(descriptor, clientRequestAllowed: false)',
	'StreamPriority')) {
    Need ($entry + $packet + $system) $needle "Lifecycle/Fika/config contract missing: $needle"
}
foreach ($needle in @('packet.StreamId = Hash(packet.ProfileId', 'packet.PeakSpeed = packet.TriggerKind == BloodStreamTriggerKind.LightWound ? 0.06f : 1.5f',
	'packet.Radius = packet.TriggerKind == BloodStreamTriggerKind.LightWound ? 0.0022f : 0.0052f', 'packet.Seed = packet.StreamId')) {
	Need $system $needle "Authority does not normalize client-owned heavy-wound request data: $needle"
}
foreach ($needle in @('light ? 0.06f : 1.5f, light ? 0.0022f : 0.0052f',
	'BaseFlowState', 'HasPersistentBase', 'HasHeartbeatPulse', 'UpdateStreamFlow(index, emitter, now)',
	'StreamRingSample', 'StreamSamples', 'AppendStreamSample', 'PruneStreamSamples', 'ClearStreamSamples',
	'state.TraceValid', 'now >= state.NextTraceAt',
	'BloodReferenceParcels', 'BloodReusedFlightTimes',
	'Mathf.Lerp(NormalSpeedScale(kind), HeartbeatSpeedScale(kind), pressure)',
	'float blend = snapToTarget ? 1f : WobbleTransition(emitter, now)',
	'BaseTraceInterval = 0.12f', 'BaseDecalInterval = 0.75f',
	'QueueImpact', 'TriggerHeartbeat', 'BaseDepositInterval', 'ResolveWobbledFrame',
	'NormalSpeedScale', 'HeartbeatSpeedScale', 'StreamMaximumFlight',
	'duration, 2.0f, 0.0078f', 'return 0.58f', 'return 0.25f', 'return 0.74f',
	'MaximumFlightSeconds = 1.8f')) {
	Need $system $needle "Persistent continuous-flow profile missing: $needle"
}
foreach ($needle in @('WoundOffshootRecedeSeconds = 0.52f',
	'UsesWoundOffshootHandoff', 'WoundOffshootPressure',
	'emitter.NextSampleAt = now',
	'UpdateWoundOffshootFlow(int emitterIndex',
	'Mathf.Lerp(NormalSpeedScale(kind), HeartbeatSpeedScale(kind), pressure)',
	'state.Radius = emitter.Descriptor.Radius * strength * Mathf.Lerp(BaseRadiusScale(kind), 1f, pressure)',
	'AppendStreamSample(emitterIndex, emitter, state, now)',
	'center = PositionAt(sample.Origin, sample.Velocity, sample.Gravity, age)',
	'AddWoundParcels(index, emitter, now)')) {
	Need $system $needle "Immutable wound-emission history missing: $needle"
}
foreach ($needle in @('kind == BloodStreamTriggerKind.DismembermentStump;',
	'MaximumDismembermentDecalsPerFrame = 6', 'MaximumDismembermentDecalsPerSecond = 20',
	'DismembermentSpatialDecalCooldown = 0.24f', 'DismembermentSpatialDecalCellSize = 0.10f',
	'impact.Kind == BloodStreamTriggerKind.DismembermentStump ? 6',
	'0.32f * Mathf.Lerp(1.5f, 1f, strength)',
	'0.42f * Mathf.Lerp(1.6f, 1f, strength)')) {
	Need $system $needle "Dismemberment parcel/deposition profile missing: $needle"
}
foreach ($needle in @('WoundParcelDirectionSpread = 0.028f',
	'WoundParcelLengthOverlap = 1.18f', 'MaximumWoundParcelLength = 0.12f',
	'WoundParcelSides = 6', 'WoundParcelAxialRings = 4',
	'Vector2 spread = RandomInsideUnitCircle(ref spreadSeed) * WoundParcelDirectionSpread',
	'Velocity = velocity', 'AddWoundParcels(int emitterIndex',
	'AddWoundParcel(StreamRingSample sample, float now)',
	'Vector3 instantaneousVelocity = sample.Velocity + sample.Gravity * age',
	'instantaneousSpeed * StreamSampleInterval * WoundParcelLengthOverlap',
	'Mathf.Sqrt(referenceLength / Mathf.Max(referenceLength, length))')) {
	Need $system $needle "Independent wound-parcel geometry missing: $needle"
}
foreach ($rejected in @('RetiredWoundFlow', 'RetireCurrentWoundFlow', 'AddWoundOffshootStreams',
	'EmissionSegmentAnchorValid', 'EmissionSegmentAnchorOrigin', 'EmissionSegmentAnchorDirection',
	'anchorMoved')) {
	if ($system.Contains($rejected)) { $failures.Add("Obsolete connected wound-flow workaround remains: $rejected") }
}
foreach ($rejected in @('private struct PulseParcel', 'Parcels = new', 'AddTube(PulseParcel',
	'BaseReach(', 'PulseReach(', 'LimitFlightDuration(', 'AddPersistentBase', 'BaseTubeRings',
	'Mathf.Lerp(NormalSpeedScale(kind), HeartbeatSpeedScale(kind), heartbeat)')) {
	if ($system.Contains($rejected)) { $failures.Add("Emission-history implementation still contains obsolete full-curve geometry: $rejected") }
}
foreach ($needle in @('FiniteFadeStart = 0.75f', 'FiniteEndStrength = 0.12f',
	'BeginDrain(emitter)', 'StreamStrength(emitter, now)', 'emitter.DrainEndsAt',
	'Mathf.SmoothStep(0f, 1f, drain)',
	'state.Radius = emitter.Descriptor.Radius * strength * Mathf.Lerp(BaseRadiusScale(kind), 1f, pressure)')) {
	Need $system $needle "Smooth finite-stream taper/drain contract missing: $needle"
}
foreach ($needle in @('Singleton<Effects>.Instance.EmitBleeding(point, impact.Normal)',
	'BaseDecalInterval * Mathf.Lerp(2.2f, 1f, strength)',
	'impact.Priority != priority',
	'QueueImpact(now + Mathf.Max(0.02f, state.ImpactAge)', 'now < impact.ArrivesAt',
	'BloodImpactsExpired', 'Singleton<Effects>.Instance.EmitBloodOnEnvironment(point, impact.Normal)',
	'MaximumDecalsPerFrame', 'MaximumDecalsPerSecond')) {
	Need $system $needle "Arrival-timed bounded decal contract missing: $needle"
}
if ($system.Contains('ContinuousFlow') -or $system.Contains('NextFlowAt') -or $system.Contains('FlowIndex')) {
	$failures.Add("Persistent base flow still depends on evictable continuous parcels.")
}
foreach ($needle in @('StopLivingStreams', 'waitForHeadExit: true', 'waitForHeadExit: false')) {
    Need $kill $needle "Death-priority stream contract missing: $needle"
}
foreach ($needle in @('StartProceduralDismembermentStreams', 'float gibDuration = bodyDuration * 0.5f',
	'StartLocalProceduralStream', 'gibAnchor')) {
    Need $system $needle "Procedural body/gib stump stream contract missing: $needle"
}
Need $head 'PulsatingBloodStreamSystem.ResolveHeadExit' "Resolved procedural head exits do not upgrade the delayed stream."
Need $head 'PulsatingBloodStreamSystem.RejectHeadExit' "Rejected procedural head exits do not choose the entry fallback."
if (Test-Path -LiteralPath (Join-Path $repoRoot 'src\Rupture\Dismemberment\Classes\ParticleFloorPainter.cs')) {
    $failures.Add("ParticleFloorPainter source was not removed.")
}
foreach ($needle in @('Shader "Rupture/PulsatingBloodStream"', 'uv_MainTex', 'RenderType"="Opaque"')) {
    Need $shader $needle "Object-UV opaque stream shader missing: $needle"
}
foreach ($needle in @('blood_splash_d', 'blood_splash_n', 'RPT Generated Blood Albedo', 'Shader.Find("Standard")')) {
    Need $material $needle "EFT texture or dark-red material fallback missing: $needle"
}
foreach ($needle in @('new Color(0.82f, 0.18f, 0.16f', 'SetFloat("_Smoothness", 0.94f)',
	'SetFloat("_NormalStrength", 1.1f)')) {
	Need $material $needle "Dense wet-blood material tuning missing: $needle"
}
foreach ($needle in @('Create All Examples', 'Heavy Entry', 'Head Exit', 'Entry Fallback', 'Moving Stump',
    'Floor Impact', 'Wall Impact', 'PostMortem Fade', 'Small periodic EmitBleeding base mark',
	'Strong pulse EmitBloodOnEnvironment cluster', 'AddParcel', 'ApplyParcelSpread', 'ParcelDirectionSpread = 0.028f',
	'Pause or Resume',
	'Advance Single Pulse Step', 'RunCase(1)', 'RunCase(8)', 'RunCase(24)',
	'persistentBaseGenerationMilliseconds', 'heartbeatGenerationMilliseconds', 'collisionRefreshMilliseconds',
	'heartbeatDeformationMilliseconds', 'parcelGenerationMilliseconds', 'maximumRenderedParcels', 'parcelVertexCount', 'parcelTriangleCount',
	'maximumStreamSamplesPerEmitter', 'AddCapturedHistory', 'AddCapturedPreviewHistory',
	'BenchmarkWoundPressure', 'FindImpactAge',
	'baseDecalEvents', 'pulseDecalEvents', 'totalP99Milliseconds', 'managedSteadyStateBytes')) {
    Need $preview $needle "Unity preview/benchmark contract missing: $needle"
}
Need $bundleBuild 'Rupture/PulsatingBloodStream' "Wound bundle builder does not verify the stream shader."
if ($package -match 'blood_3dfx|SourceArchive|payloadRoot') { $failures.Add("Package still consumes the retired arterial payload.") }
Need $package 'Rupture-$version-$workingTreeHash.zip' "Full package does not use the version and working-tree hash release archive name."
if ($preview.Contains('returningOrigin') -or $preview.Contains('returningVelocity')) {
	$failures.Add("Unity preview still creates a separate returning-normal stream.")
}

foreach ($rejected in @('BloodMote', 'SpawnMote', 'AddMote', 'AddHistoricalStreams', 'AddHistoricalStreamGroup',
    'ConnectAxialRings', 'AddTubeIndices', 'AddBallisticRange', 'TargetAxialSpacing', 'UseParcels')) {
    if (($system + $preview).Contains($rejected)) { $failures.Add("Retired tube/spray path remains: $rejected") }
}
$rendering = Read-Repo 'src/Rupture/Dismemberment/Classes/BloodStreams/PulsatingBloodStreamSystem.Rendering.cs'
if ($rendering.Contains('TriggerKind') -or $rendering.Contains('UsesWoundOffshootHandoff')) {
    $failures.Add('Rendering must not select different geometry by wound trigger kind.')
}
Need $head 'PulsatingBloodStreamSystem.StartPostMortemHeadWound' 'Corpse head wounds must retain their local flow entry point.'
if (!$system.Contains('DripImpacts[i] = default;')) {
    $failures.Add('Completed heartbeat impacts must retire without secondary spray.')
}
foreach ($counter in @('BloodParcelSamplesVisited', 'BloodParcelsInstanced', 'BloodParcelsCpuFallback', 'BloodParcelUploadBytes')) {
    Need $rendering $counter "Parcel workload counter missing: $counter"
}
$parcelRenderer = Read-Repo 'unity/RuptureWound/Assets/Rupture/BloodStreams/BloodParcelRenderer.cs'
foreach ($needle in @('Capacity = 24 * 96', 'ComputeBuffer(Capacity, 64', 'DrawMeshInstancedProcedural', 'ShadowCastingMode.Off, true', 'if (!Ready || Count >= Capacity) return false', '_buffer.Release()')) {
    Need $parcelRenderer $needle "Instanced parcel bounds, ownership or cleanup missing: $needle"
}
Need $system 'ParcelRenderer.Dispose()' 'Raid teardown must release instanced parcel resources.'
Need $system '_gpuParcels && ParcelRenderer.Add' 'CPU parcel fallback must remain active when instancing is unavailable.'
foreach ($needle in @('UNITY_PROCEDURAL_INSTANCING_ENABLED', 'StructuredBuffer<ParcelData>', 'parcel.originTime.xyz + velocity * age', '0x27D4EB2Du')) {
    Need $shader $needle "GPU parcel trajectory/shape contract missing: $needle"
}
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { [Console]::Error.WriteLine("FAIL: $_") }
    exit 1
}
Write-Output "PASS: wound pressure affects only newly launched parcels; each parcel preserves its world-space ballistic path, deterministic spread, velocity stretch, and gravity-driven thinning without cross-parcel triangles."
Write-Output "PASS: parcel-only rendering, arrival-timed native world decals, ordered Fika lifecycle, Unity examples, and the 1/8/24-emitter benchmark are source-wired."
