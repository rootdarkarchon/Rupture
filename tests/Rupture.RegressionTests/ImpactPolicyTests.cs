using System;
using System.Reflection;

internal static class ImpactPolicyTests
{
    private static Type _policy;
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static object Call(object state, string name, params object[] args)
    {
        var method = _policy.GetMethod(name, Flags);
        int supplied = args.Length;
        Array.Resize(ref args, method.GetParameters().Length);
        for (int i = supplied; i < args.Length; i++) args[i] = Type.Missing;
        return method.Invoke(state, args);
    }
    private static object New() => Activator.CreateInstance(_policy, true);
    private static float Total(object state) => (float)_policy.GetProperty("Total", Flags).GetValue(state);
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    private static bool Near(float a, float b) => Math.Abs(a-b) < 0.0001f;

    internal static void Run(Assembly assembly)
    {
        var eligible = assembly.GetType("Rupture.Impacts.ImpactStateService",true).GetMethod("Eligible",Flags);
        var damage = new EFT.Ballistics.DamageInfo { DamageType = EFT.EDamageType.Bullet, Damage = 1f };
        Check((bool)eligible.Invoke(null,new object[] {damage}), "positive ballistic body damage is eligible");
        damage.BlockedBy = new EFT.MongoID("0123456789abcdef01234567");
        Check(!(bool)eligible.Invoke(null,new object[] {damage}), "blocked armor-only damage is excluded");
        damage.BlockedBy = null; damage.DeflectedBy = new EFT.MongoID("0123456789abcdef01234567");
        Check(!(bool)eligible.Invoke(null,new object[] {damage}), "deflected armor-only damage is excluded");
        damage.DeflectedBy = null; damage.OverDamageFrom = EBodyPart.LeftArm;
        Check(!(bool)eligible.Invoke(null,new object[] {damage}), "redistributed damage is excluded");
        damage.OverDamageFrom = null;
        foreach (var kind in new[] { EFT.EDamageType.Melee, EFT.EDamageType.HeavyBleeding, EFT.EDamageType.Explosion })
        {
            damage.DamageType = kind;
            Check(!(bool)eligible.Invoke(null,new object[] {damage}), "nonballistic damage excluded: " + kind);
        }
        _policy = assembly.GetType("Rupture.Impacts.LimbImpactPolicy", true);
        Check(Near((float)Call(null,"Momentum",4f,900f),3.6f), "actual impact momentum uses grams-to-kilograms conversion");
        Check(Near((float)Call(null,"Momentum",4f,450f),1.8f), "speed loss reduces accumulation independently of presentation");
        foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, float.MaxValue })
            Check((float)Call(null,"Momentum",invalid, invalid) == 0, "invalid/overflow momentum rejected: " + invalid);
        Check(Near((float)Call(null,"Chance",3.6f),0.09f) && Near((float)Call(null,"Chance",7.2f),0.36f) &&
            Near((float)Call(null,"Chance",10.8f),0.81f) && (float)Call(null,"Chance",12f) == 1, "12 N.s increasing squared chance");
        object limb = New(), other = New();
        Check((bool)Call(limb,"Add",1UL,3.6f) && !(bool)Call(limb,"Add",1UL,3.6f) && Near(Total(limb),3.6f), "duplicate callbacks contribute once");
        Check(!(bool)Call(limb,"Begin",false,0f), "living limb never severs even on a winning roll");
        Check((bool)Call(other,"Add",1UL,3.6f) && Near(Total(other),3.6f), "same projectile may damage another independent limb");
        Check((bool)Call(limb,"Add",2UL,3.6f) && (bool)Call(limb,"Add",3UL,3.6f) && (bool)Call(limb,"Add",4UL,3.6f), "distinct pellets or fragments accumulate");
        Check(Near(Total(limb),12f) && !(bool)Call(limb,"Begin",false,0.99f), "guaranteed threshold still cannot sever alive");
        Check((bool)Call(limb,"Begin",true,0.9999f) && !(bool)Call(limb,"Begin",true,0f), "death/corpse threshold guarantees one pending request");
        Call(limb,"Complete",false);
        Check(Near(Total(limb),12f), "failed geometry retains buildup");
        Check(!(bool)Call(limb,"Add",4UL,3.6f) && (bool)Call(limb,"Add",5UL,1f) && (bool)Call(limb,"Begin",true,0.99f), "later distinct hit permits retry after failure");
        Call(limb,"Complete",true);
        Check(!(bool)Call(limb,"Add",6UL,1f) && !(bool)Call(limb,"Begin",true,0f), "committed limb never severs twice");
        Call(other,"Heal",true,10f,50f,60f);
        Check(Near(Total(other),3.6f), "partial healing retains total");
        Call(other,"Heal",false,10f,60f,60f);
        Check(Near(Total(other),3.6f), "corpse health changes cannot reset buildup");
        Call(other,"Heal",true,9.9995f,59.9995f,60f);
        Check(Near(Total(other),3.6f), "near-maximum partial healing does not reset buildup");
        Call(other,"Heal",true,10f,60f,60f);
        Check(Total(other) == 0 && !(bool)Call(other,"Add",1UL,3.6f), "full healing resets total but rejects delayed duplicate projectile");
        object snapshot = New(); Call(snapshot,"Import",8f); Call(snapshot,"Remember",42UL);
        Check(Near(Total(snapshot),8f) && !(bool)Call(snapshot,"Add",42UL,8f), "death snapshot includes lethal hit exactly once");
        object heavy = New(); Call(heavy,"Add",99UL,12.8f);
        Check((bool)Call(heavy,"Begin",true,1f), "single heavy lethal impact guarantees sever request including Unity RNG upper endpoint");
        Check(Near((float)Call(null,"Chance",6f,6f),1f) && Near((float)Call(null,"Chance",6f,24f),0.0625f),
            "lower and higher configured thresholds scale sever probability");
        object custom = New(); Call(custom,"Add",101UL,18f,24f);
        Check(Near(Total(custom),18f) && !(bool)Call(custom,"Begin",true,0.6f,24f),
            "higher threshold retains momentum beyond the former cap and requires a winning roll");
        Call(custom,"Add",102UL,6f,24f);
        Check(Near(Total(custom),24f) && (bool)Call(custom,"Begin",true,1f,24f), "custom threshold guarantees sever at its endpoint");
        object imported = New(); Call(imported,"Import",18f,24f);
        Check(Near(Total(imported),18f), "authority import preserves totals above the default threshold");
        Check(!(bool)Call(imported,"Begin",false,0f,6f) && (bool)Call(imported,"Begin",true,1f,6f),
            "changed threshold applies to existing buildup while living limbs remain intact");
        foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            Check(Near((float)Call(null,"Chance",6f,invalid),0.25f), "invalid threshold falls back to the default: " + invalid);
    }
}
