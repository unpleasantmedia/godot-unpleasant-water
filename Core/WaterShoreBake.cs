using Godot;

namespace UnpleasantMedia.UnpleasantWater;

/// <summary>A WaterBody's baked distances from the shore: made by its Bake Shore button.</summary>
[Tool]
[GlobalClass]
public partial class WaterShoreBake : Resource
{
    /// <summary>How far each place is from the shore, in metres, in the red channel of 32-bit floats.</summary>
    [Export]
    public Image? Map { get; set; }

    /// <summary>The part of the world it covers: x, z, width and depth.</summary>
    [Export]
    public Vector4 Area { get; set; }
}
