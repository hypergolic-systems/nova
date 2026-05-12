// Nova-specific telemetry topic types.
//
// Wire types are positional tuples that mirror the JSON emitted by
// the C# topics. Three per-part topics live alongside the structure
// topic:
//   NovaPart/<id>     → resources + virtual-component view
//   NovaScience/<id>  → instrument capabilities + experiment frames
//   NovaStorage/<id>  → DataStorage counters + inline file list
// UI types are clean named objects; `decodeStructure`, `decodePart`,
// `decodeScience`, `decodeStorage` translate at the boundary so
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
  | 'tank'
  | 'science-instrument'
  | 'science-storage'
  | 'thermal'
  | 'command-source';

// ---------- Wire (frame) types ---------------------------------

export type NovaResourceFrame = [
  resourceId: string,
  amount: number,
  capacity: number,
  rate: number,
];

// One named tuple per kind so visualization code can import the
// specific frame type it cares about without re-narrowing the union.
export type NovaSolarFrame    = ['S', currentEcRate: number, maxEcRate: number, deployed: 0 | 1, sunlit: 0 | 1, retractable: 0 | 1, ratedEcRate: number];
export type NovaBatteryFrame  = ['B', soc: number, capacity: number, rate: number];
export type NovaWheelFrame    = ['W', motorRate: number, busRate: number, bufferFraction: number, refillActive: 0 | 1, motorRated: number, busRated: number];
export type NovaLightFrame    = ['L', rate: number, ratedRate: number];
// Per-buffer slice on a TankVolume — `[resource, capacity, contents]`,
// litres. The tank carries its own buffers on this frame so consumers
// (e.g. the editor's Tanks view) don't have to disambiguate which
// entries in the part's flat `resources` list are tank slices vs.
// unrelated buffers (Battery's Electric Charge, etc.).
export type NovaTankSliceFrame = [resource: string, capacity: number, contents: number];
export type NovaTankFrame     = ['T', volume: number, slices: NovaTankSliceFrame[]];
export type NovaCommandFrame  = [
  'C',
  idleRate: number,
  testLoadRate: number,
  testLoadMaxRate: number,
  testLoadActive: 0 | 1,
  idleRated: number,
];
export type NovaProbeFrame    = [
  'P',
  idleRate: number,
  testLoadRate: number,
  testLoadMaxRate: number,
  testLoadActive: 0 | 1,
  idleRated: number,
  sasLevel: number,
  commandBytes: number,
  commandCapacityBytes: number,
  commandRefillBps: number,
  commandDecayBps: number,
  commandConsumeBps: number,
];
export type NovaFuelCellFrame = [
  'F',
  currentOutput: number,
  maxOutput: number,
  isActive: 0 | 1,
  validUntilSec: number,
  manifoldFraction: number,
  refillActive: 0 | 1,
];
export type NovaRtgFrame = [
  'R',
  currentRate: number,
  currentPower: number,
  referencePower: number,
  declineWattsPerKerbinYear: number,
  wasteHeatW: number,
  exportW: number,
  rejectionW: number,
  currentTempC: number,
  maxOperatingTempC: number,
  dTdtCps: number,
];

export type NovaRadiatorFrame = [
  'X',
  currentCoolingW: number,
  maxCoolingW: number,
  isDeployed: 0 | 1,
  isDeployable: 0 | 1,
];

// Per-decoupler state. `fullSeparation` is the editor-time toggle —
// when on, the decoupler releases every attached neighbour at once
// (stock "separator" semantics). `canFullSeparate` is the capability
// bit (false for radial decouplers, where there's only one attach
// face); the UI greys the checkbox out when false rather than hiding
// it so the player still sees the affordance and learns the rule.
// `ejectionForce` is the design-rated impulse, useful for context.
export type NovaDecouplerFrame = [
  'D',
  fullSeparation: 0 | 1,
  canFullSeparate: 0 | 1,
  ejectionForce: number,
];

// One progressive observation record on disk. The wire shape mirrors
// the proto fields. Direct vs interpolated is determined by which set
// of trailing fields is populated:
//
//  Direct (atm-profile)   → recordedMin/MaxAltM
//  Interpolated (lts)     → startUt + endUt + sliceDurationSeconds
//
// Both kinds share the leading prefix (subject id, experiment id, etc.)
// so the UI's file modal renders all files with one schema.
export type NovaScienceFileFrame = [
  subjectId: string,
  experimentId: string,
  fidelity: number,
  producedAt: number,
  instrument: string,
  recordedMinAltM: number,
  recordedMaxAltM: number,
  startUt:              number,
  endUt:                number,
  sliceDurationSeconds: number,
];

// NovaStorage/<id> wire frame. `usedBytes` here is the displayed-bytes
// total (sums fidelity × size per file) so the gauge lerps as data
// accrues. Reservation lives C#-side and isn't exposed.
export type NovaStorageFrame = [
  partId: string,
  usedBytes: number,
  capacityBytes: number,
  fileCount: number,
  files: NovaScienceFileFrame[],
];

// Atmospheric Profile experiment state. Layers + saved fidelities for
// the body the vessel currently orbits. `active` reflects the
// instrument's `AtmActive` flag. `altitude` drives indicator overlays;
// clamping to layer bounds is the UI's job.
//
// `currentLayerName` is one of: a layer name (e.g. "troposphere"),
// the sentinel "surface" when below the per-body floor (1 km on
// Kerbin), or "" when above atmosphere / no atmosphere.
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
  enabled: 0 | 1,
  currentLayerName: string,                       // "" / "surface" / layer name
  transitMinAlt: number,                          // m; 0 when no observation
  transitMaxAlt: number,                          // m; 0 when no observation
  destinationStorage: string,                     // "" when no storage on vessel
  bodyName: string,
  altitude: number,
  temperatureK: number,                           // live atmospheric temp at vessel
  // Each layer entry: [name, topAltMeters]. bottom-to-top order; the
  // layer's effective bottom is the previous layer's top (or the
  // body's surface floor for index 0).
  layers: [name: string, topAlt: number][],
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
  enabled: 0 | 1,
  recordedMinPhase: number,                       // 0..1; 0 when no observation
  recordedMaxPhase: number,                       // 0..1; 0 when no observation
  destinationStorage: string,                     // "" when no storage on vessel
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
  | NovaRadiatorFrame
  | NovaBatteryFrame
  | NovaWheelFrame
  | NovaLightFrame
  | NovaTankFrame
  | NovaCommandFrame
  | NovaProbeFrame
  | NovaFuelCellFrame
  | NovaRtgFrame
  | NovaDecouplerFrame;

export type NovaPartFrame = [
  partId: string,
  resources: NovaResourceFrame[],
  components: NovaComponentFrame[],
];

// NovaScience/<id> wire frame. A part may host multiple instruments
// — each gets its own inner tuple. Within an instrument, the
// `experiments` list holds one EXA / EXL tuple per supported
// experiment kind; today every instrument is a Thermometer so both
// atm + lts emit. Each experiment frame's `experimentId` (slot 1)
// doubles as the capability id, so the wire stays single-source.
export type NovaInstrumentEntry = [
  instrumentName: string,
  experiments: (NovaAtmExperimentFrame | NovaLtsExperimentFrame)[],
];
export type NovaScienceFrame = [
  partId: string,
  instruments: NovaInstrumentEntry[],
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
  /** BoL design rate at full sun, independent of orientation / sunlit /
   *  deployment state. The editor view uses this as the panel's
   *  contribution to the load-vs-source balance; the flight view sticks
   *  with `rate / maxRate` for live behavior. */
  ratedRate: number;
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
  /** Design peak motor draw, W (single-axis-full). Editor view shows
   *  this as the wheel's contribution to consumption since `motorRate`
   *  is 0 with no solver running. */
  motorRated: number;
  /** Design buffer-refill ceiling, W (= RefillRateWatts). What the
   *  bus sees at refill peak in flight; serves as the editor row's
   *  bus-side rated draw. */
  busRated: number;
}

export interface LightState {
  /** Live EC/s draw, post-LP-throttle. */
  rate: number;
  /** Design draw, W. Independent of toggle / solver state — used by
   *  the editor view for the consumption balance. */
  ratedRate: number;
}

export interface TankSlice {
  /** Canonical resource name (e.g. "Liquid Oxygen"). */
  resource: string;
  /** Allocated litres for this slice. */
  capacity: number;
  /** Currently filled litres. 0 ≤ contents ≤ capacity. */
  contents: number;
}

export interface TankState {
  /** Geometric capacity of the tank in litres. */
  volume: number;
  /** Per-buffer slice list — what this tank actually holds, separate
   *  from any other buffers on the part. The editor Tanks view
   *  consumes this directly; reading from `NovaPart.resources` would
   *  conflate Battery EC and other unrelated component buffers with
   *  the tank's own slices. */
  slices: TankSlice[];
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
  /** Design rated `idleDraw`, W. Solver-independent — used by the
   *  editor view since `idleRate` is 0 without an LP solve. */
  idleRated: number;
}

export interface ProbeState {
  /** See `CommandState.idleRate`. */
  idleRate: number;
  /** See `CommandState.testLoadRate`. */
  testLoadRate: number;
  /** See `CommandState.testLoadMaxRate`. */
  testLoadMaxRate: number;
  /** See `CommandState.testLoadActive`. */
  testLoadActive: boolean;
  /** See `CommandState.idleRated`. */
  idleRated: number;
  /** Stock SASServiceLevel, 0..3. Carried as data today; consumed
   *  once Nova's SAS replacement lands. */
  sasLevel: number;
  /** Live StoredCommands ledger, bytes — the per-probe authority pool
   *  spent by attitude/throttle inputs and decayed continuously. */
  commandBytes: number;
  /** Maximum ledger fill, bytes. Constant per probe. */
  commandCapacityBytes: number;
  /** Bytes/s currently flowing in from the comms `Receive` job (KSC →
   *  this vessel). 0 when the link is dark. */
  commandRefillBps: number;
  /** Bytes/s of constant decay, regardless of comms / inputs.
   *  Probe-specific config. */
  commandDecayBps: number;
  /** Live consumption demand, bytes/s — `inputCostBps × |input|` set
   *  by the C# gate before it tries to spend. Reads as the player's
   *  intent: a held stick over a starved buffer keeps this at its
   *  full magnitude even though no actual deduction lands. */
  commandConsumeBps: number;
}

export interface ScienceFile {
  /** Stable, value-equal identifier for "what this file is about".
   *  Encodes experiment + body + variant + optional slice index. */
  subjectId: string;
  /** "atm-profile" / "lts" / etc. — drives palette and label. */
  experimentId: string;
  /** Quality, 0..1. Cached snapshot — telemetry recomputes on emit
   *  (interpolated files climb live as UT advances). */
  fidelity: number;
  /** UT (seconds) when the file was first created (observation began). */
  producedAt: number;
  /** Player-facing name of the instrument that produced this file
   *  ("2HOT Thermometer", etc.). */
  instrument: string;

  // Direct (atm-profile): altitude bounds the vessel has covered.
  // Zeros for interpolated files.
  recordedMinAltM: number;
  recordedMaxAltM: number;

  // Interpolated (lts): file's start_ut, slice end_ut, and slice
  // duration. UI derives fidelity at render time as
  // `clamp((min(now, end) - start) / slice_duration, 0, 1)`.
  startUt:              number;
  endUt:                number;
  sliceDurationSeconds: number;
}

export interface AtmExperimentState {
  /** "atm-profile". Used by ScienceView to match this state to the
   *  matching experiment-id from the instrument's capability list. */
  experimentId: string;
  /** True iff the instrument is observing right now (drives orange
   *  overlay on whichever layer the vessel currently transits). */
  active: boolean;
  /** True iff the active observation can still reach full fidelity
   *  at segment end. False = "in progress but will fall short" (drives
   *  the dull-orange variant). Always true for atm-profile today;
   *  reserved slot for future EC-gated transit sealing. */
  willComplete: boolean;
  /** User-controlled enable flag. Independent from `active` — a vessel
   *  out of any atmospheric layer reports `enabled=true, active=false`. */
  enabled: boolean;
  /** Layer the vessel is currently transiting (matches the wire-format
   *  layer name, e.g. "stratosphere"), the sentinel `"surface"` when
   *  below the body's surface floor (1 km on Kerbin), or `""` above
   *  atmosphere / no atmosphere. */
  currentLayerName: string;
  /** Min altitude (m) recorded during the current layer transit.
   *  0 = no observation captured yet. */
  transitMinAlt: number;
  /** Max altitude (m) recorded during the current layer transit.
   *  0 = no observation captured yet. */
  transitMaxAlt: number;
  /** Player-facing title of the part where the next sealed file will
   *  land. Empty string = no `DataStorage` with capacity on the vessel. */
  destinationStorage: string;
  /** Current vessel body — drives label and the layer table. */
  bodyName: string;
  /** Live vessel altitude in meters. UI clamps to layer bounds. */
  altitude: number;
  /** Live atmospheric temperature at the vessel's position, in Kelvin.
   *  Sourced from the flight integrator — same as the stock readout.
   *  The UI accumulates these per-frame samples client-side to plot
   *  the temperature-vs-altitude profile of the current regime. */
  temperatureK: number;
  /** Bottom→top layer table for `bodyName`. Empty when the body has
   *  no atmosphere. Each layer's effective bottom is the previous
   *  layer's top (or the body's surface floor for index 0). */
  layers: {
    name: string;
    top: number;             // m
  }[];
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
  /** User-controlled enable flag. */
  enabled: boolean;
  /** Min phase (0..1, fraction of body-year) observed during the
   *  current slice while the accumulator was active. 0 = no
   *  observation captured yet. */
  recordedMinPhase: number;
  /** Max phase (0..1) observed during the current slice. 0 = none. */
  recordedMaxPhase: number;
  /** Player-facing title of the part where the next sealed file will
   *  land. Empty string = no `DataStorage` with capacity on the vessel. */
  destinationStorage: string;
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
  /** Internal mix-volume manifold fill, 0..1 (LH₂ + LOx combined).
   *  Production drains the manifold off-LP at the combined reactant
   *  rate; refill tops it up at envelope-friendly mix-L/s. See
   *  `docs/lp_hygiene.md`. */
  manifoldFraction: number;
  /** Refill device hysteresis state: ON below 10%, OFF at 100%. */
  refillActive: boolean;
}

export interface RtgState {
  /** LP-throttled actual flow into the EC bus. Equals `currentPower`
   *  unless consumers can't absorb the full output (rare for RTGs). */
  currentRate: number;
  /** Decay-limited maximum the RTG can produce now —
   *  `referencePower × (1 − stepDrop)^stepIndex(UT)` from the
   *  Pu-238 decay curve. */
  currentPower: number;
  /** Beginning-of-Life design output. Constant for the part's
   *  lifetime; the gauge denominator. */
  referencePower: number;
  /** Predicted output loss over the next Kerbin year, in W. */
  declineWattsPerKerbinYear: number;
  /** Waste heat flowing into the buffer, W. Equals total Pu decay
   *  power minus the electrical fraction the TEG diverts onto wires
   *  (~94% of decay heat for an MMRTG). The thermal subsystem only
   *  ever sees this fraction; the electrical fraction leaves the RTG
   *  on the EC bus and thermalizes wherever a consumer lands it. */
  wasteHeatW: number;
  /** Heat exported to the cooling loop (radiator bus) this solve, W.
   *  LP-throttled — equals min(production, available radiator capacity)
   *  when buffer is empty; can exceed production (drains buffer) when
   *  the loop has spare capacity and the buffer holds stored heat. */
  exportW: number;
  /** Passive heat rejection rate from the device body to environment,
   *  W. Linear-in-temperature approximation; lerps between vacuum and
   *  atm endpoints by static pressure. Cooling loop has priority —
   *  when the loop covers production, buffer drains to empty and
   *  rejection automatically falls to 0. */
  rejectionW: number;
  /** Current device temperature, °C. Implicit ambient = 0 °C; values
   *  represent °C above ambient. */
  currentTempC: number;
  /** Max operating temperature, °C. The buffer caps at this temp; the
   *  device damages above (future work). Gauge denominator. */
  maxOperatingTempC: number;
  /** Rate of change of device temperature, °C/s. Positive = warming;
   *  negative = cooling. Equals (production − cooling − rejection) /
   *  thermalMass. Becomes 0 at thermal equilibrium. */
  dTdtCps: number;
}

export interface RadiatorState {
  /** LP-throttled cooling consumed this solve, W. 0 when bus dry. */
  currentCoolingW: number;
  /** Cooling capacity at current atmospheric pressure, W. Lerps
   *  between vacuum and atm endpoints. */
  maxCoolingW: number;
  /** Deploy state. Folding rads round-trip the player's toggle;
   *  fixed panels are always deployed. */
  isDeployed: boolean;
  /** True iff the radiator can be retracted/extended. Folding rads
   *  expose a toggle; fixed panels don't. */
  isDeployable: boolean;
}

export interface DecouplerState {
  /** Player-set toggle: when true, firing releases every attached
   *  neighbour (children + parent) at once instead of just the
   *  explosive-node side. */
  fullSeparation: boolean;
  /** Capability bit: false for radial decouplers (single attach face)
   *  where the toggle has no meaning. UI greys the control out. */
  canFullSeparate: boolean;
  /** Design-rated ejection impulse, kN. */
  ejectionForce: number;
}

export interface NovaPart {
  id: string;
  resources: NovaResourceFlow[];
  solar: SolarState[];
  battery: BatteryState[];
  wheel: WheelState[];
  light: LightState[];
  tank: TankState[];
  command: CommandState[];
  probe: ProbeState[];
  fuelCell: FuelCellState[];
  rtg: RtgState[];
  radiator: RadiatorState[];
  decoupler: DecouplerState[];
}

// One instrument's decoded science payload. `experimentIds` is the
// derived capability list (slot 1 of each emitted experiment frame).
// `atmExperiment` / `ltsExperiment` are present iff the instrument
// emits that experiment kind.
export interface InstrumentScience {
  name: string;
  experimentIds: string[];
  atmExperiment: AtmExperimentState | undefined;
  ltsExperiment: LtsExperimentState | undefined;
}

// NovaScience/<id> decoded shape. A part may host multiple
// instruments; each gets its own entry in `instruments`.
export interface NovaScience {
  id: string;
  instruments: InstrumentScience[];
}

// NovaStorage/<id> decoded shape — one DataStorage's counters and
// inline file list.
export interface NovaStorage {
  id: string;
  usedBytes: number;
  capacityBytes: number;
  fileCount: number;
  files: ScienceFile[];
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

// Editor-scene parallel of NovaVesselStructureTopic. Single-instance
// (ship id constant `"editor"`) since the VAB/SPH only ever holds one
// ShipConstruct at a time. Wire shape matches NovaVesselStructureFrame
// exactly — `decodeStructure` works against either.
export const NovaEditorShipStructureTopic: Topic<NovaVesselStructureFrame> =
  topic<NovaVesselStructureFrame>('NovaEditorShipStructure/editor');

// Wire payload for setTankCustom: [resourceName, capacity, startingAmount].
// `capacity` is in litres (matches Buffer.Capacity); `startingAmount` in
// the same units, must satisfy 0 ≤ startingAmount ≤ capacity. The mod
// rejects any payload whose summed capacity exceeds the part's
// TankVolume.Volume.
export type TankCustomEntry = [resource: string, capacity: number, contents: number];

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
   * Editor-only. Replace this part's tank loadout with the supplied
   * resource/capacity/contents triples. Rejected outside the editor;
   * also rejected when the summed capacity exceeds the part's
   * `TankVolume.Volume`, when a resource name doesn't resolve, or
   * when contents fall outside [0, capacity].
   */
  setTankCustom(tanks: TankCustomEntry[]): void;

  /**
   * Toggle the debug "test load" on this part's command pod (a
   * fixed-rate EC consumer used to exercise the power system).
   * No-op if the part has no `NovaCommandModule` with a configured
   * `testLoadRate`. The flag is non-persistent — every flight load
   * resets it to false.
   */
  setCommandTestLoad(active: boolean): void;

  /**
   * Extend (`true`) or retract (`false`) a deployable radiator. No-op
   * on fixed panels (`isDeployable=false`). State round-trips on save
   * via `RadiatorState.is_deployed`. No animation in the model — the
   * effect is immediate.
   */
  setRadiatorDeployed(deployed: boolean): void;

  /**
   * Editor-only. Toggle this decoupler's "Full Separation" mode —
   * when on, firing releases every attached neighbour (children +
   * parent) at once instead of just the explosive-node side (stock
   * "separator" semantics). Rejected on radial decouplers
   * (`canFullSeparate=false`); the wire frame surfaces that bit so
   * the UI can grey the control out. State round-trips on save via
   * `DecouplerState.full_separation`.
   */
  setFullSeparation(fullSeparation: boolean): void;
}

export const NovaPartTopic = (partId: string): Topic<NovaPartFrame, NovaPartOps> =>
  topic<NovaPartFrame, NovaPartOps>(`NovaPart/${partId}`);

/**
 * Inbound ops the UI can fire at a NovaScience topic. Keep in sync
 * with `NovaScienceTopic.HandleOp` in mod/Nova/Telemetry — adding a
 * method here without a matching C# case will silently no-op.
 */
export interface NovaScienceOps {
  /**
   * Toggle a specific experiment on or off on the instrument at
   * `instrumentIndex` in the part's instrument list (matches the
   * wire emit order). `experimentId` matches the wire ids
   * ("atm-profile" / "lts"). No-op when the index is out of range
   * or the experiment id is unknown. Rising-edge enable discards
   * any prior in-progress file for the current subject so the
   * fresh observation starts clean (files = unbroken periods).
   */
  setExperimentEnabled(instrumentIndex: number, experimentId: string, enabled: boolean): void;
}

export const NovaScienceTopic = (partId: string): Topic<NovaScienceFrame, NovaScienceOps> =>
  topic<NovaScienceFrame, NovaScienceOps>(`NovaScience/${partId}`);

export const NovaStorageTopic = (partId: string): Topic<NovaStorageFrame> =>
  topic<NovaStorageFrame>(`NovaStorage/${partId}`);

// ---------- Virtual scene ---------------------------------------

// Nova's virtual-scene topic. KSP's real `LoadedScene` (FLIGHT,
// SPACECENTER, EDITOR, …) is unaffected; the virtual scene is a
// UI-side concept the Hud router prefers when non-empty. Today the
// only producer is the R&D building click (sets "RND"); future
// stock-replacement views (Mission Control, Tracking Station, …) plug
// into the same topic by setting their own scene names.
//
// Wire format: [virtualScene: string]
//   "" — no override; Hud falls back to KSP's real scene
//   string — Nova scene the Hud should route to (e.g. "RND")
export type NovaSceneFrame = [virtualScene: string];

export interface NovaSceneState {
  /** Empty string when no virtual scene is active. */
  virtualScene: string;
}

/**
 * Inbound ops the UI can fire at the NovaScene topic. Keep in sync
 * with `NovaSceneTopic.HandleOp` in mod/Nova/Telemetry.
 */
export interface NovaSceneOps {
  /** Set the virtual scene. Pass `""` to clear (i.e. exit the Nova
   *  view back to whatever real KSP scene is loaded). */
  setScene(name: string): void;
}

export const NovaSceneTopic: Topic<NovaSceneFrame, NovaSceneOps> =
  topic<NovaSceneFrame, NovaSceneOps>('NovaScene');

export function decodeNovaScene(f: NovaSceneFrame): NovaSceneState {
  return { virtualScene: f[0] };
}

// ---------- Flight HUD top bar ----------------------------------

// Singleton — KSP timewarp state. UT/MET deliberately omitted; UT
// would push a wire frame per Update() during warp. The HUD top bar
// derives the mission clock from the per-vessel NovaOrbit/<id> topic
// instead.
//
// Wire format: [rate, mode]
//   rate — float, TimeWarp.CurrentRate (1.0 = realtime)
//   mode — "physics" (low/atmospheric warp) or "rails" (high warp)
export type NovaTimewarpFrame = [rate: number, mode: 'physics' | 'rails'];

export interface NovaTimewarp {
  rate: number;
  mode: 'physics' | 'rails';
}

export const NovaTimewarpTopic: Topic<NovaTimewarpFrame> =
  topic<NovaTimewarpFrame>('NovaTimewarp');

export function decodeTimewarp(f: NovaTimewarpFrame): NovaTimewarp {
  return { rate: f[0], mode: f[1] };
}

// Per-vessel — orbit elements + mission-time pair. Bundling
// missionTime/launchTime here saves a third subscription for the HUD
// top bar; both readouts share the same per-frame cadence anyway.
//
// `period` is 0 for sub-orbital / hyperbolic trajectories
// (eccentricity ≥ 1). `apA`/`peA` are 0 only when `vessel.orbit` is
// itself null (rare scene transitions); the UI greys out the orbit
// section in that case rather than displaying zeros.
// `inclination` is in degrees (matches KSP's stock unit).
export type NovaOrbitFrame = [
  vesselId: string,
  bodyName: string,
  apA: number,
  peA: number,
  eccentricity: number,
  inclination: number,
  period: number,
  missionTime: number,
  launchTime: number,
];

export interface NovaOrbit {
  vesselId: string;
  bodyName: string;
  apA: number;
  peA: number;
  eccentricity: number;
  inclination: number;
  period: number;
  missionTime: number;
  launchTime: number;
}

export const NovaOrbitTopic = (vesselId: string): Topic<NovaOrbitFrame> =>
  topic<NovaOrbitFrame>(`NovaOrbit/${vesselId}`);

export function decodeOrbit(f: NovaOrbitFrame): NovaOrbit {
  return {
    vesselId:     f[0],
    bodyName:     f[1],
    apA:          f[2],
    peA:          f[3],
    eccentricity: f[4],
    inclination:  f[5],
    period:       f[6],
    missionTime:  f[7],
    launchTime:   f[8],
  };
}

// Per-vessel — comms summary. `bottleneckBps` is the rate-limiting
// edge along the chosen vessel→KSC max-rate path; 0 when no path
// exists. `directSnr` / `directRateBps` are the direct vessel→KSC
// edge's SNR (linear) and theoretical pre-bottleneck rate; 0 when
// no direct edge exists (vessel reachable only via relay).
// `directMaxRateBps` is the antenna-pair hardware ceiling along
// vessel→KSC; the UI uses `directRateBps / directMaxRateBps` as the
// "fraction of ideal" for the signal-bar gauge so bars saturate
// independent of the absolute units this game runs at. The XMIT
// block (`tx*`) is populated only while a Packet is in flight; the
// UI hides it entirely when `txActive` is false.
export type NovaCommsFrame = [
  vesselId: string,
  hasPathToKsc: 0 | 1,
  bottleneckBps: number,
  directSnr: number,
  directRateBps: number,
  directMaxRateBps: number,
  directSnrFloor: number,
  peerLabel: string,
  txActive: 0 | 1,
  txRateBps: number,
  txDeliveredBytes: number,
  txTotalBytes: number,
];

export interface NovaComms {
  vesselId: string;
  hasPathToKsc: boolean;
  bottleneckBps: number;
  directSnr: number;
  directRateBps: number;
  directMaxRateBps: number;
  directSnrFloor: number;
  peerLabel: string;
  txActive: boolean;
  txRateBps: number;
  txDeliveredBytes: number;
  txTotalBytes: number;
}

export const NovaCommsTopic = (vesselId: string): Topic<NovaCommsFrame> =>
  topic<NovaCommsFrame>(`NovaComms/${vesselId}`);

export function decodeComms(f: NovaCommsFrame): NovaComms {
  return {
    vesselId:         f[0],
    hasPathToKsc:     f[1] === 1,
    bottleneckBps:    f[2],
    directSnr:        f[3],
    directRateBps:    f[4],
    directMaxRateBps: f[5],
    directSnrFloor:   f[6],
    peerLabel:        f[7],
    txActive:         f[8] === 1,
    txRateBps:        f[9],
    txDeliveredBytes: f[10],
    txTotalBytes:     f[11],
  };
}

// ---------- Science archive (R&D scene) -------------------------

// One subject's row in the per-(body, experiment) grid. `slice` is -1
// for variant-only experiments (atm-profile) and 0..slicesPerYear-1 for
// time-sliced experiments (lts). `sourceVessel` is the vessel name
// captured at receive-time (persisted on `ArchivedScienceRecord`); it
// is "" for unstudied gaps. UI never resolves it via runtime lookup.
export type NovaArchiveSubjectFrame = [
  variant: string,
  slice: number,
  fidelity: number,
  receivedAtUt: number,
  sourceVessel: string,
];

export type NovaArchiveBodyExperimentFrame = [
  bodyName: string,
  experimentId: string,
  subjects: NovaArchiveSubjectFrame[],
];

export type NovaArchiveBodySummaryFrame = [
  bodyName: string,
  parentName: string,
  archivedCount: number,
  possibleCount: number,
];

// Wire frame for the singleton NovaScienceArchive topic. Two parallel
// lists: a body summary roster (used by the body-list left rail and
// the solar-system completion strip) and per-(body, experiment)
// subject grids (used by the body-detail right pane).
export type NovaScienceArchiveFrame = [
  bodies: NovaArchiveBodySummaryFrame[],
  grids: NovaArchiveBodyExperimentFrame[],
];

export interface BodySummary {
  /** Body display name as KSP reports it ("Kerbin", "Mun"). */
  bodyName: string;
  /** Orbital parent's name; "" for the Sun. */
  parentName: string;
  /** Subjects with at least one transmitted record at KSC. */
  archivedCount: number;
  /** Universe of subjects this body could yield across every
   *  experiment Nova currently supports. */
  possibleCount: number;
}

export interface ArchiveSubject {
  /** Variant identifier — atm-profile layer name ("troposphere"),
   *  lts situation enum name ("SrfLanded"), etc. */
  variant: string;
  /** -1 for variant-only experiments, otherwise the slice index
   *  within the body-year (0..slicesPerYear-1 for lts). */
  slice: number;
  /** [0,1]. 0 means no archived record. */
  fidelity: number;
  /** UT when the record was received at KSC; 0 for gaps. */
  receivedAtUt: number;
  /** Vessel name persisted on the record at receive-time. "" for
   *  gaps. Persisted in the proto so it survives the source vessel
   *  being renamed, unloaded, or destroyed. */
  sourceVessel: string;
}

export interface NovaScienceArchive {
  /** Body roster in solar-system order (depth-first, matches
   *  `FlightGlobals.Bodies`). */
  bodies: BodySummary[];
  /** Indexed lookup: bodyName → experimentId → subjects[]. Subjects
   *  arrive in the experiment's canonical enumeration order
   *  (atm: bottom→top layers; lts: situations × slices 0..11). */
  subjects: Map<string, Map<string, ArchiveSubject[]>>;
}

export const NovaScienceArchiveTopic: Topic<NovaScienceArchiveFrame> =
  topic<NovaScienceArchiveFrame>('NovaScienceArchive');

function decodeArchiveSubject(f: NovaArchiveSubjectFrame): ArchiveSubject {
  return {
    variant:      f[0],
    slice:        f[1],
    fidelity:     f[2],
    receivedAtUt: f[3],
    sourceVessel: f[4],
  };
}

export function decodeScienceArchive(f: NovaScienceArchiveFrame): NovaScienceArchive {
  const [bodyFrames, gridFrames] = f;
  const bodies: BodySummary[] = bodyFrames.map(([bodyName, parentName, archivedCount, possibleCount]) => ({
    bodyName,
    parentName,
    archivedCount,
    possibleCount,
  }));
  const subjects = new Map<string, Map<string, ArchiveSubject[]>>();
  for (const [bodyName, experimentId, subjFrames] of gridFrames) {
    let perBody = subjects.get(bodyName);
    if (!perBody) {
      perBody = new Map();
      subjects.set(bodyName, perBody);
    }
    perBody.set(experimentId, subjFrames.map(decodeArchiveSubject));
  }
  return { bodies, subjects };
}

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
    tank: [],
    command: [],
    probe: [],
    fuelCell: [],
    rtg: [],
    radiator: [],
    decoupler: [],
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
          ratedRate: c[6],
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
          motorRated: c[5],
          busRated: c[6],
        });
        break;
      case 'L':
        out.light.push({ rate: c[1], ratedRate: c[2] });
        break;
      case 'T':
        out.tank.push({
          volume: c[1],
          slices: c[2].map(([resource, capacity, contents]) => ({
            resource, capacity, contents,
          })),
        });
        break;
      case 'C':
        out.command.push({
          idleRate: c[1],
          testLoadRate: c[2],
          testLoadMaxRate: c[3],
          testLoadActive: c[4] === 1,
          idleRated: c[5],
        });
        break;
      case 'P':
        out.probe.push({
          idleRate: c[1],
          testLoadRate: c[2],
          testLoadMaxRate: c[3],
          testLoadActive: c[4] === 1,
          idleRated: c[5],
          sasLevel: c[6],
          commandBytes: c[7],
          commandCapacityBytes: c[8],
          commandRefillBps: c[9],
          commandDecayBps: c[10],
          commandConsumeBps: c[11],
        });
        break;
      case 'F':
        out.fuelCell.push({
          currentOutput: c[1],
          maxOutput: c[2],
          isActive: c[3] === 1,
          validUntilSec: c[4],
          manifoldFraction: c[5],
          refillActive: c[6] === 1,
        });
        break;
      case 'R':
        out.rtg.push({
          currentRate:               c[1],
          currentPower:              c[2],
          referencePower:            c[3],
          declineWattsPerKerbinYear: c[4],
          wasteHeatW:                c[5],
          exportW:                   c[6],
          rejectionW:                c[7],
          currentTempC:              c[8],
          maxOperatingTempC:         c[9],
          dTdtCps:                   c[10],
        });
        break;
      case 'X':
        out.radiator.push({
          currentCoolingW: c[1],
          maxCoolingW:     c[2],
          isDeployed:      c[3] === 1,
          isDeployable:    c[4] === 1,
        });
        break;
      case 'D':
        out.decoupler.push({
          fullSeparation:  c[1] === 1,
          canFullSeparate: c[2] === 1,
          ejectionForce:   c[3],
        });
        break;
    }
  }
  return out;
}

function decodeFile(f: NovaScienceFileFrame): ScienceFile {
  return {
    subjectId:            f[0],
    experimentId:         f[1],
    fidelity:             f[2],
    producedAt:           f[3],
    instrument:           f[4],
    recordedMinAltM:      f[5],
    recordedMaxAltM:      f[6],
    startUt:              f[7],
    endUt:                f[8],
    sliceDurationSeconds: f[9],
  };
}

function decodeAtm(c: NovaAtmExperimentFrame): AtmExperimentState {
  return {
    experimentId:       c[1],
    active:             c[2] === 1,
    willComplete:       c[3] === 1,
    enabled:            c[4] === 1,
    currentLayerName:   c[5],
    transitMinAlt:      c[6],
    transitMaxAlt:      c[7],
    destinationStorage: c[8],
    bodyName:           c[9],
    altitude:           c[10],
    temperatureK:       c[11],
    layers:             c[12].map(([name, top]) => ({ name, top })),
    savedLocal:         new Map(c[13]),
    savedKsc:           new Map(c[14]),
  };
}

function decodeLts(c: NovaLtsExperimentFrame): LtsExperimentState {
  return {
    experimentId:       c[1],
    active:             c[2] === 1,
    willComplete:       c[3] === 1,
    enabled:            c[4] === 1,
    recordedMinPhase:   c[5],
    recordedMaxPhase:   c[6],
    destinationStorage: c[7],
    bodyName:           c[8],
    situation:          c[9],
    solarParentName:    c[10],
    slicesPerYear:      c[11],
    bodyYearSeconds:    c[12],
    currentSliceIndex:  c[13],
    phase:              c[14],
    activeFidelity:     c[15],
    savedLocal:         new Map(c[16]),
    savedKsc:           new Map(c[17]),
  };
}

function decodeInstrument(entry: NovaInstrumentEntry): InstrumentScience {
  const [name, experiments] = entry;
  let atmExperiment: AtmExperimentState | undefined;
  let ltsExperiment: LtsExperimentState | undefined;
  const experimentIds: string[] = [];
  for (const e of experiments) {
    experimentIds.push(e[1]);
    if (e[0] === 'EXA') atmExperiment = decodeAtm(e as NovaAtmExperimentFrame);
    else if (e[0] === 'EXL') ltsExperiment = decodeLts(e as NovaLtsExperimentFrame);
  }
  return { name, experimentIds, atmExperiment, ltsExperiment };
}

export function decodeScience(f: NovaScienceFrame): NovaScience {
  const [id, instruments] = f;
  return { id, instruments: instruments.map(decodeInstrument) };
}

export function decodeStorage(f: NovaStorageFrame): NovaStorage {
  const [id, usedBytes, capacityBytes, fileCount, files] = f;
  return {
    id,
    usedBytes,
    capacityBytes,
    fileCount,
    files: files.map(decodeFile),
  };
}
