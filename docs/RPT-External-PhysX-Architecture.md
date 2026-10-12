# Corpse physics ownership contract

Status: **Accepted and frozen**

This short contract is consumed by `tests/Reliability.Tests.ps1`. Detailed current behavior, ownership and failure handling are documented in [ARCHITECTURE-PHYSX.md](ARCHITECTURE-PHYSX.md) and [ARCHITECTURE-MULTIPLAYER.md](ARCHITECTURE-MULTIPLAYER.md).

- Standalone PhysX owns simulation; Unity owns presentation and gameplay queries.
- Clients predict immediately, then use kinematic full-state authority playback with one observed authority interval of delay.
- Reliable lifecycle metadata cannot block an already complete and locally bindable motion snapshot.
- Weapons are independent dynamic bodies without a corpse-hand joint.

These invariants describe committed external rigs. Build failure or backend failure preserves/restores EFT native simulation through the documented fallback. Procedural limb gibs are local graphical bodies and are excluded from authority motion snapshots.
