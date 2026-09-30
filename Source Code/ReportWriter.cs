using System.Globalization;
using System.Text;
using FrooxEngine;

namespace WorldTelemetry;

internal static class ReportWriter
{
    internal static string Build(Snapshot snapshot)
    {
        List<ObjectEntry> persistent = snapshot.Entries.Where(entry => entry.Persistent).OrderByDescending(entry => entry.SizeBytes).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
        List<ObjectEntry> temporary = snapshot.Entries.Where(entry => !entry.Persistent).OrderByDescending(entry => entry.SizeBytes).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var text = new StringBuilder();
        text.AppendLine("World Telemetry report");
        text.AppendLine("======================");
        text.AppendLine($"World:    {snapshot.WorldName}");
        if (snapshot.SessionName.Length > 0)
            text.AppendLine($"Session:  {snapshot.SessionName}");

        text.AppendLine($"Written:  {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} (scanned {snapshot.TakenLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture)})");
        text.AppendLine($"Objects:  {snapshot.Entries.Count:N0} ({persistent.Count:N0} persistent, {temporary.Count:N0} not persistent)");
        text.AppendLine($"Slots:    {snapshot.TotalSlots:N0}, with {snapshot.TotalComponents:N0} components");
        text.AppendLine($"Assets:   {Format.Bytes(snapshot.UniqueAssetBytes)} in {snapshot.UniqueAssets:N0} unique assets");
        text.AppendLine($"Scan:     {snapshot.ScanMs:F0} ms");
        text.AppendLine($"Users:    {snapshot.Entries.Count(entry => entry.IsUser):N0}, frame rate {snapshot.Fps:F0} FPS");
        AppendCpu(text, snapshot.Cpu);
        AppendNetwork(text, snapshot);
        text.AppendLine();
        text.AppendLine("How to read this report:");
        text.AppendLine("- Size is the memory the object's assets take: textures as stored on the GPU with all mip levels, mesh buffers and decoded audio. An asset shared by several objects counts toward each of them.");
        text.AppendLine("- CPU is the average time per frame spent in the object's component updates, change handling, startups and ProtoFlux while the world was measured.");
        text.AppendLine("- Components and ProtoFlux split the measured CPU time: component updates, changes and startups, and ProtoFlux node groups.");
        text.AppendLine("- GPU is an estimate, not a measurement: about 1 point per 10 microseconds of render work, from triangles, draw calls, transparent and cutout materials (transparent ones cost extra for overdraw), skinned vertices, active blendshapes, render cameras, realtime reflection probes and particles.");
        text.AppendLine("- Lighting is in milliseconds per frame. Lights tested with Measure lights on the dash show their measured cost: each light was switched off on your side only and the renderer frame time (GPU time in VR) was compared with it on. Untested lights show an estimate marked ~, from the draw calls of every renderer inside their range plus shadow map draws.");
        text.AppendLine("- Physics without Measure physics is a complexity score: mesh colliders by collision triangles, convex hulls by source vertices, simple shapes 1 point each, doubled for moving and character colliders. After Measure physics it is milliseconds of the world's physics step per frame: each tested object's colliders were switched off on your side only and the step time was compared. Untested objects are estimated from the measured ones and marked ~.");
        text.AppendLine("- Bones is the dynamic bone simulation time. Particles is the live particle count, and Audio is the audio thread time. Net/s is the sync traffic in plus out for the object, and Rebuilds/s is how often its procedural meshes were regenerated.");
        text.AppendLine("- Every ranking below repeats the same columns, so any object can be compared across metrics. Rankings show the top " + TopLength + " objects that have a value above zero.");
        text.AppendLine("- An object is a slot under the world root, or a slot with an ObjectRoot, Grabbable or user root component. Slots under the world root keep everything that isn't its own object.");
        text.AppendLine();

        bool measuredPhysics = snapshot.PhysicsMeasured > 0;
        AppendSection(text, $"USERS ({snapshot.Entries.Count(entry => entry.IsUser):N0}), LARGEST FIRST", snapshot.Entries.Where(entry => entry.IsUser).OrderByDescending(entry => entry.SizeBytes).ToList(), measuredPhysics);
        text.AppendLine();
        AppendSection(text, $"PERSISTENT OBJECTS, LARGEST FIRST ({persistent.Count:N0})", persistent, measuredPhysics);
        text.AppendLine();
        AppendSection(text, $"NOT PERSISTENT OBJECTS, LARGEST FIRST ({temporary.Count:N0})", temporary, measuredPhysics);
        text.AppendLine();
        bool timed = snapshot.Cpu is not null;
        AppendTop(text, snapshot, "OBJECTS BY FILE SIZE", entry => entry.SizeBytes, entry => Format.Bytes(entry.SizeBytes));
        if (timed)
        {
            AppendTop(text, snapshot, "OBJECTS BY TOTAL CPU TIME", entry => entry.CpuTotalMs, entry => Format.Ms(entry.CpuTotalMs));
            AppendTop(text, snapshot, "OBJECTS BY COMPONENT CPU TIME", entry => entry.ComponentMs, entry => Format.Ms(entry.ComponentMs));
            AppendTop(text, snapshot, "OBJECTS BY PROTOFLUX CPU TIME", entry => entry.FluxMs, entry => Format.Ms(entry.FluxMs));
            AppendTop(text, snapshot, "OBJECTS BY DYNAMIC BONE TIME", entry => entry.BonesMs, entry => Format.Ms(entry.BonesMs));
            AppendTop(text, snapshot, "OBJECTS BY PARTICLE TIME", entry => entry.ParticlesMs, entry => Format.Ms(entry.ParticlesMs));
            AppendTop(text, snapshot, "OBJECTS BY AUDIO TIME", entry => entry.AudioMs, entry => Format.Ms(entry.AudioMs));
        }
        AppendTop(text, snapshot, "OBJECTS BY NETWORK TRAFFIC PER SECOND", entry => entry.Extra.NetBytesPerSecond, entry => Format.Bytes((long)entry.Extra.NetBytesPerSecond) + "/s");
        AppendTop(text, snapshot, "OBJECTS BY MESH REBUILDS PER SECOND", entry => entry.Extra.MeshRebuildsPerSecond, entry => entry.Extra.MeshRebuildsPerSecond.ToString("F1", CultureInfo.InvariantCulture) + "/s");
        AppendTop(text, snapshot, "OBJECTS BY GPU ESTIMATE", entry => entry.GpuScore, entry => Format.Score(entry.GpuScore));
        AppendTop(text, snapshot, "OBJECTS BY LIGHTING COST", entry => entry.Light.ValueMs, entry => LightText(entry.Light));
        AppendTop(text, snapshot, "OBJECTS BY PHYSICS COST", entry => measuredPhysics ? entry.Physics.ValueMs : entry.Physics.Score, entry => PhysicsText(entry.Physics, measuredPhysics));
        AppendTop(text, snapshot, "OBJECTS BY SLOT COUNT", entry => entry.Slots, entry => entry.Slots.ToString("N0", CultureInfo.InvariantCulture));
        AppendTop(text, snapshot, "OBJECTS BY COMPONENT COUNT", entry => entry.Components, entry => entry.Components.ToString("N0", CultureInfo.InvariantCulture));
        AppendTop(text, snapshot, "OBJECTS BY PROTOFLUX NODE COUNT", entry => entry.FluxNodes, entry => entry.FluxNodes.ToString("N0", CultureInfo.InvariantCulture));
        AppendTop(text, snapshot, "OBJECTS BY WARNING COUNT", entry => entry.Warnings.Count, entry => entry.Warnings.Count.ToString(CultureInfo.InvariantCulture));
        AppendWarnings(text, snapshot);
        text.AppendLine();
        AppendSlots(text, snapshot, "SLOTS WITH THE LARGEST OWN FILE SIZE", node => node.OwnSize?.TotalBytes ?? 0);
        if (timed)
        {
            text.AppendLine();
            AppendSlots(text, snapshot, "SLOTS WITH THE MOST OWN CPU TIME", node => snapshot.Ms(node.OwnCpu));
            text.AppendLine();
            AppendSlots(text, snapshot, "SLOTS WITH THE MOST OWN DYNAMIC BONE TIME", node => snapshot.Ms(node.OwnCpu, (int)CpuCategory.DynamicBones));
        }
        text.AppendLine();
        AppendSlots(text, snapshot, "SLOTS WITH THE HIGHEST OWN GPU ESTIMATE", node => node.OwnGpu?.Score ?? 0);
        text.AppendLine();
        AppendSlots(text, snapshot, "SLOTS WITH THE MOST OWN NETWORK TRAFFIC", node => node.OwnExtra?.NetBytesPerSecond ?? 0);
        text.AppendLine();
        AppendSlots(text, snapshot, "SLOTS WITH THE HIGHEST OWN LIGHTING COST", node => node.OwnLight?.ValueMs ?? 0);
        text.AppendLine();
        AppendSlots(text, snapshot, "SLOTS WITH THE HIGHEST OWN PHYSICS COST", node => measuredPhysics ? node.OwnPhysics?.ValueMs ?? 0 : node.OwnPhysics?.Score ?? 0);
        return text.ToString();
    }

    private static void AppendCpu(StringBuilder text, CpuSummary? cpu)
    {
        if (cpu is null)
        {
            text.AppendLine($"CPU:      not measured. Set key_profile_seconds above 0 or press Profile CPU on the dash screen.");
            return;
        }
        text.AppendLine($"CPU:      measured for {Format.Seconds(cpu.Seconds)} ({cpu.Frames:N0} frames). Timed work {Format.Ms(cpu.TotalMs)} per frame, {Format.Ms(cpu.AttributedMs)} of it in the objects below.");
        text.AppendLine($"          updates {Format.Ms(cpu.CategoryMs[(int)CpuCategory.Updates])}, changes {Format.Ms(cpu.CategoryMs[(int)CpuCategory.Changes])}, startups {Format.Ms(cpu.CategoryMs[(int)CpuCategory.Startups])}, ProtoFlux {Format.Ms(cpu.CategoryMs[(int)CpuCategory.ProtoFlux])}, dynamic bones {Format.Ms(cpu.CategoryMs[(int)CpuCategory.DynamicBones])}, particles {Format.Ms(cpu.CategoryMs[(int)CpuCategory.Particles])} (background), audio {Format.Ms(cpu.CategoryMs[(int)CpuCategory.Audio])} (audio thread)");
        text.AppendLine($"          Timed {cpu.CoverageLine}");
        if (cpu.Coverage < 0.9 || cpu.MissingHooks > 0)
            text.AppendLine($"          Only {cpu.Coverage * 100:F0}% of the component calls were timed, so CPU times may be low.");
    }

    private const int TopLength = 25;

    private static void AppendNetwork(StringBuilder text, Snapshot snapshot)
    {
        if (snapshot.Extra is null)
            return;

        double total = snapshot.Entries.Sum(entry => entry.Extra.NetBytesPerSecond);
        text.AppendLine($"Network:  {Format.Bytes((long)total)}/s attributed to objects, {Format.Bytes((long)snapshot.NetUnattributedBytesPerSecond)}/s not attributed to any slot (measured for {Format.Seconds(snapshot.Extra.Seconds)}).");
        double particles = snapshot.Entries.Sum(entry => entry.Extra.Particles);
        double rebuilds = snapshot.Entries.Sum(entry => entry.Extra.MeshRebuildsPerSecond);
        text.AppendLine($"          {particles:N0} live particles, {rebuilds:F1} mesh rebuilds per second, {snapshot.Entries.Sum(entry => entry.Extra.BoneChains):N0} dynamic bone chains.");
    }

    private static void AppendTop(StringBuilder text, Snapshot snapshot, string title, Func<ObjectEntry, double> value, Func<ObjectEntry, string> shown)
    {
        List<ObjectEntry> top = snapshot.Entries.Where(entry => value(entry) > 0).OrderByDescending(value).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).Take(TopLength).ToList();
        string heading = $"{title} (TOP {TopLength})";
        text.AppendLine(heading);
        text.AppendLine(new string('-', heading.Length));
        if (top.Count == 0)
        {
            text.AppendLine("(none)");
            text.AppendLine();
            return;
        }
        text.AppendLine($"{"#",5}  {"Value",12}  {"Size",10}  {"CPU/frame",11}  {"GPU",7}  {"Net/s",9}  {"Warn",4}  {"Slots",7}  Name  (parent path)");
        int rank = 0;
        foreach (ObjectEntry entry in top)
        {
            rank++;
            string cpu = entry.CpuTotalMs > 0 ? Format.Ms(entry.CpuTotalMs) : "-";
            string net = entry.Extra.NetBytesPerSecond > 0 ? Format.Bytes((long)entry.Extra.NetBytesPerSecond) : "-";
            text.AppendLine($"{rank,5}  {shown(entry),12}  {Format.Bytes(entry.SizeBytes),10}  {cpu,11}  {Format.Score(entry.GpuScore),7}  {net,9}  {entry.Warnings.Count,4}  {entry.Slots,7:N0}  {entry.Name}{(entry.IsUser ? " [user]" : "")}  ({entry.Path})");
        }
        text.AppendLine();
    }

    private static void AppendSection(StringBuilder text, string title, List<ObjectEntry> entries, bool measuredPhysics)
    {
        text.AppendLine(title);
        text.AppendLine(new string('-', title.Length));
        if (entries.Count == 0)
        {
            text.AppendLine("(none)");
            return;
        }
        text.AppendLine($"{"#",5}  {"Size",10}  {"Textures",10}  {"Meshes",10}  {"Audio",10}  {"CPU/frame",11}  {"Comp. CPU",11}  {"Flux CPU",11}  {"GPU",7}  {"Light",11}  {"Physics",9}  {"Bones",11}  {"Particles",9}  {"Audio",11}  {"Net/s",9}  {"Rebuilds/s",10}  {"Warn",4}  {"Triangles",9}  {"Draws",6}  {"Transp.",7}  {"Slots",7}  {"Comps",7}  {"Nodes",6}  Name  (parent path)");
        int rank = 0;
        foreach (ObjectEntry entry in entries)
        {
            rank++;
            string cpu = entry.CpuTotalMs > 0 ? Format.Ms(entry.CpuTotalMs) : "-";
            string component = entry.ComponentMs > 0 ? Format.Ms(entry.ComponentMs) : "-";
            string flux = entry.FluxMs > 0 ? Format.Ms(entry.FluxMs) : "-";
            text.AppendLine($"{rank,5}  {Format.Bytes(entry.SizeBytes),10}  {Format.Bytes(entry.Assets.TextureBytes),10}  {Format.Bytes(entry.Assets.MeshBytes),10}  {Format.Bytes(entry.Assets.AudioBytes),10}  {cpu,11}  {component,11}  {flux,11}  {Format.Score(entry.GpuScore),7}  {LightText(entry.Light),11}  {PhysicsText(entry.Physics, measuredPhysics),9}  {(entry.BonesMs > 0 ? Format.Ms(entry.BonesMs) : entry.Extra.BoneChains > 0 ? $"{entry.Extra.BoneChains} chains" : "-"),11}  {(entry.Extra.Particles > 0 ? Format.Count((long)entry.Extra.Particles) : "-"),9}  {(entry.AudioMs > 0 ? Format.Ms(entry.AudioMs) : "-"),11}  {(entry.Extra.NetBytesPerSecond > 0 ? Format.Bytes((long)entry.Extra.NetBytesPerSecond) : "-"),9}  {(entry.Extra.MeshRebuildsPerSecond > 0 ? entry.Extra.MeshRebuildsPerSecond.ToString("F1", CultureInfo.InvariantCulture) : "-"),10}  {entry.Warnings.Count,4}  {Format.Count(entry.Gpu.Triangles),9}  {entry.Gpu.DrawCalls,6:N0}  {entry.Gpu.TransparentDraws,7:N0}  {entry.Slots,7:N0}  {entry.Components,7:N0}  {entry.FluxNodes,6:N0}  {entry.Name}  ({entry.Path})");
        }
    }

    private const int SlotListLength = 50;

    private static void AppendWarnings(StringBuilder text, Snapshot snapshot)
    {
        List<ObjectEntry> flagged = snapshot.Entries.Where(entry => entry.Warnings.Count > 0).OrderByDescending(entry => entry.Warnings.Count).ThenByDescending(entry => entry.SizeBytes).ToList();
        string heading = $"WARNINGS ({flagged.Count:N0} objects)";
        text.AppendLine(heading);
        text.AppendLine(new string('-', heading.Length));
        if (flagged.Count == 0)
        {
            text.AppendLine("(none)");
            return;
        }
        foreach (ObjectEntry entry in flagged)
        {
            text.AppendLine($"{entry.Name}  ({entry.Path})");
            foreach (string warning in entry.Warnings)
                text.AppendLine("    - " + warning);
        }
    }

    private static string PhysicsText(PhysicsTotals physics, bool measured)
    {
        if (physics.Colliders == 0)
            return "-";

        if (!measured)
            return Format.Score(physics.Score);

        return physics.Measured > 0 ? Format.Ms(physics.ValueMs) : "~" + Format.Ms(physics.ValueMs);
    }

    private static string LightText(LightTotals light)
    {
        if (light.Lights == 0)
            return "-";

        return light.Measured == light.Lights ? Format.Ms(light.ValueMs) : "~" + Format.Ms(light.ValueMs);
    }

    private static void AppendSlots(StringBuilder text, Snapshot snapshot, string title, Func<SlotNode, double> value)
    {
        List<SlotNode> top = snapshot.Nodes.Where(node => value(node) > 0).OrderByDescending(value).Take(SlotListLength).ToList();
        string heading = $"{title} (TOP {SlotListLength}, a slot's own components only)";
        text.AppendLine(heading);
        text.AppendLine(new string('-', heading.Length));
        if (top.Count == 0)
        {
            text.AppendLine("(none)");
            return;
        }
        text.AppendLine($"{"#",5}  {"Own size",10}  {"Own CPU",11}  {"Own GPU",7}  {"Incl. size",10}  {"Incl. CPU",11}  {"Slots",7}  {"Persist",7}  Name  (parent path)");
        int rank = 0;
        foreach (SlotNode node in top)
        {
            rank++;
            string ownCpu = node.OwnCpu is null ? "-" : Format.Ms(snapshot.Ms(node.OwnCpu));
            string cpu = node.Cpu is null ? "-" : Format.Ms(snapshot.Ms(node.Cpu));
            text.AppendLine($"{rank,5}  {Format.Bytes(node.OwnSize?.TotalBytes ?? 0),10}  {ownCpu,11}  {Format.Score(node.OwnGpu?.Score ?? 0),7}  {Format.Bytes(node.Size.TotalBytes),10}  {cpu,11}  {node.Slots,7:N0}  {(node.Persistent ? "yes" : "no"),7}  {node.Name}  ({node.Path()})");
        }
    }

    internal static string Save(string worldName, string text, bool latest)
    {
        string directory = LogsDirectory();
        Directory.CreateDirectory(directory);
        string stamp = latest ? "latest" : DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture);
        string path = Path.Combine(directory, $"WorldTelemetry {SafeFileName(worldName)} {stamp}.txt");
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, path, true);
        return path;
    }

    private static string LogsDirectory()
    {
        string? appPath = Engine.Current?.AppPath;
        if (string.IsNullOrWhiteSpace(appPath))
            appPath = AppContext.BaseDirectory;

        return Path.Combine(appPath, "Logs");
    }

    private static string SafeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var safe = new StringBuilder(name.Length);
        foreach (char c in name)
            safe.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);

        string result = safe.ToString().Trim().TrimEnd('.');
        if (result.Length > 60)
            result = result[..60].Trim();

        return result.Length > 0 ? result : "World";
    }
}
