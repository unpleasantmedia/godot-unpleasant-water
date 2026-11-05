# UnpleasantWater

A water plugin for Godot 4.7 (.NET version).

- `WaterBody`: still water inside an outline, for a **Sea**, **Lake** or **Pond**
- `WaterPath`: water that flows along a curve, for a **River**, **Creek** or **Waterfall**
- A material per type, in `Materials/`
- Height and current queries for code
- `WaterBuoyancy`: floats a `RigidBody3D` and lets the current carry it
- Terrain carving, through a small interface to any terrain

## Installing

Copy the plugin to `addons/unpleasant_water` and enable it under **Project > Project Settings > Plugins**.

## Placing water

Add a `WaterBody` or `WaterPath` and pick its **Type**. Edit its curve with the 3D view's Path3D tools.

**WaterBody**: the curve is its outline, and its surface is at the node's height. **Cell Size** sets how closely its mesh follows waves and the outline. A material with **Shore Waves** on (Sea, by default) bakes how far each place is from the shore to a file in `res://Terrain` the first time it opens in the editor, for waves that grow away from land (**Offshore Wave Height**, **Length**, **Speed** and **Distance**) and break on it (**Surf Distance**, **Shore Distance**, **Spacing**, **Speed**, **Foam** and **Deform**), staying **River Clearance** metres clear of the mouths of rivers that run into it. Press **Bake Shore** after changing the ground or the outline, and save the scene so it keeps the link to the file.

**WaterPath**: water runs from the curve's first point to its last, and runs faster and foams more where the curve is steep. For a waterfall, make the curve drop steeply. **Width** sets its width, and **Point Widths** can set one per point. **Flow Speed** sets its speed in m/s (0 uses its material's), and **Point Flow Speeds** can override that per point, with 0 meaning no override. Set **Source** to a water node to make it flow from that water: its first point stays joined to it, at its height, and it stays at that height until it has cleared it, then eases down to its curve. A path that ends over a `WaterBody` within 5 m of its height joins it in the same way. At either end it runs on into that water and fades out over **Join Blend**, and a body it runs into is hidden under it, coming back just before its last point. Where a steep drop levels out, a spray rises; **Splashes** turns it off, **Splash Amount** sets how much, and **Splash Min Steepness** how steep a drop must be to splash. `Splash.tscn` holds the look of every splash; **Customize Splash** copies it onto one path to edit there, as **Splash Motion** (shape, direction, speed, gravity, scale and colour), **Splash Mesh** and **Splash Lifetime**. Clicking a point with the Path3D tools on Select selects it, to move with the transform gizmo. **Fit Points To Ground** puts the points on the ground, never above the point before.

To draw a `WaterPath` in the 3D view, select any water node and press **Draw** in the toolbar. Click to add points: each lands on the ground, never above the point before. Drag a point to move it, and click the line to insert one. **Delete** removes the selected point, **Alt+wheel** changes its width, and a right-click or **Esc** finishes the path. Starting on water sets it as **Source**, and clicking on a lake or sea no higher than the last point ends the path there.

To generate a river, press **Generate** and click a source on the ground. It runs down the steepest way until it reaches other water, the ground's edge, or the toolbar's **Stop at** height. A hollow with no way out becomes a `WaterBody` lake, filled to its lowest way out, and a new `WaterPath` flows on from there; a small hollow is just run through. It starts **Widen from** wide and widens gradually, point by point, to **Widen to** at its end (0 keeps it at **Widen from**). Each point gets a **Point Flow Speed** by how steep the river is there: **Speed** on the flat, up to **Slope x** times that on a straight drop. A point with its own speed flows at exactly that, without the usual speed-up on slopes. It's one step to undo.

**Erode Terrain**, in a `WaterPath`'s inspector, wears its bed into the ground over **Erosion Passes**, each going **Erosion Depth** deeper in the middle, unevenly by **Erosion Noise**. The bed rises to the surface at the water's edge, so the water meets its banks. Beyond that, ground above the water slopes up from it over **Bank** plus **Erosion Widen** per pass, in full at the river's widest and in proportion where it is narrower. With **Paint Riverbed** on, it also paints the ground under the water, and **Riverbed Margin** past its edge, with the textures ticked in **Riverbed Textures**, mixing them unevenly if there are several; the ground needs to implement `IWaterGroundPaint`. It also lowers the river's points by **Erosion Water Drop**, each time, so the water lies below its banks; less towards where it joins other water, whose height it keeps there. It also erodes the rivers that flow on from a lake it ends in, and it's one step to undo.

To change a type's look, edit its file in `Materials/`. To change one node's, give it a **Custom Material**.

## Carving terrain

**Carve Terrain** digs **Depth** under the water and blends the ground back over **Bank**. It only ever lowers the ground, and it's one step to undo. Connect your terrain once, for example from a module initializer:

```csharp
WaterGround.Finder = node => MyGroundAdapterFor(node); // returns an IWaterGround
```

## Using it from code

```csharp
if (Water.At(position) is { } water)
    GD.Print(water.Height, water.Flow); // surface height with waves, current in m/s
```

The waves in `Height` follow the shader's closely, but not exactly, as they run on separate clocks.

## Buoyancy

Add a `WaterBuoyancy` as a child of a `RigidBody3D`, and set **Size** to the body's. **Float** is its lift against its weight when fully under, so above 1 it floats.

## Underwater

While the camera is under a surface, the screen fills with that water's volume, thicker the further off. A material's **Underwater Colour** sets it, using its deep colour when that colour's alpha is 0, and **Underwater Density** sets how quickly it thickens.

## Limits

- It's drawn with alpha and doesn't refract what's behind it.
