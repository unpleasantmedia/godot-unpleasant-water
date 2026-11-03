#if TOOLS
using Godot;

namespace UnpleasantMedia.UnpleasantWater.Editor;

/// <summary>
/// The plugin itself. WaterBody and WaterPath are global classes, edited with the 3D view's Path3D
/// tools and their inspector buttons; this adds selecting a WaterPath's points.
/// </summary>
[Tool]
public partial class WaterPlugin : EditorPlugin
{
    private WaterPointGizmos? _points;

    public override void _EnterTree()
    {
        _points = new WaterPointGizmos();
        AddNode3DGizmoPlugin(_points);
    }

    public override void _ExitTree()
    {
        if (_points is not null)
            RemoveNode3DGizmoPlugin(_points);
        _points = null;
    }
}
#endif
