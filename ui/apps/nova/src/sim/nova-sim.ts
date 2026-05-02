// Dev-only Nova-aware Ksp wrapper. Delegates everything to a real
// `SimulatedKsp` (so the navball / staging / PAW continue to work
// against the Dragonglass fixtures), but intercepts subscriptions to
// the Nova-prefixed topics — `NovaVesselStructure/<id>` and
// `NovaPart/<id>` — and emits canned frames so the SCI / PWR / RES
// views have something to render in the browser.
//
// The fixture data is shaped to match the live broadcaster's wire
// format (positional tuples). See `nova-topics.ts` for the type-level
// contract; this file is the only place that ever produces those
// tuples by hand (the C# side uses JsonWriter).
//
// A 100 ms internal timer ticks the simulation forward so the science
// indicators show motion: altitude oscillates through Kerbin's atm
// layers (~90 s period), the LTS body marker drifts around the orbit
// ring, and active-slice fidelity ramps + resets at slice boundaries.

import { SimulatedKsp } from '@dragonglass/telemetry/simulated';
import type { Ksp, Topic } from '@dragonglass/telemetry/core';
import type {
  NovaVesselStructureFrame,
  NovaPartFrame,
  NovaPartStructFrame,
  NovaComponentFrame,
  NovaScienceFileFrame,
} from '../telemetry/nova-topics';

// Match Dragonglass's simulator's active vessel id so flight.vesselId
// resolves consistently. Hard-coded — there's no public getter for it
// on the SimulatedKsp class, but the value is stable across DG versions.
const SIM_VESSEL_ID = 'sim-vessel';

const VESSEL_STRUCT_PREFIX = 'NovaVesselStructure/';
const PART_PREFIX = 'NovaPart/';

// ---------- Time + environment simulation -------------------------

// Kerbin year — used to drive LTS phase / slice boundaries. Real
// Kerbin: 9_203_545 s. Plays back fast enough below to demo a slice
// rollover within ~30 s real time.
const KERBIN_YEAR_S = 9_203_545;
const SLICES_PER_YEAR = 12;
const SLICE_DURATION_S = KERBIN_YEAR_S / SLICES_PER_YEAR;

// One real-time second = this many sim seconds. Tuned so a slice
// elapses in ~30 s real time (you can watch the orange wedge fill
// and snap to the next slice without leaving the tab).
const SIM_TIME_SCALE = SLICE_DURATION_S / 30;

// Atmosphere oscillation. simAltitude sweeps 15 km ↔ 75 km on a 90 s
// real-time period — crosses 18 km / 45 km / 70 km so you watch the
// pointer move across layer boundaries.
const ALT_PERIOD_S = 90;
const ALT_MIN_M = 15_000;
const ALT_MAX_M = 75_000;

const realTimeSec = (): number => performance.now() / 1000;

// LTS subjects that already have files in storage. Slices 0–2 sealed
// at full fidelity; slice 3 mid-fill from a previous attempt; slice 4
// barely started. The active accumulator (currentSliceIndex) is
// driven separately by simNowUT so the fixture doesn't pre-seal it.
const SAVED_LOCAL_LTS: [number, number][] = [
  [0, 1.00],
  [1, 1.00],
  [2, 1.00],
  [3, 0.74],
  [4, 0.31],
];

// ---------- Fixture: a small probe stack with science gear ---------

interface PartFixture {
  id: string;
  name: string;       // KSP internal part name
  title: string;      // Player-facing
  parentId: string | null;
  tags: NovaPartStructFrame[4];
  build: () => NovaComponentFrame[];
}

const KERBIN_LAYERS: [string, number][] = [
  ['troposphere',  18_000],
  ['stratosphere', 45_000],
  ['mesosphere',   70_000],
];

// Storage seed. Atm files mirror the saved-local layers in the EXA
// frame; LTS files mirror the saved-local slice list. Keeping these
// in sync means the file modal and the indicator agree on what data
// "exists locally". producedAt offsets are visual filler.
const SEED_FILES: NovaScienceFileFrame[] = [
  ['atm-profile@Kerbin:troposphere',   'atm-profile', 1.00,    120, '2HOT Thermometer'],
  ['atm-profile@Kerbin:stratosphere',  'atm-profile', 1.00,    480, '2HOT Thermometer'],
  ['atm-profile@Kerbin:mesosphere',    'atm-profile', 1.00,    920, '2HOT Thermometer'],
  ['lts@Kerbin:SrfLanded:0',           'lts',         1.00,    767_000, '2HOT Thermometer'],
  ['lts@Kerbin:SrfLanded:1',           'lts',         1.00,  1_534_000, '2HOT Thermometer'],
  ['lts@Kerbin:SrfLanded:2',           'lts',         0.74,  2_301_000, '2HOT Thermometer'],
  ['lts@Kerbin:SrfLanded:3',           'lts',         0.31,  3_068_000, '2HOT Thermometer'],
];

function currentAltitude(): number {
  const phase = (realTimeSec() / ALT_PERIOD_S) * Math.PI * 2;
  // Sine swing centred between min/max.
  const mid = (ALT_MIN_M + ALT_MAX_M) / 2;
  const amp = (ALT_MAX_M - ALT_MIN_M) / 2;
  return mid + Math.sin(phase) * amp;
}

function currentSimUT(): number {
  return realTimeSec() * SIM_TIME_SCALE;
}

function ltsState() {
  const ut = currentSimUT();
  const phase = (ut % KERBIN_YEAR_S) / KERBIN_YEAR_S;
  const sliceIdx = Math.floor(phase * SLICES_PER_YEAR);
  const sliceStart = sliceIdx * SLICE_DURATION_S;
  const intoSlice = (ut % KERBIN_YEAR_S) - sliceStart;
  const phaseInSlice = intoSlice / SLICE_DURATION_S;
  // On odd-indexed slices, simulate "instrument turned on late" — the
  // accumulator lags real elapsed time by 30%, so the slice will seal
  // at partial fidelity. Drives the dull-orange variant in the demo.
  const lagFraction = sliceIdx % 2 === 1 ? 0.3 : 0;
  const activeFid = Math.max(0, Math.min(1, phaseInSlice - lagFraction));
  const willComplete = activeFid + (1 - phaseInSlice) >= 0.99;
  return { phase, sliceIdx, activeFid, willComplete };
}

const FIXTURE_PARTS: PartFixture[] = [
  {
    id: '5001',
    name: 'mk1pod_v2',
    title: 'Mk1 Command Pod',
    parentId: null,
    tags: ['power-store', 'power-consume'],
    build: () => [
      // Battery: 50% SoC, slow drain.
      ['B', 0.5, 200, -0.4],
      // Command idle draw.
      ['C', 0.05, 0, 0, 0],
    ],
  },
  {
    id: '5002',
    name: 'sensorThermometer',
    title: '2HOT Thermometer',
    parentId: '5001',
    tags: ['power-consume', 'science-instrument'],
    build: () => {
      const altitude = currentAltitude();
      const lts = ltsState();
      return [
        ['IN', '2HOT Thermometer', ['atm-profile', 'lts']],
        // EXA — current Kerbin atmosphere state. Active flag is on,
        // so whichever layer the altitude pointer is in gets an
        // orange overlay. Saved-local mirrors the SEED_FILES.
        [
          'EXA',
          'atm-profile',
          1,            // active
          1,            // willComplete (atm always 1 today)
          'Kerbin',
          altitude,
          KERBIN_LAYERS,
          [
            ['troposphere',  1.0],
            ['stratosphere', 1.0],
            ['mesosphere',   1.0],
          ],
          [],
        ],
        // EXL — Kerbin LTS state. solarParentName=Kerbin (Kerbin is
        // already a solar child). currentSliceIndex/phase advance
        // with the timer; saved-local matches SEED_FILES.
        [
          'EXL',
          'lts',
          1,                              // active
          lts.willComplete ? 1 : 0,        // willComplete (varies by slice)
          'Kerbin',
          'SrfLanded',
          'Kerbin',
          SLICES_PER_YEAR,
          KERBIN_YEAR_S,
          lts.sliceIdx,
          lts.phase,
          lts.activeFid,
          SAVED_LOCAL_LTS,
          [],
        ],
      ];
    },
  },
  {
    id: '5003',
    name: 'probeCoreOcto_v2',
    title: 'OKTO2 Probe Core',
    parentId: '5001',
    tags: ['power-consume', 'science-storage'],
    build: () => [
      // Command idle draw.
      ['C', 0.02, 0, 0, 0],
      // Data storage. usedBytes derived from the file mix; capacity
      // matches the cfg-side default so the gauge isn't full.
      [
        'DS',
        SEED_FILES.reduce((s, [, exp]) => s + (exp === 'lts' ? 5_000 : 1_000), 0),
        102_400,
        SEED_FILES.length,
        SEED_FILES,
      ],
    ],
  },
];

const FIXTURE_VESSEL_NAME = 'Probe Lab I';

// ---------- The wrapper ---------------------------------------------

export class NovaSimulatedKsp implements Ksp {
  private inner = new SimulatedKsp();

  // Track Nova topic subscribers per topic name. The 100 ms timer
  // re-emits frames to every callback, so live indicators (altitude
  // pointer, LTS active-fidelity, body marker) animate.
  private novaSubs = new Map<string, Set<(frame: unknown, t: number) => void>>();
  private tickHandle: ReturnType<typeof setInterval> | null = null;

  connect(): Promise<void> {
    if (this.tickHandle == null) {
      this.tickHandle = setInterval(() => this.tickEmit(), 100);
    }
    return this.inner.connect();
  }

  destroy(): void {
    if (this.tickHandle != null) {
      clearInterval(this.tickHandle);
      this.tickHandle = null;
    }
    this.novaSubs.clear();
    this.inner.destroy();
  }

  subscribe<T, Ops>(
    topic: Topic<T, Ops>,
    cb: (frame: T, tObserved: number) => void,
  ): () => void {
    if (topic.name.startsWith(VESSEL_STRUCT_PREFIX) || topic.name.startsWith(PART_PREFIX)) {
      return this.subscribeNova(topic.name, cb as (f: unknown, t: number) => void);
    }
    return this.inner.subscribe(topic, cb);
  }

  send: Ksp['send'] = (topic, op, ...args) => {
    // No Nova ops handled in dev. Forward to the inner sim.
    this.inner.send(topic, op, ...args);
  };

  // --- Nova topic dispatch ---

  private subscribeNova(
    topicName: string,
    cb: (frame: unknown, tObserved: number) => void,
  ): () => void {
    let set = this.novaSubs.get(topicName);
    if (!set) {
      set = new Set();
      this.novaSubs.set(topicName, set);
    }
    set.add(cb);

    // Replay the current frame on subscribe — same contract as the
    // live sidecar (subscribers see the latest snapshot immediately).
    const frame = this.frameFor(topicName);
    if (frame !== undefined) cb(frame, realTimeSec());

    return () => {
      set!.delete(cb);
      if (set!.size === 0) this.novaSubs.delete(topicName);
    };
  }

  private tickEmit(): void {
    if (this.novaSubs.size === 0) return;
    const t = realTimeSec();
    for (const [topicName, callbacks] of this.novaSubs) {
      const frame = this.frameFor(topicName);
      if (frame === undefined) continue;
      for (const cb of callbacks) cb(frame, t);
    }
  }

  private frameFor(topicName: string): unknown | undefined {
    if (topicName === VESSEL_STRUCT_PREFIX + SIM_VESSEL_ID) {
      return this.vesselStructureFrame();
    }
    if (topicName.startsWith(PART_PREFIX)) {
      const partId = topicName.slice(PART_PREFIX.length);
      return this.partFrame(partId);
    }
    return undefined;
  }

  private vesselStructureFrame(): NovaVesselStructureFrame {
    const parts: NovaPartStructFrame[] = FIXTURE_PARTS.map((p) => [
      p.id, p.name, p.title, p.parentId, p.tags as never,
    ]);
    return [SIM_VESSEL_ID, FIXTURE_VESSEL_NAME, parts];
  }

  private partFrame(partId: string): NovaPartFrame | undefined {
    const p = FIXTURE_PARTS.find((q) => q.id === partId);
    if (!p) return undefined;
    return [partId, [], p.build()];
  }
}
