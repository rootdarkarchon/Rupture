using System.Collections.Generic;
using EFT.HealthSystem;

namespace Rupture.Health
{

internal static class NativeHealthEffects
{
	// Borrow the matched controller's existing list without LINQ or array copies.
	// Unknown controller families retain each caller's native-query fallback.
	internal static IReadOnlyList<EFT.HealthSystem.IHealthEffect> TryGet(IHealthController health) =>
		health is ActiveHealthController active ? active.Effects :
		health is EFT.HealthSystem.OfflineHealthController observed ? observed.Effects :
		health is EFT.HealthSystem.NetworkHealthController network ? network.Effects : null;
}
}
