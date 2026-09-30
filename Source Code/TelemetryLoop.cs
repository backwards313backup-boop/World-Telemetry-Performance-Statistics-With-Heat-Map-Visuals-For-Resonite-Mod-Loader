using FrooxEngine;

namespace WorldTelemetry;

internal static class TelemetryLoop
{
    private const int MaximumFailures = 20;
    private const long PauseMs = 30000;

    private sealed class Part
    {
        internal required string Name { get; init; }
        internal required Action Tick { get; init; }
        internal Action? Cleanup { get; init; }
        internal int Failures;
        internal long PausedUntil;
    }

    private static readonly Part[] Parts =
    [
        new Part { Name = "CPU profiler", Tick = CpuProfiler.Tick, Cleanup = CpuProfiler.Cancel },
        new Part { Name = "light measurement", Tick = LightProfiler.Tick, Cleanup = () => LightProfiler.Cancel("Light measurement stopped after repeated errors.") },
        new Part { Name = "physics measurement", Tick = PhysicsProfiler.Tick, Cleanup = () => PhysicsProfiler.Cancel("Physics measurement stopped after repeated errors.") },
        new Part { Name = "scanner", Tick = Telemetry.Tick },
        new Part { Name = "heatmap", Tick = Heatmap.Tick, Cleanup = Heatmap.Shutdown },
        new Part { Name = "selection box", Tick = Selection.Tick, Cleanup = Selection.Clear },
        new Part { Name = "dash screen", Tick = DashScreen.Tick },
        new Part { Name = "thumbnails", Tick = Thumbnails.Tick, Cleanup = Thumbnails.ClearQueue }
    ];

    private static int _started;

    internal static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        Task.Run(async () =>
        {
            World? userspace = Userspace.UserspaceWorld;
            while (userspace is null || userspace.IsDestroyed)
            {
                await Task.Delay(250).ConfigureAwait(false);
                userspace = Userspace.UserspaceWorld;
            }
            userspace.RunSynchronously(() =>
            {
                userspace.Coroutines.StartCoroutine(Run());
                WorldTelemetryMod.Log("Userspace is ready. The World Telemetry dash screen is active.");
            });
        });
    }

    private static IEnumerator<Context> Run()
    {
        while (true)
        {
            long now = Environment.TickCount64;
            foreach (Part part in Parts)
                if (now >= part.PausedUntil)
                    TickPart(part, now);

            yield return Context.WaitForNextUpdate();
        }
    }

    private static void TickPart(Part part, long now)
    {
        try
        {
            part.Tick();
            part.Failures = 0;
        }
        catch (Exception ex)
        {
            if (++part.Failures < MaximumFailures)
            {
                if (part.Failures == 1)
                    WorldTelemetryMod.LogWarning($"WorldTelemetry {part.Name} update failed: {ex}");

                return;
            }
            part.Failures = 0;
            part.PausedUntil = now + PauseMs;
            WorldTelemetryMod.LogWarning($"WorldTelemetry {part.Name} failed {MaximumFailures} times in a row and is paused for {Format.Seconds(PauseMs / 1000.0)}. Last error: {ex.Message}");
            try
            {
                part.Cleanup?.Invoke();
            }
            catch (Exception cleanup)
            {
                WorldTelemetryMod.LogWarning($"Could not clean up the {part.Name}: {cleanup.Message}");
            }
        }
    }
}
