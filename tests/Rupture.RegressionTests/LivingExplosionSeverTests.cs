using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using Mono.Cecil;
using UnityEngine;

internal static class LivingExplosionSeverTests
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Run(Assembly core)
    {
        void Check(bool ok, string name)
        { if (!ok) throw new Exception("Living explosion: " + name); Console.WriteLine("PASS: living explosion " + name); }
        Type policy = core.GetType("Rupture.Health.LivingExplosionSeverPolicy", true);
        float Chance(float d, float r = 5f, float damage = 65f, float hp = 65f) =>
            (float)policy.GetMethod("Probability", Flags).Invoke(null, new object[] { d, r, damage, hp });
        bool Select(float d, float roll, float damage = 65f) =>
            (bool)policy.GetMethod("Select", Flags).Invoke(null, new object[] { d, 5f, damage, 65f, roll });
        Check(Chance(0f) == 1f && Chance(.5f, damage: .01f) == 1f && Select(.5f, 1f, .01f),
            "damaging hits within 0.5 m guarantee either leg independently of its HP and damage magnitude");
        Check(Chance(5f) == 0 && Chance(50f) == 0 && !Select(5f, 0f), "radius boundary and distant fragments cannot sever");
        Check(Math.Abs(Chance(2.75f) - .125f) < .00001f && Math.Abs(Chance(2.75f, damage: 32.5f) - .0625f) < .00001f,
            "cubic midpoint is 12.5 percent at full leg damage and 6.25 percent at half damage");
        Check(Chance(2.75f, damage: 650f) == Chance(2.75f) && Chance(.2f, .3f) == 1 && Chance(.3f, .3f) == 0,
            "health scaling caps at one and guaranteed zone never exceeds actual blast radius");
        float previous = 1f;
        for (int i = 0; i <= 500; i++)
        {
            float p = Chance(i / 100f, damage: 32.5f);
            CheckRange(p >= 0 && p <= previous); previous = p;
        }
        Check(true, "distance falloff remains bounded and monotonic through the entire radius");
        foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            Check(Chance(.2f, invalid) == 0 && Chance(.2f, damage: invalid) == 0 && Chance(.2f, hp: invalid) == 0,
                "invalid radius, applied damage or maximum HP fails closed: " + invalid);
        foreach (float invalid in new[] { -1f, float.NaN, float.PositiveInfinity })
            Check(Chance(invalid) == 0 && !Select(.2f, invalid), "invalid distance or roll fails closed: " + invalid);
        Check(!Select(.2f, 1.01f) && Select(2.75f, .124f) && !Select(2.75f, .125f) && !Select(.2f, 0, 0),
            "selection honors chance boundary and requires actual health damage");

        var rolls = new Dictionary<long, byte>();
        bool Remember(long seq, int leg) => (bool)policy.GetMethod("Remember", Flags).Invoke(null, new object[] { rolls, seq, leg });
        Check(Remember(1, 0) && !Remember(1, 0) && Remember(1, 1) && !Remember(1, 1) && Remember(2, 0),
            "radial and repeated fragment callbacks get at most one roll per blast per leg");
        Check(!Remember(0, 0) && !Remember(3, -1) && !Remember(3, 2), "invalid blast identity and non-leg inputs are rejected");
        for (long i = 3; i < 200; i++) Remember(i, 0);
        Check(rolls.Count == 128 && !rolls.ContainsKey(1) && !Remember(199, 0), "per-bot blast memory is bounded and retains recent identities");
        Type bot = core.GetType("Rupture.Health.BotSeverPolicy", true);
        bool Commit(bool explosion, bool only, float hp, uint revision = 3) =>
            (bool)bot.GetMethod("CanCommit", Flags).Invoke(null, new object[] { explosion, only, hp, 3u, revision });
        Check(Commit(true, false, 65) && Commit(true, true, 65) && Commit(true, false, 0),
            "healthy and blacked legs admit blast cuts with explosions-only either off or on");
        Check(!Commit(false, true, 0) && !Commit(false, false, 65) && Commit(false, false, 0),
            "explosions-only blocks bullets while normal bullets still require an already blacked leg");
        Check(!Commit(true, true, 65, 4) && !Commit(false, false, 0, 4), "healing cancels both pending blast and bullet reservations");

        Type scope = core.GetType("Rupture.Impacts.ExplosionEventScope", true);
        Type events = core.GetType("Rupture.Impacts.LivingExplosionEvents", true);
        object sample = Activator.CreateInstance(core.GetType("Rupture.Impacts.ImpactData", true));
        object Enter(Vector3 center, float radius) => scope.GetMethod("EnterBlast", Flags).Invoke(null, new object[] { center, radius });
        void Restore(object prior) => scope.GetMethod("Restore", Flags).Invoke(null, new[] { prior });
        void Register(float now) => events.GetMethod("RegisterAt", Flags).Invoke(null, new object[] { null, now });
        void Reset() => events.GetMethod("Reset", Flags).Invoke(null, null);
        long Sequence(Vector3 center) => (long)scope.GetMethod("SequenceFor", Flags).Invoke(null, new object[] { center });
        (bool Found, long Sequence, Vector3 Center, float Radius) Resolve(Vector3 origin, float now, EDamageType type = EDamageType.GrenadeFragment)
        {
            var damage = default(DamageInfo); damage.DamageType = type; damage.MasterOrigin = origin;
            object[] args = { damage, sample, now, 0L, default(Vector3), 0f };
            bool found = (bool)events.GetMethod("ResolveAt", Flags).Invoke(null, args);
            return (found, (long)args[3], (Vector3)args[4], (float)args[5]);
        }
        Vector3 center = new Vector3(2, 3, 4), other = new Vector3(4, 3, 2);
        Reset();
        object wrapper = scope.GetMethod("Enter", Flags).Invoke(null, new object[] { center });
        long identity = Sequence(center);
        object blast = Enter(center, 5);
        try
        {
            Register(10);
            var resolved = Resolve(center, 10);
            Check(resolved.Found && resolved.Sequence == identity && resolved.Radius == 5 && resolved.Center == center,
                "shared native handler enriches grenade identity with its actual radius");
            Check(Resolve(default, 10).Found && !Resolve(other, 10).Found, "synchronous zero-origin radial damage uses active center without stealing older fragment origins");
            object nested = Enter(center, 8);
            try { Check(Sequence(center) != identity && Resolve(center, 10).Radius == 8, "nested shared blast has a distinct identity and radius"); }
            finally { Restore(nested); }
            Check(Resolve(center, 10).Sequence == identity && Resolve(center, 10).Radius == 5, "nested finalizer restores the outer blast metadata");
        }
        finally { Restore(blast); Restore(wrapper); }
        Check(Resolve(center, 24).Found && Resolve(center, 24).Sequence == identity && !Resolve(center, 26).Found,
            "delayed fragments retain actual blast metadata for 15 seconds then expire");
        Check(!Resolve(center, 24, EDamageType.Bullet).Found && !Resolve(default, 24).Found, "bullets and fragments with unknown origins cannot use the blast registry");
        Reset();
        for (int i = 0; i < 2; i++)
        { object prior = Enter(center, 5 + i); try { Register(30); } finally { Restore(prior); } }
        Check(!Resolve(center, 30).Found, "ambiguous repeated explosions at one center cannot grant extra delayed-fragment rolls");
        Reset();
        for (int i = 0; i < 130; i++)
        { object prior = Enter(new Vector3(i + 1, 2, 3), 5); try { Register(40); } finally { Restore(prior); } }
        Check(!Resolve(new Vector3(1, 2, 3), 40).Found && Resolve(new Vector3(130, 2, 3), 40).Found,
            "global blast registry retains at most the latest 128 explosions");
        Reset(); Check(!Resolve(new Vector3(130, 2, 3), 40).Found, "raid reset retires all retained blast identities");

        using var module = AssemblyDefinition.ReadAssembly(core.Location);
        using var game = AssemblyDefinition.ReadAssembly(typeof(Player).Assembly.Location);
        string[] Calls(string type, string method) => module.MainModule.GetType(type).Methods.Single(m => m.Name == method)
            .Body.Instructions.Where(i => i.Operand is MethodReference).Select(i => ((MethodReference)i.Operand).Name).ToArray();
        string service = "Rupture.Health.BotSeverStateService", hooks = "Rupture.Impacts.ImpactHooks";
        var nativeBlast = game.MainModule.GetType("EFT.ExplosionSharedMethods").Methods.Single(m => m.Name == "Explosion");
        foreach (var parameter in module.MainModule.GetType(hooks).Methods.Single(m => m.Name == "BlastPrefix").Parameters.Where(p => p.Name != "__state"))
            Check(nativeBlast.Parameters.Any(p => p.Name == parameter.Name && p.ParameterType.FullName == parameter.ParameterType.FullName),
                "installed shared explosion hook parameter ABI: " + parameter.Name);
        Check(Calls(hooks, "BlastPrefix").Contains("get_MaxExplosionDistance") && Calls(hooks, "BlastPrefix").Contains("Register") &&
            Calls(hooks, "BlastFinally").Contains("Restore"), "shared native hook captures real radius and restores scope on exceptions");
        Check(Calls(hooks, "DamagePostfix").Contains("ExplosionDamaged") && Calls(service, "ExplosionDamaged").Contains("Eligible") &&
            Calls(service, "ExplosionDamaged").Contains("Resolve") && Calls(service, "ExplosionDamaged").Contains("TryGetExplosionDistance") &&
            Calls(service, "ExplosionDamaged").Contains("Remember") && !Calls(service, "ExplosionDamaged").Any(n => n == "WasBlacked" || n == "Begin" || n == "Accumulate"),
            "actual damage dispatch uses bot eligibility and blast geometry without blacked-health or accumulated momentum gates");
        Check(Calls("Rupture.Impacts.ImpactStateService", "RecordDamage").Contains("get_LivingLegSeveringExplosionsOnly") &&
            Calls(service, "Damaged").Contains("get_LivingLegSeveringExplosionsOnly") && Calls(service, "BeforeCutCommit").Contains("CanCommit") &&
            Calls(service, "Reset").Contains("Reset"), "cause restriction guards accumulation, queue and commit while raid cleanup retires blasts");
    }
    private static void CheckRange(bool ok) { if (!ok) throw new Exception("Living explosion falloff is not bounded and monotonic"); }
}
