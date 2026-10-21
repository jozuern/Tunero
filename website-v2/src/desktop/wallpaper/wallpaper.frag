#version 300 es
// The demo PC's wallpaper: twisting ribbons of light over a deep base. uClutter (0 = everything fixed, 1 = neglected)
// makes it restless: faster (on the CPU side), wobbly, grainy and muddy. It always runs smoothly: a site about a faster
// PC must never look like it lags. Brightness never pulses over the whole screen: every clutter effect is zero-mean.
precision highp float;

uniform vec2 uRes;      // drawing buffer size, pixels
uniform float uPhase;   // animation time in seconds; the CPU speeds it up with clutter
uniform float uClutter; // eased, 0 to 1
uniform float uTheme;   // 0 dark, 1 light; eased so a theme switch cross-fades
uniform float uSeed;    // grain seed, a new one per frame
uniform vec4 uRipples[4]; // x, y (0 to 1 from the top left), age in seconds, strength (0 = none)
uniform float uBloom;   // seconds since the PC was fully fixed, or -1
uniform float uSplit;   // before/after: the divider as a fraction of the width, or -1

out vec4 outColor;

// Palette, set once per pixel in main() from the theme.
vec3 gBgTop;
vec3 gBgBottom;
vec3 gGlowA;
vec3 gGlowB;
vec3 gSpec;
vec3 gShade;       // what the unlit side of a ribbon is multiplied by
vec3 gShadowColor; // what a cast shadow multiplies the colour behind by
float gGlow;

// A fast integer hash (Hugo Elias style), good enough for grain.
uint hashUint(uint value) {
  value = (value << 13u) ^ value;
  return value * (value * value * 15731u + 789221u) + 1376312589u;
}

float hash2(vec2 cell, float salt) {
  uvec2 bits = uvec2(ivec2(cell) + 32768);
  uint mixed = hashUint(bits.x + hashUint(bits.y + hashUint(uint(salt * 97.0) + 7u)));
  return float(mixed & 0x00ffffffu) / 16777215.0;
}

float valueNoise(vec2 point, float salt) {
  vec2 cell = floor(point);
  vec2 frac = point - cell;
  vec2 blend = frac * frac * (3.0 - 2.0 * frac);
  float a = hash2(cell, salt);
  float b = hash2(cell + vec2(1.0, 0.0), salt);
  float c = hash2(cell + vec2(0.0, 1.0), salt);
  float d = hash2(cell + vec2(1.0, 1.0), salt);
  return mix(mix(a, b, blend.x), mix(c, d, blend.x), blend.y);
}

// One ribbon of light: a band around a slowly waving centre line that twists about its own axis. The twist sets the
// projected width and turns the surface towards or away from the light, so highlights slide along it like on silk.
void ribbon(vec2 point, float time, float clutter,
            float baseY, float amp, float freq, float speed, float phase,
            float width, float twistFreq, float twistSpeed, float soft, float pixel,
            vec3 front, vec3 back, float opacity, float glow, float emissive, inout vec3 color) {
  float a1 = point.x * freq + time * speed + phase;
  float a2 = point.x * freq * 2.13 - time * speed * 1.37 + phase * 1.7;
  float centre = baseY + amp * sin(a1) + amp * 0.42 * sin(a2);
  float slope = amp * freq * (cos(a1) + 0.894 * cos(a2));
  // Restless: a fast, small wobble of the centre line.
  centre += clutter * amp * 0.22 * sin(point.x * 13.0 + time * 4.1 + phase * 3.0);

  float across = (point.y - centre) * inversesqrt(1.0 + slope * slope);
  float twist = point.x * twistFreq + time * twistSpeed + phase * 2.3;
  float facing = cos(twist);
  float halfWidth = width * (0.16 + 0.84 * abs(facing));
  float v = across / halfWidth;

  // The shadow it casts on what lies behind (down and slightly wide), then its glow.
  float shadowV = (across + width * 0.45) / (halfWidth * 1.35);
  color *= mix(vec3(1.0), gShadowColor, opacity * (1.0 - smoothstep(0.35, 1.5, abs(shadowV))));
  color += front * gGlow * glow * exp(-abs(across) / (width * 2.2));

  float edge = (soft + clutter * 0.018 + pixel * 1.2) / halfWidth;
  float mask = 1.0 - smoothstep(1.0 - edge, 1.0 + edge, abs(v));
  if (mask <= 0.0) return;

  // Surface normal: the twist plus a gentle curl across the width.
  float angle = twist + 0.95 * v;
  vec3 normal = normalize(vec3(-slope * 0.25, -sin(angle), cos(angle)));
  float frontSide = smoothstep(-0.25, 0.25, normal.z);
  normal.z = abs(normal.z);
  const vec3 light = vec3(-0.3363, 0.6726, 0.6587);
  const vec3 halfway = vec3(-0.1867, 0.3734, 0.9087);
  float diffuse = mix(max(dot(normal, light), 0.0), 1.0, emissive);
  float nh = max(dot(normal, halfway), 0.0);
  float nh2 = nh * nh;
  float nh8 = nh2 * nh2 * nh2 * nh2;
  float nh64 = nh8 * nh8 * nh8 * nh8 * nh8 * nh8 * nh8 * nh8;
  float spec = (nh64 * 0.95 + nh8 * 0.16) * (1.0 - 0.6 * clutter);
  float rim = smoothstep(0.72, 1.0, abs(v)) * 0.22;

  vec3 base = mix(back, front, frontSide);
  base *= 0.86 + 0.28 * (0.5 + 0.5 * sin(point.x * 1.3 + phase + time * 0.07));
  vec3 lit = base * mix(gShade, vec3(1.0), diffuse) + gSpec * (spec + rim);
  color = mix(color, lit, mask * opacity);
}

vec3 background(vec2 point) {
  float t = clamp(0.5 - point.y, 0.0, 1.0);
  vec3 color = mix(gBgTop, gBgBottom, t);
  color += gGlowA * exp(-2.2 * length(point - vec2(0.38, 0.06)));
  color += gGlowB * exp(-2.6 * length(point - vec2(-0.75, -0.42)));
  return color;
}

// The whole picture at one point. flow is the slow domain warp, shared by the chromatic aberration samples.
vec3 scene(vec2 point, vec2 flow, float time, float clutter, float pixel) {
  vec3 color = background(point);
  float light = uTheme;

  vec2 p0 = point + flow * 0.6;
  ribbon(p0, time, clutter, 0.20, 0.15, 1.5, 0.13, 0.0, 0.21, 1.1, 0.05, 0.06, pixel,
         mix(vec3(0.20, 0.13, 0.52), vec3(0.76, 0.72, 0.97), light),
         mix(vec3(0.10, 0.08, 0.30), vec3(0.88, 0.86, 1.00), light), 0.72, 1.0, 0.0, color);
  vec2 p1 = point + flow * 0.8;
  ribbon(p1, time, clutter, 0.03, 0.13, 2.0, -0.17, 2.1, 0.11, 1.9, -0.08, 0.022, pixel,
         mix(vec3(0.02, 0.42, 0.52), vec3(0.55, 0.82, 0.92), light),
         mix(vec3(0.02, 0.18, 0.30), vec3(0.82, 0.93, 0.98), light), 0.85, 1.0, 0.0, color);
  vec2 p2 = point + flow;
  ribbon(p2, time, clutter, -0.07, 0.15, 1.8, 0.21, 4.0, 0.14, 2.4, 0.11, 0.004, pixel,
         mix(vec3(0.05, 0.30, 0.86), vec3(0.34, 0.56, 0.98), light),
         mix(vec3(0.18, 0.52, 1.00), vec3(0.72, 0.83, 1.00), light), 0.95, 1.0, 0.0, color);
  vec2 p3 = point + flow * 1.15;
  // A thin thread of light braided around the main ribbon.
  ribbon(p3, time, clutter, -0.09, 0.12, 1.8, 0.21, 1.2, 0.02, 3.4, 0.15, 0.002, pixel,
         mix(vec3(0.50, 0.86, 1.00), vec3(1.00, 1.00, 1.00), light),
         mix(vec3(0.82, 0.94, 1.00), vec3(0.86, 0.91, 1.00), light), 0.9, 3.0, 0.65, color);
  return color;
}

void main() {
  float clutter = clamp(uClutter, 0.0, 1.0);
  vec2 uvTop = gl_FragCoord.xy / uRes;
  // Before/after: the neglected PC left of the divider, the fixed one right of it.
  if (uSplit >= 0.0) clutter = 1.0 - smoothstep(uSplit - 0.002, uSplit + 0.002, uvTop.x);
  // Ripples where something was fixed: a ripple like a drop in glass, which bends the picture as it passes, with a thin
  // rim of light, and the picture inside it clean for a moment. It slows down as it grows and fades out by 1.8 s
  // (rippleLife in wallpaper.ts); its size follows the strength.
  vec2 here = (gl_FragCoord.xy - 0.5 * uRes) / uRes.y;
  float ring = 0.0;
  float clean = 0.0;
  vec2 bendBy = vec2(0.0);
  for (int i = 0; i < 4; i++) {
    vec4 r = uRipples[i];
    if (r.w <= 0.0) continue;
    vec2 centre = vec2((r.x - 0.5) * uRes.x / uRes.y, 0.5 - r.y);
    vec2 away = here - centre;
    float d = length(away);
    float grow = 1.0 - exp(-r.z * 2.6);
    float radius = (0.06 + 0.28 * r.w) * grow;
    float width = 0.006 + 0.018 * grow;
    float fade = smoothstep(0.0, 0.06, r.z) * (1.0 - smoothstep(0.5, 1.8, r.z));
    float offset = (d - radius) / width;
    float profile = exp(-offset * offset);
    ring += r.w * fade * profile;
    // The derivative of the profile: pushes the picture outward on the rim's outer side and inward on its inner side.
    bendBy += (d > 0.0001 ? away / d : vec2(0.0)) * (-2.0 * offset * profile) * 0.01 * r.w * fade;
    clean = max(clean, r.w * smoothstep(radius, radius - 0.12, d) * (1.0 - smoothstep(0.2, 1.2, r.z)));
  }
  clutter *= 1.0 - 0.45 * clamp(clean, 0.0, 1.0);
  float light = clamp(uTheme, 0.0, 1.0);
  gBgTop = mix(vec3(0.010, 0.016, 0.042), vec3(0.968, 0.975, 0.996), light);
  gBgBottom = mix(vec3(0.014, 0.026, 0.070), vec3(0.885, 0.915, 0.985), light);
  gGlowA = mix(vec3(0.05, 0.11, 0.30), vec3(0.02, 0.03, 0.01), light);
  gGlowB = mix(vec3(0.09, 0.04, 0.20), vec3(-0.04, -0.05, 0.0), light);
  gSpec = mix(vec3(0.72, 0.88, 1.00), vec3(1.00, 1.00, 1.00), light);
  gShade = mix(vec3(0.30, 0.32, 0.42), vec3(0.72, 0.76, 0.98), light);
  gShadowColor = mix(vec3(0.62, 0.62, 0.68), vec3(0.82, 0.86, 0.98), light);
  gGlow = mix(0.10, 0.03, light);

  vec2 frag = gl_FragCoord.xy;
  vec2 uv = frag / uRes;
  float pixel = 1.0 / uRes.y;

  vec2 point = (frag - 0.5 * uRes) / uRes.y + bendBy;
  vec2 screen = point;
  // The whole composition leans: ribbons rise to the right.
  const float lean = 0.33;
  point = mat2(cos(lean), sin(lean), -sin(lean), cos(lean)) * point;

  float time = uPhase;
  vec2 flow = vec2(sin(point.y * 1.9 + time * 0.07 + 1.3 * sin(point.x * 1.2 - time * 0.05)),
                   sin(point.x * 1.5 - time * 0.06 + 1.1 * sin(point.y * 2.3 + time * 0.04))) * 0.045;
  // Restless: a fine, fast warp on top of the slow one.
  if (clutter > 0.001) {
    vec2 grit = vec2(valueNoise(point * 9.0 + time * 1.7, 11.0), valueNoise(point * 9.0 - time * 1.3, 12.0)) - 0.5;
    flow += grit * 0.035 * clutter;
  }

  vec3 color = scene(point, flow, time, clutter, pixel);

  // Neglect drains the colour into a muddy grey brown and flattens it.
  float luma = dot(color, vec3(0.299, 0.587, 0.114));
  vec3 mud = luma * mix(vec3(1.12, 0.98, 0.78), vec3(1.05, 0.99, 0.88), light);
  color = mix(color, mud, 0.8 * clutter);
  color = mix(color, mix(vec3(0.085, 0.078, 0.068), vec3(0.74, 0.71, 0.65), light), 0.22 * clutter);

  // Vignette: a soft frame when calm, heavier and dirtier when neglected.
  float vignette = dot(screen, screen);
  color *= 1.0 - mix(0.22, 0.07, light) * vignette - clutter * mix(0.35, 0.16, light) * vignette;

  // Fine grain: a dither against banding at rest, a dull film over the picture when neglected.
  uvec2 cell = uvec2(frag);
  float grain = float(hashUint(cell.x + hashUint(cell.y + hashUint(uint(uSeed)))) & 0xffffu) / 65535.0 - 0.5;
  color += grain * (0.010 + 0.06 * clutter);

  color += ring * mix(vec3(0.45, 0.72, 1.0), vec3(0.15, 0.4, 0.95), light) * 0.24;

  // Fixed: the colour blooms for a moment and a band of light sweeps across.
  if (uBloom >= 0.0) {
    float boost = smoothstep(0.0, 0.35, uBloom) * exp(-uBloom * 0.7);
    float l = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(l), color, 1.0 + 0.6 * boost) * (1.0 + 0.18 * boost);
    float along = here.x * 0.8 + here.y * 0.6;
    float sweep = exp(-pow((along - (-1.3 + uBloom * 1.5)) / 0.09, 2.0)) * step(uBloom, 2.4);
    color += sweep * mix(vec3(0.55, 0.8, 1.0), vec3(1.0), light) * 0.28;
  }

  outColor = vec4(clamp(color, 0.0, 1.0), 1.0);
}
