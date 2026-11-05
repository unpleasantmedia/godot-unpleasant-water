#if TOOLS
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantWater.Editor;

/// <summary>
/// Makes a WaterPath's points selectable in the 3D view, each moved with the usual transform gizmo:
/// Path3D's own tools can only drag them.
/// </summary>
[Tool]
public partial class WaterPointGizmos : EditorNode3DGizmoPlugin
{
    public const string PluginName = "WaterPathPoints";

    private const float ClickReach = 14f;

    public override string _GetGizmoName() => PluginName;

    public override bool _HasGizmo(Node3D forNode3D) => forNode3D is WaterPath;

    // Nothing to draw: Path3D already shows the curve and its points.
    public override void _Redraw(EditorNode3DGizmo gizmo) => gizmo.Clear();

    private static Curve3D? CurveOf(EditorNode3DGizmo gizmo) => (gizmo.GetNode3D() as WaterPath)?.Curve;

    public override int _SubgizmosIntersectRay(EditorNode3DGizmo gizmo, Camera3D camera, Vector2 screenPos)
    {
        if (gizmo.GetNode3D() is not WaterPath { Curve: { } curve } path)
            return -1;
        var nearest = -1;
        var best = ClickReach;
        for (var i = 0; i < curve.PointCount; i++)
        {
            var at = path.ToGlobal(curve.GetPointPosition(i));
            if (camera.IsPositionBehind(at))
                continue;
            var distance = camera.UnprojectPosition(at).DistanceTo(screenPos);
            if (distance < best)
                (nearest, best) = (i, distance);
        }

        if (nearest >= 0)
        {
            PickedPath = path;
            PickedPoint = nearest;
            Picks++;
        }

        return nearest;
    }

    /// <summary>The point last clicked, and how many have been, for the plugin's toolbar to follow.</summary>
    public WaterPath? PickedPath { get; private set; }
    public int PickedPoint { get; private set; }
    public int Picks { get; private set; }

    public override int[] _SubgizmosIntersectFrustum(EditorNode3DGizmo gizmo, Camera3D camera, Godot.Collections.Array<Plane> frustum)
    {
        if (gizmo.GetNode3D() is not WaterPath { Curve: { } curve } path)
            return [];
        return [.. Enumerable.Range(0, curve.PointCount).Where(i => frustum.All(p => !p.IsPointOver(path.ToGlobal(curve.GetPointPosition(i)))))];
    }

    public override Transform3D _GetSubgizmoTransform(EditorNode3DGizmo gizmo, int subgizmoId) =>
        new(Basis.Identity, CurveOf(gizmo)?.GetPointPosition(subgizmoId) ?? Vector3.Zero);

    public override void _SetSubgizmoTransform(EditorNode3DGizmo gizmo, int subgizmoId, Transform3D transform) =>
        CurveOf(gizmo)?.SetPointPosition(subgizmoId, transform.Origin);

    public override void _CommitSubgizmos(EditorNode3DGizmo gizmo, int[] ids, Godot.Collections.Array<Transform3D> restores, bool cancel)
    {
        if (CurveOf(gizmo) is not { } curve)
            return;
        if (cancel)
        {
            for (var i = 0; i < ids.Length; i++)
                curve.SetPointPosition(ids[i], restores[i].Origin);
            return;
        }

        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        undo.CreateAction("Water: Move Points");
        for (var i = 0; i < ids.Length; i++)
        {
            undo.AddDoMethod(curve, Curve3D.MethodName.SetPointPosition, ids[i], curve.GetPointPosition(ids[i]));
            undo.AddUndoMethod(curve, Curve3D.MethodName.SetPointPosition, ids[i], restores[i].Origin);
        }

        undo.CommitAction(false);
    }
}
#endif
