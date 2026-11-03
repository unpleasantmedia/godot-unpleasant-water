using Godot;

namespace UnpleasantMedia.UnpleasantWater;

/// <summary>Shows the water's volume over the screen while the camera is under its surface: the 3D view's in the editor, the game's otherwise.</summary>
[Tool]
public partial class WaterUnderwater : MeshInstance3D
{
    private static readonly Color DefaultDeep = new(0.03f, 0.2f, 0.3f);
    private const float DefaultDensity = 0.06f;

    public override void _Process(double delta)
    {
        var camera = GetViewport()?.GetCamera3D();
#if TOOLS
        if (Engine.IsEditorHint())
            camera = EditorInterface.Singleton.GetEditorViewport3D(0)?.GetCamera3D();
#endif
        if (camera is null || Water.Around(camera.GlobalPosition) is not { } water)
        {
            Visible = false;
            return;
        }

        Visible = true;
        if (Mesh?.SurfaceGetMaterial(0) is ShaderMaterial look)
        {
            var material = water.Node.Material;
            var colour = material?.GetShaderParameter("underwater_colour") is { VariantType: Variant.Type.Color } own ? own.AsColor() : Colors.Transparent;
            if (colour.A <= 0f)
                colour = material?.GetShaderParameter("deep_colour") is { VariantType: Variant.Type.Color } deep ? deep.AsColor() : DefaultDeep;
            var density = material?.GetShaderParameter("underwater_density") is { VariantType: not Variant.Type.Nil } set ? set.AsSingle() : DefaultDensity;
            look.SetShaderParameter("volume_colour", colour);
            look.SetShaderParameter("density", density);
            look.SetShaderParameter("surface_height", water.Height);
        }
    }
}
