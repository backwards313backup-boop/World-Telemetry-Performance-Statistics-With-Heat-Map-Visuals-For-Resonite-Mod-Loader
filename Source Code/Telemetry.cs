using System.Reflection;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;

namespace WorldTelemetry;

internal enum ReportMode
{
    None,
    Latest,
    Timestamped
}

internal sealed record HistoryPoint(DateTime Time, double Fps, double? CpuMs, double BonesMs, double GpuScore, double LightMs, double NetBytesPerSecond, int Objects, int Slots, int Users);

internal static class Telemetry
{
    private const int HistoryLimit = 120;

    internal static readonly List<HistoryPoint> History = new();
    internal static Snapshot? Previous { get; private set; }
    internal static int HistoryVersion { get; private set; }

    private const long ScanTimeoutMs = 120000;
    private const long ForgetIntervalMs = 2000;
    private const double ScanSliceMs = 4;

    private static readonly MethodInfo? AddNotification = AccessTools.Method(typeof(NotificationPanel), "AddNotification",
        [typeof(string), typeof(string), typeof(Uri), typeof(colorX), typeof(NotificationType), typeof(string), typeof(Uri), typeof(IAssetProvider<AudioClip>)]);

    private static CpuResult? _lastCpu;
    private static bool _scanning;
    private static long _scanStarted;
    private static int _scanGeneration;
    private static ReportMode _reportAfterProfile;
    private static bool _quietProfile;
    private static World? _queuedScan;
    private static ReportMode _queuedReport;
    private static bool _autoCycle;
    private static long _nextAuto;
    private static long _nextForget;
    private static (World World, int Seconds, ReportMode Report, bool Quiet)? _pendingProfile;

    internal static Snapshot? Latest { get; private set; }
    internal static int Version { get; private set; }
    internal static string Status { get; private set; } = "";
    internal static bool Scanning => _scanning;
    internal static bool AutoCycleRunning => _autoCycle;
    internal static bool Measuring => LightProfiler.Running || PhysicsProfiler.Running;
    internal static double SecondsToNextAuto => Math.Max(0, (_nextAuto - Environment.TickCount64) / 1000.0);

    internal static World? FocusedWorld
    {
        get
        {
            World? world = Engine.Current?.WorldManager?.FocusedWorld;
            if (world is null || world.IsDestroyed || world == Userspace.UserspaceWorld)
                return null;

            return world;
        }
    }

    internal static readonly List<(DateTime Time, string Message)> Activity = new();
    internal static int ActivityVersion { get; private set; }

    internal static void SetStatus(string status)
    {
        Status = status;
        Log(status);
    }

    internal static void Log(string message)
    {
        if (message.Length == 0 || (Activity.Count > 0 && Activity[^1].Message == message))
            return;

        Activity.Add((DateTime.Now, message));
        if (Activity.Count > 100)
            Activity.RemoveAt(0);

        ActivityVersion++;
    }

    internal static void Tick()
    {
        if (_lastCpu is CpuResult cpu && cpu.World.IsDestroyed)
            _lastCpu = null;

        if (Latest is Snapshot latest && latest.World.IsDestroyed)
        {
            Latest = null;
            Version++;
        }
        if (_scanning && Environment.TickCount64 - _scanStarted > ScanTimeoutMs)
        {
            _scanning = false;
            _scanGeneration++;
            SetStatus("The scan did not finish. The world may have closed.");
        }
        if (!CpuProfiler.Running && _pendingProfile is (World pendingWorld, int pendingSeconds, ReportMode pendingReport, bool pendingQuiet))
        {
            _pendingProfile = null;
            if (!pendingWorld.IsDestroyed)
                RequestProfile(pendingWorld, pendingSeconds, pendingReport, pendingQuiet);
        }
        if (!_scanning && !Measuring && _queuedScan is World queued)
        {
            ReportMode report = _queuedReport;
            _queuedScan = null;
            _queuedReport = ReportMode.None;
            if (queued.IsDestroyed)
            {
                if (report == ReportMode.Timestamped)
                    Notify("The world closed before its report could be written.");
            }
            else
                RequestScan(queued, report);
        }
        TickAutoScan();
        long now = Environment.TickCount64;
        if (now >= _nextForget)
        {
            _nextForget = now + ForgetIntervalMs;
            ForgetClosedWorlds();
        }
    }

    private static void ForgetClosedWorlds()
    {
        if (Previous is Snapshot previous && previous.World.IsDestroyed)
        {
            Previous = null;
            History.Clear();
            HistoryVersion++;
        }
        LightProfiler.ForgetClosedWorld();
        PhysicsProfiler.ForgetClosedWorld();
        Thumbnails.ForgetClosedWorlds();
        WarningActions.ForgetClosedWorlds();
    }

    private static void TickAutoScan()
    {
        long now = Environment.TickCount64;
        if (_autoCycle)
        {
            if (CpuProfiler.Running || _scanning || _queuedScan is not null || _pendingProfile is not null)
                return;

            _autoCycle = false;
            _nextAuto = now + WorldTelemetryMod.AutoScanSeconds * 1000L;
            return;
        }
        if (!WorldTelemetryMod.AutoScan || now < _nextAuto || CpuProfiler.Running || _scanning || Measuring || _pendingProfile is not null)
            return;

        World? world = FocusedWorld;
        if (world is null)
            return;

        _autoCycle = true;
        RequestProfile(world, WorldTelemetryMod.ProfileSeconds, WorldTelemetryMod.AutoScanReport ? ReportMode.Latest : ReportMode.None, true);
    }

    internal static void SetAutoScan(bool enabled)
    {
        WorldTelemetryMod.SetAutoScan(enabled);
        _nextAuto = 0;
        SetStatus(enabled
            ? $"Auto scan is on. The focused world is measured every {Format.Seconds(WorldTelemetryMod.AutoScanSeconds)}."
            : "Auto scan is off.");
    }

    internal static CpuResult? CpuFor(World world) => _lastCpu is CpuResult cpu && cpu.World == world ? cpu : null;

    internal static void RequestScan(World world, ReportMode report)
    {
        if (world.IsDestroyed)
            return;

        if (_scanning || Measuring)
        {
            if (_queuedScan is not null && _queuedScan != world)
                _queuedReport = ReportMode.None;

            _queuedScan = world;
            if (report > _queuedReport)
                _queuedReport = report;

            if (!_scanning)
                SetStatus("The scan will run when the light or physics measurement finishes.");

            return;
        }
        World? userspace = Userspace.UserspaceWorld;
        if (userspace is null)
            return;

        CpuResult? cpu = CpuFor(world);
        IReadOnlyDictionary<Light, LightResult>? lights = LightProfiler.ResultsFor(world);
        IReadOnlyDictionary<RefID, PhysicsResult>? physics = PhysicsProfiler.ResultsFor(world);
        int generation = ++_scanGeneration;
        _scanning = true;
        _scanStarted = Environment.TickCount64;
        SetStatus("Scanning the world...");
        world.RunSynchronously(() => world.Coroutines.StartCoroutine(RunScan(world, cpu, lights, physics, generation, report, userspace)));
    }

    private static IEnumerator<Context> RunScan(World world, CpuResult? cpu, IReadOnlyDictionary<Light, LightResult>? lights, IReadOnlyDictionary<RefID, PhysicsResult>? physics, int generation, ReportMode report, World userspace)
    {
        Scanner.ScanJob? job = null;
        string error = "";
        try
        {
            job = new Scanner.ScanJob(world, cpu, lights, physics);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            WorldTelemetryMod.LogWarning("World scan failed: " + ex);
        }
        while (job is not null && generation == _scanGeneration && !world.IsDestroyed)
        {
            bool finished = false;
            try
            {
                finished = job.Step(ScanSliceMs);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                WorldTelemetryMod.LogWarning("World scan failed: " + ex);
                job = null;
            }
            if (finished || job is null)
                break;

            yield return Context.WaitForNextUpdate();
        }
        Snapshot? snapshot = job?.Result;
        if (snapshot is null && error.Length == 0)
            error = "the world closed or the scan was replaced.";

        userspace.RunSynchronously(() => CompleteScan(generation, snapshot, error, report));
    }

    private static void CompleteScan(int generation, Snapshot? snapshot, string error, ReportMode report)
    {
        if (generation != _scanGeneration)
            return;

        _scanning = false;
        if (snapshot is null)
        {
            SetStatus("The scan failed: " + error);
            if (report == ReportMode.Timestamped)
                Notify("The world report could not be written because the scan failed.");

            return;
        }
        if (Latest is Snapshot old && old.World == snapshot.World)
            Previous = old;
        else
        {
            Previous = null;
            History.Clear();
        }
        Latest = snapshot;
        Version++;
        History.Add(new HistoryPoint(
            DateTime.Now,
            snapshot.Fps,
            snapshot.Cpu?.TotalMs,
            snapshot.Entries.Sum(entry => entry.BonesMs),
            snapshot.Entries.Sum(entry => entry.GpuScore),
            snapshot.Entries.Sum(entry => entry.Light.ValueMs),
            snapshot.Entries.Sum(entry => entry.Extra.NetBytesPerSecond) + snapshot.NetUnattributedBytesPerSecond,
            snapshot.Entries.Count,
            snapshot.TotalSlots,
            snapshot.Entries.Count(entry => entry.IsUser)));
        if (History.Count > HistoryLimit)
            History.RemoveAt(0);

        HistoryVersion++;
        SetStatus($"Scanned {snapshot.Entries.Count:N0} objects, {snapshot.TotalSlots:N0} slots and {snapshot.TotalComponents:N0} components in {snapshot.ScanMs:F0} ms.");
        if (report != ReportMode.None)
            WriteReport(snapshot, report);
    }

    internal static void RequestProfile(World world, int seconds, ReportMode report, bool quiet = false)
    {
        if (CpuProfiler.Running)
        {
            if (CpuProfiler.Target == world)
            {
                if (report > _reportAfterProfile)
                    _reportAfterProfile = report;

                if (!quiet)
                    _quietProfile = false;

                return;
            }
            ReportMode queuedReport = _pendingProfile is { } pending && pending.World == world && pending.Report > report ? pending.Report : report;
            _pendingProfile = (world, seconds, queuedReport, quiet);
            SetStatus($"Waiting for the CPU profile of another world to finish, then profiling {Scanner.PlainText(world.Name, "this world")}.");
            return;
        }
        bool started = CpuProfiler.Start(world, seconds, result =>
        {
            _lastCpu = result;
            ReportMode after = _reportAfterProfile;
            _reportAfterProfile = ReportMode.None;
            if (!_quietProfile || result.Coverage() < 0.9 || result.MissingHooks > 0)
                WorldTelemetryMod.Log($"CPU profile of {Scanner.PlainText(result.World.Name, "the world")}: {result.Frames:N0} frames in {Format.Seconds(result.Seconds)}. Timed {result.CoverageLine()}.");

            RequestScan(result.World, after);
        }, () =>
        {
            ReportMode after = _reportAfterProfile;
            _reportAfterProfile = ReportMode.None;
            SetStatus("The CPU profile stopped before it finished, usually because the world closed.");
            if (after == ReportMode.Timestamped)
                Notify("The CPU profile stopped before it finished, so the world report was not written.");
        }, out string error);
        if (!started)
        {
            SetStatus(error);
            if (report != ReportMode.None)
                RequestScan(world, report);

            return;
        }
        _reportAfterProfile = report;
        _quietProfile = quiet;
        SetStatus($"Profiling the CPU for {Format.Seconds(seconds)}...");
    }

    internal static void WriteLog()
    {
        World? world = FocusedWorld;
        if (world is null)
        {
            Notify("No world is focused, so there is nothing to report.");
            return;
        }
        int seconds = WorldTelemetryMod.KeyProfileSeconds;
        string name = Scanner.PlainText(world.Name, "this world");
        if (seconds > 0)
        {
            RequestProfile(world, seconds, ReportMode.Timestamped);
            Notify($"Measuring {name} for {Format.Seconds(seconds)}, then writing the report.");
            return;
        }
        RequestScan(world, ReportMode.Timestamped);
        Notify($"Writing the report for {name}.");
    }

    internal static void WriteReport(Snapshot snapshot, ReportMode mode)
    {
        World? userspace = Userspace.UserspaceWorld;
        string worldName = snapshot.WorldName;
        bool latest = mode == ReportMode.Latest;
        Task.Run(() =>
        {
            string message;
            bool failed = false;
            try
            {
                string text = ReportWriter.Build(snapshot);
                string path = ReportWriter.Save(worldName, text, latest);
                message = latest
                    ? $"Auto scan updated {Path.GetFileName(path)} at {DateTime.Now:HH:mm:ss}."
                    : "World report saved: " + path;
            }
            catch (Exception ex)
            {
                failed = true;
                message = "Could not save the world report: " + ex.Message;
                WorldTelemetryMod.LogWarning(message);
            }
            userspace?.RunSynchronously(() =>
            {
                SetStatus(message);
                if (!latest && !failed)
                    Notify(message);
            });
        });
    }

    internal static void Notify(string message)
    {
        WorldTelemetryMod.Log(message);
        NotificationPanel? panel = NotificationPanel.Current;
        if (panel is null || panel.IsDestroyed || AddNotification is null)
            return;

        panel.RunSynchronously(() =>
        {
            try
            {
                AddNotification.Invoke(panel, [null, message, null, RadiantUI_Constants.Neutrals.DARK, NotificationType.ToastOnly, "World Telemetry", null, null]);
            }
            catch (Exception)
            {
            }
        });
    }
}
