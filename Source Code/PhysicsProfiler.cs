using Elements.Core;
using FrooxEngine;

namespace WorldTelemetry;

internal sealed record PhysicsResult(double Ms, double Noise, int Colliders);

internal static class PhysicsProfiler
{
    private const string TestName = "WorldTelemetry Physics Test";
    private const int Blocks = 5;
    private const int SettleFrames = 6;
    private const int RestoreFrames = 3;
    private const long InstallTimeoutMs = 2000;
    private const int MaximumColliders = 400;
    private const float BodyReach = 0.8f;
    private const float BodyBelow = 0.6f;
    private const float BodyAbove = 0.3f;

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
        internal required RefID Id { get; init; }
        internal required string Name { get; init; }
        internal required Slot Target { get; init; }
        internal Slot? Slot;
        internal ValueField<bool>? Source;
        internal readonly List<ValueCopy<bool>> Copies = new();
        internal int Colliders = -1;
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
    private static readonly Dictionary<RefID, PhysicsResult> Results = new();
    private static World? _resultsWorld;
    private static int _skipped;
    private static int _nearColliders;

    internal static bool Running => _phase != Phase.Idle;
    internal static int Done => _index;
    internal static int Total => _tests.Count;
    internal static string CurrentName => _index < _tests.Count ? _tests[_index].Name : "";

    internal static IReadOnlyDictionary<RefID, PhysicsResult>? ResultsFor(World world) => _resultsWorld == world && Results.Count > 0 ? Results : null;

    internal static void Start(World world)
    {
        if (Running)
            return;

        if (LightProfiler.Running)
        {
            Telemetry.SetStatus("Wait for the light measurement to finish first.");
            return;
        }
        World? userspace = Userspace.UserspaceWorld;
        Snapshot? snapshot = Telemetry.Latest;
        if (userspace is null || snapshot is null || snapshot.World != world)
        {
            Telemetry.SetStatus("Scan the world first, then press Measure physics.");
            return;
        }
        List<(RefID Id, string Name, Slot Slot)> candidates = snapshot.Entries
            .Where(entry => entry.Physics.Colliders > 0 && !entry.Slot.IsDestroyed)
            .OrderByDescending(entry => entry.Physics.Score)
            .Take(WorldTelemetryMod.PhysicsTestMaximum)
            .Select(entry => (entry.Id, entry.Name, entry.Slot))
            .ToList();
        if (candidates.Count == 0)
        {
            Telemetry.SetStatus("No enabled colliders to measure in this world.");
            return;
        }
        _phase = Phase.Collecting;
        _world = world;
        world.RunSynchronously(() =>
        {
            var tests = new List<Test>();
            try
            {
                Slot? localRoot = world.LocalUser?.Root?.Slot;
                foreach ((RefID id, string name, Slot slot) in candidates)
                {
                    if (slot.IsDestroyed)
                        continue;

                    if (localRoot is not null && (slot == localRoot || localRoot.IsChildOf(slot, includeSelf: true) || slot.IsChildOf(localRoot, includeSelf: true)))
                        continue;

                    tests.Add(new Test { Id = id, Name = name, Target = slot });
                }
            }
            catch (Exception ex)
            {
                WorldTelemetryMod.LogWarning("Could not list the colliders: " + ex.Message);
            }
            userspace.RunSynchronously(() => Begin(world, tests));
        });
    }

    private static Component? GroundCollider(World world)
    {
        Slot? root = world.LocalUser?.Root?.Slot;
        CharacterController? controller = root?.GetComponentInChildren<CharacterController>();
        return controller?.CurrentGround as Component;
    }

    private static BoundingBox BodyBox(World world)
    {
        UserRoot? root = world.LocalUser?.Root;
        if (root is null)
            return BoundingBox.Empty();

        float3 feet = root.FeetPosition;
        float3 head = root.HeadPosition;
        float3 min = new float3(MathX.Min(feet.x, head.x) - BodyReach, MathX.Min(feet.y, head.y) - BodyBelow, MathX.Min(feet.z, head.z) - BodyReach);
        float3 max = new float3(MathX.Max(feet.x, head.x) + BodyReach, MathX.Max(feet.y, head.y) + BodyAbove, MathX.Max(feet.z, head.z) + BodyReach);
        return new BoundingBox(min, max);
    }

    private static bool Touches(Collider collider, BoundingBox body)
    {
        if (!body.IsValid || body.IsEmpty)
            return false;

        try
        {
            if (!collider.IsBoundingBoxAvailable)
                return true;

            BoundingBox bounds = collider.GlobalBoundingBox;
            return !bounds.IsValid || bounds.Intersects(body);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static void Begin(World world, List<Test> tests)
    {
        if (_phase != Phase.Collecting || _world != world)
            return;

        if (tests.Count == 0)
        {
            _phase = Phase.Idle;
            Telemetry.SetStatus("No objects with colliders could be measured. Your own avatar is never tested.");
            return;
        }
        if (_resultsWorld != world)
            Results.Clear();

        _resultsWorld = world;
        _tests = tests;
        _index = 0;
        _skipped = 0;
        _nearColliders = 0;
        Telemetry.SetStatus($"Measuring the physics cost of {tests.Count} objects. Their colliders are switched off for a moment on your side only, except the ones touching or under you. Stay still while it runs.");
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
            Cancel("Physics measurement stopped because the focused world changed.");
            return;
        }
        Test test = _tests[_index];
        long now = Environment.TickCount64;
        switch (_phase)
        {
            case Phase.Installing:
                if (test.Colliders == 0)
                {
                    _skipped++;
                    Remove(test);
                    Next();
                }
                else if (test.Colliders > 0 && test.Copies.Count > 0 && test.Copies.All(copy => copy.IsDestroyed || copy.Target.IsLinkValid))
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
                double sample = Sample(world);
                if (sample >= 0 && !double.IsNaN(sample))
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

    private static double Sample(World world)
    {
        PhysicsManager? physics = world.Physics;
        if (physics is null)
            return double.NaN;

        return physics.MainUpdateTime + physics.HapticWaitTime;
    }

    private static void StartBlock(int block)
    {
        _block = block;
        Samples[block].Clear();
        _settle = SettleFrames;
        _blockEnds = Environment.TickCount64 + (long)(WorldTelemetryMod.PhysicsTestBlockSeconds * 1000);
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
            if (test.Target.IsDestroyed)
            {
                test.Colliders = 0;
                return;
            }
            Component? standing = GroundCollider(world);
            BoundingBox body = BodyBox(world);
            int near = 0;
            List<Collider> colliders = test.Target.GetComponentsInChildren<Collider>(collider => collider.Enabled && collider.Slot.IsActive && !collider.EnabledField.IsDriven && collider.Type.Value != ColliderType.CharacterController)
                .Where(collider =>
                {
                    if (collider == standing || Touches(collider, body))
                    {
                        near++;
                        return false;
                    }
                    return true;
                })
                .Take(MaximumColliders)
                .ToList();
            _nearColliders += near;
            if (colliders.Count == 0)
            {
                test.Colliders = 0;
                return;
            }
            Slot slot = world.AddLocalSlot(TestName, false);
            slot.Tag = Scanner.OwnTag;
            ValueField<bool> source = slot.AttachComponent<ValueField<bool>>();
            source.Value.Value = true;
            foreach (Collider collider in colliders)
            {
                ValueCopy<bool> copy = slot.AttachComponent<ValueCopy<bool>>();
                copy.Source.Target = source.Value;
                copy.Target.Target = collider.EnabledField;
                test.Copies.Add(copy);
            }
            test.Slot = slot;
            test.Source = source;
            test.Colliders = colliders.Count;
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
        test.Copies.Clear();
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
        Results[test.Id] = new PhysicsResult(onMedian - offMedian, noise, test.Colliders);
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
        Dictionary<RefID, string> names = _tests.ToDictionary(test => test.Id, test => test.Name);
        KeyValuePair<RefID, PhysicsResult>[] top = Results.OrderByDescending(pair => pair.Value.Ms).Take(3).ToArray();
        string heaviest = top.Length == 0 ? "" : " Heaviest: " + string.Join(", ", top.Select(pair => $"{names.GetValueOrDefault(pair.Key, "object")} {Format.Ms(Math.Max(0, pair.Value.Ms))}")) + ".";
        string skipped = _skipped == 0 ? "" : $" Skipped {_skipped} with no testable colliders.";
        string near = _nearColliders == 0 ? "" : $" {_nearColliders} colliders under or next to you were left on, so their cost is not included.";
        Telemetry.SetStatus($"Measured the physics cost of {Results.Count} objects by the world's physics step time.{skipped}{near}{heaviest}");
        if (world is not null && !world.IsDestroyed)
            Telemetry.RequestScan(world, ReportMode.None);
    }
}
