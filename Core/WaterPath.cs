using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantWater;

/// <summary>
/// Water that flows along its curve, from its first point to its last: a river, a creek or a
/// waterfall. It flows faster and foams more where the curve is steep.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_water/Icons/Water-Path.svg")]
public partial class WaterPath : WaterNode
{
    public enum PathType { River, Creek, Waterfall }

    private const float StepLength = 1f;
    private const float FastestSpeedUp = 4f;
    private const float BendMargin = 1.15f;
    private const int MostColumns = 65;
    private const float MinimumLeaving = 4f;

    // Only a body about this close in height counts as what it runs into: from higher up it falls on it, with no blending.
    private const float MostJoinDrop = 5f;
    private const float JoinLift = 0.05f;
    private const float BodyComesBackOver = 20f;
    private const float LevelledOut = 0.5f;
    private const int FramesBetweenSplashRefreshes = 20;
    private const int MostSplashParticles = 1000;

    // How far inside its source it starts, so no gap shows where it leaves.
    private const float JoinInset = 1f;

    // The curve as last seen, to tell when a point has been added to its end.
    private ulong _knownCurve;
    private int _knownCount;
    private Vector3 _knownEnd;
    private const int MostRoundingPasses = 2000;

    /// <summary>The water it flows from: its first point stays joined to it, at that water's height. Moving that point moves where it joins.</summary>
    [Export]
    public WaterNode? Source
    {
        get => _source;
        set
        {
            _source = value;
            QueueRebuild();
        }
    }

    private WaterNode? _source;

    /// <summary>How fast it flows on the flat, in metres a second; 0 means its material's speed.</summary>
    [Export(PropertyHint.Range, "0,30,0.05,or_greater,suffix:m/s")]
    public float FlowSpeed
    {
        get => _flowSpeed;
        set
        {
            _flowSpeed = Mathf.Max(value, 0f);
            QueueRebuild();
        }
    }

    private float _flowSpeed;

    /// <summary>How fast it flows at each of the curve's points, in order; 0 or missing means <see cref="FlowSpeed"/>. It eases between points.</summary>
    [Export]
    public Godot.Collections.Array<float> PointFlowSpeeds
    {
        get => _pointFlowSpeeds;
        set
        {
            _pointFlowSpeeds = value;
            QueueRebuild();
        }
    }

    private Godot.Collections.Array<float> _pointFlowSpeeds = [];

    private PathType _type = PathType.River;
    private float _width = 6f;
    private Godot.Collections.Array<float> _pointWidths = [];

    [Export]
    public PathType Type
    {
        get => _type;
        set
        {
            _type = value;
            ApplyMaterial();
        }
    }

    /// <summary>How wide it is where no point says otherwise.</summary>
    [Export(PropertyHint.Range, "0.1,200,0.1,or_greater,suffix:m")]
    public float Width
    {
        get => _width;
        set
        {
            _width = Mathf.Max(value, 0.1f);
            QueueRebuild();
        }
    }

    /// <summary>How wide it is at each of the curve's points, in order; 0 or missing means <see cref="Width"/>.</summary>
    [Export]
    public Godot.Collections.Array<float> PointWidths
    {
        get => _pointWidths;
        set
        {
            _pointWidths = value;
            QueueRebuild();
        }
    }

    /// <summary>How far into other water it fades out, where it meets it at either end.</summary>
    [Export(PropertyHint.Range, "0.5,100,0.5,or_greater,suffix:m")]
    public float JoinBlend
    {
        get => _joinBlend;
        set
        {
            _joinBlend = Mathf.Max(value, 0.5f);
            QueueRebuild();
        }
    }

    private float _joinBlend = 40f;

    [ExportGroup("Splashes")]
    /// <summary>Whether spray rises where it comes off a steep drop onto the flat.</summary>
    [Export]
    public bool Splashes
    {
        get => _splashes;
        set
        {
            _splashes = value;
            QueueRebuild();
        }
    }

    private bool _splashes = true;

    /// <summary>How much spray for each metre of its width. The spray's look is in Splash.tscn.</summary>
    [Export(PropertyHint.Range, "0,100,1,or_greater")]
    public float SplashAmount
    {
        get => _splashAmount;
        set
        {
            _splashAmount = Mathf.Max(value, 0f);
            QueueRebuild();
        }
    }

    private float _splashAmount = 12f;

    /// <summary>How steep a drop must get to splash where it levels out (0 flat, 1 straight down).</summary>
    [Export(PropertyHint.Range, "0.01,1,0.01")]
    public float SplashMinSteepness
    {
        get => _splashMinSteepness;
        set
        {
            _splashMinSteepness = value;
            QueueRebuild();
        }
    }

    private float _splashMinSteepness = 0.15f;

    /// <summary>How the spray moves and what shape it comes from, for this path: empty uses Splash.tscn's. Its box's width follows the river's. Customize Splash fills it in to edit.</summary>
    [Export]
    public ParticleProcessMaterial? SplashMotion
    {
        get => _splashMotion;
        set
        {
            _splashMotion = value;
            _splashesOutdated = true;
            QueueRebuild();
        }
    }

    private ParticleProcessMaterial? _splashMotion;
    private bool _splashesOutdated;

    /// <summary>What each spray particle looks like, for this path: empty uses Splash.tscn's.</summary>
    [Export]
    public Mesh? SplashMesh
    {
        get => _splashMesh;
        set
        {
            _splashMesh = value;
            _splashesOutdated = true;
            QueueRebuild();
        }
    }

    private Mesh? _splashMesh;

    /// <summary>How long each spray particle lasts, in seconds; 0 uses Splash.tscn's.</summary>
    [Export(PropertyHint.Range, "0,10,0.05,or_greater,suffix:s")]
    public float SplashLifetime
    {
        get => _splashLifetime;
        set
        {
            _splashLifetime = Mathf.Max(value, 0f);
            _splashesOutdated = true;
            QueueRebuild();
        }
    }

    private float _splashLifetime;

    [ExportToolButton("Customize Splash", Icon = "GPUParticles3D")]
    public Callable CustomizeSplashButton => Callable.From(CustomizeSplash);

    /// <summary>Copies Splash.tscn's motion and mesh onto this path, to edit in the inspector.</summary>
    public void CustomizeSplash()
    {
        var splash = SplashScene.Instantiate<GpuParticles3D>();
        SplashMotion ??= (ParticleProcessMaterial)splash.ProcessMaterial.Duplicate(true);
        SplashMesh ??= (Mesh)splash.DrawPass1.Duplicate(true);
        if (SplashLifetime <= 0f)
            SplashLifetime = (float)splash.Lifetime;
        splash.Free();
        NotifyPropertyListChanged();
    }

    /// <summary>Gives its sprays the current Splash Motion, each with its own river's width.</summary>
    public void RefreshSplashMotion()
    {
        if (SplashMotion is null)
            return;
        foreach (var splash in GetChildren(true).OfType<GpuParticles3D>())
        {
            var width = (splash.ProcessMaterial as ParticleProcessMaterial)?.EmissionBoxExtents.X ?? 0f;
            var motion = (ParticleProcessMaterial)SplashMotion.Duplicate();
            motion.EmissionBoxExtents = motion.EmissionBoxExtents with { X = width };
            splash.ProcessMaterial = motion;
        }
    }

#if TOOLS
    // A particle material says nothing when its values are edited, so while this path is selected its sprays keep up with it.
    public override void _Process(double delta)
    {
        if (Engine.IsEditorHint() && SplashMotion is not null && Engine.GetProcessFrames() % FramesBetweenSplashRefreshes == 0
            && EditorInterface.Singleton.GetSelection().GetSelectedNodes().Contains(this))
            RefreshSplashMotion();
    }
#endif

    private static PackedScene SplashScene => GD.Load<PackedScene>("res://addons/unpleasant_water/Splash.tscn");

    [ExportToolButton("Fit Points To Ground", Icon = "ToolMove")]
    public Callable FitButton => Callable.From(FitToGround);

    protected override ShaderMaterial? TypeMaterial => LoadType(Type.ToString());

    protected override Curve3D NewCurve()
    {
        var curve = new Curve3D();
        curve.AddPoint(new Vector3(0f, 0f, -20f));
        curve.AddPoint(new Vector3(0f, 0f, 20f));
        return curve;
    }

    /// <summary>One step along the curve, in the node's space.</summary>
    private readonly record struct Step(Vector3 At, Vector3 Forward, Vector3 Right, float Width, float Along, float Steep, float Leaving = 1f, WaterNode? Into = null, float Ending = 1f, WaterNode? Ends = null);

    // Where each point is along the curve. A plain class, as the editor would carry an array in the node through a reload of the scripts and damage it.
    private sealed class PointPlaces(float[] offsets)
    {
        public readonly float[] Offsets = offsets;
    }

    private PointPlaces? _places;

    protected override void Outdated() => _places = null;

    private float[] PlacesOfPoints()
    {
        if (_places is not null)
            return _places.Offsets;
        var curve = Curve!;
        var offsets = new float[curve.PointCount];
        for (var i = 0; i < offsets.Length; i++)
            offsets[i] = curve.GetClosestOffset(curve.GetPointPosition(i));
        _places = new PointPlaces(offsets);
        return offsets;
    }

    private float WidthAt(float offset) =>
        BetweenPoints(offset, i => i < PointWidths.Count && PointWidths[i] > 0f ? PointWidths[i] : Width);

    private float MaterialSpeed => Material?.GetShaderParameter("flow_speed").AsSingle() ?? 0f;

    /// <summary>How fast it flows on the flat at a place along it.</summary>
    private float SpeedAt(float offset)
    {
        var whole = FlowSpeed > 0f ? FlowSpeed : MaterialSpeed;
        return BetweenPoints(offset, i => i < PointFlowSpeeds.Count && PointFlowSpeeds[i] > 0f ? PointFlowSpeeds[i] : whole);
    }

    /// <summary>A value set per point, at a place along the curve, easing from one point's to the next's.</summary>
    private float BetweenPoints(float offset, Func<int, float> Of)
    {
        var places = PlacesOfPoints();
        var count = places.Length;
        var last = 0f;
        for (var i = 0; i < count; i++)
        {
            var at = places[i];
            if (offset <= at)
                return i == 0 || at <= last ? Of(i) : Mathf.Lerp(Of(i - 1), Of(i), (offset - last) / (at - last));
            last = at;
        }

        return Of(count - 1);
    }

    private List<Step> Steps()
    {
        var steps = new List<Step>();
        if (Curve is not { PointCount: >= 2 } curve || curve.GetBakedLength() <= 0f)
            return steps;
        var length = curve.GetBakedLength();
        var count = Math.Max(1, Mathf.CeilToInt(length / StepLength));
        var points = new Vector3[count + 1];
        var widths = new float[count + 1];
        for (var i = 0; i <= count; i++)
        {
            var offset = length * i / count;
            points[i] = curve.SampleBaked(offset, true);
            widths[i] = WidthAt(offset);
        }

        RoundBends(points, widths);
        var right = Vector3.Right;
        for (var i = 0; i <= count; i++)
        {
            var ahead = points[Math.Min(i + 1, count)] - points[Math.Max(i - 1, 0)];
            var forward = ahead.LengthSquared() > 1e-8f ? ahead.Normalized() : Vector3.Forward;
            var level = new Vector3(forward.X, 0f, forward.Z);
            // Straight down, its way across stays as it was.
            if (level.LengthSquared() > 1e-4f)
                right = Vector3.Up.Cross(level).Normalized();
            steps.Add(new Step(points[i], forward, right, widths[i], length * i / count, Mathf.Clamp(-forward.Y, 0f, 1f)));
        }

        JoinOtherWater(steps, length);
        return steps;
    }

    /// <summary>
    /// Where it meets other water, at either end: its source at its start, and a body it runs into at
    /// its end. It runs on a little into that water, level with it and just above it (so it draws
    /// over it, as water drawn first keeps out what is below it), eased to and from its curve's
    /// heights at its full width, and fades out inside it.
    /// </summary>
    private void JoinOtherWater(List<Step> steps, float length)
    {
        if (!IsInsideTree() || steps.Count < 2)
            return;
        var source = Source is not null && IsInstanceValid(Source) && Source != this && Source.IsInsideTree() ? Source : null;
        var last = steps[^1];
        var ahead = last.At + LevelForward(last) * (JoinBlend + 1f);
        var into = Water.InTree.OfType<WaterBody>()
            .Where(b => b != Source && b.Covers(ToGlobal(ahead)) && Mathf.Abs(b.GlobalPosition.Y - ToGlobal(last.At).Y) <= MostJoinDrop)
            .OrderBy(b => Mathf.Abs(b.GlobalPosition.Y - ToGlobal(last.At).Y)).FirstOrDefault();
        if (source is null && into is null)
            return;
        var run = JoinBlend + steps.Max(s => s.Width) * 0.5f;
        var extra = Math.Max(1, Mathf.CeilToInt(run / StepLength));
        var startLevel = steps[0].At.Y;
        if (source is not null)
        {
            var first = steps[0];
            var back = LevelForward(first);
            for (var i = 1; i <= extra; i++)
                steps.Insert(0, first with { At = first.At - back * i * StepLength, Along = -i * StepLength, Steep = 0f });
            startLevel += source.HighestWave + JoinLift;
        }

        var endLevel = 0f;
        if (into is not null)
        {
            endLevel = ToLocal(into.GlobalPosition).Y + into.HighestWave + JoinLift;
            var forward = LevelForward(last);
            for (var i = 1; i <= extra; i++)
                steps.Add(last with { At = last.At + forward * i * StepLength, Along = length + i * StepLength, Steep = 0f });
        }

        bool Over(Step step, WaterNode water) =>
            new[] { -0.5f, -0.25f, 0f, 0.25f, 0.5f }.Any(a => water.Covers(ToGlobal(step.At + step.Right * a * step.Width)));

        // Its source's water, as far along as any of its width is still over it.
        var startClear = 0;
        if (source is not null)
        {
            for (var i = 0; i < steps.Count; i++)
            {
                if (Over(steps[i], source))
                    startClear = i + 1;
                else if (steps[i].Along > JoinInset)
                    break;
            }
        }

        var startAlong = steps[Math.Clamp(startClear, 0, steps.Count - 1)].Along;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var reach = Mathf.Max(step.Width * 0.5f, MinimumLeaving);
            var leaving = 1f;
            var y = step.At.Y;
            if (source is not null)
            {
                leaving = Mathf.SmoothStep(0f, reach, step.Along - startAlong);
                y = Mathf.Lerp(startLevel, y, leaving);
            }

            if (into is not null)
            {
                // By its last point, not the body's edge, which can be far off under the ground (a sea's often is).
                var arriving = Mathf.SmoothStep(0f, reach, length - step.Along);
                y = Mathf.Lerp(endLevel, y, arriving);
            }

            var ending = into is not null ? 1f - Mathf.SmoothStep(0f, JoinBlend, step.Along - length) : 1f;
            steps[i] = step with { At = step.At with { Y = y }, Leaving = leaving, Into = i < startClear ? source : null, Ending = ending, Ends = into };
        }
    }

    // Its steps as last built, for the masks of the water it joins. A plain class, as with the other kept arrays.
    private sealed class BuiltSteps(List<Step> steps)
    {
        public readonly List<Step> Steps = steps;
    }

    private BuiltSteps? _built;

    /// <summary>A line across it, in the world, seen from above: how much it hides water there.</summary>
    internal readonly record struct MaskRow(Vector2 Middle, Vector2 Across, float Value, float Leaving);

    /// <summary>How much of it shows at a place across it (-0.5 to 0.5), by its soft sides, which come in as it leaves other water.</summary>
    internal static float SideFade(float across, float leaving) =>
        Mathf.Lerp(1f, Mathf.Clamp((0.5f - Mathf.Abs(across)) / 0.2f, 0f, 1f), leaving);

    /// <summary>
    /// The lines across it under which a body it runs into should be hidden, all along it, as that
    /// can reach far under it. The body comes back fully before its last point, where this only then
    /// starts to fade: fading both at once would leave them see-through together.
    /// </summary>
    internal IEnumerable<MaskRow> MaskRows(WaterBody body)
    {
        if (_built is null || Curve is not { } curve)
            yield break;
        var length = curve.GetBakedLength();
        var comesBack = Mathf.Min(JoinBlend * 0.5f, BodyComesBackOver);
        foreach (var step in _built.Steps)
        {
            if (step.Ends != body || step.Along >= length)
                continue;
            var middle = ToGlobal(step.At);
            var across = GlobalBasis.Orthonormalized() * step.Right * step.Width;
            var value = 1f - Mathf.SmoothStep(length - comesBack, length, step.Along);
            yield return new MaskRow(new Vector2(middle.X, middle.Z), new Vector2(across.X, across.Z), value, step.Leaving);
        }
    }

    /// <summary>Where it runs into a body, and how far round that its surf stays away, seen from above; null if it does not.</summary>
    internal (Vector2 Middle, float Radius)? MouthAt(WaterBody body, float clearance)
    {
        if (_built is null || Curve is not { } curve || _built.Steps.Count == 0 || _built.Steps[^1].Ends != body)
            return null;
        var end = ToGlobal(curve.GetPointPosition(curve.PointCount - 1));
        return (new Vector2(end.X, end.Z), _built.Steps[^1].Width * 0.5f + clearance);
    }

    private static Vector3 LevelForward(Step step)
    {
        var level = new Vector3(step.Forward.X, 0f, step.Forward.Z);
        return level.LengthSquared() > 1e-4f ? level.Normalized() : Vector3.Up.Cross(step.Right).Normalized() * -1f;
    }

    /// <summary>
    /// Rounds the line as seen from above, wherever it bends more tightly than half its width: past
    /// that, the inside of the bend would fold over itself. Heights and its two ends stay as they are.
    /// </summary>
    private static void RoundBends(Vector3[] points, float[] widths)
    {
        // Each pass only looks again at what the last one moved, and next to it.
        var looking = new HashSet<int>(Enumerable.Range(1, Math.Max(points.Length - 2, 0)));
        for (var pass = 0; pass < MostRoundingPasses && looking.Count > 0; pass++)
        {
            var next = new HashSet<int>();
            foreach (var i in looking.Order())
            {
                var before = new Vector2(points[i].X - points[i - 1].X, points[i].Z - points[i - 1].Z);
                var after = new Vector2(points[i + 1].X - points[i].X, points[i + 1].Z - points[i].Z);
                // Too short from above is falling, not bending.
                if (before.Length() < 0.2f || after.Length() < 0.2f)
                    continue;
                var turn = Mathf.Abs(before.AngleTo(after));
                var radius = 0.5f * (before.Length() + after.Length()) / Mathf.Max(turn, 1e-4f);
                if (radius >= widths[i] * 0.5f * BendMargin)
                    continue;
                var middle = (points[i - 1] + points[i + 1]) * 0.5f;
                points[i].X = Mathf.Lerp(points[i].X, middle.X, 0.5f);
                points[i].Z = Mathf.Lerp(points[i].Z, middle.Z, 0.5f);
                for (var near = Math.Max(i - 1, 1); near <= Math.Min(i + 1, points.Length - 2); near++)
                    next.Add(near);
            }

            looking = next;
        }
    }

    /// <summary>How much faster than its material's speed water goes on a slope this steep (0 flat, 1 straight down).</summary>
    private static float SpeedUp(float steep) => 1f + (FastestSpeedUp - 1f) * Mathf.Sqrt(steep);

    /// <summary>
    /// A spray at each place it comes off a steep drop onto the flat. Its children are looked at each
    /// time, as they stay through a reload of the scripts when a list of them would not. They are
    /// kept and moved where they can be, as this runs while a point is dragged.
    /// </summary>
    private void UpdateSplashes(List<Step> steps)
    {
        var landings = new List<Step>();
        if (Splashes && SplashAmount > 0f)
        {
            // The steepest it has got since it last landed; it lands once it has eased well off that.
            // Having landed, it can only land again after getting steeper, as it can take some steps to ease off.
            var steepest = 0f;
            var eased = 0f;
            var landed = false;
            foreach (var step in steps)
            {
                if (landed)
                {
                    if (step.Steep <= eased + 0.01f)
                    {
                        eased = Mathf.Min(eased, step.Steep);
                        continue;
                    }

                    landed = false;
                    steepest = step.Steep;
                }

                steepest = Mathf.Max(steepest, step.Steep);
                if (steepest >= SplashMinSteepness && step.Steep <= steepest * LevelledOut)
                {
                    landings.Add(step);
                    landed = true;
                    eased = step.Steep;
                }
            }
        }

        var existing = GetChildren(true).OfType<GpuParticles3D>().ToList();
        for (var i = landings.Count; i < existing.Count; i++)
            existing[i].QueueFree();
        for (var i = 0; i < landings.Count; i++)
        {
            GpuParticles3D splash;
            if (i < existing.Count)
            {
                splash = existing[i];
            }
            else
            {
                splash = SplashScene.Instantiate<GpuParticles3D>();
                // Each its own, as each has its own width.
                splash.ProcessMaterial = (Material)splash.ProcessMaterial.Duplicate();
                AddChild(splash, false, InternalMode.Back);
            }

            if (SplashMotion is not null)
                splash.ProcessMaterial = (Material)SplashMotion.Duplicate();
            if (_splashesOutdated)
            {
                var original = SplashScene.Instantiate<GpuParticles3D>();
                if (SplashMotion is null)
                    splash.ProcessMaterial = (Material)original.ProcessMaterial.Duplicate();
                splash.DrawPass1 = SplashMesh ?? original.DrawPass1;
                splash.Lifetime = SplashLifetime > 0f ? SplashLifetime : original.Lifetime;
                original.Free();
            }

            var landing = landings[i];
            var downstream = new Vector3(landing.Forward.X, 0f, landing.Forward.Z).Normalized();
            splash.Transform = new Transform3D(new Basis(landing.Right, Vector3.Up, downstream), landing.At);
            if (splash.ProcessMaterial is ParticleProcessMaterial motion)
                motion.EmissionBoxExtents = motion.EmissionBoxExtents with { X = landing.Width * 0.5f };
            var amount = Math.Clamp(Mathf.RoundToInt(landing.Width * SplashAmount), 1, MostSplashParticles);
            // Setting it restarts the spray, so only when it has changed.
            if (splash.Amount != amount)
                splash.Amount = amount;
        }

        _splashesOutdated = false;
    }

    protected override ArrayMesh? BuildMesh()
    {
        var steps = Steps();
        _built = new BuiltSteps(steps);
        foreach (var body in Water.InTree.OfType<WaterBody>())
            body.QueueMask();
        UpdateSplashes(steps);
        if (steps.Count < 2)
            return null;
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<float>();
        var uvs = new List<Vector2>();
        var colours = new List<Color>();
        var indices = new List<int>();
        var uv2s = new List<Vector2>();
        // Its own speeds if it has any set; otherwise (y 0) the shader goes by its material's, so that shows at once as it is edited.
        var ownSpeed = FlowSpeed > 0f || PointFlowSpeeds.Any(s => s > 0f) ? 1f : 0f;
        // Across each step: red its speed-up (a quarter of it), green its foam, alpha fading to its edges.
        var columns = 5;
        if (steps.Any(s => s.Into is not null))
            columns = Math.Clamp(Mathf.CeilToInt(steps.Max(s => s.Width)) | 1, 5, MostColumns);
        var across = Enumerable.Range(0, columns).Select(i => (float)i / (columns - 1) - 0.5f).ToArray();
        // V runs on the time water takes to get there, at the speed the shader scrolls by, so scrolling at
        // one speed makes each place flow at its own, always downstream: the pattern just stretches where it is fast.
        var scrollSpeed = ownSpeed > 0f ? steps.Max(s => SpeedAt(s.Along)) : 0f;
        var travel = 0f;
        var lastAlong = steps[0].Along;
        foreach (var step in steps)
        {
            var relative = ownSpeed > 0f ? SpeedAt(step.Along) / Mathf.Max(scrollSpeed, 0.001f) : 1f;
            travel += (step.Along - lastAlong) / Mathf.Max(relative * SpeedUp(step.Steep), 0.01f);
            lastAlong = step.Along;
            var normal = step.Forward.Cross(step.Right).Normalized();
            var colour = new Color(SpeedUp(step.Steep) / FastestSpeedUp, Mathf.SmoothStep(0.1f, 0.6f, step.Steep), 0f);
            for (var i = 0; i < across.Length; i++)
            {
                var at = step.At + step.Right * across[i] * step.Width;
                var fading = step.Into is { } water ? 1f - Mathf.SmoothStep(0f, JoinBlend, water.InsideBy(ToGlobal(at))) : 1f;
                vertices.Add(at);
                normals.Add(normal);
                tangents.AddRange([step.Right.X, step.Right.Y, step.Right.Z, 1f]);
                uvs.Add(new Vector2(across[i] * step.Width, travel));
                // Its full width where it leaves its source, so its soft edges only start past the join.
                colours.Add(colour with { A = SideFade(across[i], step.Leaving) * fading * step.Ending });
                uv2s.Add(new Vector2(scrollSpeed, ownSpeed));
            }
        }

        for (var s = 0; s < steps.Count - 1; s++)
        {
            for (var i = 0; i < columns - 1; i++)
            {
                int a = s * columns + i, b = a + 1, c = a + columns, d = c + 1;
                indices.AddRange([a, b, c, b, d, c]);
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        arrays[(int)Mesh.ArrayType.Tangent] = tangents.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2s.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colours.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    public override Vector3 JoinPoint(Vector3 from, float inset) =>
        Curve is { PointCount: >= 2 } curve ? ToGlobal(curve.GetClosestPoint(ToLocal(from))) : from;

    /// <summary>A point just added to the end goes 1 m on from the one before, the way the last line was going.</summary>
    internal void PlaceAddedPoint()
    {
        if (Curve is not { } curve)
            return;
        var count = curve.PointCount;
        var added = curve.GetInstanceId() == _knownCurve && count == _knownCount + 1 && _knownCount >= 1
            && curve.GetPointPosition(count - 2).IsEqualApprox(_knownEnd);
        if (added)
        {
            var end = curve.GetPointPosition(count - 2);
            var line = count >= 3 ? end - curve.GetPointPosition(count - 3) : Vector3.Zero;
            curve.SetPointPosition(count - 1, end + (line.LengthSquared() > 1e-8f ? line.Normalized() : Vector3.Back));
        }

        _knownCurve = curve.GetInstanceId();
        _knownCount = count;
        _knownEnd = count > 0 ? curve.GetPointPosition(count - 1) : Vector3.Zero;
    }

    protected override void Changed()
    {
        if (Engine.IsEditorHint())
            PlaceAddedPoint();
        if (Source is null || !IsInstanceValid(Source) || Source == this || !Source.IsInsideTree() || !IsInsideTree() || Curve is not { PointCount: >= 2 } curve)
            return;
        var join = Source.JoinPoint(ToGlobal(curve.GetPointPosition(1)), JoinInset);
        var local = ToLocal(join);
        // Only when it has moved, as moving it asks for another rebuild.
        if (!local.IsEqualApprox(curve.GetPointPosition(0)))
            curve.SetPointPosition(0, local);
    }

    public override float InsideBy(Vector3 at)
    {
        if (Curve is not { PointCount: >= 2 } curve)
            return 0f;
        var local = ToLocal(at);
        var offset = curve.GetClosestOffset(local);
        var centre = curve.SampleBaked(offset, true);
        return Mathf.Max(WidthAt(offset) * 0.5f - new Vector2(local.X - centre.X, local.Z - centre.Z).Length(), 0f);
    }

    public override bool Covers(Vector3 at)
    {
        if (Curve is not { PointCount: >= 2 } curve)
            return false;
        var local = ToLocal(at);
        var offset = curve.GetClosestOffset(local);
        var centre = curve.SampleBaked(offset, true);
        return new Vector2(local.X - centre.X, local.Z - centre.Z).Length() <= WidthAt(offset) * 0.5f;
    }

    public override WaterSample? Sample(Vector3 at)
    {
        if (Curve is not { PointCount: >= 2 } curve)
            return null;
        var local = ToLocal(at);
        var offset = curve.GetClosestOffset(local);
        var centre = curve.SampleBaked(offset, true);
        var level = new Vector2(local.X - centre.X, local.Z - centre.Z);
        if (level.Length() > WidthAt(offset) * 0.5f)
            return null;
        var length = curve.GetBakedLength();
        var ahead = curve.SampleBaked(Mathf.Min(offset + 0.5f, length), true) - curve.SampleBaked(Mathf.Max(offset - 0.5f, 0f), true);
        if (ahead.LengthSquared() <= 1e-8f)
            return null;
        var forward = ahead.Normalized();
        var speed = SpeedAt(offset) * SpeedUp(Mathf.Clamp(-forward.Y, 0f, 1f));
        var surface = ToGlobal(centre);
        return SampleWith(new Vector3(at.X, surface.Y, at.Z), GlobalBasis.Orthonormalized() * forward * speed);
    }

    protected override Dictionary<(int X, int Z), float> Carved(IWaterGround ground)
    {
        var result = new Dictionary<(int X, int Z), float>();
        var spacing = ground.VertexSpacing;
        foreach (var step in Steps().Where(s => s.Along >= 0f && s.Along <= Curve!.GetBakedLength()))
        {
            var centre = ToGlobal(step.At);
            var core = step.Width * 0.5f;
            var reach = core + Bank;
            var middle = new Vector2(centre.X, centre.Z);
            var (fromX, toX) = (Mathf.FloorToInt((middle.X - reach) / spacing), Mathf.CeilToInt((middle.X + reach) / spacing));
            var (fromZ, toZ) = (Mathf.FloorToInt((middle.Y - reach) / spacing), Mathf.CeilToInt((middle.Y + reach) / spacing));
            for (var z = fromZ; z <= toZ; z++)
            {
                for (var x = fromX; x <= toX; x++)
                {
                    if (ground.SampleHeight(x, z) is not { } height)
                        continue;
                    var distance = (new Vector2(x, z) * spacing).DistanceTo(middle);
                    if (distance > reach)
                        continue;
                    var wanted = distance <= core
                        ? centre.Y - Depth * (1f - Mathf.SmoothStep(core * 0.5f, core, distance))
                        : Mathf.Lerp(centre.Y, height, Mathf.SmoothStep(core, reach, distance));
                    // The deepest any step wants: over a bend, one step's bank must not fill another's bed.
                    result[(x, z)] = result.TryGetValue((x, z), out var other) ? Mathf.Min(other, wanted) : wanted;
                }
            }
        }

        return result;
    }

    /// <summary>Sets each point on the ground, never above the one before, as water does not run uphill.</summary>
    public void FitToGround()
    {
        if (WaterGround.Finder?.Invoke(this) is not { } ground || Curve is not { } curve)
        {
            GD.PushWarning("UnpleasantWater: no ground to fit to. Set WaterGround.Finder to connect a terrain.");
            return;
        }

        var before = Enumerable.Range(0, curve.PointCount).Select(curve.GetPointPosition).ToArray();
        var after = new Vector3[before.Length];
        var lowest = float.PositiveInfinity;
        for (var i = 0; i < before.Length; i++)
        {
            var world = ToGlobal(before[i]);
            var height = ground.HeightAt(world);
            if (!float.IsNaN(height))
                world.Y = Mathf.Min(height, lowest);
            lowest = Mathf.Min(lowest, world.Y);
            after[i] = ToLocal(world);
        }
#if TOOLS
        if (Engine.IsEditorHint())
        {
            var undo = EditorInterface.Singleton.GetEditorUndoRedo();
            undo.CreateAction("Water: Fit Points To Ground");
            undo.AddDoMethod(this, MethodName.SetPointPositions, after);
            undo.AddUndoMethod(this, MethodName.SetPointPositions, before);
            undo.CommitAction();
            return;
        }
#endif
        SetPointPositions(after);
    }

    /// <summary>Moves the curve's points; public for undo.</summary>
    public void SetPointPositions(Vector3[] points)
    {
        for (var i = 0; i < points.Length && i < Curve!.PointCount; i++)
            Curve.SetPointPosition(i, points[i]);
    }
}
