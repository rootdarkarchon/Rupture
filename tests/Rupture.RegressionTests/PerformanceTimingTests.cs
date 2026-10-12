using System;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;
using BepInEx.Configuration;

internal static class PerformanceTimingTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static void Run(Assembly assembly)
    {
        Type world = assembly.GetType("Rupture.Ragdolls.Classes.ShadowRagdollWorld", true);
        Type type = world.GetNestedType("TimingMetric", BindingFlags.NonPublic);
        object metric = Activator.CreateInstance(type, true);
        MethodInfo record = type.GetMethod("Record", Instance);
        double Value(string property) => (double)type.GetProperty(property, Instance).GetValue(metric);
        long Samples() => (long)type.GetProperty("Samples", Instance).GetValue(metric);
        double P99() => (double)type.GetMethod("P99Milliseconds", Instance).Invoke(metric, null);
        void Record(double value) => record.Invoke(metric, new object[] { value });

        Check(Samples() == 0 && Value("RecentAverageMilliseconds") == 0 && P99() == 0, "empty timing window");
        Record(100);
        for (int i = 0; i < 511; i++) Record(1);
        Check(Samples() == 512 && Math.Abs(Value("RecentAverageMilliseconds") - 611d / 512d) < 1e-9,
            "recent average includes an early spike before the ring wraps");
        Record(1);
        Check(Value("RecentAverageMilliseconds") == 1 && P99() == 1 && Value("MaximumMilliseconds") == 100 &&
            Math.Abs(Value("AverageMilliseconds") - 612d / 513d) < 1e-9,
            "expired spike leaves recent statistics but remains in cumulative mean and maximum");
        for (int i = 0; i < 600; i++) Record(2);
        Check(Value("RecentAverageMilliseconds") == 2 && P99() == 2, "recent statistics survive repeated wraparound");
        type.GetMethod("Reset", Instance).Invoke(metric, null);
        Check(Samples() == 0 && Value("AverageMilliseconds") == 0 && Value("RecentAverageMilliseconds") == 0 &&
            Value("MaximumMilliseconds") == 0 && P99() == 0, "raid/toggle reset clears both timing windows");
        Record(3);
        Check(Samples() == 1 && Value("RecentAverageMilliseconds") == 3 && P99() == 3, "new capture has no stale samples");
        Record(5);
        double PerFrame(long frames) => (double)type.GetMethod("AveragePerFrame", Instance).Invoke(metric, new object[] { frames });
        Check(Value("AverageMilliseconds") == 4 && Value("TotalMilliseconds") == 8 && PerFrame(1) == 8 && PerFrame(4) == 2,
            "multiple operations per frame and idle frames normalize total cost rather than per-call mean");
        Check(PerFrame(0) == 0 && PerFrame(-1) == 0, "empty frame denominator is safe");
        object snapshot = Activator.CreateInstance(type, true);
        type.GetMethod("CopyTo", Instance).Invoke(metric, new[] { snapshot });
        type.GetMethod("Reset", Instance).Invoke(metric, null);
        string report = System.Threading.Tasks.Task.Run(() => ((IFormattable)snapshot).ToString(null, System.Globalization.CultureInfo.InvariantCulture)).GetAwaiter().GetResult();
        Check(report == "5.000/4.000/5.000/5.000ms/2", "worker formats an immutable snapshot after live metrics reset");

        Type scope = world.GetNestedType("MainThreadTrace", BindingFlags.NonPublic);
        // The disabled/default scope must dispose without invoking a Unity profiler API.
        ((IDisposable)Activator.CreateInstance(scope)).Dispose();
        VerifyWorker(assembly, world, type);
        Console.WriteLine("PASS: performance timing recent/cumulative windows, wraparound, reset, frame normalization and disabled scope.");
    }

    private static void VerifyWorker(Assembly assembly, Type world, Type metricType)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var pluginLog = assembly.GetType("Rupture.RupturePlugin", true).GetProperty("Log", statics);
        object previousLog = pluginLog.GetValue(null);
        var debug = (ConfigEntryBase)assembly.GetType("Rupture.RuptureConfiguration", true).GetProperty("EnableDebugLogging", statics).GetValue(null);
        object previousDebug = debug.BoxedValue;
        using var source = new ManualLogSource("worker timing regression");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var lines = new List<string>(); int writerThread = 0, mainThread = Environment.CurrentManagedThreadId;
        source.LogEvent += (_, e) =>
        {
            writerThread = Environment.CurrentManagedThreadId;
            lock (lines) lines.Add(e.Data.ToString());
            if (!entered.IsSet) { entered.Set(); if (!release.Wait(5000)) throw new Exception("diagnostic test gate timeout"); }
        };
        var pending = world.GetField("_performanceReportPending", statics);
        Action queue = () => world.GetMethod("LogMainThreadStageTimings", statics).Invoke(null, null);
        Action wait = () => Check(SpinWait.SpinUntil(() => (int)pending.GetValue(null) == 0, 5000), "worker finishes without a main-thread join");
        try
        {
            debug.BoxedValue = true;
            pluginLog.SetValue(null, Activator.CreateInstance(assembly.GetType("Rupture.RuptureLog", true), Instance, null, new object[] { source }, null));
            var live = world.GetField("PluginUpdateTiming", statics).GetValue(null);
            metricType.GetMethod("Reset", Instance).Invoke(live, null);
            metricType.GetMethod("Record", Instance).Invoke(live, new object[] { 3d });
            metricType.GetMethod("Record", Instance).Invoke(live, new object[] { 5d });
            queue(); Check(entered.Wait(5000), "worker reaches the sink");
            for (int i = 0; i < 30; i++) queue();
            metricType.GetMethod("Record", Instance).Invoke(live, new object[] { 999d });
            release.Set(); wait();
            Check(writerThread != mainThread && lines.Count == 13, "slow sink writes one bounded report on a worker");
            Check(lines[0].Contains("update:5.000/4.000/5.000/5.000ms/2") && lines[1].Contains("updateFrames=2;"), "callback and stage report share the frozen frame boundary");
            lines.Clear(); entered.Reset(); release.Reset();
            queue(); Check(entered.Wait(5000), "next report starts after the previous worker retires");
            world.GetMethod("ResetMainThreadStageTimings", statics).Invoke(null, null);
            release.Set(); wait();
            Check(lines.Count == 1, "raid reset suppresses remaining stale report lines");
            lines.Clear(); entered.Reset(); release.Reset();
            world.GetField("_performanceLoggingWasEnabled", statics).SetValue(null, true);
            queue(); Check(entered.Wait(5000), "report starts before logging is disabled");
            world.GetMethod("UpdateStatusLogging", statics).Invoke(null, new object[] { false });
            release.Set(); wait();
            Check(lines.Count == 1, "logging disable cancels an in-flight report");
        }
        finally { release.Set(); wait(); pluginLog.SetValue(null, previousLog); debug.BoxedValue = previousDebug; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Performance timing: " + message);
    }
}
