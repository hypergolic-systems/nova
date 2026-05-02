// Dev-only Nova-aware Ksp wrapper. Delegates everything to a real
// `SimulatedKsp` (so the navball / staging / PAW continue to work
// against the Dragonglass fixtures), but intercepts subscriptions to
// the Nova-prefixed topics — `NovaVesselStructure/<id>`,
// `NovaPart/<id>`, `NovaScience/<id>`, `NovaStorage/<id>` — and
// emits canned frames so the SCI / PWR / RES views have something
// to render in the browser.
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
  NovaScienceFrame,
  NovaStorageFrame,
  NovaAtmExperimentFrame,
  NovaLtsExperimentFrame,
  NovaScienceFileFrame,
} from '../telemetry/nova-topics';

// Match Dragonglass's simulator's active vessel id so flight.vesselId
// resolves consistently. Hard-coded — there's no public getter for it
// on the SimulatedKsp class, but the value is stable across DG versions.
const SIM_VESSEL_ID = 'sim-vessel';

const VESSEL_STRUCT_PREFIX = 'NovaVesselStructure/';
const PART_PREFIX     = 'NovaPart/';
const SCIENCE_PREFIX  = 'NovaScience/';
const STORAGE_PREFIX  = 'NovaStorage/';

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

// Tuple shapes minus the leading partId — fixture builders return
// these and `frameFor` prepends the id.
type InstrumentEntry = [
  instrumentName: string,
  experiments: (NovaAtmExperimentFrame | NovaLtsExperimentFrame)[],
];
type ScienceBody = [instruments: InstrumentEntry[]];
type StorageBody = [
  usedBytes: number,
  capacityBytes: number,
  fileCount: number,
  files: NovaScienceFileFrame[],
];

interface PartFixture {
  id: string;
  name: string;       // KSP internal part name
  title: string;      // Player-facing
  parentId: string | null;
  tags: NovaPartStructFrame[4];
  /** Resources + virtual-component view — published on NovaPart/<id>. */
  build: () => NovaComponentFrame[];
  /** Optional science payload — published on NovaScience/<id> if set. */
  buildScience?: () => ScienceBody;
  /** Optional storage payload — published on NovaStorage/<id> if set. */
  buildStorage?: () => StorageBody;
}

// Each tuple: [name, topAlt]. Mirrors
// `AtmosphericProfileExperiment.Layers["Kerbin"]` C#-side so the sim
// and live wire frames produce identical shapes.
const KERBIN_LAYERS: [string, number][] = [
  ['troposphere',  18_000],
  ['stratosphere', 45_000],
  ['mesosphere',   70_000],
];

// Surface floor (m) — mirror of C# `SurfaceFloorMeters`. Below this
// altitude, layer detection returns null and (in dev sim) the
// telemetry currentLayerName flips to "surface".
const ATM_SURFACE_FLOOR_M = 1_000;

// Effective bottom altitude for fidelity-span purposes — first layer
// floors at the surface; subsequent layers start at the previous
// layer's top.
function layerBottomAlt(layerIdx: number): number {
  return layerIdx === 0
    ? ATM_SURFACE_FLOOR_M
    : KERBIN_LAYERS[layerIdx - 1][1];
}

const INSTRUMENT = '2HOT Thermometer';

// Build a complete-or-partial atm-profile file frame from recorded
// altitude bounds. Fidelity comes from altitude-coverage: span vs
// effective layer span (top - max(prev layer top, surface floor)).
function makeAtmFile(
  layer: 'troposphere' | 'stratosphere' | 'mesosphere',
  recMinAlt: number, recMaxAlt: number,
  producedAt: number,
): NovaScienceFileFrame {
  const idx = KERBIN_LAYERS.findIndex(([n]) => n === layer);
  const [, top] = KERBIN_LAYERS[idx];
  const bottom  = layerBottomAlt(idx);
  const span    = top - bottom;
  const captured = Math.max(0, recMaxAlt - recMinAlt);
  const fidelity = span > 0 ? Math.min(1, captured / span) : 0;
  return [
    `atm-profile@Kerbin:${layer}`,
    'atm-profile',
    fidelity,
    producedAt,
    INSTRUMENT,
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
    0, 0,                // direct fields unused
    startUT, endUT, SLICE_DURATION_S,
  ];
}

// Storage seed. Mk1 holds atm-profile records from a prior mission;
// OKTO2 holds lts records. Atm files show varied coverage: tropo only
// got 1–10 km (partial fill); strato fully covered; meso untracked.
const MK1_FILES: NovaScienceFileFrame[] = [
  // Troposphere — covered 1–10 km of the effective 1–18 km span ≈ 53%.
  makeAtmFile('troposphere',  1_000, 10_000, 120),
  // Stratosphere — fully covered.
  makeAtmFile('stratosphere', 18_000, 45_000, 480),
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

// LTS recorded-phase bracket — same idea; resets on slice rollover.
let simLtsLastSlice = -1;
let simLtsRecordedMin = Infinity;
let simLtsRecordedMax = -Infinity;

function currentLayerName(altitude: number): string {
  if (altitude < ATM_SURFACE_FLOOR_M) return 'surface';
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

// Storage display-bytes lerps with each file's fidelity, mirroring
// `DataStorage.DisplayedBytes` C#-side.
function storageDisplayedBytes(files: NovaScienceFileFrame[]): number {
  let total = 0;
  for (const f of files) {
    const exp = f[1];
    const size = exp === 'lts' ? 5_000 : 1_000;
    const fid  = Math.max(0, Math.min(1, f[2]));
    total += fid * size;
  }
  return Math.round(total);
}

function buildThermometerInstrument(): InstrumentEntry {
  const altitude = currentAltitude();
  const lts = ltsState();
  const layerName = currentLayerName(altitude);
  // "surface" is a UI sentinel for sub-floor; doesn't count as being
  // in a real layer for transit / activity purposes.
  const inLayer = layerName !== '' && layerName !== 'surface';

  // Track atm transit bracket. Reset on layer change.
  const atmEnabled = simEnabled['atm-profile'];
  if (atmEnabled && layerName !== simAtmLastLayer) {
    simAtmTransitMin = inLayer ? altitude : Infinity;
    simAtmTransitMax = inLayer ? altitude : -Infinity;
  } else if (atmEnabled && inLayer) {
    simAtmTransitMin = Math.min(simAtmTransitMin, altitude);
    simAtmTransitMax = Math.max(simAtmTransitMax, altitude);
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

  const atmActive = atmEnabled && inLayer;
  const ltsActive = ltsEnabled;

  // Cheap stand-in for KSP's atmospheric temperature curve so the
  // sim has something plausible to feed the temp plot — lapse rate
  // through the troposphere, isotherm above, with a 100 K floor.
  const tempK = Math.max(100, 290 - altitude * 0.0035);

  const atmFrame: NovaAtmExperimentFrame = [
    'EXA',
    'atm-profile',
    atmActive ? 1 : 0,
    1,                                     // willComplete (atm always 1 today)
    atmEnabled ? 1 : 0,
    layerName,                              // "" / "surface" / layer name
    atmHasBracket ? simAtmTransitMin  : 0,
    atmHasBracket ? simAtmTransitMax  : 0,
    atmEnabled ? 'Mk1 Command Pod' : '',
    'Kerbin',
    altitude,
    tempK,
    KERBIN_LAYERS,
    MK1_FILES.map((f) => [
      f[0].split(':')[1],   // "atm-profile@Kerbin:troposphere" → "troposphere"
      f[2],                  // cached fidelity
    ]),
    [],
  ];
  const ltsFrame: NovaLtsExperimentFrame = [
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
  ];
  return ['2HOT Thermometer', [atmFrame, ltsFrame]];
}

function buildThermometerScience(): ScienceBody {
  return [[buildThermometerInstrument()]];
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
    ],
    // Small science drive — holds the atm-profile files.
    buildStorage: () => [
      storageDisplayedBytes(MK1_FILES),
      51_200,
      MK1_FILES.length,
      MK1_FILES,
    ],
  },
  {
    id: '5002',
    name: 'sensorThermometer',
    title: '2HOT Thermometer',
    parentId: '5001',
    tags: ['power-consume', 'science-instrument'],
    build: () => [],   // no resources / components — pure instrument
    buildScience: buildThermometerScience,
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
    ],
    // Larger drive on the probe core — holds the LTS files.
    buildStorage: () => [
      storageDisplayedBytes(OKTO2_FILES),
      102_400,
      OKTO2_FILES.length,
      OKTO2_FILES,
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
    if (this.isNovaTopic(topic.name)) {
      return this.subscribeNova(topic.name, cb as (f: unknown, t: number) => void);
    }
    return this.inner.subscribe(topic, cb);
  }

  send: Ksp['send'] = (topic, op, ...args) => {
    // Intercept Nova ops we recognise — currently just the experiment
    // toggle. Mutating `simEnabled` makes the next emit reflect the
    // change; we also force-emit immediately so the UI doesn't have to
    // wait for the next 100 ms tick to see the result.
    if (topic.name.startsWith(SCIENCE_PREFIX) && op === 'setExperimentEnabled') {
      // args = [instrumentIndex, experimentId, enabled]; the sim
      // hosts a single Thermometer per instrument-bearing fixture, so
      // we ignore instrumentIndex and key on experimentId only.
      const [, experimentId, enabled] = args as unknown as [number, string, boolean];
      simEnabled[experimentId] = enabled;
      // Reset transit/recorded brackets on disable so re-enable starts fresh.
      if (!enabled) {
        if (experimentId === 'atm-profile') {
          simAtmTransitMin = Infinity;
          simAtmTransitMax = -Infinity;
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

  private isNovaTopic(name: string): boolean {
    return name.startsWith(VESSEL_STRUCT_PREFIX)
        || name.startsWith(PART_PREFIX)
        || name.startsWith(SCIENCE_PREFIX)
        || name.startsWith(STORAGE_PREFIX);
  }

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
      return this.partFrame(topicName.slice(PART_PREFIX.length));
    }
    if (topicName.startsWith(SCIENCE_PREFIX)) {
      return this.scienceFrame(topicName.slice(SCIENCE_PREFIX.length));
    }
    if (topicName.startsWith(STORAGE_PREFIX)) {
      return this.storageFrame(topicName.slice(STORAGE_PREFIX.length));
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

  private scienceFrame(partId: string): NovaScienceFrame | undefined {
    const p = FIXTURE_PARTS.find((q) => q.id === partId);
    if (!p?.buildScience) return undefined;
    const [instruments] = p.buildScience();
    return [partId, instruments];
  }

  private storageFrame(partId: string): NovaStorageFrame | undefined {
    const p = FIXTURE_PARTS.find((q) => q.id === partId);
    if (!p?.buildStorage) return undefined;
    const [used, cap, count, files] = p.buildStorage();
    return [partId, used, cap, count, files];
  }
}
