// The demo PC's sounds, made with Web Audio (no files): a notification chime, a soft pop when something shatters, a
// click for the guided fix and a chord when the PC is fixed. Off until the visitor turns it on with the speaker in the
// tray; the choice is kept in localStorage as sound = on or off.
import { storageKeys } from '../lib/storage-keys.ts';

const key = storageKeys.sound;
let enabled = read();
let context: AudioContext | null = null;
const listeners = new Set<() => void>();

function read(): boolean {
  try {
    return localStorage.getItem(key) === 'on';
  } catch {
    return false;
  }
}

export const soundOn = () => enabled;

export function setSound(value: boolean) {
  enabled = value;
  try {
    localStorage.setItem(key, value ? 'on' : 'off');
  } catch {
    // Storage blocked: the choice holds until the page is left.
  }
  if (value) audio();
  for (const listener of listeners) listener();
}

export function onSoundChange(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** The audio context, made on the first sound after a click (browsers allow audio only then). */
function audio(): AudioContext | null {
  if (!enabled || typeof AudioContext === 'undefined') return null;
  context ??= new AudioContext();
  if (context.state === 'suspended') void context.resume();
  return context;
}

/** One soft sine note with a quick attack and an exponential tail. */
function note(frequency: number, start: number, length: number, volume: number, type: OscillatorType = 'sine') {
  const ctx = audio();
  if (!ctx) return;
  const at = ctx.currentTime + start;
  const osc = ctx.createOscillator();
  const gain = ctx.createGain();
  osc.type = type;
  osc.frequency.setValueAtTime(frequency, at);
  gain.gain.setValueAtTime(0, at);
  gain.gain.linearRampToValueAtTime(volume, at + 0.012);
  gain.gain.exponentialRampToValueAtTime(0.0001, at + length);
  osc.connect(gain).connect(ctx.destination);
  osc.start(at);
  osc.stop(at + length + 0.05);
}

/** A notification: two rising notes, like a desktop chime. */
export function chime() {
  note(784, 0, 0.5, 0.05);
  note(1175, 0.09, 0.7, 0.04);
}

/** Something shattering: a short filtered noise burst with a falling tone under it. */
export function pop() {
  const ctx = audio();
  if (!ctx) return;
  const length = 0.22;
  const buffer = ctx.createBuffer(1, Math.round(ctx.sampleRate * length), ctx.sampleRate);
  const data = buffer.getChannelData(0);
  for (let i = 0; i < data.length; i++) data[i] = (Math.random() * 2 - 1) * (1 - i / data.length) ** 3;
  const source = ctx.createBufferSource();
  source.buffer = buffer;
  const filter = ctx.createBiquadFilter();
  filter.type = 'bandpass';
  filter.frequency.value = 2400;
  filter.Q.value = 0.8;
  const gain = ctx.createGain();
  gain.gain.value = 0.08;
  source.connect(filter).connect(gain).connect(ctx.destination);
  source.start();
  note(520, 0, 0.18, 0.025, 'triangle');
}

/** A mouse click for the guided fix. */
export function click() {
  note(1800, 0, 0.04, 0.03, 'square');
}

/** The PC is fixed: a bright major chord, one note after the other. */
export function chord() {
  [523.25, 659.25, 783.99, 1046.5].forEach((frequency, i) => note(frequency, i * 0.07, 1.8, 0.035));
}
