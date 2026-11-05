using System;
using System.Collections.Generic;
using Godot;

namespace UnpleasantMedia.UnpleasantWater;

/// <summary>
/// The ground water is fitted to and carved into, on a grid of samples in world space. The plugin
/// knows no terrain itself: a project connects its own through <see cref="WaterGround.Finder"/>.
/// </summary>
public interface IWaterGround
{
    /// <summary>How many metres apart its samples are.</summary>
    float VertexSpacing { get; }

    /// <summary>How high the ground is at a place, or NaN where there is none.</summary>
    float HeightAt(Vector3 at);

    /// <summary>A sample's height, or null outside the ground.</summary>
    float? SampleHeight(int x, int z);

    /// <summary>Sets samples' heights, and redraws the ground.</summary>
    void SetHeights(IEnumerable<(int X, int Z, float Height)> heights);
}

/// <summary>A ground that can also be painted with textures, for riverbeds. What a sample's paint means is its own: water only reads it, and asks for it changed.</summary>
public interface IWaterGroundPaint
{
    /// <summary>Its textures' names, by index.</summary>
    IReadOnlyList<string> TextureNames { get; }

    /// <summary>A sample's paint, or null outside the ground.</summary>
    int? SamplePaint(int x, int z);

    /// <summary>A sample's paint with these two textures, blended this far towards the second, showing as painted.</summary>
    int Painted(int paint, int first, int second, float blend);

    /// <summary>A sample's paint with a texture blended in this far (0 to 1) over what shows there now, for the edge of painting.</summary>
    int PaintedOver(int paint, int texture, float amount);

    /// <summary>Sets samples' paint, and redraws the ground.</summary>
    void SetPaint(IEnumerable<(int X, int Z, int Paint)> paint);
}

/// <summary>Finds the ground for a water node.</summary>
public static class WaterGround
{
    /// <summary>Set by the project: gives the ground for a water node, or null if there is none.</summary>
    public static Func<Node, IWaterGround?>? Finder { get; set; }
}
