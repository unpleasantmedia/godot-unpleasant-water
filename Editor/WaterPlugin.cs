#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantWater.Editor;

/// <summary>
/// The plugin itself. WaterBody and WaterPath are global classes, edited with the 3D view's Path3D
/// tools and their inspector buttons; this adds selecting a WaterPath's points, and drawing paths
/// in the 3D view as roads are drawn, with water never running uphill.
/// </summary>
[Tool]
public partial class WaterPlugin : EditorPlugin
{
    private const float PickRadius = 14f;
    private const float WidthStep = 0.5f;
    private const float StillClick = 4f;
    private const float FirstMarch = 2f;
    private const float MarchGrowth = 0.01f;
    private const int RefineSteps = 10;

    private WaterPointGizmos? _points;
    private Control? _toolbar;
    private Button _drawButton = null!;
    private Button? _generateButton;
    private SpinBox? _stopHeightSpin;
    private SpinBox? _widenSpin;
    private SpinBox? _widenFromSpin;
    private SpinBox? _speedSpin;
    private SpinBox? _slopeBoostSpin;
    private OptionButton _typeMenu = null!;
    private SpinBox _widthSpin = null!;
    private SpinBox _pointWidthSpin = null!;

    private WaterPath? _path;
    private int _point = -1;
    private bool _finished;
    private bool _dragging;
    private Curve3D? _pressedCurve;
    private Vector2? _rightPressedAt;

    private bool Drawing => _drawButton?.ButtonPressed == true;

    private WaterPath? Path => _path is not null && IsInstanceValid(_path) && _path.IsInsideTree() && _path.Curve is not null ? _path : null;

    public override void _EnterTree()
    {
        _points = new WaterPointGizmos();
        AddNode3DGizmoPlugin(_points);
        MakeToolbar();
    }

    /// <summary>
    /// Puts the toolbar up if it is not there. Not only on entering the tree: a plugin already running
    /// when its scripts are rebuilt with a toolbar new to them does not enter it again.
    /// </summary>
    private void MakeToolbar()
    {
        if (_toolbar is not null && IsInstanceValid(_toolbar))
        {
            if (_slopeBoostSpin is not null)
                return;
            // Put up by scripts from before it had this button.
            RemoveControlFromContainer(CustomControlContainer.SpatialEditorMenu, _toolbar);
            _toolbar.QueueFree();
        }

        _toolbar = GD.Load<PackedScene>("res://addons/unpleasant_water/Editor/WaterToolbar.tscn").Instantiate<Control>();
        _drawButton = _toolbar.GetNode<Button>("%DrawButton");
        _generateButton = _toolbar.GetNode<Button>("%GenerateButton");
        _stopHeightSpin = _toolbar.GetNode<SpinBox>("%StopHeightSpin");
        _widenSpin = _toolbar.GetNode<SpinBox>("%WidenSpin");
        _widenFromSpin = _toolbar.GetNode<SpinBox>("%WidenFromSpin");
        _speedSpin = _toolbar.GetNode<SpinBox>("%SpeedSpin");
        _slopeBoostSpin = _toolbar.GetNode<SpinBox>("%SlopeBoostSpin");
        _typeMenu = _toolbar.GetNode<OptionButton>("%TypeMenu");
        _widthSpin = _toolbar.GetNode<SpinBox>("%WidthSpin");
        _pointWidthSpin = _toolbar.GetNode<SpinBox>("%PointWidthSpin");
        // By Godot's Connect, to named methods: these last through a rebuild of the scripts.
        _typeMenu.Connect(OptionButton.SignalName.ItemSelected, new Callable(this, MethodName.ChooseType));
        _widthSpin.Connect(Range.SignalName.ValueChanged, new Callable(this, MethodName.ChooseWidth));
        _pointWidthSpin.Connect(Range.SignalName.ValueChanged, new Callable(this, MethodName.ChoosePointWidth));
        _toolbar.Visible = false;
        AddControlToContainer(CustomControlContainer.SpatialEditorMenu, _toolbar);
    }

    private void ChoosePointWidth(double width) => SetPointWidth((float)width);

    public override void _ExitTree()
    {
        if (_points is not null)
            RemoveNode3DGizmoPlugin(_points);
        _points = null;
        if (_toolbar is not null)
        {
            RemoveControlFromContainer(CustomControlContainer.SpatialEditorMenu, _toolbar);
            _toolbar.QueueFree();
        }

        _toolbar = null;
    }

    public override bool _Handles(GodotObject @object) => @object is WaterNode;

    public override void _Edit(GodotObject? @object)
    {
        MakeToolbar();
        if (@object is not null)
            _toolbar!.Visible = true;
        var path = @object as WaterPath;
        if (path != _path)
        {
            _path = path;
            _point = -1;
            _finished = false;
        }

        ShowInToolbar();
        UpdateOverlays();
    }

    public override void _MakeVisible(bool visible)
    {
        MakeToolbar();
        _toolbar!.Visible = visible;
    }

    /// <summary>The toolbar shows what is selected, without that counting as a change to it.</summary>
    private void ShowInToolbar()
    {
        if (Path is { } path)
        {
            _typeMenu.Select((int)path.Type);
            _widthSpin.SetValueNoSignal(path.Width);
        }

        var widths = Path?.PointWidths;
        _pointWidthSpin.SetValueNoSignal(widths is not null && _point >= 0 && _point < widths.Count ? widths[_point] : 0f);
        _pointWidthSpin.Editable = Path is not null && _point >= 0;
    }

    private void ChooseType(long type)
    {
        if (Path is not { } path)
            return;
        var undo = GetUndoRedo();
        undo.CreateAction("Water: Set Type");
        undo.AddDoProperty(path, WaterPath.PropertyName.Type, (int)type);
        undo.AddUndoProperty(path, WaterPath.PropertyName.Type, (int)path.Type);
        undo.CommitAction();
    }

    private void ChooseWidth(double width)
    {
        if (Path is not { } path)
            return;
        var undo = GetUndoRedo();
        undo.CreateAction("Water: Set Width", UndoRedo.MergeMode.Ends);
        undo.AddDoProperty(path, WaterPath.PropertyName.Width, (float)width);
        undo.AddUndoProperty(path, WaterPath.PropertyName.Width, path.Width);
        undo.CommitAction();
    }

    /// <summary>Sets the selected point's width, as a step to undo that further changes run into.</summary>
    private void SetPointWidth(float width)
    {
        if (Path is not { } path || _point < 0 || _point >= path.Curve.PointCount)
            return;
        var widths = path.PointWidths.Duplicate();
        while (widths.Count <= _point)
            widths.Add(0f);
        widths[_point] = Mathf.Max(width, 0f);
        var undo = GetUndoRedo();
        undo.CreateAction("Water: Set Point Width", UndoRedo.MergeMode.Ends);
        undo.AddDoProperty(path, WaterPath.PropertyName.PointWidths, widths);
        undo.AddUndoProperty(path, WaterPath.PropertyName.PointWidths, path.PointWidths.Duplicate());
        undo.CommitAction();
        ShowInToolbar();
    }

    /// <summary>The river from a source clicked, with the lakes it fills, as one step to undo.</summary>
    private void Generate(Vector3 source)
    {
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (WaterGround.Finder?.Invoke(root) is not { } ground)
        {
            GD.PushWarning("UnpleasantWater: no ground to run down. Set WaterGround.Finder to connect a terrain.");
            return;
        }

        var plan = new WaterRiverPlanner(ground, Water.InTree.OfType<WaterBody>()).Plan(source, (float)_stopHeightSpin!.Value);
        if (plan.Rivers.Count == 0 && plan.Lakes.Count == 0)
        {
            GD.PushWarning("UnpleasantWater: nowhere to run from there: nothing lower, or it is already at the Stop At height.");
            return;
        }

        var undo = GetUndoRedo();
        undo.CreateAction("Water: Generate River");
        void Add(WaterNode node, Vector3 at)
        {
            undo.AddDoMethod(root, Node.MethodName.AddChild, node, true);
            undo.AddDoMethod(node, Node.MethodName.SetOwner, root);
            undo.AddDoProperty(node, Node3D.PropertyName.GlobalPosition, at);
            undo.AddDoReference(node);
            undo.AddUndoMethod(root, Node.MethodName.RemoveChild, node);
        }

        var lakes = new List<WaterBody>();
        foreach (var planned in plan.Lakes)
        {
            var middle = planned.Outline.Aggregate(Vector2.Zero, (sum, p) => sum + p) / planned.Outline.Length;
            var outline = new Curve3D { Closed = true };
            foreach (var point in planned.Outline)
                outline.AddPoint(new Vector3(point.X - middle.X, 0f, point.Y - middle.Y));
            var lake = new WaterBody { Name = "Lake", Type = WaterBody.BodyType.Lake, Curve = outline };
            Add(lake, new Vector3(middle.X, planned.Level, middle.Y));
            lakes.Add(lake);
        }

        WaterPath? first = null;
        // Widening over the whole river's length, its stretches one after another.
        var width = (float)_widenFromSpin!.Value;
        var endWidth = (float)_widenSpin!.Value;
        var wholeLength = plan.Rivers.Sum(r => r.Points.Zip(r.Points.Skip(1), (a, b) => a.DistanceTo(b)).Sum());
        var along = 0f;
        foreach (var planned in plan.Rivers)
        {
            var origin = planned.Points[0];
            var curve = new Curve3D();
            var widths = new Godot.Collections.Array<float>();
            for (var i = 0; i < planned.Points.Length; i++)
            {
                if (i > 0)
                    along += planned.Points[i].DistanceTo(planned.Points[i - 1]);
                curve.AddPoint(planned.Points[i] - origin);
                widths.Add(endWidth > 0f && wholeLength > 0f ? Mathf.Lerp(width, endWidth, along / wholeLength) : width);
            }

            var river = new WaterPath { Name = ((WaterPath.PathType)_typeMenu.Selected).ToString(), Type = (WaterPath.PathType)_typeMenu.Selected, Width = width, PointWidths = widths, Curve = curve };
            // Each point's speed by how steep the river is either side of it.
            var speeds = new Godot.Collections.Array<float>();
            for (var i = 0; i < planned.Points.Length; i++)
            {
                var (above, below) = (planned.Points[Mathf.Max(i - 1, 0)], planned.Points[Mathf.Min(i + 1, planned.Points.Length - 1)]);
                var steep = Mathf.Clamp((above.Y - below.Y) / Mathf.Max(above.DistanceTo(below), 0.01f), 0f, 1f);
                speeds.Add((float)_speedSpin!.Value * (1f + ((float)_slopeBoostSpin!.Value - 1f) * Mathf.Sqrt(steep)));
            }

            river.PointFlowSpeeds = speeds;
            Add(river, origin);
            if (planned.FromLake is { } from)
                undo.AddDoProperty(river, WaterPath.PropertyName.Source, lakes[from]);
            first ??= river;
        }

        undo.CommitAction();
        Select((WaterNode?)first ?? lakes[0]);
        GD.Print($"UnpleasantWater: generated {plan.Rivers.Count} river stretch(es) and {plan.Lakes.Count} lake(s). Erode Terrain is in the river's inspector.");
    }

    /// <summary>A point picked with the transform gizmo, outside drawing, is the toolbar's selected point too.</summary>
    private void FollowGizmoPick()
    {
        if (_points is null || _points.Picks == _picksSeen)
            return;
        _picksSeen = _points.Picks;
        if (_points.PickedPath != _path)
            return;
        _point = _points.PickedPoint;
        ShowInToolbar();
        UpdateOverlays();
    }

    private int _picksSeen;

    public override int _Forward3DGuiInput(Camera3D camera, InputEvent @event)
    {
        FollowGizmoPick();
        if (_generateButton?.ButtonPressed == true)
        {
            if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click)
                return (int)AfterGuiInput.Pass;
            _generateButton.ButtonPressed = false;
            if (Hit(camera, click.Position) is { } source)
                Generate(source);
            return (int)AfterGuiInput.Stop;
        }

        if (!Drawing)
            return (int)AfterGuiInput.Pass;

        switch (@event)
        {
            case InputEventMouseMotion motion when _dragging:
                DragTo(camera, motion.Position);
                return (int)AfterGuiInput.Stop;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                Press(camera, press.Position);
                return (int)AfterGuiInput.Stop;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                EndDrag();
                return (int)AfterGuiInput.Stop;

            case InputEventMouseButton { Pressed: true, AltPressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel when Path is { } path && _point >= 0:
                var current = _point < path.PointWidths.Count && path.PointWidths[_point] > 0f ? path.PointWidths[_point] : path.Width;
                SetPointWidth(Mathf.Max(current + (wheel.ButtonIndex == MouseButton.WheelUp ? WidthStep : -WidthStep), WidthStep));
                return (int)AfterGuiInput.Stop;

            // The right button still turns the camera: only a click that does not move finishes.
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } right:
                if (right.Pressed)
                    _rightPressedAt = right.Position;
                else if (_rightPressedAt is { } from && from.DistanceTo(right.Position) < StillClick)
                    Finish();
                return (int)AfterGuiInput.Pass;

            case InputEventKey { Keycode: Key.Escape, Pressed: true }:
                Finish();
                return (int)AfterGuiInput.Stop;

            case InputEventKey { Keycode: Key.Delete or Key.Backspace, Pressed: true } when Path is not null && _point >= 0:
                RemovePoint();
                return (int)AfterGuiInput.Stop;
        }

        return (int)AfterGuiInput.Pass;
    }

    private void Finish()
    {
        _finished = true;
        _point = -1;
        ShowInToolbar();
        UpdateOverlays();
    }

    private void Press(Camera3D camera, Vector2 mouse)
    {
        // A point of any path: picked, and dragged if the mouse moves.
        if (PointUnder(camera, mouse) is var (near, index))
        {
            if (near != _path)
                Select(near);
            _path = near;
            _point = index;
            _finished = false;
            BeginDrag();
            ShowInToolbar();
            UpdateOverlays();
            return;
        }

        if (Path is { } path && OnLine(camera, mouse, path) is { } insertAt)
        {
            InsertPoint(path, insertAt.Index, insertAt.At);
            BeginDrag();
            return;
        }

        if (Hit(camera, mouse) is not { } at)
            return;
        if (Path is { HasNewCurve: false } going && !_finished)
            Append(going, at);
        else
            Start(at, Path is { HasNewCurve: true } fresh ? fresh : null);
    }

    /// <summary>A path begun at a place, one step to undo: in a node still as new if there is one, or else a new one. Begun on water, it flows from that water.</summary>
    private void Start(Vector3 at, WaterPath? fresh)
    {
        var source = WaterAt(at, null);
        if (source is not null)
            at.Y = SurfaceOf(source, at);
        var curve = new Curve3D();
        curve.AddPoint(Vector3.Zero);
        var undo = GetUndoRedo();
        undo.CreateAction("Water: Start Path");
        var path = fresh;
        if (path is null)
        {
            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            path = new WaterPath { Name = ((WaterPath.PathType)_typeMenu.Selected).ToString(), Type = (WaterPath.PathType)_typeMenu.Selected, Width = (float)_widthSpin.Value, Curve = curve };
            undo.AddDoMethod(root, Node.MethodName.AddChild, path, true);
            undo.AddDoMethod(path, Node.MethodName.SetOwner, root);
            undo.AddDoReference(path);
            undo.AddUndoMethod(root, Node.MethodName.RemoveChild, path);
        }
        else
        {
            undo.AddDoProperty(path, Path3D.PropertyName.Curve, curve);
            undo.AddUndoProperty(path, Path3D.PropertyName.Curve, path.Curve);
            undo.AddUndoProperty(path, Node3D.PropertyName.GlobalPosition, path.GlobalPosition);
            undo.AddUndoProperty(path, WaterPath.PropertyName.Source, path.Source is null ? default(Variant) : path.Source);
        }

        undo.AddDoProperty(path, Node3D.PropertyName.GlobalPosition, at);
        if (source is not null)
            undo.AddDoProperty(path, WaterPath.PropertyName.Source, source);
        _path = path;
        _point = 0;
        _finished = false;
        undo.CommitAction();
        Select(path);
        ShowInToolbar();
        UpdateOverlays();
    }

    /// <summary>A point added at a path's end, never above the last; over a lake or sea no higher than it, it ends there.</summary>
    private void Append(WaterPath path, Vector3 at)
    {
        var curve = (Curve3D)path.Curve.Duplicate();
        var before = path.ToGlobal(curve.GetPointPosition(curve.PointCount - 1));
        at.Y = Mathf.Min(at.Y, before.Y);
        var ended = false;
        if (WaterAt(at, path) is WaterBody body && body != path.Source && body.GlobalPosition.Y <= before.Y)
        {
            at.Y = body.GlobalPosition.Y;
            ended = true;
        }

        curve.AddPoint(path.ToLocal(at));
        Commit("Water: Add Point", path, curve);
        _point = curve.PointCount - 1;
        if (ended)
            Finish();
        ShowInToolbar();
        UpdateOverlays();
    }

    private void InsertPoint(WaterPath path, int index, Vector3 local)
    {
        var curve = (Curve3D)path.Curve.Duplicate();
        curve.AddPoint(local, Vector3.Zero, Vector3.Zero, index);
        var undo = GetUndoRedo();
        undo.CreateAction("Water: Insert Point");
        AddCurveChange(undo, path, curve);
        AddListChange(undo, path, WaterPath.PropertyName.PointWidths, path.PointWidths, list => InsertBetween(list, index));
        AddListChange(undo, path, WaterPath.PropertyName.PointFlowSpeeds, path.PointFlowSpeeds, list => InsertBetween(list, index));
        undo.CommitAction();
        _point = index;
        ShowInToolbar();
        UpdateOverlays();
    }

    private void RemovePoint()
    {
        var path = Path!;
        // Fewer than two points is no path at all: undo starting it instead.
        if (path.Curve.PointCount <= 2 || _point >= path.Curve.PointCount)
            return;
        var index = _point;
        var curve = (Curve3D)path.Curve.Duplicate();
        curve.RemovePoint(index);
        var undo = GetUndoRedo();
        undo.CreateAction("Water: Remove Point");
        AddCurveChange(undo, path, curve);
        AddListChange(undo, path, WaterPath.PropertyName.PointWidths, path.PointWidths, list =>
        {
            if (index < list.Count)
                list.RemoveAt(index);
        });
        AddListChange(undo, path, WaterPath.PropertyName.PointFlowSpeeds, path.PointFlowSpeeds, list =>
        {
            if (index < list.Count)
                list.RemoveAt(index);
        });
        undo.CommitAction();
        _point = Mathf.Min(index, curve.PointCount - 1);
        ShowInToolbar();
        UpdateOverlays();
    }

    /// <summary>A value for a new point between two others: halfway, if both set one; or else none of its own.</summary>
    private static void InsertBetween(Godot.Collections.Array<float> list, int index)
    {
        if (index > list.Count)
            return;
        var value = index > 0 && index < list.Count && list[index - 1] > 0f && list[index] > 0f ? (list[index - 1] + list[index]) / 2f : 0f;
        list.Insert(index, value);
    }

    private static void AddListChange(EditorUndoRedoManager undo, WaterPath path, StringName property, Godot.Collections.Array<float> list, System.Action<Godot.Collections.Array<float>> change)
    {
        var changed = list.Duplicate();
        change(changed);
        undo.AddDoProperty(path, property, changed);
        undo.AddUndoProperty(path, property, list.Duplicate());
    }

    // A new curve each step, not the same one changed: that is also how the path tells this from Path3D's own tools adding a point.
    private static void AddCurveChange(EditorUndoRedoManager undo, WaterPath path, Curve3D curve, Curve3D? was = null)
    {
        undo.AddDoProperty(path, Path3D.PropertyName.Curve, curve);
        undo.AddUndoProperty(path, Path3D.PropertyName.Curve, was ?? path.Curve);
    }

    private void Commit(string name, WaterPath path, Curve3D curve)
    {
        var undo = GetUndoRedo();
        undo.CreateAction(name);
        AddCurveChange(undo, path, curve);
        undo.CommitAction();
    }

    private void BeginDrag()
    {
        _dragging = true;
        _pressedCurve = (Curve3D)Path!.Curve.Duplicate();
    }

    /// <summary>The point dragged goes where the mouse is, never above the one before, and those after it down below it where they need to, from where they were when picked.</summary>
    private void DragTo(Camera3D camera, Vector2 mouse)
    {
        if (Path is not { } path || _pressedCurve is not { } pressed || _point < 0 || _point >= path.Curve.PointCount || Hit(camera, mouse) is not { } at)
            return;
        if (_point > 0)
            at.Y = Mathf.Min(at.Y, path.ToGlobal(path.Curve.GetPointPosition(_point - 1)).Y);
        path.Curve.SetPointPosition(_point, path.ToLocal(at));
        var lowest = at.Y;
        for (var i = _point + 1; i < path.Curve.PointCount; i++)
        {
            var world = path.ToGlobal(pressed.GetPointPosition(i));
            world.Y = Mathf.Min(world.Y, lowest);
            lowest = world.Y;
            path.Curve.SetPointPosition(i, path.ToLocal(world));
        }

        UpdateOverlays();
    }

    private void EndDrag()
    {
        var wasDragging = _dragging;
        _dragging = false;
        if (!wasDragging || Path is not { } path || _pressedCurve is not { } pressed)
            return;
        _pressedCurve = null;
        var moved = Enumerable.Range(0, pressed.PointCount).Any(i => !pressed.GetPointPosition(i).IsEqualApprox(path.Curve.GetPointPosition(i)));
        if (!moved)
            return;
        var undo = GetUndoRedo();
        undo.CreateAction("Water: Move Point");
        AddCurveChange(undo, path, (Curve3D)path.Curve.Duplicate(), pressed);
        undo.CommitAction();
    }

    private static void Select(Node node)
    {
        var selection = EditorInterface.Singleton.GetSelection();
        selection.Clear();
        selection.AddNode(node);
    }

    /// <summary>The point of any path nearest the mouse, within reach; the selected path's first.</summary>
    private (WaterPath Path, int Index)? PointUnder(Camera3D camera, Vector2 mouse)
    {
        (WaterPath, int)? nearest = null;
        var best = PickRadius;
        foreach (var path in Water.InTree.OfType<WaterPath>().OrderBy(p => p == _path ? 0 : 1))
        {
            for (var i = 0; path.Curve is { } curve && i < curve.PointCount; i++)
            {
                var at = path.ToGlobal(curve.GetPointPosition(i));
                if (camera.IsPositionBehind(at))
                    continue;
                var distance = camera.UnprojectPosition(at).DistanceTo(mouse);
                if (distance < best)
                    (nearest, best) = ((path, i), distance);
            }
        }

        return nearest;
    }

    /// <summary>Where on a path's line the mouse is, within reach, in the path's space: and the index a point put there takes.</summary>
    private static (int Index, Vector3 At)? OnLine(Camera3D camera, Vector2 mouse, WaterPath path)
    {
        var curve = path.Curve;
        if (curve.PointCount < 2)
            return null;
        var baked = curve.GetBakedPoints();
        var best = PickRadius;
        Vector3? found = null;
        foreach (var local in baked)
        {
            var world = path.ToGlobal(local);
            if (camera.IsPositionBehind(world))
                continue;
            var distance = camera.UnprojectPosition(world).DistanceTo(mouse);
            if (distance < best)
                (found, best) = (local, distance);
        }

        if (found is not { } at)
            return null;
        var offset = curve.GetClosestOffset(at);
        var index = 1;
        while (index < curve.PointCount - 1 && curve.GetClosestOffset(curve.GetPointPosition(index)) < offset)
            index++;
        return (index, at);
    }

    /// <summary>The water covering a place, seen from above, other than a path's own; a lake or sea before another path.</summary>
    private static WaterNode? WaterAt(Vector3 at, WaterPath? except) =>
        Water.InTree.Where(n => n != except && n.Covers(at)).OrderBy(n => n is WaterBody ? 0 : 1).FirstOrDefault();

    private static float SurfaceOf(WaterNode water, Vector3 at) =>
        water is WaterBody ? water.GlobalPosition.Y : water.Sample(at)?.Height ?? at.Y;

    /// <summary>What the mouse is over: the nearer of the ground and a lake's or sea's surface; with neither, the height of the path's last point, or 0.</summary>
    private Vector3? Hit(Camera3D camera, Vector2 mouse)
    {
        var from = camera.ProjectRayOrigin(mouse);
        var way = camera.ProjectRayNormal(mouse);
        var nearest = float.PositiveInfinity;
        if (WaterGround.Finder?.Invoke(EditorInterface.Singleton.GetEditedSceneRoot()) is { } ground && GroundAlong(ground, from, way, camera.Far) is { } onGround)
            nearest = onGround;

        foreach (var body in Water.InTree.OfType<WaterBody>())
        {
            if (Mathf.Abs(way.Y) < 1e-5f)
                continue;
            var along = (body.GlobalPosition.Y - from.Y) / way.Y;
            if (along > 0f && along < nearest && body.Covers(from + way * along))
                nearest = along;
        }

        if (!float.IsInfinity(nearest))
            return from + way * nearest;
        if (Mathf.Abs(way.Y) < 1e-5f)
            return null;
        var height = Path is { Curve.PointCount: > 0 } path ? path.ToGlobal(path.Curve.GetPointPosition(path.Curve.PointCount - 1)).Y : 0f;
        var toPlane = (height - from.Y) / way.Y;
        return toPlane > 0f ? from + way * toPlane : null;
    }

    /// <summary>How far along a ray it meets the ground, stepping further the further off, then closing in.</summary>
    private static float? GroundAlong(IWaterGround ground, Vector3 from, Vector3 way, float far)
    {
        var last = 0f;
        var along = 0f;
        while (along < far)
        {
            var step = Mathf.Max(FirstMarch, along * MarchGrowth);
            along = Mathf.Min(along + step, far);
            var at = from + way * along;
            var height = ground.HeightAt(at);
            if (!float.IsNaN(height) && at.Y <= height)
            {
                for (var i = 0; i < RefineSteps; i++)
                {
                    var middle = (last + along) / 2f;
                    var there = from + way * middle;
                    var heightThere = ground.HeightAt(there);
                    if (!float.IsNaN(heightThere) && there.Y <= heightThere)
                        along = middle;
                    else
                        last = middle;
                }

                return along;
            }

            last = along;
        }

        return null;
    }

    public override void _Forward3DDrawOverViewport(Control overlay)
    {
        if (!Drawing || Path is not { } path || _point < 0 || _point >= path.Curve.PointCount)
            return;
        var camera = EditorInterface.Singleton.GetEditorViewport3D().GetCamera3D();
        var at = path.ToGlobal(path.Curve.GetPointPosition(_point));
        if (camera is not null && !camera.IsPositionBehind(at))
            overlay.DrawArc(camera.UnprojectPosition(at), PickRadius, 0f, Mathf.Tau, 24, Colors.White, 2f);
    }
}
#endif
