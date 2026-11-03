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

/// <summary>Finds the ground for a water node.</summary>
public static class WaterGround
{
    /// <summary>Set by the project: gives the ground for a water node, or null if there is none.</summary>
    public static Func<Node, IWaterGround?>? Finder { get; set; }
}
