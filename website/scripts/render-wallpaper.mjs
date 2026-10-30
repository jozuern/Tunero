// Renders the stills that stand in for the wallpaper until its shader draws (Wallpaper.svelte): the shader's own first
// frame (stillPhase in wallpaper.ts), neglected and fixed, in both themes, as WebP in public/wallpaper/. The picture
// depends only on the screen's height (it is centred and scaled by it), so one wide still, shown at the screen's height
// and centred, matches the first frame on any screen up to 32:9. The grain is left out: it would triple the files, and
// the shader brings it in as it fades over the still.
//
// Run after a change to wallpaper.frag or to stillPhase: npm run render:wallpaper. The e2e test "the stand-in is the
// shader's first frame" fails while the stills are out of date. Needs Playwright's Chromium (npx playwright install
// chromium), or CHROMIUM_PATH.
import { chromium } from '@playwright/test';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
export const stillHeight = 720;
export const stillWidth = Math.round((stillHeight * 32) / 9);
/** The stills: theme 0 dark or 1 light, clutter 1 neglected or 0 fixed. */
export const stills = [
  { file: 'dark-neglected.webp', theme: 0, clutter: 1 },
  { file: 'dark-fixed.webp', theme: 0, clutter: 0 },
  { file: 'light-neglected.webp', theme: 1, clutter: 1 },
  { file: 'light-fixed.webp', theme: 1, clutter: 0 },
];

/** The shader's source without its grain, and the phase its first frame is drawn at. */
export async function shaderSource() {
  const fragment = await readFile(`${root}src/desktop/wallpaper/wallpaper.frag`, 'utf8');
  const driver = await readFile(`${root}src/desktop/wallpaper/wallpaper.ts`, 'utf8');
  const grain = /\n\s*color \+= grain \* [^;]+;/;
  if (!grain.test(fragment)) throw new Error('wallpaper.frag: the grain line is not where this script looks for it');
  const phase = driver.match(/const stillPhase = ([\d.]+);/);
  if (!phase) throw new Error('wallpaper.ts: no stillPhase');
  return { fragment: fragment.replace(grain, ''), phase: Number(phase[1]) };
}

/** Draws one still in the page and returns it as a data URL of the given type. */
export function drawStill(page, source, still, width, height, type = 'image/png', quality = 1) {
  return page.evaluate(
    ([fragment, phase, theme, clutter, width, height, type, quality]) => {
      const canvas = document.createElement('canvas');
      canvas.width = width;
      canvas.height = height;
      const gl = canvas.getContext('webgl2', { preserveDrawingBuffer: true, antialias: false, alpha: false });
      const vertex = `#version 300 es
void main() {
  vec2 corner = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
  gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
}`;
      const shader = (kind, text) => {
        const s = gl.createShader(kind);
        gl.shaderSource(s, text);
        gl.compileShader(s);
        if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s) ?? 'compile');
        return s;
      };
      const program = gl.createProgram();
      gl.attachShader(program, shader(gl.VERTEX_SHADER, vertex));
      gl.attachShader(program, shader(gl.FRAGMENT_SHADER, fragment));
      gl.linkProgram(program);
      gl.useProgram(program);
      gl.bindVertexArray(gl.createVertexArray());
      gl.viewport(0, 0, width, height);
      const uniform = (name) => gl.getUniformLocation(program, name);
      gl.uniform2f(uniform('uRes'), width, height);
      gl.uniform1f(uniform('uPhase'), phase);
      gl.uniform1f(uniform('uClutter'), clutter);
      gl.uniform1f(uniform('uTheme'), theme);
      gl.uniform1f(uniform('uSeed'), 1);
      gl.uniform4fv(uniform('uRipples'), new Float32Array(16));
      gl.uniform1f(uniform('uBloom'), -1);
      gl.uniform1f(uniform('uSplit'), -1);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
      return canvas.toDataURL(type, quality);
    },
    [source.fragment, source.phase, still.theme, still.clutter, width, height, type, quality],
  );
}

export function launch() {
  return chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined, args: ['--enable-unsafe-swiftshader'] });
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const source = await shaderSource();
  const browser = await launch();
  const page = await browser.newPage();
  await mkdir(`${root}public/wallpaper`, { recursive: true });
  for (const still of stills) {
    const url = await drawStill(page, source, still, stillWidth, stillHeight, 'image/webp', 0.9);
    const bytes = Buffer.from(url.slice(url.indexOf(',') + 1), 'base64');
    await writeFile(`${root}public/wallpaper/${still.file}`, bytes);
    console.log(`public/wallpaper/${still.file}: ${Math.round(bytes.length / 1024)} KB`);
  }
  await browser.close();
}
