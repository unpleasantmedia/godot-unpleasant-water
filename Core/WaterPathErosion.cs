using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantWater;

/// <summary>Wearing a river's bed into the ground, pass by pass, deeper and wider each time.</summary>
public partial class WaterPath
{
    [ExportGroup("Erosion")]
    /// <summary>How many times Erode Terrain goes over it.</summary>
    [Export(PropertyHint.Range, "1,50,1,or_greater")]
    public int ErosionPasses { get; set; } = 3;

    /// <summary>How much deeper each pass wears the middle of its bed, under the water.</summary>
    [Export(PropertyHint.Range, "0.05,10,0.05,or_greater,suffix:m")]
    public float ErosionDepth { get; set; } = 0.5f;

    /// <summary>How much further out from its edge the slope of its banks reaches each pass, at its widest; less in proportion where it is narrower. Ground already below the water is not touched.</summary>
    [Export(PropertyHint.Range, "0,20,0.1,or_greater,suffix:m")]
    public float ErosionWiden { get; set; } = 1f;

    /// <summary>How unevenly it wears, from none to as much again.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float ErosionNoise { get; set; } = 0.3f;

    /// <summary>How far Erode Terrain lowers its points, so its water lies below the banks it has cut rather than level with them. Less towards where it joins other water, whose height it keeps there.</summary>
    [Export(PropertyHint.Range, "0,5,0.05,or_greater,suffix:m")]
    public float ErosionWaterDrop { get; set; } = 0.5f;

    /// <summary>Its curve with its points lowered by <see cref="ErosionWaterDrop"/>.</summary>
    private Curve3D LoweredCurve()
    {
        var curve = (Curve3D)Curve!.Duplicate();
        var length = curve.GetBakedLength();
        var steps = Steps();
        var endsInWater = steps.Count > 0 && steps[^1].Ends is not null;
        var fromWater = Source is not null;
        for (var i = 0; i < curve.PointCount; i++)
        {
            var at = curve.GetPointPosition(i);
            var along = curve.GetClosestOffset(at);
            var drop = ErosionWaterDrop;
            if (endsInWater)
                drop *= Mathf.SmoothStep(0f, JoinBlend, length - along);
            if (fromWater)
                drop *= Mathf.SmoothStep(0f, JoinBlend, along);
            curve.SetPointPosition(i, at + Vector3.Down * drop);
        }

        return curve;
    }

    /// <summary>Whether Erode Terrain also paints the ground under the water with <see cref="RiverbedTextures"/>.</summary>
    [Export]
    public bool PaintRiverbed { get; set; }

    /// <summary>Which of the terrain's textures its bed is painted with: with several, they mix unevenly along it. Each is a bit, by its index.</summary>
    [Export(PropertyHint.Flags, "")]
    public int RiverbedTextures { get; set; }

    /// <summary>How far past the water's edge, on each side, its bed's paint reaches.</summary>
    [Export(PropertyHint.Range, "0,50,0.5,or_greater,suffix:m")]
    public float RiverbedMargin { get; set; } = 4f;

    // The terrain's texture names as the inspector's tick boxes.
    public override void _ValidateProperty(Godot.Collections.Dictionary property)
    {
        if (property["name"].AsStringName() == PropertyName.RiverbedTextures && IsInsideTree()
            && WaterGround.Finder?.Invoke(this) is IWaterGroundPaint paint)
            property["hint_string"] = string.Join(",", paint.TextureNames.Take(MostRiverbedTextures));
    }

    private const int MostRiverbedTextures = 32;

    private const float ErosionNoiseSize = 0.05f;

    /// <summary>What it wants of the paint under its water, added to what is wanted already.</summary>
    private void PaintBed(IWaterGround ground, IWaterGroundPaint paint, Dictionary<(int X, int Z), (int Was, int Now)> painted)
    {
        var textures = Enumerable.Range(0, MostRiverbedTextures).Where(i => (RiverbedTextures >> i & 1) != 0).ToList();
        if (textures.Count == 0)
        {
            GD.PushWarning($"UnpleasantWater: {Name} has Paint Riverbed on, but no Riverbed Textures ticked.");
            return;
        }

        var spacing = ground.VertexSpacing;
        var noise = new FastNoiseLite { Frequency = ErosionNoiseSize, Seed = (int)(Name.ToString().Hash() & 0x7fffffff) };
        var length = Curve!.GetBakedLength();
        var amounts = new Dictionary<(int X, int Z), float>();
        foreach (var step in Steps().Where(s => s.Along >= 0f && s.Along <= length))
        {
            var centre = ToGlobal(step.At);
            // Solid to halfway out across the margin, then fading out as far past it.
            var solid = step.Width * 0.5f + RiverbedMargin * 0.5f;
            var core = step.Width * 0.5f + RiverbedMargin * 1.5f;
            var middle = new Vector2(centre.X, centre.Z);
            var (fromX, toX) = (Mathf.FloorToInt((middle.X - core) / spacing), Mathf.CeilToInt((middle.X + core) / spacing));
            var (fromZ, toZ) = (Mathf.FloorToInt((middle.Y - core) / spacing), Mathf.CeilToInt((middle.Y + core) / spacing));
            for (var z = fromZ; z <= toZ; z++)
            {
                for (var x = fromX; x <= toX; x++)
                {
                    var distance = (new Vector2(x, z) * spacing).DistanceTo(middle);
                    if (distance > core)
                        continue;
                    // The most any step wants here: the step nearest it, over a bend.
                    var amount = (1f - Mathf.SmoothStep(solid, Mathf.Max(core, solid + 0.01f), distance)) * MouthFade(step, length);
                    if (amount <= 0f)
                        continue;
                    if (amounts.TryGetValue((x, z), out var more) && more >= amount)
                        continue;
                    amounts[(x, z)] = amount;
                    int was;
                    if (painted.TryGetValue((x, z), out var earlier))
                        was = earlier.Was;
                    else if (paint.SamplePaint(x, z) is { } read)
                        was = read;
                    else
                        continue;
                    // Somewhere along the list, by the noise: between two of its textures, blended by how far between.
                    var place = Mathf.Clamp(noise.GetNoise2D(x * spacing, z * spacing) * 0.5f + 0.5f, 0f, 1f) * (textures.Count - 1);
                    var low = Mathf.Min(Mathf.FloorToInt(place), textures.Count - 1);
                    var high = Mathf.Min(low + 1, textures.Count - 1);
                    painted[(x, z)] = (was, amount >= 1f
                        ? paint.Painted(was, textures[low], textures[high], place - low)
                        : paint.PaintedOver(was, place - low > 0.5f ? textures[high] : textures[low], amount));
                }
            }
        }
    }

    /// <summary>Sets ground samples' paint, in threes (x, z, paint); public for undo.</summary>
    public void SetGroundPaint(int[] triples)
    {
        if (WaterGround.Finder?.Invoke(this) is IWaterGroundPaint paint)
            paint.SetPaint(Enumerable.Range(0, triples.Length / 3).Select(i => (triples[i * 3], triples[i * 3 + 1], triples[i * 3 + 2])));
    }

    [ExportToolButton("Erode Terrain", Icon = "Terrain")]
    public Callable ErodeButton => Callable.From(ErodeTerrain);

    /// <summary>Wears its bed and those of the rivers it runs on into through a lake, as one step to undo.</summary>
    public void ErodeTerrain()
    {
        if (WaterGround.Finder?.Invoke(this) is not { } ground)
        {
            GD.PushWarning("UnpleasantWater: no ground to erode. Set WaterGround.Finder to connect a terrain.");
            return;
        }

        var heights = new Dictionary<(int X, int Z), float>();
        var originals = new Dictionary<(int X, int Z), float>();
        var painted = new Dictionary<(int X, int Z), (int Was, int Now)>();
        var rivers = RiversOnFromHere();
        var lowered = rivers.Select(r => r.LoweredCurve()).ToList();
        foreach (var path in rivers)
        {
            path.Erode(ground, heights, originals);
            if (!path.PaintRiverbed)
                continue;
            if (ground is IWaterGroundPaint paint)
                path.PaintBed(ground, paint, painted);
            else
                GD.PushWarning("UnpleasantWater: this ground can't be painted.");
        }

        var changed = heights.Where(h => h.Value < originals[h.Key]).ToList();
        if (changed.Count == 0 && painted.Count == 0)
            return;
        var after = changed.Select(h => new Vector3(h.Key.X, h.Value, h.Key.Z)).ToArray();
        var paintAfter = painted.SelectMany(p => new[] { p.Key.X, p.Key.Z, p.Value.Now }).ToArray();
#if TOOLS
        if (Engine.IsEditorHint())
        {
            var before = changed.Select(h => new Vector3(h.Key.X, originals[h.Key], h.Key.Z)).ToArray();
            var undo = EditorInterface.Singleton.GetEditorUndoRedo();
            undo.CreateAction("Water: Erode Terrain");
            undo.AddDoMethod(this, WaterNode.MethodName.SetGroundHeights, after);
            undo.AddUndoMethod(this, WaterNode.MethodName.SetGroundHeights, before);
            undo.AddDoMethod(this, MethodName.SetGroundPaint, paintAfter);
            undo.AddUndoMethod(this, MethodName.SetGroundPaint, painted.SelectMany(p => new[] { p.Key.X, p.Key.Z, p.Value.Was }).ToArray());
            for (var i = 0; i < rivers.Count; i++)
            {
                undo.AddDoProperty(rivers[i], Path3D.PropertyName.Curve, lowered[i]);
                undo.AddUndoProperty(rivers[i], Path3D.PropertyName.Curve, rivers[i].Curve);
            }

            undo.CommitAction();
            return;
        }
#endif
        SetGroundHeights(after);
        SetGroundPaint(paintAfter);
        for (var i = 0; i < rivers.Count; i++)
            rivers[i].Curve = lowered[i];
    }

    /// <summary>This river, and each that flows out of a lake or other water its end lies in, and so on.</summary>
    private List<WaterPath> RiversOnFromHere()
    {
        var found = new List<WaterPath> { this };
        for (var i = 0; i < found.Count; i++)
        {
            if (found[i].Curve is not { PointCount: >= 2 } curve)
                continue;
            var end = found[i].ToGlobal(curve.GetPointPosition(curve.PointCount - 1));
            foreach (var body in Water.InTree.OfType<WaterBody>().Where(b => b.Covers(end)))
                found.AddRange(Water.InTree.OfType<WaterPath>().Where(p => p.Source == body && !found.Contains(p)));
        }

        return found;
    }

    /// <summary>How much of its bed there is at a step: running out to none over Join Blend before it ends in other water, leaving no pit showing through that water.</summary>
    private float MouthFade(Step step, float length) =>
        step.Ends is null ? 1f : Mathf.SmoothStep(0f, JoinBlend, length - step.Along);

    private void Erode(IWaterGround ground, Dictionary<(int X, int Z), float> heights, Dictionary<(int X, int Z), float> originals)
    {
        var spacing = ground.VertexSpacing;
        var noise = new FastNoiseLite { Frequency = ErosionNoiseSize, Seed = (int)(Name.ToString().Hash() & 0x7fffffff) };
        var length = Curve!.GetBakedLength();
        var steps = Steps().Where(s => s.Along >= 0f && s.Along <= length).ToList();
        var widest = steps.Count > 0 ? steps.Max(s => s.Width) : 1f;
        for (var pass = 1; pass <= ErosionPasses; pass++)
        {
            // What this pass takes off each sample: over a bend, the most any step wants, not all of them added up.
            var worn = new Dictionary<(int X, int Z), float>();
            foreach (var step in steps)
            {
                var centre = ToGlobal(step.At);
                var core = step.Width * 0.5f;
                // Its banks in keeping with its width here: a set margin would make a narrow stretch's cut as wide as a wide one's.
                var reach = core + (Bank + ErosionWiden * pass) * step.Width / widest;
                var middle = new Vector2(centre.X, centre.Z);
                var (fromX, toX) = (Mathf.FloorToInt((middle.X - reach) / spacing), Mathf.CeilToInt((middle.X + reach) / spacing));
                var (fromZ, toZ) = (Mathf.FloorToInt((middle.Y - reach) / spacing), Mathf.CeilToInt((middle.Y + reach) / spacing));
                for (var z = fromZ; z <= toZ; z++)
                {
                    for (var x = fromX; x <= toX; x++)
                    {
                        var distance = (new Vector2(x, z) * spacing).DistanceTo(middle);
                        if (distance > reach || Current(ground, x, z, heights, originals) is not { } height)
                            continue;
                        // Under the water, its bed deepest in the middle and up to the surface at the water's edge, so the water
                        // meets its banks; past that, what stands over the water sloping up from it, and the rest of the ground left alone.
                        var uneven = 1f + ErosionNoise * noise.GetNoise2D(x * spacing, z * spacing);
                        var wanted = distance <= core
                            ? centre.Y - ErosionDepth * pass * uneven * MouthFade(step, length) * (1f - Mathf.SmoothStep(core * 0.5f, core, distance))
                            : Mathf.Lerp(centre.Y, height, Mathf.SmoothStep(core, reach, distance));
                        // Part of the way each pass, all of it by the last.
                        var amount = Mathf.Max(height - wanted, 0f) / (ErosionPasses - pass + 1);
                        worn[(x, z)] = worn.TryGetValue((x, z), out var other) ? Mathf.Max(other, amount) : amount;
                    }
                }
            }

            foreach (var (place, amount) in worn)
                heights[place] -= amount;
        }
    }

    private static float? Current(IWaterGround ground, int x, int z, Dictionary<(int X, int Z), float> heights, Dictionary<(int X, int Z), float> originals)
    {
        if (heights.TryGetValue((x, z), out var known))
            return known;
        if (ground.SampleHeight(x, z) is not { } height)
            return null;
        originals[(x, z)] = height;
        heights[(x, z)] = height;
        return height;
    }
}
