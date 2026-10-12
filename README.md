![Rupture](logo.png)

Rupture adds procedural gore, injury effects and reactive corpse physics to **SPT 4.1**, with optional **Fika** support.

> [!NOTE]
> Rupture was written with OpenAI Codex under human direction, review and testing. Live-game validation remains necessary; automated checks do not establish compatibility with every setup.

## Features

- **Dismemberment and head wounds:** Bullets and explosions can sever limbs through the character's actual skin and clothing. Lethal and postmortem headshots can create entrance wounds, wound channels and exit damage, with optional headgear detachment.
- **Living bot leg severing:** Eligible bots can lose legs and remain prone, able to crawl, fight and heal. Human players and player scavs are protected. Untreated stumps cause severe health loss; completed surgery stops it but leaves the missing limb and a permanent one-HP maximum on that leg.
- **Blood and visible fractures:** Light wounds drip, heavy wounds pulse, and blood soaks clothing. Broken limbs can show exposed bone. Treatment stops further blood supply while stains remain; treated fractures lose their visuals.
- **Reactive bodies and weapons:** Corpses and dropped weapons respond to bullets, explosions and the environment, then settle. Settled corpses can wake when hit again.
- **Persistent casings:** Graphical clients retain spent casings with local collision, scatter and sound. Casings stay until capacity eviction or raid end.

## Installation

1. Download the release ZIP for your SPT version.
2. Extract it into your SPT installation folder.
3. Check that `Rupture.dll` and its accompanying files are directly inside `BepInEx/plugins/rupture/`.
4. Launch the game.

For **Fika**, install the **same Rupture release on every client and the host or headless instance**. Integration activates automatically. The included `Rupture.Fika.dll` can remain installed when playing solo; Fika is optional.

The current build baseline is **SPT 4.1.6 / EFT 0.16.9.40743** on Windows x64, with **Fika Core 2.4.4**, server **2.4.2** and headless **1.5.3**. SPT 4.0 is no longer supported. Newer SPT/Fika versions are allowed to load, but changed APIs may require an updated Rupture release. Fika Core 2.4.3 and older require an older compatible Rupture build. Automated baseline checks have passed; live raid acceptance remains pending.

## Settings

Use the BepInEx configuration menu or edit `BepInEx/config/com.rootdarkarchon.rupture.cfg`. Many options appear under advanced settings. Start with the defaults; extreme values can cause visual or performance problems.

- **Blood Effects Enabled** also controls clothing soaking and living fracture visuals.
- **Living Bot Leg Severing** also requires **Dismemberment Enabled**. Bullet buildup starts only on a leg already at zero HP; the hit that blacks it contributes nothing. The default living threshold is 24 N s, separate from the 12 N s corpse threshold. Healing or completed treatment resets living buildup. Explosion cuts use damaging leg hits and distance instead.
- **Living Leg Severing For Bosses And Special AI** is off by default. Ordinary scavs and AI BEAR/USEC PMCs are eligible; special AI roles are protected unless enabled. **Living Leg Severing From Explosions Only** can disable bullet-based living cuts.
- Severed stumps cause four times native heavy-bleeding health loss. Bandages, tourniquets, stims and passive healing cannot stop this condition; actual completed surgery is required. Disabling severing prevents new injuries while preserving existing ones. See the [living severing guide](docs/Living-Bot-Leg-Severing.md) for details.
- **Automatic Headgear Detachment** is enabled by default. Disable it to keep equipment on corpses while retaining head wounds.
- **Ragdoll Sleep Time** defaults to five seconds; bodies may settle earlier. **Localized Ragdoll Physics** enables standalone corpse simulation and preserves or restores native physics on backend failure.
- **Maximum Ground Decals** changes the game's shared decal capacity when the next world loads.
- **Persistent Casing Physics** retains up to **4,096 casings** by default. Lower capacity to reduce load; zero or disabling the feature releases retained casings. Casing settings and motion are local and inactive on headless.

In Fika, a graphical host controls shared settings. A headless host normally adopts the requesting client's settings for that raid and distributes them to other clients. Enable **Force Headless Configuration** on headless to use its own settings instead; changes take effect next raid. These temporary overrides preserve saved preferences. Diagnostics and machine-specific settings remain local.

## Limitations and troubleshooting

- Complex clothing, many simultaneous injuries and dense casing piles can cause substantial frame spikes. Historical fixture measurements are collected in the [performance guide](docs/PERFORMANCE.md); they are not a promise of raid FPS improvements.
- Unsupported meshes may skip a wound or cut. Failed limb reconstruction retains the intact limb and its hit collider. Head wounds are limited to four successful wounds per corpse.
- Fika synchronizes injury decisions and corpse/weapon motion; wound geometry, blood, stains and detached limb motion are generated locally. Appearance can differ between clients, and initial corpse synchronization can take a few frames. Other historical injuries are not generally replayed for late joins; living severed-bot state is refreshed periodically.
- Other gore, corpse or physics mods may conflict. Rupture requests HollywoodFX's existing exclusions for overlapping effects, but compatibility with every version or mod combination is unverified.
- BodyDragging requires a compatible consumer adapter to use Rupture's corpse control; installing Rupture alone does not integrate it.
- Mixed Rupture releases are rejected. If Fika is present and Rupture's adapter cannot initialize, Rupture stops initialization and logs the failure.

For headgear or pickup failures, keep complete BepInEx logs from the host/headless and affected clients, including the victim, approximate kill time and whether pickup was from the corpse or floor. `Headgear audit` warnings are available with debug logging off. Enable **Enable Debug Logging** when collecting additional diagnostics. See the [runtime checklist](docs/RPT-Ragdoll-Runtime-Inspection.md) for reproduction scenarios and unresolved live checks.

## Development and license

Build inputs, checks and packaging requirements are in [development setup](docs/DEVELOPMENT.md). Implementation references are in the [architecture guide](docs/ARCHITECTURE.md), [corpse-control API](docs/CORPSE-CONTROL-API.md) and [physics ownership contract](docs/RPT-External-PhysX-Architecture.md).

Rupture-authored source, documentation and assets are licensed under [MIT](LICENSE). Required third-party notices remain with their dependencies and runtime distribution.
