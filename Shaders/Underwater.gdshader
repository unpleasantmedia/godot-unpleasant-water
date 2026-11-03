shader_type spatial;
render_mode unshaded, blend_mix, depth_test_disabled, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;

// The water around a camera that is under it, over the whole screen: thicker the further off what is seen.

uniform vec3 volume_colour : source_color = vec3(0.03, 0.2, 0.3);
uniform float density = 0.06;
uniform float surface_height = 0.0;

uniform sampler2D depth_texture : hint_depth_texture, filter_nearest, repeat_disable;

void vertex() {
	POSITION = vec4(VERTEX.xy * 2.0, 1.0, 1.0);
}

void fragment() {
	float scene = texture(depth_texture, SCREEN_UV).r;
	vec4 behind = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, scene, 1.0);
	vec3 view = abs(behind.w) > 0.00001 ? behind.xyz / behind.w : vec3(0.0, 0.0, -100000.0);
	float travelled = length(view);
	// Looking up, the water ends at its surface, which is not in the depth texture, being see-through.
	vec3 direction = (INV_VIEW_MATRIX * vec4(normalize(view), 0.0)).xyz;
	if (direction.y > 0.001) {
		travelled = min(travelled, max(surface_height - INV_VIEW_MATRIX[3].y, 0.0) / direction.y);
	}

	ALBEDO = volume_colour;
	ALPHA = 1.0 - exp(-travelled * density);
}
