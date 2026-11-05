shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_always, diffuse_burley, specular_schlick_ggx;

// Vertex colours from the mesh: red is a quarter of how much faster it flows here, green its foam, blue how far it has taken on the look of the water it ends in, alpha fades to its edges.
// On a path, UV's y already allows for how fast it flows at each place, so one scroll speed serves it all.
// UV2 from a path with its own speeds: x that scroll speed, y 1 (0 goes by flow_speed instead).

group_uniforms colour;
uniform vec3 shallow_colour : source_color = vec3(0.2, 0.55, 0.55);
uniform vec3 deep_colour : source_color = vec3(0.03, 0.2, 0.3);
// How quickly it goes from shallow to deep, and from clear to opaque, per metre.
uniform float absorption : hint_range(0.0, 5.0, 0.01) = 0.5;
uniform float clearest : hint_range(0.0, 1.0, 0.01) = 0.35;
uniform float roughness_amount : hint_range(0.0, 1.0, 0.01) = 0.08;
// The look of the water a path ends in, set from that water.
uniform vec3 joining_shallow : source_color = vec3(0.2, 0.55, 0.55);
uniform vec3 joining_deep : source_color = vec3(0.03, 0.2, 0.3);
uniform float joining_absorption = 0.5;
uniform float joining_clearest = 0.35;
// Metres of water depth over which a body thins away at the shore round a river's mouth.
uniform float mouth_shore_fade = 1.5;

group_uniforms ripples;
uniform sampler2D ripple_normal : hint_normal, filter_linear_mipmap, repeat_enable;
uniform float ripple_strength : hint_range(0.0, 2.0, 0.01) = 0.6;
// How many metres one repeat of the ripples covers, for the large layer and the small one.
uniform float ripple_size = 12.0;
uniform float small_ripple_size = 3.5;

group_uniforms movement;
// True to run along the mesh's UVs (a path); false to drift with the wind in world space (a body).
uniform bool flowing = false;
// Metres a second: along the path if flowing, otherwise the ripples' drift with the wind.
uniform float flow_speed = 0.2;
uniform vec2 wind_direction = vec2(1.0, 0.4);

group_uniforms waves;
uniform float wave_height = 0.0;
uniform float wave_length = 20.0;
uniform float wave_speed = 1.0;

group_uniforms offshore;
// Waves that build up away from the shore, up to their full size this many metres out. They need a shore map: see WaterBody.
uniform float offshore_wave_height = 0.0;
uniform float offshore_wave_length = 110.0;
uniform float offshore_wave_speed = 0.5;
uniform float offshore_distance = 600.0;

group_uniforms surf;
// Breaking waves running in to the shore, and their foam.
uniform bool shore_waves = false;
uniform sampler2D shore_map : filter_linear, repeat_disable;
uniform vec4 shore_area = vec4(0.0, 0.0, 1.0, 1.0);
// How far out they start, how far apart they are and how fast they come in (metres, metres a second).
uniform float surf_distance = 90.0;
uniform float surf_spacing = 26.0;
uniform float surf_speed = 4.0;
uniform float surf_foam : hint_range(0.0, 2.0, 0.01) = 1.0;
// How close to the shore it comes, in metres.
uniform float surf_shore_distance = 0.0;
// How many metres its lines wander from following the shore exactly.
uniform float surf_deform = 18.0;
// How many metres beyond a river's width its surf stays clear of its mouth.
uniform float surf_river_clearance = 250.0;

group_uniforms underwater;
// The water seen from under its surface. With no alpha, its deep colour.
uniform vec4 underwater_colour : source_color = vec4(0.0, 0.0, 0.0, 0.0);
// How quickly it thickens with distance, per metre.
uniform float underwater_density = 0.06;

group_uniforms foam;
uniform sampler2D foam_texture : filter_linear_mipmap, repeat_enable;
uniform vec3 foam_colour : source_color = vec3(0.9, 0.95, 0.95);
uniform float foam_size = 5.0;
// How far from shore the foam reaches, in metres of depth.
uniform float shore_foam = 0.4;
// How much foam the steep parts of a path have.
uniform float steep_foam : hint_range(0.0, 2.0, 0.01) = 1.0;
group_uniforms;

// Where rivers joining this water hide it (red) and keep its surf away (green), in the world (x, z, width, depth), with a copy of the material made for each body that has any.
uniform bool masked = false;
uniform sampler2D water_mask : hint_default_black, filter_linear, repeat_disable;
uniform vec4 mask_area = vec4(0.0, 0.0, 1.0, 1.0);

uniform sampler2D depth_texture : hint_depth_texture, filter_nearest, repeat_disable;

varying vec3 world_position;

const float WAVE_ANGLES[5] = { 0.0, 0.55, -0.8, 1.35, -0.3 };
const float WAVE_SIZES[5] = { 1.0, 0.73, 0.51, 1.37, 0.31 };
const float WAVE_SPEEDS[5] = { 1.0, 1.13, 1.31, 0.71, 1.57 };
const float WAVE_HEIGHTS[5] = { 0.38, 0.24, 0.16, 0.14, 0.08 };
const float WAVE_WANDER[5] = { 1.0, -1.3, 1.7, 0.8, -2.1 };

// Several waves of unrelated sizes, speeds and directions, with their phases wandering over distance, so no two crests line up for long.
// Water.cs works out the same, for what stands on it: change them together.
float wave_set(vec2 at, float time, float size, float speed) {
	float wander = 0.9 * sin(dot(at, vec2(0.0071, 0.0113)) + time * 0.07) + 0.7 * sin(dot(at, vec2(-0.0127, 0.0059)) - time * 0.05);
	float base = atan(wind_direction.y, wind_direction.x);
	float sum = 0.0;
	for (int i = 0; i < 5; i++) {
		vec2 direction = vec2(cos(base + WAVE_ANGLES[i]), sin(base + WAVE_ANGLES[i]));
		float k = TAU / (size * WAVE_SIZES[i]);
		sum += WAVE_HEIGHTS[i] * sin(k * dot(direction, at) + speed * WAVE_SPEEDS[i] * time + wander * WAVE_WANDER[i] + float(i) * 1.7);
	}
	return sum;
}

// How far from the shore a place is, in metres; far off where the map says nothing.
float shore_distance(vec2 at) {
	vec2 place = (at - shore_area.xy) / shore_area.zw;
	if (!shore_waves || place.x < 0.0 || place.y < 0.0 || place.x > 1.0 || place.y > 1.0) {
		return 100000.0;
	}
	return textureLod(shore_map, place, 0.0).r;
}

float wave(vec2 at, float time, float offshore) {
	float near = wave_height > 0.0 && wave_length > 0.0 ? wave_height * wave_set(at, time, wave_length, wave_speed) : 0.0;
	float far = offshore > 0.0 && offshore_wave_height > 0.0 ? offshore * offshore_wave_height * wave_set(at, time, offshore_wave_length, offshore_wave_speed) : 0.0;
	return near + far;
}

void vertex() {
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	if (!flowing && (wave_height > 0.0 || offshore_wave_height > 0.0)) {
		float offshore = smoothstep(0.0, max(offshore_distance, 1.0), shore_distance(world_position.xz));
		// Flat round a river's mouth (where its surf is kept away): a wave standing over the river's surface would block it out.
		float settled = 1.0;
		if (masked) {
			vec2 place = (world_position.xz - mask_area.xy) / mask_area.zw;
			if (place.x >= 0.0 && place.y >= 0.0 && place.x <= 1.0 && place.y <= 1.0) {
				settled = 1.0 - textureLod(water_mask, place, 0.0).g;
			}
		}
		float lift = wave(world_position.xz, TIME, offshore) * settled;
		float dx = wave(world_position.xz + vec2(0.5, 0.0), TIME, offshore) * settled - lift;
		float dz = wave(world_position.xz + vec2(0.0, 0.5), TIME, offshore) * settled - lift;
		VERTEX.y += lift;
		NORMAL = normalize(vec3(-dx * 2.0, 1.0, -dz * 2.0));
	}
}

// Moves a texture in two overlapping cycles, each fading out before it jumps back, so speed can vary without stretching.
vec3 cycling_normal(vec2 at, vec2 direction, float speed, float size, float offset, float time) {
	float cycle = 2.0;
	float first = fract(time / cycle + offset);
	float second = fract(time / cycle + offset + 0.5);
	float mixing = abs(first * 2.0 - 1.0);
	vec3 a = texture(ripple_normal, (at - direction * speed * cycle * first) / size + offset).xyz;
	vec3 b = texture(ripple_normal, (at - direction * speed * cycle * second) / size + offset + 0.37).xyz;
	return mix(a, b, mixing);
}

float cycling_foam(vec2 at, vec2 direction, float speed, float time) {
	float cycle = 2.0;
	float first = fract(time / cycle);
	float second = fract(time / cycle + 0.5);
	float mixing = abs(first * 2.0 - 1.0);
	float a = texture(foam_texture, (at - direction * speed * cycle * first) / foam_size).r;
	float b = texture(foam_texture, (at - direction * speed * cycle * second) / foam_size + 0.37).r;
	return mix(a, b, mixing);
}

void fragment() {
	float shown = 1.0;
	float calm = 0.0;
	if (masked) {
		vec2 place = (world_position.xz - mask_area.xy) / mask_area.zw;
		if (place.x >= 0.0 && place.y >= 0.0 && place.x <= 1.0 && place.y <= 1.0) {
			vec2 from_rivers = texture(water_mask, place).rg;
			shown = 1.0 - from_rivers.r;
			calm = from_rivers.g;
		}
		// From below it is the nearer surface, so where it fades it would keep out the river above it, showing the sky. There it is one or the other.
		if (CAMERA_POSITION_WORLD.y < world_position.y) {
			if (shown < 0.5) {
				discard;
			}
			shown = 1.0;
		} else if (shown <= 0.01) {
			discard;
		}
	}

	vec2 at = flowing ? UV : world_position.xz;
	vec2 direction = flowing ? vec2(0.0, 1.0) : normalize(wind_direction);
	float speed = flowing ? (UV2.y > 0.5 ? UV2.x : flow_speed) : flow_speed * COLOR.r * 4.0;

	vec3 large;
	vec3 small;
	float pattern;
	if (flowing) {
		float travelled = speed * TIME;
		large = texture(ripple_normal, (at - vec2(0.0, travelled)) / ripple_size).xyz;
		small = texture(ripple_normal, (at - vec2(0.0, travelled)) / small_ripple_size + 0.21).xyz;
		// Layers of unrelated sizes, moving at different speeds, with a much larger one deciding where foam gathers: no repeat lines up.
		float fine = texture(foam_texture, (at - vec2(0.0, travelled)) / foam_size).r;
		float coarse = texture(foam_texture, (at - vec2(0.0, travelled * 0.83)) / (foam_size * 2.73) + vec2(0.37, 0.11)).r;
		float gather = texture(foam_texture, (at - vec2(0.0, travelled * 0.61)) / (foam_size * 7.19) + vec2(0.71, 0.53)).r;
		pattern = (fine * 0.55 + coarse * 0.45) * mix(0.75, 1.3, smoothstep(0.25, 0.75, gather));
	} else {
		large = cycling_normal(at, direction, speed, ripple_size, 0.0, TIME);
		// The small layer turned against the large one, and both stronger and weaker from place to place, so their repeats don't show as a grid.
		vec2 turned = mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8)) * at;
		small = cycling_normal(turned, mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8)) * direction, speed * 1.4, small_ripple_size, 0.21, TIME);
		pattern = cycling_foam(at, direction, speed, TIME);
	}

	NORMAL_MAP = normalize(vec3(large.xy + small.xy - 1.0, 1.0)) * 0.5 + 0.5;
	float patches = flowing ? 0.5 : texture(foam_texture, at / (ripple_size * 9.3) + vec2(TIME * 0.001, 0.37)).r;
	NORMAL_MAP_DEPTH = ripple_strength * mix(0.55, 1.45, patches);

	// How deep the water is along this view, from the depth of what is behind it.
	float scene = texture(depth_texture, SCREEN_UV).r;
	vec4 behind = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, scene, 1.0);
	float depth = max(VERTEX.z - behind.z / behind.w, 0.0);
	float deepness = 1.0 - exp(-depth * mix(absorption, joining_absorption, COLOR.b));

	float near_shore = shore_foam > 0.0 ? (1.0 - smoothstep(0.0, shore_foam, depth)) * (1.0 - calm) : 0.0;
	// Round a river's mouth it has no waterline to see: it thins away into the shallows.
	vec4 ground_below = INV_VIEW_MATRIX * vec4(behind.xyz / behind.w, 1.0);
	float thinned = mix(1.0, smoothstep(0.0, mouth_shore_fade, world_position.y - ground_below.y), calm);
	float foam = smoothstep(0.5, 0.75, pattern * (near_shore + COLOR.g * steep_foam));

	// Breaking waves: each is a line of foam at its crest, running in to the shore, trailing foam behind it. Some are stronger than others, and each breaks in stretches.
	float crest_shade = 0.0;
	if (shore_waves && surf_foam > 0.0) {
		float from_shore = shore_distance(world_position.xz);
		float start = surf_shore_distance + 6.0;
		float zone = smoothstep(surf_shore_distance, start, from_shore) * (1.0 - smoothstep(mix(start, surf_distance, 0.55), max(surf_distance, start + 1.0), from_shore)) * (1.0 - calm);
		if (zone > 0.0) {
			float wander = texture(foam_texture, world_position.xz / 211.0).r * surf_deform;
			float running = (from_shore + wander) / surf_spacing + TIME * surf_speed / surf_spacing;
			float which = floor(running);
			float place = fract(running);
			float strength = fract(sin(which * 12.9898) * 43758.5453);
			float stretches = smoothstep(0.35, 0.6, texture(foam_texture, world_position.xz / 97.0 + vec2(which * 0.37, which * 0.19)).r + strength * 0.25);
			float breaking = zone * stretches * mix(0.45, 1.0, strength);
			float trail = exp(-place * 7.0) * breaking;
			float broken = texture(foam_texture, world_position.xz / (foam_size * 0.61) + vec2(0.0, TIME * 0.02)).r;
			foam = max(foam, smoothstep(0.32, 0.62, trail * surf_foam * (0.45 + broken)));
			crest_shade = smoothstep(0.82, 1.0, place) * breaking;
		}
	}

	ALBEDO = mix(mix(mix(shallow_colour, joining_shallow, COLOR.b), mix(deep_colour, joining_deep, COLOR.b), deepness) * (1.0 - crest_shade * 0.3), foam_colour, foam);
	ROUGHNESS = mix(roughness_amount, 0.9, foam);
	SPECULAR = 0.5;
	ALPHA = mix(mix(mix(clearest, joining_clearest, COLOR.b), 1.0, deepness), 1.0, foam) * COLOR.a * shown * thinned;
}
