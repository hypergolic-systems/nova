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

// Each tuple: [name, topAlt, bottomPressureAtm, topPressureAtm].
// Mirrors `AtmosphericProfileExperiment.Layers["Kerbin"]` C#-side so
// the sim and live wire frames produce identical shapes.
const KERBIN_LAYERS: [string, number, number, number][] = [
  ['troposphere',  18_000, 1.000, 0.092],
  ['stratosphere', 45_000, 0.092, 0.005],
  ['mesosphere',   70_000, 0.005, 0.000],
];

// Approximate pressure at altitude — log-decay from sea-level 1 atm
// to ~0 at the top of mesosphere (70 km), tuned so each layer's
// boundary roughly hits the table values. Real KSP uses an
// atmosphere curve; this is good enough for the indicator demo.
function pressureForAltitude(alt: number): number {
  if (alt <= 0)      return 1.0;
  if (alt >= 70_000) return 0;
  return Math.exp(-alt / 6_000);
}

const INSTRUMENT = '2HOT Thermometer';

// Build a complete (full-fidelity) atm-profile file frame — the layer
// has been fully covered. recorded bounds match the layer span.
function makeAtmFile(
  layer: 'troposphere' | 'stratosphere' | 'mesosphere',
  recMinP: number, recMaxP: number,
  recMinAlt: number, recMaxAlt: number,
  producedAt: number,
): NovaScienceFileFrame {
  const layerEntry = KERBIN_LAYERS.find(([n]) => n === layer)!;
  const [, , bottomP, topP] = layerEntry;
  const span = Math.abs(bottomP - topP);
  const captured = Math.max(0, recMaxP - recMinP);
  const fidelity = span > 0 ? Math.min(1, captured / span) : 0;
  return [
    `atm-profile@Kerbin:${layer}`,
    'atm-profile',
    fidelity,
    producedAt,
    INSTRUMENT,
    recMinP, recMaxP, bottomP, topP,
    recMinAlt, recMaxAlt,
    0, 0, 0,             // start_ut / end_ut / slice_duration unused
  ];
}

// Build an interpolated lts file. start_ut = startUT, end_ut = endUT,
// slice_duration = SLICE_DURATION_S. Fidelity is recomputed at read
// time, so the cached snapshot is just informational.
function makeLtsFile(sliceIdx: number, startUT: number, endUT: number): NovaScienceFileFrame {
  const span = endUT - startUT;
  const cached = Math.max(0, Math.min(1, span / SLICE_DURATION_S));
  return [
    `lts@Kerbin:SrfLanded:${sliceIdx}`,
    'lts',
    cached,
    startUT,
    INSTRUMENT,
    0, 0, 0, 0, 0, 0,    // direct fields unused
    startUT, endUT, SLICE_DURATION_S,
  ];
}

// Storage seed. Mk1 holds atm-profile records from a prior mission;
// OKTO2 holds lts records. The atm files show varied coverage: tropo
// only got the lower ~half of its pressure range (visible as a
// partial-fill blue band in the indicator); strato fully covered;
// meso untracked.
const MK1_FILES: NovaScienceFileFrame[] = [
  // Troposphere — only covered 0–8 km / 1.0 atm down to ~0.5 atm.
  // Recorded span = 0.5 of layer's 0.908 atm range ≈ 55% fidelity.
  makeAtmFile('troposphere',  0.500, 1.000, 0,      8_000,  120),
  // Stratosphere — fully covered.
  makeAtmFile('stratosphere', 0.005, 0.092, 18_000, 45_000, 480),
];
const OKTO2_FILES: NovaScienceFileFrame[] = [
  makeLtsFile(0, 0,                                                SLICE_DURATION_S),
  makeLtsFile(1, SLICE_DURATION_S,                                 SLICE_DURATION_S * 2),
  makeLtsFile(2, SLICE_DURATION_S * 2,                             SLICE_DURATION_S * 3),
  makeLtsFile(3, SLICE_DURATION_S * 3 + SLICE_DURATION_S * 0.26,   SLICE_DURATION_S * 4),
  makeLtsFile(4, SLICE_DURATION_S * 4 + SLICE_DURATION_S * 0.69,   SLICE_DURATION_S * 5),
];

// User-toggle state for the two experiments. Mutated by `send()` when
// the SCI tab fires `setExperimentEnabled`. The next emit reads it.
// Defaults OFF — matches the C# defaults so the player has to opt
// each experiment in explicitly.
const simEnabled: Record<string, boolean> = {
  'atm-profile': false,
  'lts':         false,
};

// Atm transit bracket — tracked per real-time tick from `simAltitude`.
// Resets when the vessel crosses a layer boundary.
let simAtmLastLayer: string = '';
let simAtmTransitMin = Infinity;
let simAtmTransitMax = -Infinity;
let simAtmTransitMinP = Infinity;
let simAtmTransitMaxP = -Infinity;

// LTS recorded-phase bracket — same idea; resets on slice rollover.
let simLtsLastSlice = -1;
let simLtsRecordedMin = Infinity;
let simLtsRecordedMax = -Infinity;

function currentLayerName(altitude: number): string {
  for (const [name, top] of KERBIN_LAYERS) if (altitude < top) return name;
  return '';
}

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
    tags: ['power-store', 'power-consume', 'science-storage'],
    build: () => [
      // Battery: 50% SoC, slow drain.
      ['B', 0.5, 200, -0.4],
      // Command idle draw.
      ['C', 0.05, 0, 0, 0],
      // Small science drive — holds the atm-profile files.
      [
        'DS',
        MK1_FILES.reduce((s, [, exp]) => s + (exp === 'lts' ? 5_000 : 1_000), 0),
        51_200,
        MK1_FILES.length,
        MK1_FILES,
      ],
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
      const pressure = pressureForAltitude(altitude);
      const lts = ltsState();
      const layerName = currentLayerName(altitude);

      // Track atm transit bracket. Reset on layer change.
      const atmEnabled = simEnabled['atm-profile'];
      if (atmEnabled && layerName !== simAtmLastLayer) {
        simAtmTransitMin = layerName === '' ? Infinity  : altitude;
        simAtmTransitMax = layerName === '' ? -Infinity : altitude;
        simAtmTransitMinP = layerName === '' ? Infinity  : pressure;
        simAtmTransitMaxP = layerName === '' ? -Infinity : pressure;
      } else if (atmEnabled && layerName !== '') {
        simAtmTransitMin = Math.min(simAtmTransitMin, altitude);
        simAtmTransitMax = Math.max(simAtmTransitMax, altitude);
        simAtmTransitMinP = Math.min(simAtmTransitMinP, pressure);
        simAtmTransitMaxP = Math.max(simAtmTransitMaxP, pressure);
      }
      simAtmLastLayer = layerName;
      const atmHasBracket = simAtmTransitMax >= simAtmTransitMin;

      // Track lts recorded-phase bracket. Reset on slice rollover.
      const ltsEnabled = simEnabled['lts'];
      if (ltsEnabled && lts.sliceIdx !== simLtsLastSlice) {
        simLtsRecordedMin = lts.phase;
        simLtsRecordedMax = lts.phase;
      } else if (ltsEnabled) {
        simLtsRecordedMin = Math.min(simLtsRecordedMin, lts.phase);
        simLtsRecordedMax = Math.max(simLtsRecordedMax, lts.phase);
      }
      simLtsLastSlice = lts.sliceIdx;
      const ltsHasBracket = ltsEnabled && simLtsRecordedMax >= simLtsRecordedMin;

      // active = enabled && in regime. Mirrors the C# state computation.
      const atmActive = atmEnabled && layerName !== '';
      const ltsActive = ltsEnabled;  // sim is always in an applicable situation

      return [
        ['IN', '2HOT Thermometer', ['atm-profile', 'lts']],
        // EXA — current Kerbin atmosphere state.
        [
          'EXA',
          'atm-profile',
          atmActive ? 1 : 0,
          1,                                     // willComplete (atm always 1 today)
          atmEnabled ? 1 : 0,
          layerName,                              // currentLayerName
          atmHasBracket ? simAtmTransitMin  : 0,  // transitMinAlt
          atmHasBracket ? simAtmTransitMax  : 0,  // transitMaxAlt
          atmHasBracket ? simAtmTransitMinP : 0,  // transitMinPressureAtm
          atmHasBracket ? simAtmTransitMaxP : 0,  // transitMaxPressureAtm
          pressure,                               // currentPressureAtm
          atmEnabled ? 'Mk1 Command Pod' : '',    // destinationStorage when enabled

          'Kerbin',
          altitude,
          KERBIN_LAYERS,
          // Derive savedLocal from MK1_FILES so the indicator's filled
          // bands match what's actually in storage. Each tuple is
          // (layerName, cached fidelity from the file's own snapshot).
          MK1_FILES.map((f) => [
            f[0].split(':')[1],   // "atm-profile@Kerbin:troposphere" → "troposphere"
            f[2],                  // cached fidelity
          ]),
          [],
        ],
        // EXL — Kerbin LTS state.
        [
          'EXL',
          'lts',
          ltsActive ? 1 : 0,
          lts.willComplete ? 1 : 0,
          ltsEnabled ? 1 : 0,
          ltsHasBracket ? simLtsRecordedMin : 0,
          ltsHasBracket ? simLtsRecordedMax : 0,
          ltsEnabled ? 'OKTO2 Probe Core' : '',
          'Kerbin',
          'SrfLanded',
          'Kerbin',
          SLICES_PER_YEAR,
          KERBIN_YEAR_S,
          lts.sliceIdx,
          lts.phase,
          ltsActive ? lts.activeFid : 0,
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
      // Larger drive on the probe core — holds the LTS files.
      [
        'DS',
        OKTO2_FILES.reduce((s, [, exp]) => s + (exp === 'lts' ? 5_000 : 1_000), 0),
        102_400,
        OKTO2_FILES.length,
        OKTO2_FILES,
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
    // Intercept Nova ops we recognise — currently just the experiment
    // toggle. Mutating `simEnabled` makes the next emit reflect the
    // change; we also force-emit immediately so the UI doesn't have to
    // wait for the next 100 ms tick to see the result.
    if (topic.name.startsWith(PART_PREFIX) && op === 'setExperimentEnabled') {
      const [experimentId, enabled] = args as unknown as [string, boolean];
      simEnabled[experimentId] = enabled;
      // Reset transit/recorded brackets on disable so re-enable starts fresh.
      if (!enabled) {
        if (experimentId === 'atm-profile') {
          simAtmTransitMin = Infinity;
          simAtmTransitMax = -Infinity;
          simAtmTransitMinP = Infinity;
          simAtmTransitMaxP = -Infinity;
          simAtmLastLayer = '';
        } else if (experimentId === 'lts') {
          simLtsRecordedMin = Infinity;
          simLtsRecordedMax = -Infinity;
          simLtsLastSlice = -1;
        }
      }
      this.tickEmit();
      return;
    }
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
