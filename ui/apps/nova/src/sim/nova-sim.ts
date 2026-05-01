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

import { SimulatedKsp } from '@dragonglass/telemetry/simulated';
import type { Ksp, Topic } from '@dragonglass/telemetry/core';
import type {
  NovaVesselStructureFrame,
  NovaPartFrame,
  NovaPartStructFrame,
  NovaComponentFrame,
} from '../telemetry/nova-topics';

// Match Dragonglass's simulator's active vessel id so flight.vesselId
// resolves consistently. Hard-coded — there's no public getter for it
// on the SimulatedKsp class, but the value is stable across DG versions.
const SIM_VESSEL_ID = 'sim-vessel';

const VESSEL_STRUCT_PREFIX = 'NovaVesselStructure/';
const PART_PREFIX = 'NovaPart/';

// ---------- Fixture: a small probe stack with science gear ---------

interface PartFixture {
  id: string;
  name: string;       // KSP internal part name
  title: string;      // Player-facing
  parentId: string | null;
  tags: NovaPartStructFrame[4];
  components: NovaComponentFrame[];
}

// Files to seed the data store with. A mix of atm-profile (always
// fidelity 1.0) and lts (varying fidelity) so the modal's columns
// have visual variety. Tuple shape mirrors NovaScienceFileFrame.
const SEED_FILES: [string, string, number, number, string][] = [
  // [subjectId,                       experimentId,  fid,  producedAt,  instrument]
  ['atm-profile@Kerbin:troposphere',   'atm-profile', 1.00,    120, '2HOT Thermometer'],
  ['atm-profile@Kerbin:stratosphere',  'atm-profile', 1.00,    480, '2HOT Thermometer'],
  ['atm-profile@Kerbin:mesosphere',    'atm-profile', 1.00,    920, '2HOT Thermometer'],
  ['lts@Kerbin:SrfLanded:0',           'lts',         1.00,    767_000, '2HOT Thermometer'],
  ['lts@Kerbin:SrfLanded:1',           'lts',         1.00,  1_534_000, '2HOT Thermometer'],
  ['lts@Kerbin:SrfLanded:2',           'lts',         0.74,  2_301_000, '2HOT Thermometer'],
  ['lts@Kerbin:SrfLanded:3',           'lts',         0.31,  3_068_000, '2HOT Thermometer'],
];

const FIXTURE_PARTS: PartFixture[] = [
  {
    id: '5001',
    name: 'mk1pod_v2',
    title: 'Mk1 Command Pod',
    parentId: null,
    tags: ['power-store', 'power-consume'],
    components: [
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
    components: [
      // No 'T' frame yet — that lands with the Experiments panel.
      // For the moment the part shows up only in the structure list.
    ],
  },
  {
    id: '5003',
    name: 'probeCoreOcto_v2',
    title: 'OKTO2 Probe Core',
    parentId: '5001',
    tags: ['power-consume', 'science-storage'],
    components: [
      // Command idle draw.
      ['C', 0.02, 0, 0, 0],
      // Data storage with the seeded file list.
      ['DS',
        // usedBytes — atm files are 1KB, lts files are 5KB
        SEED_FILES.reduce((s, [, exp]) => s + (exp === 'lts' ? 5_000 : 1_000), 0),
        // capacityBytes
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

  // Track Nova topic subscribers so we can fan out a frame to all of
  // them when the data is requested. Frames are static today; if we
  // start mutating them on a timer (live counter etc), this map lets
  // a re-emit pass do it cheaply.
  private novaSubs = new Map<string, Set<(frame: unknown, t: number) => void>>();

  connect(): Promise<void> {
    return this.inner.connect();
  }

  destroy(): void {
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
    if (frame !== undefined) cb(frame, performance.now() / 1000);

    return () => {
      set!.delete(cb);
      if (set!.size === 0) this.novaSubs.delete(topicName);
    };
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
    return [partId, [], p.components];
  }
}
