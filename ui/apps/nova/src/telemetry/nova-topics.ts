// Nova-specific telemetry topic types.
//
// Wire types are positional tuples that mirror the JSON emitted by
// the C# topics (see mod/Nova/Telemetry/NovaPartTopic.cs and
// NovaVesselStructureTopic.cs). UI types are clean named objects;
// `decodeStructure` and `decodePart` translate at the boundary so
// Svelte components only see the friendly shape.

import { topic, type Topic } from '@dragonglass/telemetry/core';

// ---------- System tags ----------------------------------------

// Mirrors `Nova.Core.Components.SystemTags` constants. Keep in sync
// — a typo here makes a part silently disappear from a view.
export type SystemTag =
  | 'power-gen'
  | 'power-consume'
  | 'power-store'
  | 'propulsion'
  | 'rcs'
  | 'attitude'
  | 'storage'
  | 'science-instrument'
  | 'science-storage';

// ---------- Wire (frame) types ---------------------------------

export type NovaResourceFrame = [
  resourceId: string,
  amount: number,
  capacity: number,
  rate: number,
];

// One named tuple per kind so visualization code can import the
// specific frame type it cares about without re-narrowing the union.
export type NovaSolarFrame    = ['S', currentEcRate: number, maxEcRate: number, deployed: 0 | 1, sunlit: 0 | 1, retractable: 0 | 1];
export type NovaBatteryFrame  = ['B', soc: number, capacity: number, rate: number];
export type NovaWheelFrame    = ['W', motorRate: number, busRate: number, bufferFraction: number, refillActive: 0 | 1];
export type NovaLightFrame    = ['L', rate: number];
export type NovaEngineFrame   = ['E', alternatorMaxRate: number, alternatorRate: number];
export type NovaTankFrame     = ['T', volume: number];
export type NovaCommandFrame  = [
  'C',
  idleRate: number,
  testLoadRate: number,
  testLoadMaxRate: number,
  testLoadActive: 0 | 1,
];
export type NovaFuelCellFrame = [
  'F',
  currentOutput: number,
  maxOutput: number,
  isActive: 0 | 1,
  validUntilSec: number,
  lh2ManifoldFraction: number,
  loxManifoldFraction: number,
  refillActive: 0 | 1,
];

// One observation result on disk. Subject id encodes (experiment, body,
// variant, optional slice index); the UI synthesises display values
// from it deterministically.
export type NovaScienceFileFrame = [
  subjectId: string,
  experimentId: string,
  fidelity: number,
  producedAt: number,
  instrument: string,
];

export type NovaDataStorageFrame = [
  'DS',
  usedBytes: number,
  capacityBytes: number,
  fileCount: number,
  files: NovaScienceFileFrame[],
];

// Capability descriptor for a science instrument. Static metadata
// (name + experiment id list). Live progress per experiment ships as
// separate `EXA` / `EXL` frames on the same instrument part.
export type NovaInstrumentFrame = [
  'IN',
  instrumentName: string,
  experimentIds: string[],
];

// Atmospheric Profile experiment state. Layers + saved fidelities for
// the body the vessel currently orbits. `active` reflects the
// instrument's `AtmActive` flag. `altitude` drives the altitude
// pointer overlay; clamping to layer bounds is the UI's job.
//
// `willComplete` is the bright/dull-orange selector: 1 = the active
// observation will reach full fidelity at segment end (transit-out
// for atm; slice rollover for lts); 0 = the segment is in progress
// but will fall short. Atm-profile is a transit-trigger today and
// always emits `1`, but the slot is final so future sat-throttled
// atm sealing won't need a wire change.
export type NovaAtmExperimentFrame = [
  'EXA',
  experimentId: string,
  active: 0 | 1,
  willComplete: 0 | 1,
  bodyName: string,
  altitude: number,
  layers: [name: string, top: number][],         // bottom→top, top in m
  savedLocal: [layerName: string, fidelity: number][],
  savedKsc:   [layerName: string, fidelity: number][],
];

// Long-Term Study experiment state. 12 sectors per body-year per
// (body, situation). `phase` and `currentSliceIndex` are redundant
// with each other but `phase` lets the UI position the body marker
// continuously. `solarParentName` is the body whose Sun-orbit ring
// the indicator draws (Mun → Kerbin; Gilly → Eve; Kerbin → Kerbin).
export type NovaLtsExperimentFrame = [
  'EXL',
  experimentId: string,
  active: 0 | 1,
  willComplete: 0 | 1,
  bodyName: string,
  situation: string,
  solarParentName: string,
  slicesPerYear: number,
  bodyYearSeconds: number,
  currentSliceIndex: number,
  phase: number,                                  // 0..1
  activeFidelity: number,                         // 0..1
  savedLocal: [sliceIndex: number, fidelity: number][],
  savedKsc:   [sliceIndex: number, fidelity: number][],
];

export type NovaComponentFrame =
  | NovaSolarFrame
  | NovaBatteryFrame
  | NovaWheelFrame
  | NovaLightFrame
  | NovaEngineFrame
  | NovaTankFrame
  | NovaCommandFrame
  | NovaFuelCellFrame
  | NovaDataStorageFrame
  | NovaInstrumentFrame
  | NovaAtmExperimentFrame
  | NovaLtsExperimentFrame;

export type NovaPartFrame = [
  partId: string,
  resources: NovaResourceFrame[],
  components: NovaComponentFrame[],
];

export type NovaPartStructFrame = [
  partId: string,
  partName: string,    // KSP internal name (e.g. "solarPanels3")
  partTitle: string,   // player-facing display title (e.g. "OX-STAT Photovoltaic Panels")
  parentId: string | null,
  tags: SystemTag[],
];

export type NovaVesselStructureFrame = [
  vesselId: string,
  vesselName: string,
  parts: NovaPartStructFrame[],
];

// ---------- UI-facing types ------------------------------------

export interface NovaResourceFlow {
  /** Canonical resource name as registered in Nova.Core.Resources
   *  (e.g. "Electric Charge", "Liquid Hydrogen"). The UI maps this to
   *  a short code via `resource-codes.ts` for in-row display. */
  resourceId: string;
  /** Current units stored. */
  amount: number;
  /** Maximum units (>0 — zero-capacity buffers are filtered upstream). */
  capacity: number;
  /** Live flow rate in units/s; positive = filling, negative = draining. */
  rate: number;
}

export interface SolarState {
  /** Actual EC/s output last tick — LP-throttled by demand. Drops to
   *  0 when batteries are full and no consumer wants the power. */
  rate: number;
  /** Optimal EC/s output in the panel's current orientation, gated on
   *  sunlit + deployed. The "max" of the current/max readout. */
  maxRate: number;
  deployed: boolean;
  sunlit: boolean;
  /** True iff the panel can be retracted after deployment. Drives
   *  whether the UI offers a toggle (true) or a one-shot open
   *  button (false). Fixed (non-deployable) panels also report
   *  false, but they're always `deployed: true` so no button shows. */
  retractable: boolean;
}

export interface BatteryState {
  /** State-of-charge, 0..1. */
  soc: number;
  /** Capacity in EC. */
  capacity: number;
  /** Current EC/s flow; positive = filling, negative = draining. */
  rate: number;
}

export interface WheelState {
  /** Live W flowing into the motor this tick — the torque-side
   *  consumption, sourced from the buffer + bus combined. Always
   *  non-negative. */
  motorRate: number;
  /** Live W flowing from the EC bus into the wheel system this tick
   *  (the refill device's actual delivery). 0 when refill is off; up
   *  to `RefillRateWatts` when on. The bus-facing draw — what the
   *  rest of the vessel sees the wheel pulling. */
  busRate: number;
  /** Energy-buffer fill, 0..1. The buffer holds ~10 s of single-axis
   *  full operation; refill (from EC bus) trips on at 10 % and off at
   *  100 %. */
  bufferFraction: number;
  /** True while the buffer is refilling from the bus (between the
   *  10 % trip-on and the 100 % trip-off). */
  refillActive: boolean;
}

export interface LightState {
  /** Live EC/s draw, post-LP-throttle. */
  rate: number;
}

export interface EngineState {
  /** Nominal alternator capacity at full activity, EC/s. */
  alternatorMaxRate: number;
  /** Live EC/s output, post-LP-solve. The LP throttles converter
   *  activity to whatever balances current load — display this
   *  directly; do not multiply by engine throttle. */
  alternatorRate: number;
}

export interface TankState {
  /** Geometric capacity of the tank in litres. The current resource
   *  loadout is in `NovaPart.resources`; this `volume` is the input
   *  the editor uses to compute new buffer capacities when the player
   *  picks a different preset via Set Tank Config. */
  volume: number;
}

export interface CommandState {
  /** Live W consumed by the always-on avionics baseline (post-LP-
   *  throttle — drops below the rated draw only when the bus is
   *  starved). */
  idleRate: number;
  /** Live W consumed by the debug test load. Zero when the toggle
   *  is off; capped at `testLoadMaxRate` when on; throttled below
   *  that under starvation. */
  testLoadRate: number;
  /** Configured ceiling for the test load — what it would draw on
   *  a healthy bus when toggled on. Surfaced for tooltips ("Toggle
   *  1000 W debug load"). */
  testLoadMaxRate: number;
  /** Toggle state for the debug load. Reset to false on every flight
   *  load (intentionally non-persistent — a forgotten toggle shouldn't
   *  silently drain a vessel after a quickload). */
  testLoadActive: boolean;
}

export interface ScienceFile {
  /** Stable, value-equal identifier for "what this file is about".
   *  Encodes experiment + body + variant + optional slice index. The
   *  UI synthesises display values from this string deterministically. */
  subjectId: string;
  /** "atm-profile" / "lts" / etc. — drives palette and label. */
  experimentId: string;
  /** Quality, 0..1. atm-profile always 1.0; lts grows over a slice. */
  fidelity: number;
  /** UT (seconds) when the file was sealed. */
  producedAt: number;
  /** Player-facing name of the instrument that produced this file
   *  ("2HOT Thermometer", etc.). Stamped at emit-time so files in
   *  storage carry attribution after the producing part is gone. */
  instrument: string;
}

export interface InstrumentState {
  /** Player-facing instrument name ("2HOT Thermometer"). */
  name: string;
  /** Wire-format experiment ids the instrument can run. UI maps to
   *  display labels via `science-labels`. */
  experimentIds: string[];
}

export interface AtmExperimentState {
  /** "atm-profile". Used by ScienceView to match this state to the
   *  matching experiment-id from the InstrumentState capability list. */
  experimentId: string;
  /** True iff the instrument is observing right now (drives orange
   *  overlay on whichever layer the vessel currently transits). */
  active: boolean;
  /** True iff the active observation can still reach full fidelity
   *  at segment end. False = "in progress but will fall short" (drives
   *  the dull-orange variant). Always true for atm-profile today;
   *  reserved slot for future EC-gated transit sealing. */
  willComplete: boolean;
  /** Current vessel body — drives label and the layer table. */
  bodyName: string;
  /** Live vessel altitude in meters. UI clamps to layer bounds. */
  altitude: number;
  /** Bottom→top layer table for `bodyName`. Empty when the body has
   *  no atmosphere. Each layer's bottom = previous layer's top (or 0
   *  for index 0). */
  layers: { name: string; top: number }[];
  /** Per-layer best fidelity stored on this vessel (0..1). Layers
   *  with no saved file are omitted from the wire and absent here. */
  savedLocal: Map<string, number>;
  /** Per-layer KSC archive fidelity. Empty until transmission ships. */
  savedKsc: Map<string, number>;
}

export interface LtsExperimentState {
  /** "lts". */
  experimentId: string;
  /** True iff the instrument is currently accumulating against the
   *  current slice (drives orange overlay on `currentSliceIndex`). */
  active: boolean;
  /** True iff the in-progress accumulator can still reach 1.0 by
   *  slice end. False = the instrument started accumulating after
   *  some of the slice elapsed (or is throttled), so the slice will
   *  seal at partial fidelity even if active stays on. Drives the
   *  dull-orange variant on the current sector. */
  willComplete: boolean;
  bodyName: string;
  /** Stock ExperimentSituations enum name (e.g. "SrfLanded"). */
  situation: string;
  /** Body whose Sun-orbit ring the indicator draws. For Mun this is
   *  "Kerbin"; for Kerbin it's "Kerbin"; for the Sun it's "Sun". */
  solarParentName: string;
  /** 12 today; surfaced for forward-compat. */
  slicesPerYear: number;
  /** Length of the body-year in seconds (Sun-orbit period of the
   *  solar-parent body). UI computes per-slice ETA as
   *  `(1 - phaseInSlice) * bodyYearSeconds / slicesPerYear`. */
  bodyYearSeconds: number;
  /** Index of the slice the vessel currently sits in, 0..slicesPerYear-1. */
  currentSliceIndex: number;
  /** Position around the body-year (0..1). Drives the orbiting
   *  body marker — continuous between slice boundaries. */
  phase: number;
  /** Active accumulator fidelity for the in-progress slice. 0..1. */
  activeFidelity: number;
  /** Per-slice best stored fidelity on this vessel. Slices with no
   *  saved file are absent. */
  savedLocal: Map<number, number>;
  /** Per-slice KSC archive fidelity. Empty until transmission ships. */
  savedKsc: Map<number, number>;
}

export interface DataStorageState {
  /** Bytes occupied by the files currently in storage. Live counter
   *  — files come and go as experiments emit and (eventually) transmit. */
  usedBytes: number;
  /** Capacity ceiling in bytes. New files are dropped when used == capacity. */
  capacityBytes: number;
  /** Same as `files.length` — duplicated on the wire to support a future
   *  paged-fetch UI that doesn't always materialise the file list. */
  fileCount: number;
  files: ScienceFile[];
}

export interface FuelCellState {
  /** Live W output, post-LP-throttle. Drops to 0 when `isActive` is
   *  false or batteries are full and consumers don't want the power. */
  currentOutput: number;
  /** Rated W output at full demand. */
  maxOutput: number;
  /** Hysteresis state set by VirtualVessel each tick: ON below 20%
   *  vessel-wide battery SoC, OFF above 80%. Mid-band is "hold". */
  isActive: boolean;
  /** Seconds until the next predicted production flip — the soonest
   *  of SoC threshold crossing and manifold-empty event. `Infinity`
   *  when no transition is reachable. */
  validUntilSec: number;
  /** Internal LH₂ manifold fill, 0..1. Production drains the manifold
   *  off-LP at the reactant rate; refill tops it up at envelope-
   *  friendly L/s. See `docs/lp_hygiene.md`. */
  lh2ManifoldFraction: number;
  /** Internal LOx manifold fill, 0..1. */
  loxManifoldFraction: number;
  /** Refill device hysteresis state: ON when either manifold dips
   *  below 10%, OFF when both reach 100%. */
  refillActive: boolean;
}

export interface NovaPart {
  id: string;
  resources: NovaResourceFlow[];
  solar: SolarState[];
  battery: BatteryState[];
  wheel: WheelState[];
  light: LightState[];
  engine: EngineState[];
  tank: TankState[];
  command: CommandState[];
  fuelCell: FuelCellState[];
  dataStorage: DataStorageState[];
  instrument: InstrumentState[];
  atmExperiment: AtmExperimentState[];
  ltsExperiment: LtsExperimentState[];
}

export interface NovaPartStruct {
  id: string;
  /** KSP internal name (e.g. "solarPanels3"). Useful for matching
   *  against ModuleManager configs and for debugging. */
  name: string;
  /** Player-facing display title, with redundant category suffixes
   *  ("Photovoltaic Panels", "Rechargeable Battery Pack", …) stripped
   *  by `shortenPartTitle` so rows stay readable. */
  title: string;
  parentId: string | null;
  tags: SystemTag[];
}

export interface NovaVesselStructure {
  vesselId: string;
  name: string;
  parts: NovaPartStruct[];
}

// ---------- Topic factories ------------------------------------

export const NovaVesselStructureTopic = (
  vesselId: string,
): Topic<NovaVesselStructureFrame> =>
  topic<NovaVesselStructureFrame>(`NovaVesselStructure/${vesselId}`);

/**
 * Inbound ops the UI can fire at a per-part NovaPart topic. Keep in
 * sync with `NovaPartTopic.HandleOp` in mod/Nova/Telemetry — adding
 * a method here without a matching C# case will silently no-op.
 */
export interface NovaPartOps {
  /**
   * Extend (`true`) or retract (`false`) this part's deployable
   * solar panel. No-op if the part has no `NovaDeployableSolar`
   * module, if the panel is mid-animation, or if the requested
   * state would retract a non-retractable panel in flight. Per-panel
   * only — symmetry counterparts are NOT walked by the mod side, so
   * fire one op per panel you want to actuate (or use the SOLAR
   * subgroup bulk control to dispatch a fan-out).
   */
  setSolarDeployed(deployed: boolean): void;

  /**
   * Editor-only. Replace this part's tank loadout with the named
   * preset (one of the ids in `editor/tank-presets.ts`), built fresh
   * against the part's geometric `volume`. No-op if the part has no
   * `NovaTankModule`, the preset id is unknown, or the call lands
   * outside `GameScenes.EDITOR`.
   */
  setTankConfig(presetId: string): void;

  /**
   * Toggle the debug "test load" on this part's command pod (a
   * fixed-rate EC consumer used to exercise the power system).
   * No-op if the part has no `NovaCommandModule` with a configured
   * `testLoadRate`. The flag is non-persistent — every flight load
   * resets it to false.
   */
  setCommandTestLoad(active: boolean): void;
}

export const NovaPartTopic = (partId: string): Topic<NovaPartFrame, NovaPartOps> =>
  topic<NovaPartFrame, NovaPartOps>(`NovaPart/${partId}`);

// ---------- Decoders -------------------------------------------

// Stock parts repeat their category in the title ("OX-STAT Photovoltaic
// Panels", "Z-100 Rechargeable Battery Pack"). Strip the redundant tail
// at the decode boundary so every consumer gets pre-shortened titles.
const TITLE_SUFFIXES = [
  'Photovoltaic Panels',
  'Liquid Fuel Engine',
  'Rechargeable Battery Pack',
  'Radioisotope Thermoelectric Generator',
  'Fuel Tank',
];

function shortenPartTitle(title: string): string {
  for (const s of TITLE_SUFFIXES) {
    if (title.endsWith(s)) return title.slice(0, -s.length).trimEnd();
  }
  return title;
}

export function decodeStructure(
  f: NovaVesselStructureFrame,
): NovaVesselStructure {
  const [vesselId, name, parts] = f;
  return {
    vesselId,
    name,
    parts: parts.map(([id, partName, partTitle, parentId, tags]) => ({
      id,
      name: partName,
      title: shortenPartTitle(partTitle),
      parentId,
      tags,
    })),
  };
}

export function decodePart(f: NovaPartFrame): NovaPart {
  const [id, resources, components] = f;
  const out: NovaPart = {
    id,
    resources: resources.map(([resourceId, amount, capacity, rate]) => ({
      resourceId,
      amount,
      capacity,
      rate,
    })),
    solar: [],
    battery: [],
    wheel: [],
    light: [],
    engine: [],
    tank: [],
    command: [],
    fuelCell: [],
    dataStorage: [],
    instrument: [],
    atmExperiment: [],
    ltsExperiment: [],
  };
  for (const c of components) {
    switch (c[0]) {
      case 'S':
        out.solar.push({
          rate: c[1],
          maxRate: c[2],
          deployed: c[3] === 1,
          sunlit: c[4] === 1,
          retractable: c[5] === 1,
        });
        break;
      case 'B':
        out.battery.push({ soc: c[1], capacity: c[2], rate: c[3] });
        break;
      case 'W':
        out.wheel.push({
          motorRate: c[1],
          busRate: c[2],
          bufferFraction: c[3],
          refillActive: c[4] === 1,
        });
        break;
      case 'L':
        out.light.push({ rate: c[1] });
        break;
      case 'E':
        out.engine.push({
          alternatorMaxRate: c[1],
          alternatorRate: c[2],
        });
        break;
      case 'T':
        out.tank.push({ volume: c[1] });
        break;
      case 'C':
        out.command.push({
          idleRate: c[1],
          testLoadRate: c[2],
          testLoadMaxRate: c[3],
          testLoadActive: c[4] === 1,
        });
        break;
      case 'F':
        out.fuelCell.push({
          currentOutput: c[1],
          maxOutput: c[2],
          isActive: c[3] === 1,
          validUntilSec: c[4],
          lh2ManifoldFraction: c[5],
          loxManifoldFraction: c[6],
          refillActive: c[7] === 1,
        });
        break;
      case 'IN':
        out.instrument.push({
          name: c[1],
          experimentIds: c[2],
        });
        break;
      case 'DS':
        out.dataStorage.push({
          usedBytes: c[1],
          capacityBytes: c[2],
          fileCount: c[3],
          files: c[4].map(([subjectId, experimentId, fidelity, producedAt, instrument]) => ({
            subjectId,
            experimentId,
            fidelity,
            producedAt,
            instrument,
          })),
        });
        break;
      case 'EXA':
        out.atmExperiment.push({
          experimentId:    c[1],
          active:          c[2] === 1,
          willComplete:    c[3] === 1,
          bodyName:        c[4],
          altitude:        c[5],
          layers:          c[6].map(([name, top]) => ({ name, top })),
          savedLocal:      new Map(c[7]),
          savedKsc:        new Map(c[8]),
        });
        break;
      case 'EXL':
        out.ltsExperiment.push({
          experimentId:      c[1],
          active:            c[2] === 1,
          willComplete:      c[3] === 1,
          bodyName:          c[4],
          situation:         c[5],
          solarParentName:   c[6],
          slicesPerYear:     c[7],
          bodyYearSeconds:   c[8],
          currentSliceIndex: c[9],
          phase:             c[10],
          activeFidelity:    c[11],
          savedLocal:        new Map(c[12]),
          savedKsc:          new Map(c[13]),
        });
        break;
    }
  }
  return out;
}
