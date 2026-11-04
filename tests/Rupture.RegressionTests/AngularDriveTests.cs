using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

internal static class AngularDriveTests
{
    internal static void Run(Assembly assembly)
    {
        const BindingFlags flags=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        void Check(bool value,string name) { if(!value)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name); }
        const string angular="Rupture.Ragdolls.Classes.AngularDriveExperiment", world="Rupture.Ragdolls.Classes.ShadowRagdollWorld";
        Type experiment=assembly.GetType(angular,true);
        Check(!(bool)experiment.GetProperty("Enabled",flags).GetValue(null) && (float)experiment.GetField("Duration",flags).GetRawConstantValue()==.45f,
            "console comparison override defaults off; configured production resistance has a bounded 450ms transition");
        using var module=ModuleDefinition.ReadModule(assembly.Location);
        bool Calls(string type,string method,string target)=>module.GetType(type).Methods.Where(m=>m.Name==method&&m.HasBody)
            .SelectMany(m=>m.Body.Instructions).Any(i=>i.Operand is MethodReference called&&called.Name==target);
        Check(Calls(angular,"Select","Inherited") && Calls("Rupture.Ragdolls.Classes.RootVelocityExperiment","Select","get_Enabled") &&
            Calls("Rupture.Ragdolls.Patches.RootVelocityHooks","After","Capture"),
            "angular controls fix inherited velocity and capture the native Start pose");
        Check(Calls(world,"Simulate","BeforeExternalStep") && Calls(world,"CompleteExternalStep","ExternalStepCompleted") &&
            Calls(world,"MakeDormant","End") && Calls("Rupture.Ragdolls.Classes.LethalImpulseExperiment","End","End"),
            "angular lifetime follows the existing owner, dormancy and settlement boundaries");
        Check(Calls(world,"TryPrepareAngularDrives","get_IsValid") && Calls(world,"TryPrepareAngularDrives","get_IsBroken") &&
            Calls(world,"TryPrepareAngularDrives","IsDragHeld"),"angular setup rejects invalid, broken and dragged connections");
        var methods=module.GetType(world).Methods.Where(m=>m.HasBody && (m.Name.Contains("Angular")||m.Name=="TryAngularPair"));
        Check(!methods.SelectMany(m=>m.Body.Instructions).Any(i=>i.Operand is MethodReference c&&
            (c.Name=="set_rotation"||c.Name=="set_angularVelocity"||c.Name=="AddTorque"||c.Name=="set_velocity"||c.Name=="WakeUp")),
            "angular experiment issues joint drives without writing body poses/velocities or waking bodies");
        string backend=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(assembly.Location),"Rupture.PhysXBackend.dll");
        if(!System.IO.File.Exists(backend)) backend=System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(assembly.Location),"../../../../Rupture.PhysXBackend/bin/Release/netstandard2.1/Rupture.PhysXBackend.dll"));
        using var native=ModuleDefinition.ReadModule(backend);
        var api=native.GetType("Rupture.PhysXBackend.NativeApi");
        Check(new[]{"SetD6Drives","GetD6Drives"}.All(name=>api.Methods.Where(m=>m.IsConstructor&&m.HasBody)
            .SelectMany(m=>m.Body.Instructions).Any(i=>i.Operand is FieldReference f&&f.Name==name &&
                i.Previous?.Operand is MethodReference c&&c.Name=="TryLoad")),
            "new native drive exports are optional additive ABI capability");
        var runtime=native.GetType("Rupture.PhysXBackend.ExternalPhysXRuntime");
        Check(new[]{"TrySetAngularDrives","TryReadAngularDrives"}.All(name=>{
            var instructions=runtime.Methods.Single(m=>m.Name==name).Body.Instructions;
            return instructions.Any(i=>i.Operand is MethodReference c&&c.Name=="AtControlBoundary") &&
                instructions.Any(i=>i.Operand is MethodReference c&&c.Name=="InvokeOwner");
        }),"native angular configuration and reads use completed-step owner transactions");
    }
}
