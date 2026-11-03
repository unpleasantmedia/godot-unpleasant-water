using Godot;

namespace UnpleasantMedia.UnpleasantWater;

/// <summary>Floats its parent RigidBody3D on any water it is in, and lets the current carry it, by the corners of a box round it.</summary>
[GlobalClass]
[Icon("res://addons/unpleasant_water/Icons/Water.svg")]
public partial class WaterBuoyancy : Node
{
    /// <summary>The size of the box of points tested, round the body's middle.</summary>
    [Export(PropertyHint.None, "suffix:m")]
    public Vector3 Size { get; set; } = new(2f, 1f, 2f);

    /// <summary>How hard it floats when fully under, against its own weight: above 1 it floats, below 1 it sinks.</summary>
    [Export(PropertyHint.Range, "0,10,0.05,or_greater")]
    public float Float { get; set; } = 2f;

    /// <summary>How strongly the water slows it and carries it along, per second.</summary>
    [Export(PropertyHint.Range, "0,20,0.1,or_greater")]
    public float Drag { get; set; } = 1.5f;

    /// <summary>How strongly the water slows its turning, per second.</summary>
    [Export(PropertyHint.Range, "0,20,0.1,or_greater")]
    public float TurnDrag { get; set; } = 1.5f;

    public override void _PhysicsProcess(double delta)
    {
        if (GetParent() is not RigidBody3D body)
            return;
        var half = Size * 0.5f;
        Vector3[] corners =
        [
            new(-half.X, -half.Y, -half.Z), new(half.X, -half.Y, -half.Z),
            new(-half.X, -half.Y, half.Z), new(half.X, -half.Y, half.Z),
        ];
        var weight = body.Mass * body.GravityScale * ProjectSettings.GetSetting("physics/3d/default_gravity").AsSingle();
        var submerged = 0f;
        foreach (var corner in corners)
        {
            var at = body.GlobalTransform * corner;
            if (Water.At(at) is not { } water)
                continue;
            var under = Mathf.Clamp((water.Height - at.Y) / Mathf.Max(Size.Y, 0.01f), 0f, 1f);
            if (under <= 0f)
                continue;
            submerged += under / corners.Length;
            var offset = at - body.GlobalPosition;
            var share = body.Mass / corners.Length;
            var pointVelocity = body.LinearVelocity + body.AngularVelocity.Cross(offset);
            var force = Vector3.Up * weight * Float * under / corners.Length + (water.Flow - pointVelocity) * Drag * under * share;
            body.ApplyForce(force, offset);
        }

        if (submerged > 0f)
            body.ApplyTorque(-body.AngularVelocity * TurnDrag * submerged * body.Mass);
    }
}
