using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class FractureTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    internal static void Run(Assembly plugin)
    {
        var descriptor = plugin.GetType("Rupture.Dismemberment.Classes.Fractures.FractureAppearance", true);
        var constructor = descriptor.GetConstructor(Flags, null, new[] { typeof(string), typeof(int) }, null);
        object Create(string id, int part) => constructor.Invoke(new object[] { id, part });
        T Field<T>(object value, string name) => (T)descriptor.GetField(name, Flags).GetValue(value);
        string Key(object value) => string.Join("|", descriptor.GetFields(Flags).Select(f => f.GetValue(value).ToString()));
        int upper = 0, lower = 0, second = 0, upperSecond = 0, lowerSecond = 0, upperLeftLowerRight = 0;
        for (int i = 0; i < 10000; i++)
        {
            string id = "fracture-fixture-" + i;
            for (int part = 3; part <= 6; part++)
            {
                object value = Create(id, part);
                if (Key(value) != Key(Create(id, part))) throw new Exception("fracture descriptor changes on reconstruction");
                bool proximal = Convert.ToByte(descriptor.GetField("Level", Flags).GetValue(value)) == 1;
                int count = Field<int>(value, "BoneCount"); float fraction = Field<float>(value, "Fraction");
                int sites = Field<int>(value, "SiteCount");
                float tilt = Field<float>(value, "Tilt");
                if (tilt < 20f || tilt > 30f) throw new Exception("fracture tilt must remain shallow");
                if (count != (proximal ? 1 : 2) || sites < 1 || sites > 2 ||
                    fraction < (proximal ? .375f : .10f) || fraction > (proximal ? .425f : .20f))
                    throw new Exception("invalid fracture anatomy/site/count");
                if (proximal) upper++; else lower++;
                if (sites == 2)
                {
                    second++; if (proximal) upperSecond++; else lowerSecond++;
                    object opposite = descriptor.GetMethod("ForSite", Flags).Invoke(value, new object[] { 1 });
                    if (Field<int>(opposite, "BoneCount") != count || Field<int>(opposite, "Site") != 1 ||
                        Math.Abs(Field<float>(opposite, "Fraction") + fraction - 1f) > .00001f ||
                        Field<float>(opposite, "Fraction") - fraction < .14999f ||
                        Field<uint>(opposite, "ShapeSeed") != Field<uint>(value, "ShapeSeed"))
                        throw new Exception("opposite wound must mirror the same bone/pair with separate placement");
                }
            }
            if (Convert.ToByte(descriptor.GetField("Level", Flags).GetValue(Create(id, 3))) == 1 &&
                Convert.ToByte(descriptor.GetField("Level", Flags).GetValue(Create(id, 4))) == 2) upperLeftLowerRight++;
        }
        Check(Math.Abs(upper / 40000d - .5) < .02 && Math.Abs(second / 40000d - .5) < .02 &&
            Math.Abs(upperSecond / (double)upper - .5) < .02 && Math.Abs(lowerSecond / (double)lower - .5) < .02 &&
            Math.Abs(upperLeftLowerRight / 10000d - .25) < .02,
            $"40,000 deterministic fracture descriptors: upper={upper}, lower={lower}, opposite wounds={second}; independent feature/limb streams");
        var anatomy = plugin.GetType("Rupture.Dismemberment.Classes.Limbs.ProceduralLimbWoundGeometry", true);
        var level = plugin.GetType("Rupture.Dismemberment.Classes.Limbs.LimbSeverLevel", true);
        float[][] expected = { new[] { 20f, 18f }, new[] { 16f, 11f }, new[] { 15f, 11f },
            new[] { 29f, 26f }, new[] { 27f, 20f }, new[] { 14f, 14f } };
        int caseIndex = 0;
        foreach (bool leg in new[] { false, true })
            foreach (int segment in new[] { 1, 2 })
                for (int bone = 0; bone < segment; bone++)
                {
                    var diameters = (float[])anatomy.GetMethod("BoneDiameterBaselineForTests", Flags).Invoke(null,
                        new object[] { leg, Enum.ToObject(level, segment), bone });
                    Check(Math.Abs(diameters[0] * 1000 - expected[caseIndex][0]) < .001 &&
                        Math.Abs(diameters[1] * 1000 - expected[caseIndex++][1]) < .001, "shared fracture/gib anatomical diameter " + caseIndex);
                }
        using var assembly = AssemblyDefinition.ReadAssembly(plugin.Location);
        using var game = AssemblyDefinition.ReadAssembly(typeof(EFT.Ballistics.Shot).Assembly.Location);
        var predicate = game.MainModule.Types.Single(t => t.FullName == "EFT.HealthSystem.BaseHealthController`1").NestedTypes.Single(t => t.Name == "CG_IsBodyPartBroken")
            .Methods.Single(m => m.Name == "method_0");
        Check(predicate.Body.Instructions.Any(i => i.OpCode == OpCodes.Isinst && ((TypeReference)i.Operand).FullName == "EFT.HealthSystem.IFracture") &&
            predicate.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "get_Active"), "installed EFT fracture predicate remains active EFT.HealthSystem.IFracture");
        var system = assembly.MainModule.GetType("Rupture.Dismemberment.Classes.Fractures.FractureSystem");
        var mask = system.Methods.Single(m => m.Name == "FractureMask");
        Check(mask.Body.Instructions.Any(i => i.OpCode == OpCodes.Isinst && ((TypeReference)i.Operand).FullName == "EFT.HealthSystem.IFracture") &&
            !mask.Body.Instructions.Any(i => i.Operand is MethodReference m && (m.Name == "GetBodyPartHealth" || m.Name == "IsBodyPartDestroyed")),
            "fracture state has no HP/blackout exclusion");
        var startup = assembly.MainModule.GetType("Rupture.RupturePlugin");
        foreach (string method in new[] { "Update", "LateUpdate", "OnDestroy" })
        {
            var calls = startup.Methods.Single(m => m.Name == method).Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
            bool direct = calls.Any(m => m.DeclaringType.FullName == system.FullName);
            bool sharedReset = calls.Any(m => m.DeclaringType.FullName == startup.FullName && m.Name == "ResetRuntimeServices") &&
                startup.Methods.Single(m => m.Name == "ResetRuntimeServices").Body.Instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.FullName == system.FullName && m.Name == "Reset");
            Check(direct || sharedReset, "fracture lifecycle wired into " + method);
        }
        var reset = assembly.MainModule.GetType("Rupture.Dismemberment.Classes.Limbs.LimbSeverMaterialProvider").Methods.Single(m => m.Name == "Reset");
        var resetCalls = reset.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        Check(Array.FindIndex(resetCalls, m => m.DeclaringType.FullName == system.FullName && m.Name == "Reset") <
            Array.FindIndex(resetCalls, m => m.Name == "Destroy"), "fracture owners are removed before the shared material is destroyed, including raid-start reset");
    }
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
}
