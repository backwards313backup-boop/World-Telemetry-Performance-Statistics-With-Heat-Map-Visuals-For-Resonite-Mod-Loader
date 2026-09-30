using FrooxEngine;

namespace WorldTelemetry;

internal sealed record LightResult(double Ms, double Noise, int Samples);

internal static class LightProfiler
{
    private const string TestName = "WorldTelemetry Light Test";
    private const int Blocks = 5;
    private const int SettleFrames = 4;
    private const int RestoreFrames = 3;
    private const long InstallTimeoutMs = 2000;

    private enum Phase
    {
        Idle,
        Collecting,
        Installing,
        Measuring,
        Restoring
    }

    private sealed class Test
    {
        internal required Light Light { get; init; }
        internal required string Name { get; init; }
        internal Slot? Slot;
        internal ValueField<bool>? Source;
        internal ValueCopy<bool>? Copy;
    }

    private static Phase _phase = Phase.Idle;
    private static World? _world;
    private static List<Test> _tests = new();
    private static int _index;
    private static int _block;
    private static long _blockEnds;
    private static long _phaseStarted;
    private static int _settle;
    private static int _restoreFrames;
    private static readonly List<double>[] Samples = Enumerable.Range(0, Blocks).Select(_ => new List<double>()).ToArray();
    private static readonly Dictionary<Light, LightResult> Results = new(ReferenceEqualityComparer.Instance);
    private static World? _resultsWorld;
    private static int _skipped;
    private static bool _usedGpuTime;

    internal static bool Running => _phase != Phase.Idle;
    internal static int Done => _index;
    internal static int Total => _tests.Count;
    internal static string CurrentName => _index < _tests.Count ? _tests[_index].Name : "";

    internal static IReadOnlyDictionary<Light, LightResult>? ResultsFor(World world) => _resultsWorld == world && Results.Count > 0 ? Results : null;

    internal static int MeasuredCount(World world) => _resultsWorld == world ? Results.Count : 0;

    internal static void Start(World world)
    {
        if (Running)
            return;

        if (PhysicsProfiler.Running)
        {
            Telemetry.SetStatus("Wait for the physics measurement to finish first.");
            return;
        }
        World? userspace = Userspace.UserspaceWorld;
        if (userspace is null)
            return;

        _phase = Phase.Collecting;
        _world = world;
        int maximum = WorldTelemetryMod.LightTestMaximum;
        world.RunSynchronously(() =>
        {
            List<Test> tests = new();
            try
            {
                tests = world.RootSlot.GetComponentsInChildren<Light>(light => light.Enabled && light.Slot.IsActive && !light.EnabledField.IsDriven)
                    .OrderByDescending(Weight)
                    .Take(maximum)
                    .Select(light => new Test { Light = light, Name = Scanner.PlainText(light.Slot.Name, "Light") })
                    .ToList();
            }
            catch (Exception ex)
            {
                WorldTelemetryMod.LogWarning("Could not list the lights: " + ex.Message);
            }
            userspace.RunSynchronously(() => Begin(world, tests));
        });
    }

    private static double Weight(Light light)
    {
        double range = light.LightType.Value == Renderite.Shared.LightType.Directional ? 1000 : light.Range.Value;
        return range * (light.ShadowType.Value == Renderite.Shared.ShadowType.None ? 1 : 6);
    }

    private static void Begin(World world, List<Test> tests)
    {
        if (_phase != Phase.Collecting || _world != world)
            return;

        if (tests.Count == 0)
        {
            _phase = Phase.Idle;
            Telemetry.SetStatus("No enabled realtime lights to measure in this world.");
            return;
        }
        if (_resultsWorld != world)
            Results.Clear();

        _resultsWorld = world;
        _tests = tests;
        _index = 0;
        _skipped = 0;
        _usedGpuTime = false;
        Telemetry.SetStatus($"Measuring {tests.Count} lights. Each light is switched off for a moment on your side only. Stand where you normally are and look around the area.");
        Install();
    }

    internal static void Cancel(string reason)
    {
        if (!Running)
            return;

        if (_index < _tests.Count)
            Remove(_tests[_index]);

        _phase = Phase.Idle;
        Telemetry.SetStatus(reason);
    }

    internal static void Tick()
    {
        if (_phase is Phase.Idle or Phase.Collecting)
            return;

        World? world = _world;
        if (world is null || world.IsDestroyed)
        {
            _phase = Phase.Idle;
            return;
        }
        if (world != Telemetry.FocusedWorld)
        {
            Cancel("Light measurement stopped because the focused world changed.");
            return;
        }
        Test test = _tests[_index];
        long now = Environment.TickCount64;
        switch (_phase)
        {
            case Phase.Installing:
                if (test.Copy is not null && test.Copy.Target.IsLinkValid)
                {
                    StartBlock(0);
                    _phase = Phase.Measuring;
                }
                else if (now - _phaseStarted > InstallTimeoutMs)
                {
                    _skipped++;
                    Remove(test);
                    Next();
                }
                break;
            case Phase.Measuring:
                if (_settle > 0)
                {
                    _settle--;
                    break;
                }
                double sample = Sample();
                if (sample > 0 && !double.IsNaN(sample))
                    Samples[_block].Add(sample);

                if (now < _blockEnds)
                    break;

                if (_block + 1 < Blocks)
                {
                    StartBlock(_block + 1);
                    break;
                }
                SetEnabled(test, true);
                _restoreFrames = RestoreFrames;
                _phase = Phase.Restoring;
                break;
            case Phase.Restoring:
                if (--_restoreFrames > 0)
                    break;

                Record(test);
                Remove(test);
                Next();
                break;
        }
    }

    private static double Sample()
    {
        PerformanceStats? stats = Engine.Current?.PerfStats;
        if (stats is null)
            return double.NaN;

        if (stats.RenderTime > 0)
        {
            _usedGpuTime = true;
            return stats.RenderTime * 1000.0;
        }
        return stats.ImmediateFPS > 0 ? 1000.0 / stats.ImmediateFPS : double.NaN;
    }

    private static void StartBlock(int block)
    {
        _block = block;
        Samples[block].Clear();
        _settle = SettleFrames;
        _blockEnds = Environment.TickCount64 + (long)(WorldTelemetryMod.LightTestBlockSeconds * 1000);
        SetEnabled(_tests[_index], block % 2 == 0);
    }

    private static void Install()
    {
        if (_index >= _tests.Count)
        {
            Finish();
            return;
        }
        Test test = _tests[_index];
        _phase = Phase.Installing;
        _phaseStarted = Environment.TickCount64;
        World world = _world!;
        foreach (List<double> block in Samples)
            block.Clear();

        world.RunSynchronously(() =>
        {
            if (test.Light.IsDestroyed || test.Light.EnabledField.IsDriven)
                return;

            Slot slot = world.AddLocalSlot(TestName, false);
            slot.Tag = Scanner.OwnTag;
            ValueField<bool> source = slot.AttachComponent<ValueField<bool>>();
            source.Value.Value = true;
            ValueCopy<bool> copy = slot.AttachComponent<ValueCopy<bool>>();
            copy.Source.Target = source.Value;
            copy.Target.Target = test.Light.EnabledField;
            test.Slot = slot;
            test.Source = source;
            test.Copy = copy;
        });
    }

    private static void SetEnabled(Test test, bool enabled)
    {
        World? world = _world;
        if (world is null || test.Source is null)
            return;

        ValueField<bool> source = test.Source;
        world.RunSynchronously(() =>
        {
            if (!source.IsDestroyed)
                source.Value.Value = enabled;
        });
    }

    private static void Remove(Test test)
    {
        World? world = _world;
        Slot? slot = test.Slot;
        ValueField<bool>? source = test.Source;
        test.Slot = null;
        test.Source = null;
        test.Copy = null;
        if (world is null || slot is null || world.IsDestroyed)
            return;

        world.RunSynchronously(() =>
        {
            if (source is not null && !source.IsDestroyed)
                source.Value.Value = true;

            world.Coroutines.RunInUpdates(2, () =>
            {
                if (!slot.IsDestroyed)
                    slot.Destroy();
            });
        });
    }

    private static void Record(Test test)
    {
        var on = new List<double>();
        var off = new List<double>();
        for (int i = 0; i < Blocks; i++)
            (i % 2 == 0 ? on : off).AddRange(Samples[i]);

        if (on.Count < 3 || off.Count < 3)
        {
            _skipped++;
            return;
        }
        double onMedian = Median(on);
        double offMedian = Median(off);
        double noise = Math.Sqrt(Variance(on, on.Average()) / on.Count + Variance(off, off.Average()) / off.Count);
        Results[test.Light] = new LightResult(onMedian - offMedian, noise, on.Count + off.Count);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) * 0.5;
    }

    internal static void ForgetClosedWorld()
    {
        if (Running)
            return;

        if (_resultsWorld is not null && _resultsWorld.IsDestroyed)
        {
            Results.Clear();
            _resultsWorld = null;
        }
        if (_world is not null && _world.IsDestroyed)
        {
            _world = null;
            _tests = new List<Test>();
            _index = 0;
        }
    }

    private static double Variance(List<double> values, double mean) => values.Count < 2 ? 0 : values.Sum(value => (value - mean) * (value - mean)) / (values.Count - 1);

    private static void Next()
    {
        _index++;
        Install();
    }

    private static void Finish()
    {
        _phase = Phase.Idle;
        World? world = _world;
        string method = _usedGpuTime ? "GPU time" : "renderer frame time (keep the frame rate uncapped for this)";
        KeyValuePair<Light, LightResult>[] top = Results.Where(pair => !pair.Key.IsDestroyed).OrderByDescending(pair => pair.Value.Ms).Take(3).ToArray();
        string heaviest = top.Length == 0 ? "" : " Heaviest: " + string.Join(", ", top.Select(pair => $"{Scanner.PlainText(pair.Key.Slot.Name, "Light")} {Format.Ms(Math.Max(0, pair.Value.Ms))}")) + ".";
        Telemetry.SetStatus($"Measured {Results.Count} lights by {method}{(_skipped > 0 ? $", {_skipped} could not be tested" : "")}.{heaviest}");
        if (world is not null && !world.IsDestroyed)
            Telemetry.RequestScan(world, ReportMode.None);
    }
}
