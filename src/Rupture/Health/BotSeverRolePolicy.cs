using EFT;

namespace Rupture.Health;

internal static class BotSeverRolePolicy
{
    // Whitelist ordinary combatants: pmcBot is the raider role, and exUsec
    // is a rogue. Neither is an ordinary SPT PMC despite the name/side.
    internal static bool Allowed(WildSpawnType? role, bool includeSpecial)
    {
        if (!role.HasValue) return false;
        if (includeSpecial) return true;
        switch (role.Value)
        {
            case WildSpawnType.assault:
            case WildSpawnType.marksman:
            case WildSpawnType.cursedAssault:
            case WildSpawnType.assaultGroup:
            case WildSpawnType.pmcBEAR:
            case WildSpawnType.pmcUSEC:
                return true;
            default:
                return false;
        }
    }
}
