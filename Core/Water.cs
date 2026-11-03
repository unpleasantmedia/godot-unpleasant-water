using System.Collections.Generic;
using Godot;

namespace UnpleasantMedia.UnpleasantWater;

/// <summary>The water at a place: its surface's height, with waves, and its current in metres a second.</summary>
public readonly record struct WaterSample(float Height, Vector3 Flow, WaterNode Node);

/// <summary>Asks about the water in the scene.</summary>
public static class Water
{
    internal static readonly List<WaterNode> InTree = [];

    /// <summary>The water over or under a place, or null if there is none. With several, the surface nearest in height.</summary>
    public static WaterSample? At(Vector3 at)
    {
        WaterSample? best = null;
        foreach (var node in InTree)
        {
            if (node.Sample(at) is { } sample && (best is null || Mathf.Abs(sample.Height - at.Y) < Mathf.Abs(best.Value.Height - at.Y)))
                best = sample;
        }

        return best;
    }

    /// <summary>The water around a place under its surface, or null if it is not in any: above it, or in the air or the ground below it.</summary>
    public static WaterSample? Around(Vector3 at)
    {
        if (At(at) is not { } water || at.Y >= water.Height)
            return null;
        var ground = WaterGround.Finder?.Invoke(water.Node)?.HeightAt(at);
        return ground is { } height && !float.IsNaN(height) && height > at.Y ? null : water;
    }

    private static readonly float[] WaveAngles = [0f, 0.55f, -0.8f, 1.35f, -0.3f];
    private static readonly float[] WaveSizes = [1f, 0.73f, 0.51f, 1.37f, 0.31f];
    private static readonly float[] WaveSpeeds = [1f, 1.13f, 1.31f, 0.71f, 1.57f];
    private static readonly float[] WaveHeights = [0.38f, 0.24f, 0.16f, 0.14f, 0.08f];
    private static readonly float[] WaveWander = [1f, -1.3f, 1.7f, 0.8f, -2.1f];

    /// <summary>
    /// The height a material's waves add at a place, this far from fully offshore (0 to 1), as its
    /// shader works it out (change them together); the shader's clock is only followed closely.
    /// </summary>
    internal static float WaveHeight(ShaderMaterial? material, Vector2 at, float time, float offshore)
    {
        if (material is null)
            return 0f;
        float Of(string name) => material.GetShaderParameter(name).AsSingle();
        var wind = material.GetShaderParameter("wind_direction").AsVector2();
        var near = Of("wave_height") > 0f && Of("wave_length") > 0f ? Of("wave_height") * WaveSet(wind, at, time, Of("wave_length"), Of("wave_speed")) : 0f;
        var far = offshore > 0f && Of("offshore_wave_height") > 0f
            ? offshore * Of("offshore_wave_height") * WaveSet(wind, at, time, Of("offshore_wave_length"), Of("offshore_wave_speed")) : 0f;
        return near + far;
    }

    private static float WaveSet(Vector2 wind, Vector2 at, float time, float size, float speed)
    {
        var wander = 0.9f * Mathf.Sin(at.Dot(new Vector2(0.0071f, 0.0113f)) + time * 0.07f) + 0.7f * Mathf.Sin(at.Dot(new Vector2(-0.0127f, 0.0059f)) - time * 0.05f);
        var angle = Mathf.Atan2(wind.Y, wind.X);
        var sum = 0f;
        for (var i = 0; i < WaveAngles.Length; i++)
        {
            var direction = new Vector2(Mathf.Cos(angle + WaveAngles[i]), Mathf.Sin(angle + WaveAngles[i]));
            var k = Mathf.Tau / (size * WaveSizes[i]);
            sum += WaveHeights[i] * Mathf.Sin(k * direction.Dot(at) + speed * WaveSpeeds[i] * time + wander * WaveWander[i] + i * 1.7f);
        }

        return sum;
    }
}
