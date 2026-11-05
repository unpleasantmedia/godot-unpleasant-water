using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantWater;

/// <summary>
/// A piece of water drawn from its curve, which the 3D view's Path3D tools edit. Its mesh is an
/// internal child, kept out of the saved scene and made again from the curve.
/// </summary>
[Tool]
public abstract partial class WaterNode : Path3D
{
    private MeshInstance3D? _mesh;
    private bool _rebuilding;

    private ShaderMaterial? _customMaterial;

    /// <summary>A material to use instead of its type's.</summary>
    [Export]
    public ShaderMaterial? CustomMaterial
    {
        get => _customMaterial;
        set
        {
            _customMaterial = value;
            ApplyMaterial();
        }
    }

    [ExportGroup("Terrain")]
    /// <summary>How far under the surface Carve Terrain digs.</summary>
    [Export(PropertyHint.Range, "0.1,50,0.1,or_greater,suffix:m")]
    public float Depth { get; set; } = 2f;

    /// <summary>How far past the water Carve Terrain blends back into the ground.</summary>
    [Export(PropertyHint.Range, "0,50,0.5,or_greater,suffix:m")]
    public float Bank { get; set; } = 4f;

    [ExportToolButton("Carve Terrain", Icon = "Terrain")]
    public Callable CarveButton => Callable.From(CarveTerrain);

    /// <summary>The material it draws with.</summary>
    public ShaderMaterial? Material => CustomMaterial ?? TypeMaterial;

    /// <summary>What its mesh is actually given.</summary>
    protected virtual ShaderMaterial? DrawnMaterial => Material;

    protected abstract ShaderMaterial? TypeMaterial { get; }

    protected abstract ArrayMesh? BuildMesh();

    /// <summary>The water at a place in the world, or null if it is not over this water.</summary>
    public abstract WaterSample? Sample(Vector3 at);

    /// <summary>What Carve Terrain wants of the ground: each sample's height given its current one, for samples it touches.</summary>
    protected abstract Dictionary<(int X, int Z), float> Carved(IWaterGround ground);

    /// <summary>Where a path coming from a place joins this water, in the world: this far inside it if it can be.</summary>
    public abstract Vector3 JoinPoint(Vector3 from, float inset);

    /// <summary>How far inside its surface a place in the world is, seen from above, in metres; 0 or less if it is outside.</summary>
    public abstract float InsideBy(Vector3 at);

    /// <summary>How far above its resting height its surface can get, with its waves.</summary>
    internal float HighestWave => Material?.GetShaderParameter("wave_height").AsSingle() ?? 0f;

    /// <summary>Whether its surface is drawn at a place in the world, seen from above.</summary>
    public abstract bool Covers(Vector3 at);

    /// <summary>The curve a new node starts with.</summary>
    protected abstract Curve3D NewCurve();

    public override void _EnterTree()
    {
        if (!Water.InTree.Contains(this))
            Water.InTree.Add(this);
        SetNotifyTransform(true);
        var changed = new Callable(this, MethodName.QueueRebuild);
        if (!IsConnected(Path3D.SignalName.CurveChanged, changed))
            Connect(Path3D.SignalName.CurveChanged, changed);
    }

    public override void _ExitTree() => Water.InTree.Remove(this);

    public override void _Notification(int what)
    {
        if (what == NotificationTransformChanged)
        {
            RebuildPathsFromHere();
            // What a path joins depends on where it is in the world, though its mesh's shape does not.
            if (this is WaterPath)
                QueueRebuild();
        }
    }

    // Paths flowing from this water follow it when it changes.
    private void RebuildPathsFromHere()
    {
        foreach (var node in Water.InTree)
        {
            if (node is WaterPath path && path.Source == this)
                path.QueueRebuild();
        }
    }

    public override void _Ready()
    {
        // In the editor a new Path3D comes with an empty curve, not none.
        if (Curve is not { PointCount: > 0 })
            Curve = NewCurve();
        _mesh = new MeshInstance3D { Name = "Surface", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled, ExtraCullMargin = 4f };
        AddChild(_mesh, false, InternalMode.Back);
        // One in the scene is enough, on whichever water gets here first.
        if (!Water.InTree.Any(w => w.GetChildren(true).OfType<WaterUnderwater>().Any()))
            AddChild(GD.Load<PackedScene>("res://addons/unpleasant_water/Underwater.tscn").Instantiate(), false, InternalMode.Back);
        Rebuild();
    }

    /// <summary>Makes its mesh again soon, once however many changes come at once.</summary>
    internal void QueueRebuild()
    {
        Outdated();
        if (_rebuilding)
            return;
        _rebuilding = true;
        CallDeferred(MethodName.Rebuild);
    }

    private void Rebuild()
    {
        _rebuilding = false;
        Changed();
        RebuildPathsFromHere();
        if (_mesh is null)
            return;
        _mesh.Mesh = BuildMesh();
        ApplyMaterial();
    }

    /// <summary>Called as soon as anything about it changes, for anything worked out from its curve.</summary>
    protected virtual void Outdated()
    {
    }

    /// <summary>Called when its shape has changed, for anything kept from it.</summary>
    protected virtual void Changed()
    {
    }

    protected void ApplyMaterial()
    {
        if (_mesh is not null)
            _mesh.MaterialOverride = DrawnMaterial;
    }

    /// <summary>The material of a type, from its file in the plugin.</summary>
    protected static ShaderMaterial? LoadType(string type) =>
        GD.Load<ShaderMaterial>($"res://addons/unpleasant_water/Materials/{type}.tres");

    /// <summary>A sample of its surface at a place, with its material's waves added.</summary>
    protected WaterSample SampleWith(Vector3 surface, Vector3 flow, float offshore = 1f) =>
        new(surface.Y + Water.WaveHeight(Material, new Vector2(surface.X, surface.Z), Time.GetTicksMsec() / 1000f, offshore), flow, this);

    /// <summary>Digs the ground out to what this water wants, as one step to undo.</summary>
    public void CarveTerrain()
    {
        if (WaterGround.Finder?.Invoke(this) is not { } ground)
        {
            GD.PushWarning("UnpleasantWater: no ground to carve. Set WaterGround.Finder to connect a terrain.");
            return;
        }

        var before = new List<Vector3>();
        var after = new List<Vector3>();
        foreach (var ((x, z), height) in Carved(ground))
        {
            if (ground.SampleHeight(x, z) is { } was && height < was)
            {
                before.Add(new Vector3(x, was, z));
                after.Add(new Vector3(x, height, z));
            }
        }

        if (after.Count == 0)
            return;
#if TOOLS
        if (Engine.IsEditorHint())
        {
            var undo = EditorInterface.Singleton.GetEditorUndoRedo();
            undo.CreateAction("Water: Carve Terrain");
            undo.AddDoMethod(this, MethodName.SetGroundHeights, after.ToArray());
            undo.AddUndoMethod(this, MethodName.SetGroundHeights, before.ToArray());
            undo.CommitAction();
            return;
        }
#endif
        SetGroundHeights([.. after]);
    }

    /// <summary>Sets ground samples' heights, each given as (x, height, z); public for undo.</summary>
    public void SetGroundHeights(Vector3[] heights) =>
        WaterGround.Finder?.Invoke(this)?.SetHeights(heights.Select(h => ((int)h.X, (int)h.Z, h.Y)));
}
