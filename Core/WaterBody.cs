using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantWater;

/// <summary>Water that lies still within an outline: a sea, a lake or a pond. Its surface is at the node's height, and the outline's own heights are ignored.</summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_water/Icons/Water-Body.svg")]
public partial class WaterBody : WaterNode
{
    public enum BodyType { Sea, Lake, Pond }

    private const int MostCellsAcross = 512;

    // A plain class, as the editor would carry an array in the node through a reload of the scripts and damage it.
    private sealed class OutlineCache(Vector2[] points)
    {
        public readonly Vector2[] Points = points;
    }

    private OutlineCache? _outline;

    private BodyType _type = BodyType.Lake;
    private float _cellSize = 2f;

    [Export]
    public BodyType Type
    {
        get => _type;
        set
        {
            _type = value;
            ApplyMaterial();
        }
    }

    /// <summary>How far apart its mesh's points are: smaller shows waves better, and costs more.</summary>
    [Export(PropertyHint.Range, "0.25,32,0.25,or_greater,suffix:m")]
    public float CellSize
    {
        get => _cellSize;
        set
        {
            _cellSize = Mathf.Max(value, 0.25f);
            QueueRebuild();
        }
    }

    // Where it is hidden under the rivers that join it, and a copy of its material that knows.
    private const int LargestMask = 2048;
    private const float MaskWait = 0.25f;
    private const float DefaultRiverClearance = 250f;
    private const float RiverSurfMargin = 20f;

    // Across a river, it only starts to be hidden where the river's soft sides are this solid, so there is always water under them.
    private const float HiddenFrom = 0.6f;
    private ImageTexture? _mask;
    private Vector4 _maskArea;
    private ShaderMaterial? _maskedFrom;
    private ShaderMaterial? _masked;
    private bool _maskQueued;

    protected override ShaderMaterial? DrawnMaterial
    {
        get
        {
            if ((_mask is null && _shoreMap is null) || Material is not { } material)
                return Material;
            if (_masked is null || _maskedFrom != material)
            {
                _masked = (ShaderMaterial)material.Duplicate();
                _maskedFrom = material;
            }

            _masked.SetShaderParameter("masked", _mask is not null);
            _masked.SetShaderParameter("water_mask", _mask);
            _masked.SetShaderParameter("mask_area", _maskArea);
            if (_shoreMap is not null)
            {
                _masked.SetShaderParameter("shore_map", _shoreMap);
                _masked.SetShaderParameter("shore_area", _shoreArea);
            }

            return _masked;
        }
    }

    // How far each place is from the shore, for waves that grow away from it and break on it. Plain, as with the outline.
    private sealed class ShoreCache(float[] metres, int width, int height)
    {
        public readonly float[] Metres = metres;
        public readonly int Width = width;
        public readonly int Height = height;
    }

    private const int LargestShoreMap = 1024;
    private const float FinestShoreMap = 4f;
    private const int ShoreBakeTries = 5;
    private const string ShoreFolder = "res://Terrain";
    private ShoreCache? _shore;
    private ImageTexture? _shoreMap;
    private Vector4 _shoreArea;
    private WaterShoreBake? _shoreBake;

    /// <summary>Its baked distances from the shore, kept in their own file. Bake Shore makes it, and saving the scene keeps the link to it.</summary>
    [Export]
    public WaterShoreBake? ShoreBake
    {
        get => _shoreBake;
        set
        {
            _shoreBake = value;
            UseShoreBake();
        }
    }

    [ExportToolButton("Bake Shore", Icon = "Bake")]
    public Callable BakeShoreButton => Callable.From(() => BakeShore());

    public override void _Ready()
    {
        base._Ready();
        UseShoreBake();
#if TOOLS
        // Baked once by itself, in the editor, if it has none yet: after that only when asked.
        if (Engine.IsEditorHint() && _shoreBake is null)
            CallDeferred(MethodName.TryBakingShore, ShoreBakeTries);
#endif
    }

    // The ground may not be there yet as a scene opens, so it asks again.
    private void TryBakingShore(int tries)
    {
        if (Material?.GetShaderParameter("shore_waves").AsBool() != true || !IsInsideTree())
            return;
        BakeShore(baked =>
        {
            if (!baked && tries > 1 && IsInstanceValid(this) && IsInsideTree())
                GetTree().CreateTimer(1f).Timeout += () => { if (IsInstanceValid(this)) TryBakingShore(tries - 1); };
        });
    }

    private void UseShoreBake()
    {
        if (_shoreBake?.Map is not { } map)
        {
            _shore = null;
            _shoreMap = null;
            ApplyMaterial();
            return;
        }

        var metres = new float[map.GetWidth() * map.GetHeight()];
        System.Buffer.BlockCopy(map.GetData(), 0, metres, 0, metres.Length * 4);
        _shore = new ShoreCache(metres, map.GetWidth(), map.GetHeight());
        _shoreMap = ImageTexture.CreateFromImage(map);
        _shoreArea = _shoreBake.Area;
        ApplyMaterial();
    }

    /// <summary>Saves a new bake to its file, and takes it up, for the scene to link to.</summary>
    private void KeepShoreBake(WaterShoreBake bake)
    {
#if TOOLS
        if (Engine.IsEditorHint())
        {
            var scene = GetTree().EditedSceneRoot?.SceneFilePath.GetFile().GetBaseName();
            var path = $"{ShoreFolder}/{(string.IsNullOrEmpty(scene) ? "Water" : scene)}-{Name}-Shore.res";
            DirAccess.MakeDirRecursiveAbsolute(ShoreFolder);
            if (ResourceSaver.Save(bake, path, ResourceSaver.SaverFlags.Compress) != Error.Ok)
                GD.PushWarning($"UnpleasantWater: could not save the shore bake to {path}.");
            else
                bake = ResourceLoader.Load<WaterShoreBake>(path, null, ResourceLoader.CacheMode.Replace);
            ShoreBake = bake;
            EditorInterface.Singleton.MarkSceneAsUnsaved();
            return;
        }
#endif
        ShoreBake = bake;
    }

    /// <summary>Works out how far each place is from the shore, from the ground. Run it again after changing the ground or the outline. False if there is no ground to go by.</summary>
    public void BakeShore(System.Action<bool>? done = null)
    {
        var outline = Outline();
        if (outline.Length < 3 || WaterGround.Finder?.Invoke(this) is not { } ground)
        {
            done?.Invoke(false);
            return;
        }

        var box = Bounds(outline);
        var perPixel = Mathf.Max(FinestShoreMap, Mathf.Max(box.Size.X, box.Size.Y) / LargestShoreMap);
        var (width, height) = (Mathf.CeilToInt(box.Size.X / perPixel) + 1, Mathf.CeilToInt(box.Size.Y / perPixel) + 1);
        var surface = GlobalPosition.Y;
        var toWorld = GlobalTransform;
        // Worked out on a thread (the ground's heights read there; nothing must change it meanwhile), then kept here.
        var started = WaterJob.Start(this, $"Bake Shore ({Name})", job =>
        {
        job.SetTotal(height * 3L);
        var metres = new float[width * height];
        var anyLand = false;
        for (var z = 0; z < height; z++)
        {
            for (var x = 0; x < width; x++)
            {
                var ground_height = ground.HeightAt(toWorld * (new Vector3(box.Position.X + x * perPixel, 0f, box.Position.Y + z * perPixel)));
                var land = !float.IsNaN(ground_height) && ground_height >= surface;
                anyLand |= land;
                metres[z * width + x] = land ? 0f : float.MaxValue;
            }

            job.Step();
        }

        if (!anyLand)
            return null;
        // There and back over the map, each place taking the nearest its neighbours have found.
        var (straight, diagonal) = (perPixel, perPixel * 1.41421f);
        for (var z = 0; z < height; z++)
        {
            for (var x = 0; x < width; x++)
            {
                var best = metres[z * width + x];
                if (x > 0)
                    best = Mathf.Min(best, metres[z * width + x - 1] + straight);
                if (z > 0)
                {
                    best = Mathf.Min(best, metres[(z - 1) * width + x] + straight);
                    if (x > 0)
                        best = Mathf.Min(best, metres[(z - 1) * width + x - 1] + diagonal);
                    if (x < width - 1)
                        best = Mathf.Min(best, metres[(z - 1) * width + x + 1] + diagonal);
                }

                metres[z * width + x] = best;
            }

            job.Step();
        }

        for (var z = height - 1; z >= 0; z--)
        {
            for (var x = width - 1; x >= 0; x--)
            {
                var best = metres[z * width + x];
                if (x < width - 1)
                    best = Mathf.Min(best, metres[z * width + x + 1] + straight);
                if (z < height - 1)
                {
                    best = Mathf.Min(best, metres[(z + 1) * width + x] + straight);
                    if (x < width - 1)
                        best = Mathf.Min(best, metres[(z + 1) * width + x + 1] + diagonal);
                    if (x > 0)
                        best = Mathf.Min(best, metres[(z + 1) * width + x - 1] + diagonal);
                }

                metres[z * width + x] = best;
            }

            job.Step();
        }

        var bytes = new byte[metres.Length * 4];
        System.Buffer.BlockCopy(metres, 0, bytes, 0, bytes.Length);
        return bytes;
        }, bytes =>
        {
            if (bytes is null || !IsInstanceValid(this))
            {
                done?.Invoke(false);
                return;
            }

            var corner = toWorld * new Vector3(box.Position.X, 0f, box.Position.Y);
            KeepShoreBake(new WaterShoreBake
            {
                Map = Image.CreateFromData(width, height, false, Image.Format.Rf, bytes),
                Area = new Vector4(corner.X, corner.Z, (width - 1) * perPixel, (height - 1) * perPixel),
            });
            done?.Invoke(true);
        }, readsTerrain: true);
        if (!started)
            done?.Invoke(false);
    }

    /// <summary>How far towards its full offshore waves a place is, from 0 at the shore to 1.</summary>
    private float OffshoreAt(Vector3 at)
    {
        if (_shore is not { } shore || _shoreArea.Z <= 0f || _shoreArea.W <= 0f)
            return 1f;
        var x = Mathf.RoundToInt((at.X - _shoreArea.X) / _shoreArea.Z * (shore.Width - 1));
        var z = Mathf.RoundToInt((at.Z - _shoreArea.Y) / _shoreArea.W * (shore.Height - 1));
        if (x < 0 || z < 0 || x >= shore.Width || z >= shore.Height)
            return 1f;
        var range = Mathf.Max(Material?.GetShaderParameter("offshore_distance").AsSingle() ?? 1f, 1f);
        return Mathf.SmoothStep(0f, range, shore.Metres[z * shore.Width + x]);
    }

    /// <summary>Paints its mask again soon, once however many rivers change at once.</summary>
    internal void QueueMask()
    {
        if (_maskQueued)
            return;
        _maskQueued = true;
        // At once the first time; after that not on every step of a point being dragged.
        if (_mask is null || !IsInsideTree())
            CallDeferred(MethodName.UpdateMask);
        else
            GetTree().CreateTimer(MaskWait).Timeout += UpdateMask;
    }

    private void UpdateMask()
    {
        _maskQueued = false;
        if (!IsInstanceValid(this))
            return;
        var paths = Water.InTree.OfType<WaterPath>().ToList();
        var rows = paths.SelectMany(p => p.MaskRows(this).Select(r => (Path: p, Row: r))).ToList();
        var clearance = Material?.GetShaderParameter("surf_river_clearance") is { VariantType: not Variant.Type.Nil } set ? set.AsSingle() : DefaultRiverClearance;
        var mouths = paths.Select(p => p.MouthAt(this, clearance)).OfType<(Vector2 Middle, float Radius)>().ToList();
        if (rows.Count == 0)
        {
            if (_mask is not null)
            {
                _mask = null;
                ApplyMaterial();
            }

            return;
        }

        var low = new Vector2(float.MaxValue, float.MaxValue);
        var high = new Vector2(float.MinValue, float.MinValue);
        foreach (var (_, row) in rows)
        {
            var reach = new Vector2(Mathf.Abs(row.Across.X), Mathf.Abs(row.Across.Y)) * 0.5f + Vector2.One * (RiverSurfMargin + 1f);
            low = low.Min(row.Middle - reach);
            high = high.Max(row.Middle + reach);
        }

        foreach (var (middle, radius) in mouths)
        {
            low = low.Min(middle - Vector2.One * radius);
            high = high.Max(middle + Vector2.One * radius);
        }

        var size = high - low;
        var perPixel = Mathf.Max(1f, Mathf.Max(size.X, size.Y) / LargestMask);
        var (width, height) = (Mathf.CeilToInt(size.X / perPixel) + 1, Mathf.CeilToInt(size.Y / perPixel) + 1);
        // Two to each: how much it is hidden, and how much surf is kept away.
        var pixels = new byte[width * height * 2];
        foreach (var (middle, radius) in mouths)
        {
            var (fromX, toX) = (Mathf.FloorToInt((middle.X - radius - low.X) / perPixel), Mathf.CeilToInt((middle.X + radius - low.X) / perPixel));
            var (fromZ, toZ) = (Mathf.FloorToInt((middle.Y - radius - low.Y) / perPixel), Mathf.CeilToInt((middle.Y + radius - low.Y) / perPixel));
            for (var z = System.Math.Max(fromZ, 0); z <= toZ && z < height; z++)
            {
                for (var x = System.Math.Max(fromX, 0); x <= toX && x < width; x++)
                {
                    var away = (low + new Vector2(x, z) * perPixel).DistanceTo(middle);
                    var calm = (byte)((1f - Mathf.SmoothStep(radius * 0.6f, radius, away)) * 255f);
                    var index = (z * width + x) * 2 + 1;
                    pixels[index] = System.Math.Max(pixels[index], calm);
                }
            }
        }

        // No surf along a river either, nor a little to each side of it, up its banks.
        foreach (var (_, row) in rows)
        {
            var wide = row.Across.Length() + RiverSurfMargin * 2f;
            var along = row.Across.Normalized();
            var count = Mathf.CeilToInt(wide / (perPixel * 0.7f));
            for (var i = 0; i <= count; i++)
            {
                var off = wide * i / count - wide * 0.5f;
                var at = row.Middle + along * off;
                // All of it beside the river, easing to none over the margin, so no line shows where it stops.
                var keptAway = (byte)((1f - Mathf.SmoothStep(row.Across.Length() * 0.5f, wide * 0.5f, Mathf.Abs(off))) * 255f);
                for (var dz = 0; dz <= 1; dz++)
                {
                    for (var dx = 0; dx <= 1; dx++)
                    {
                        var x = Mathf.FloorToInt((at.X - low.X) / perPixel) + dx;
                        var z = Mathf.FloorToInt((at.Y - low.Y) / perPixel) + dz;
                        if (x >= 0 && z >= 0 && x < width && z < height)
                            pixels[(z * width + x) * 2 + 1] = System.Math.Max(pixels[(z * width + x) * 2 + 1], keptAway);
                    }
                }
            }
        }

        foreach (var (_, row) in rows)
        {
            var length = row.Across.Length();
            var count = Mathf.CeilToInt(length / (perPixel * 0.7f));
            var along = row.Across.Normalized();
            for (var i = 0; i <= count; i++)
            {
                var across = (float)i / count - 0.5f;
                var at = row.Middle + along * (across * length);
                var byteValue = (byte)(row.Value * Mathf.SmoothStep(HiddenFrom, 1f, WaterPath.SideFade(across, row.Leaving, row.Soft)) * 255f);
                if (byteValue == 0)
                    continue;
                // Two pixels each way, to leave no holes between one row and the next.
                for (var dz = 0; dz <= 1; dz++)
                {
                    for (var dx = 0; dx <= 1; dx++)
                    {
                        var x = Mathf.FloorToInt((at.X - low.X) / perPixel) + dx;
                        var z = Mathf.FloorToInt((at.Y - low.Y) / perPixel) + dz;
                        if (x >= 0 && z >= 0 && x < width && z < height)
                            pixels[(z * width + x) * 2] = System.Math.Max(pixels[(z * width + x) * 2], byteValue);
                    }
                }
            }
        }

        _mask = ImageTexture.CreateFromImage(Image.CreateFromData(width, height, false, Image.Format.Rg8, pixels));
        _maskArea = new Vector4(low.X, low.Y, width * perPixel, height * perPixel);
        ApplyMaterial();
    }

    protected override ShaderMaterial? TypeMaterial => LoadType(Type.ToString());

    protected override Curve3D NewCurve()
    {
        var curve = new Curve3D { Closed = true };
        foreach (var (x, z) in new[] { (-20f, -20f), (20f, -20f), (20f, 20f), (-20f, 20f) })
            curve.AddPoint(new Vector3(x, 0f, z));
        return curve;
    }

    protected override void Changed()
    {
        _outline = null;
        QueueMask();
    }

    /// <summary>The outline, seen from above in the node's own space.</summary>
    private Vector2[] Outline()
    {
        if (_outline is not null)
            return _outline.Points;
        var baked = Curve?.GetBakedPoints() ?? [];
        // At most about this many, as Carve Terrain measures each sample against them all.
        var every = Math.Max(1, baked.Length / 128);
        var points = baked.Where((_, i) => i % every == 0).Select(p => new Vector2(p.X, p.Z)).ToArray();
        _outline = new OutlineCache(points);
        return points;
    }

    private static Rect2 Bounds(Vector2[] points)
    {
        var box = new Rect2(points[0], Vector2.Zero);
        foreach (var point in points)
            box = box.Expand(point);
        return box;
    }

    private float CellFor(Rect2 box) => Mathf.Max(CellSize, Mathf.Max(box.Size.X, box.Size.Y) / MostCellsAcross);

    private static bool CellTouches(Vector2 low, float cell, Vector2[] outline) =>
        new[] { low, low + new Vector2(cell, 0f), low + new Vector2(0f, cell), low + new Vector2(cell, cell), low + new Vector2(cell, cell) * 0.5f }
            .Any(p => Geometry2D.IsPointInPolygon(p, outline));

    public override float InsideBy(Vector3 at)
    {
        var outline = Outline();
        var local = ToLocal(at);
        var place = new Vector2(local.X, local.Z);
        if (outline.Length < 3 || !Geometry2D.IsPointInPolygon(place, outline))
            return 0f;
        var edge = float.MaxValue;
        for (var i = 0; i < outline.Length; i++)
            edge = Mathf.Min(edge, Geometry2D.GetClosestPointToSegment(place, outline[i], outline[(i + 1) % outline.Length]).DistanceTo(place));
        return edge;
    }

    // By its mesh's cells, which reach a little past the outline.
    public override bool Covers(Vector3 at)
    {
        var outline = Outline();
        if (outline.Length < 3)
            return false;
        var box = Bounds(outline);
        var cell = CellFor(box);
        var local = ToLocal(at);
        var offset = new Vector2(local.X, local.Z) - box.Position;
        if (offset.X < 0f || offset.Y < 0f || offset.X > box.Size.X + cell || offset.Y > box.Size.Y + cell)
            return false;
        return CellTouches(box.Position + (offset / cell).Floor() * cell, cell, outline);
    }

    protected override ArrayMesh? BuildMesh()
    {
        var outline = Outline();
        if (outline.Length < 3)
            return null;
        var box = Bounds(outline);
        var cell = CellFor(box);
        var (across, along) = (Mathf.CeilToInt(box.Size.X / cell) + 1, Mathf.CeilToInt(box.Size.Y / cell) + 1);
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        var used = new Dictionary<(int, int), int>();
        int Vertex(int x, int z)
        {
            if (used.TryGetValue((x, z), out var index))
                return index;
            vertices.Add(new Vector3(box.Position.X + x * cell, 0f, box.Position.Y + z * cell));
            return used[(x, z)] = vertices.Count - 1;
        }

        for (var z = 0; z < along; z++)
        {
            for (var x = 0; x < across; x++)
            {
                // Every cell touching the outline, so the ground it meets decides the shore.
                if (!CellTouches(box.Position + new Vector2(x, z) * cell, cell, outline))
                    continue;
                int a = Vertex(x, z), b = Vertex(x + 1, z), c = Vertex(x, z + 1), d = Vertex(x + 1, z + 1);
                indices.AddRange([a, b, c, b, d, c]);
            }
        }

        if (indices.Count == 0)
            return null;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = Enumerable.Repeat(Vector3.Up, vertices.Count).ToArray();
        arrays[(int)Mesh.ArrayType.Tangent] = Enumerable.Range(0, vertices.Count).SelectMany(_ => new[] { 1f, 0f, 0f, 1f }).ToArray();
        arrays[(int)Mesh.ArrayType.Color] = Enumerable.Repeat(new Color(0.25f, 0f, 0f, 1f), vertices.Count).ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    public override Vector3 JoinPoint(Vector3 from, float inset)
    {
        var outline = Outline();
        if (outline.Length < 3)
            return from;
        var local = ToLocal(from);
        var towards = new Vector2(local.X, local.Z);
        var edge = outline[0];
        for (var i = 0; i < outline.Length; i++)
        {
            var near = Geometry2D.GetClosestPointToSegment(towards, outline[i], outline[(i + 1) % outline.Length]);
            if (near.DistanceSquaredTo(towards) < edge.DistanceSquaredTo(towards))
                edge = near;
        }

        var inner = edge + (edge - towards).Normalized() * inset;
        var join = Geometry2D.IsPointInPolygon(inner, outline) ? inner : edge;
        return ToGlobal(new Vector3(join.X, 0f, join.Y));
    }

    public override WaterSample? Sample(Vector3 at)
    {
        var local = ToLocal(at);
        var outline = Outline();
        if (outline.Length < 3 || !Geometry2D.IsPointInPolygon(new Vector2(local.X, local.Z), outline))
            return null;
        return SampleWith(new Vector3(at.X, GlobalPosition.Y, at.Z), Vector3.Zero, OffshoreAt(at));
    }

    protected override Dictionary<(int X, int Z), float> Carved(IWaterGround ground)
    {
        var result = new Dictionary<(int X, int Z), float>();
        var outline = Outline();
        if (outline.Length < 3)
            return result;
        var spacing = ground.VertexSpacing;
        var box = Bounds(outline).Grow(Bank);
        var offset = new Vector2(GlobalPosition.X, GlobalPosition.Z);
        var surface = GlobalPosition.Y;
        for (var z = Mathf.FloorToInt((box.Position.Y + offset.Y) / spacing); z <= Mathf.CeilToInt((box.End.Y + offset.Y) / spacing); z++)
        {
            for (var x = Mathf.FloorToInt((box.Position.X + offset.X) / spacing); x <= Mathf.CeilToInt((box.End.X + offset.X) / spacing); x++)
            {
                if (ground.SampleHeight(x, z) is not { } height)
                    continue;
                var at = new Vector2(x, z) * spacing - offset;
                var edge = float.MaxValue;
                for (var i = 0; i < outline.Length; i++)
                    edge = Mathf.Min(edge, Geometry2D.GetClosestPointToSegment(at, outline[i], outline[(i + 1) % outline.Length]).DistanceTo(at));
                if (Geometry2D.IsPointInPolygon(at, outline))
                    result[(x, z)] = surface - Depth * Mathf.SmoothStep(0f, Mathf.Max(Bank, spacing), edge);
                else if (edge < Bank)
                    result[(x, z)] = Mathf.Lerp(surface, height, Mathf.SmoothStep(0f, Bank, edge));
            }
        }

        return result;
    }
}
