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
// Per-buffer slice on a TankVolume. The tank carries its own buffers
// on this frame so consumers (e.g. the editor's Tanks view) don't
// have to disambiguate which entries in the part's flat `resources`
// list are tank slices vs. unrelated buffers (Battery's Electric
// Charge, etc.).
//
// Slot 3 is the insulation tier (structural, set in the editor).
// Slot 4 is the runtime cryocooler stage (0=off, 1=stage 1 BAC-class,
// 2=stage 2 ZBO full). Slot 5 is `MaxStage(tier)` — lets the UI shape
// the stage toggle (0 = hide, 1 = on/off, 2 = off/s1/s2) without
// hard-coding tier→stage rules.
// Slot 6 is the realised boiloff fraction per Earth day (pre-blended
// with cooler Activity — never raw LP activity); slots 7/8 are the
// realised cooler EC draw and waste-heat output (W) for stage>0,
// 0 for passive tiers, non-cryo slices, and stage=0.
export type NovaTankSliceFrame = [
  resource: string,
  capacity: number,
  contents: number,
  tier: number,
  stage: number,
  maxStage: number,
  boiloffFractionPerDay: number,
  coolerEcW: number,
  coolerHeatW: number,
];
export type NovaTankFrame     = ['T', volume: number, slices: NovaTankSliceFrame[]];

// Per-slice insulation tier. Int values match
// `Nova.Core.Persistence.Protos.InsulationTier` exactly.
export enum InsulationTier {
  MLI       = 0,
  HeavyMLI  = 1,
  BAC       = 2,
  ZBO       = 3,
}
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
  currentEcW: number,
  maxEcW: number,
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

// LV-N NTR reactor. `state` maps to ReactorState enum below; `throttleSetpoint`
// is what the slew is currently chasing (player input, gated by MinThrottle,
// or 0 when shutdown is queued). `currentThrustKn` is reactor-state-gated
// (zero outside Throttled) — same wire-physical-observables rule as other
// kinds. `lh2FlowKgs` is the actual delivered LH₂ mass flow last solve.
export type NovaNuclearFrame = [
  'N',
  state: number,
  coreTempK: number,
  throttleActual: number,
  throttleSetpoint: number,
  currentThrustKn: number,
  maxThrustKn: number,
  lh2FlowKgs: number,
  thermalPowerW: number,
  shutdownRequested: 0 | 1,
];

// Mirrors Nova.Core.Components.Propulsion.ReactorState. Order matters —
// the wire value is the int cast of the C# enum.
export enum ReactorState {
  Cold      = 0,
  Warming   = 1,
  Idle      = 2,
  Throttled = 3,
  Cooling   = 4,
}

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
  | NovaDecouplerFrame
  | NovaNuclearFrame;

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
  /** Insulation / cooling tier for this slice. MLI is the design
   *  baseline (no volume penalty); HeavyMLI / BAC pay a surface-area
   *  penalty; ZBO additionally pays a cold-finger penalty. */
  tier: InsulationTier;
  /** Runtime cryocooler stage. 0 = off (passive insulation only);
   *  1 = stage 1 (BAC-class single-stage cooler — applies on BAC + ZBO
   *  tiers, behaves identically on either); 2 = stage 2 (ZBO full
   *  deep cold-finger). */
  stage: number;
  /** Maximum stage the tier supports. 0 for passive tiers (no toggle),
   *  1 for BAC (on/off), 2 for ZBO (off/s1/s2). Drives the toggle's
   *  shape in the UI without hard-coding tier→stage rules. */
  maxStage: number;
  /** Realised boiloff fraction per Earth day at the current cooler
   *  Activity — 0 for non-cryogenic resources and for ZBO at full
   *  supply, larger when an active stage degrades to its passive
   *  fallback (EC starvation or saturated heat bus). */
  boiloffFractionPerDay: number;
  /** Live EC draw (W) of this slice's cryocooler — 0 for passive
   *  tiers, stage=0, and non-cryogenic resources. */
  coolerEcW: number;
  /** Live waste heat (W) emitted to the vessel heat bus by this
   *  slice's cryocooler. 0 for stage=0 / passive. Radiators have to
   *  reject this; if they can't, LP drops the cooler's Activity and
   *  boiloffFractionPerDay rises accordingly. */
  coolerHeatW: number;
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
  /** Live EC draw, W. 0 for passive panels (no pumps); >0 for
   *  folding rads that run a working fluid loop. */
  currentEcW: number;
  /** Design EC draw at full cooling, W. Editor view uses this when
   *  the LP isn't running. */
  maxEcW: number;
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

export interface NuclearReactorState {
  /** Cold / Warming / Idle / Throttled / Cooling. Drives the on/off
   *  button label and panel chrome. */
  state: ReactorState;
  /** Live core temperature, K. */
  coreTempK: number;
  /** Slewed reactor throttle, 0..1. Lags `throttleSetpoint` by up to
   *  10 s at SlewRatePerSec = 0.1/s. */
  throttleActual: number;
  /** What the slew is chasing — typically `vessel.mainThrottle` when
   *  in Throttled and 0 otherwise. */
  throttleSetpoint: number;
  /** Reactor-state-gated thrust output (kN). Zero outside Throttled. */
  currentThrustKn: number;
  /** Rated thrust, kN. */
  maxThrustKn: number;
  /** LH₂ mass flow last solve, kg/s. Non-zero in Warming/Idle/Throttled
   *  (idle cooling), zero in Cold/Cooling. */
  lh2FlowKgs: number;
  /** Fission heat being produced this tick (W). Computed C#-side from
   *  the state + slewed throttle, so the cfg's IdlePowerW / MaxPowerW
   *  calibration stays the single source of truth. */
  thermalPowerW: number;
  /** Latched flag: a SetReactorActive(false) op while in Throttled
   *  arms this; the state machine auto-sequences down to Cooling once
   *  the slew lands at 0. */
  shutdownRequested: boolean;
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
  nuclear: NuclearReactorState[];
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
   * when contents fall outside [0, capacity]. Every slice's insulation
   * tier is reset to MLI as part of the swap — follow with
   * `setTankInsulation` if the preset wants HeavyMLI / BAC / ZBO.
   */
  setTankCustom(tanks: TankCustomEntry[]): void;

  /**
   * Editor-only. Replace this tank's per-slice insulation tier vector,
   * one entry per existing slice in slice order. Rejected when the
   * length mismatches the slice count, an entry is out of range, or
   * the resulting Σ capacity × (1 + tierVolumePenalty) exceeds the
   * part's `TankVolume.Volume`. Tiers persist via `TankStructure.insulation`.
   * Resets every slice's `stage` to 0 (cooler off) — the UI follows
   * with `setTankCooler` to restore the desired runtime state.
   */
  setTankInsulation(tiers: InsulationTier[]): void;

  /**
   * Replace the per-slice runtime cryocooler-stage vector. Each entry
   * is 0 (off), 1 (stage 1, BAC-class — valid on BAC and ZBO tiers),
   * or 2 (stage 2, full ZBO — valid only on ZBO). Rejected when the
   * length doesn't match the slice count or any entry is out of range
   * for its slice's tier (e.g. stage=2 on a BAC slice).
   * In flight the mod invalidates the vessel after a successful change
   * so the LP rebuilds with the new cooler max-rates.
   */
  setTankCooler(stages: number[]): void;

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

  /**
   * Flight-only. Toggle a nuclear engine's reactor.
   *
   * `true` from Cold begins the warmup sequence; `false` from Idle
   * begins cooldown; `false` from Throttled latches `shutdownRequested`
   * so the state machine auto-sequences down to Cooling once the
   * throttle slews to 0. No-op on parts without a NovaNuclearEngineModule
   * and on already-terminal states (Cooling, Cold while requesting off).
   */
  setReactorActive(active: boolean): void;

  /**
   * Flight-only intent — set the reactor's player-throttle setpoint
   * (0..1). In real flight the in-game NovaNuclearEngineModule
   * overwrites PlayerThrottle every FixedUpdate from the vessel's
   * mainThrottle (keyboard input), so a UI op here is short-lived;
   * the sim's headless runner has no FixedUpdate, so the value
   * latches and the reactor's slew chases it. The UI uses this for
   * the draggable TGT marker.
   */
  setReactorPlayerThrottle(throttle: number): void;
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

// ---------- Editor part-info popup ------------------------------

// Singleton hover-state topic emitted by `NovaPartInfoTopic` (C# side).
// One frame per hover event: an empty array clears the popup, a populated
// frame opens it at the given screen anchor with the part's static design
// spec.
//
// Distinct schema from `NovaPartFrame`. Same single-char kind prefix per
// component family (E/T/B/S/R/...) because the kind is a property of the
// component itself, but each frame here carries *design* values
// (thrust, capacity, EC draw at full intensity) — the runtime/LP fields
// the per-part topic carries are meaningless before the part is placed.
//
// The icon rect (`iconX/Y/W/H`) is the parts-list icon's screen-space
// bounding box in browser pixels (C# converts from Unity's Y-up).
//
// `propellants` list (Engine / NuclearEngine / Rcs / FuelCell): each
// entry is `[resourceName, volumeRatio]`; ratios are author-side volumes,
// not mass.
export type NovaInfoEngineFrame      = ['E', engineClass: string, thrustKn: number, ispS: number, gimbalDeg: number, propellants: [resource: string, ratio: number][]];
export type NovaInfoNuclearFrame     = ['N', thrustKn: number, ispS: number, idleTempK: number, opTempK: number, idlePowerW: number, maxPowerW: number, warmupSec: number, slewPerSec: number, propellants: [resource: string, ratio: number][]];
export type NovaInfoRcsFrame         = ['M', thrusterPowerKn: number, thrusterCount: number, ispS: number, propellants: [resource: string, ratio: number][]];
export type NovaInfoTankFrame        = ['T', volumeL: number, maxRateLps: number, slices: [resource: string, capacityL: number, tier: number][]];
export type NovaInfoBatteryFrame     = ['B', capacityJ: number, maxRateW: number];
export type NovaInfoFuelCellFrame    = ['F', maxOutputW: number, lh2RateKgs: number, loxRateKgs: number];
export type NovaInfoSolarFrame       = ['S', chargeRateW: number, isTracking: 0 | 1, isDeployable: 0 | 1];
export type NovaInfoRtgFrame         = ['R', referencePowerW: number, halfLifeDays: number, thermalOutputW: number, maxOpTempC: number, vacuumRejectionW: number, atmRejectionW: number];
export type NovaInfoWheelFrame       = ['W', pitchTorqueKnm: number, yawTorqueKnm: number, rollTorqueKnm: number, electricRateW: number];
export type NovaInfoRadiatorFrame    = ['X', vacuumCoolingW: number, atmCoolingW: number, ecPerWattCooling: number, isDeployable: 0 | 1];
export type NovaInfoLightFrame       = ['L', drawW: number];
export type NovaInfoCommandFrame     = ['C', idleDrawW: number, testLoadRateW: number];
export type NovaInfoProbeFrame       = ['P', idleDrawW: number, testLoadRateW: number, sasLevel: number, commandCapBytes: number, commandDecayBps: number, commandReceiveBps: number, inputCostBps: number];
export type NovaInfoAntennaFrame     = ['A', txPowerW: number, gain: number, maxRateBps: number, refDistanceM: number];
export type NovaInfoDecouplerFrame   = ['D', ejectionForceKn: number, canFullSeparate: 0 | 1, allowedResources: string[]];
export type NovaInfoDockingFrame     = ['K', nodeType: string];
export type NovaInfoCrewFrame        = ['Y', crewCapacity: number];
export type NovaInfoStorageFrame     = ['Z', capacityBytes: number];
export type NovaInfoThermometerFrame = ['H', instrumentName: string, ecRateW: number];

export type NovaInfoComponentFrame =
  | NovaInfoEngineFrame
  | NovaInfoNuclearFrame
  | NovaInfoRcsFrame
  | NovaInfoTankFrame
  | NovaInfoBatteryFrame
  | NovaInfoFuelCellFrame
  | NovaInfoSolarFrame
  | NovaInfoRtgFrame
  | NovaInfoWheelFrame
  | NovaInfoRadiatorFrame
  | NovaInfoLightFrame
  | NovaInfoCommandFrame
  | NovaInfoProbeFrame
  | NovaInfoAntennaFrame
  | NovaInfoDecouplerFrame
  | NovaInfoDockingFrame
  | NovaInfoCrewFrame
  | NovaInfoStorageFrame
  | NovaInfoThermometerFrame;

// Wire frame for the singleton NovaPartInfo topic. Empty array = nothing
// hovered; populated frame = open against the parts catalog's right
// edge. The icon rect (`iconX/Y/W/H`) gives the icon's screen-space
// bounding box; the catalog rect (`catalogLeftX/RightX`) bounds the
// outer parts catalog panel in the same coord space. The UI anchors the
// popup flush against `catalogRightX`, flipping to `catalogLeftX -
// popupWidth` if the right side would clip the viewport. `isPinned`
// is the sticky flag toggled by right-click in C#; the UI mirrors it as
// a pin glyph in the title bar but has no other state effect.
export type NovaPartInfoFrame =
  | []
  | [
      internalName: string,
      displayTitle: string,
      manufacturer: string,
      description: string,
      dryMassKg: number,
      costFunds: number,
      iconX: number,
      iconY: number,
      iconW: number,
      iconH: number,
      catalogLeftX: number,
      catalogRightX: number,
      isPinned: 0 | 1,
      components: NovaInfoComponentFrame[],
    ];

// Decoded forms — clean named objects per kind. Same translation
// boundary the rest of nova-topics.ts uses (`decodePart`, etc.).

export interface PropellantSpec {
  resource: string;
  /** Volume ratio as authored in the cfg. Engine code derives mass flow
   *  from thrust + Isp; the ratio just controls per-resource shares of
   *  that flow. */
  ratio: number;
}

export interface EngineSpec {
  /** Cfg-declared label (Booster / Sustainer / Vacuum / Ionic / ...).
   *  Surfaced in the part-info marquee as the class line; the value is
   *  whatever the engine cfg supplied — the UI does not infer. */
  class: string;
  thrustKn: number;
  ispS: number;
  /** 0 for non-gimbaling engines (UI hides the row). */
  gimbalDeg: number;
  propellants: PropellantSpec[];
}

export interface NuclearSpec extends Omit<EngineSpec, 'class'> {
  /** Steady-state idle core temperature (K) when reactor is on but
   *  throttle is 0. */
  idleTempK: number;
  /** Steady-state operating core temperature (K) at full throttle. */
  opTempK: number;
  /** Fission power at idle, W. */
  idlePowerW: number;
  /** Fission power at full throttle, W. */
  maxPowerW: number;
  /** Cold→Idle warmup duration, seconds. */
  warmupSec: number;
  /** Throttle slew rate per second (0..1 units / s). */
  slewPerSec: number;
}

export interface RcsSpec {
  /** kN per nozzle. Total thrust = thrusterPowerKn × thrusterCount. */
  thrusterPowerKn: number;
  thrusterCount: number;
  ispS: number;
  propellants: PropellantSpec[];
}

export interface TankSpec {
  /** Geometric envelope, litres. Sum of slice capacities ≤ this
   *  (insulation tiers can take a slice of the budget). */
  volumeL: number;
  /** Part-level pipe ceiling, litres per second (in or out). */
  maxRateLps: number;
  slices: { resource: string; capacityL: number; tier: InsulationTier }[];
}

export interface BatterySpec {
  /** Joules. 1 EC = 1 J in Nova. */
  capacityJ: number;
  /** Charge/discharge rate ceiling, W. */
  maxRateW: number;
}

export interface FuelCellSpec {
  maxOutputW: number;
  /** LH₂ consumption at full output, kg/s. */
  lh2RateKgs: number;
  /** LOX consumption at full output, kg/s. */
  loxRateKgs: number;
}

export interface SolarSpec {
  /** Optimal W at 1 AU full sun, normal-incidence. */
  chargeRateW: number;
  isTracking: boolean;
  isDeployable: boolean;
}

export interface RtgSpec {
  /** Electrical W at beginning-of-life. */
  referencePowerW: number;
  /** Isotope half-life in days (Pu-238 ≈ 32 032 d). */
  halfLifeDays: number;
  /** Waste heat at BoL, W. */
  thermalOutputW: number;
  /** Upper safe device temperature, °C. */
  maxOpTempC: number;
  /** Passive heat rejection ceiling in vacuum, W. */
  vacuumRejectionW: number;
  /** Passive heat rejection ceiling at 1 atm, W. */
  atmRejectionW: number;
}

export interface WheelSpec {
  pitchTorqueKnm: number;
  yawTorqueKnm: number;
  rollTorqueKnm: number;
  /** EC draw per unit intensity (intensity ∈ [0, 3]), W. */
  electricRateW: number;
}

export interface RadiatorSpec {
  /** Cooling capacity in vacuum, W. */
  vacuumCoolingW: number;
  /** Cooling capacity at 1 atm, W (folding rads get an atm bonus). */
  atmCoolingW: number;
  /** Pump cost: W per W of cooling. 0 for passive panels. */
  ecPerWattCooling: number;
  isDeployable: boolean;
}

export interface LightSpec {
  drawW: number;
}

export interface CommandSpec {
  /** Always-on avionics draw, W. */
  idleDrawW: number;
  /** Configured ceiling for the debug test load, W. 0 = no test load. */
  testLoadRateW: number;
}

export interface ProbeSpec {
  idleDrawW: number;
  testLoadRateW: number;
  /** Stock SAS service level, 0..3. */
  sasLevel: number;
  /** Command ledger maximum, bytes. */
  commandCapBytes: number;
  /** Continuous decay drain, B/s. */
  commandDecayBps: number;
  /** Refill ceiling from a healthy KSC link, B/s. */
  commandReceiveBps: number;
  /** B/s burned per unit input magnitude (full-stick attitude, etc.). */
  inputCostBps: number;
}

export interface AntennaSpec {
  txPowerW: number;
  /** Antenna gain (dimensionless, ≥1). */
  gain: number;
  /** Hardware data-rate ceiling, b/s. */
  maxRateBps: number;
  /** Design distance the antenna pair achieves `maxRateBps` at, m. */
  refDistanceM: number;
}

export interface DecouplerSpec {
  ejectionForceKn: number;
  /** False for radial decouplers (only one face — toggle has no effect). */
  canFullSeparate: boolean;
  /** Canonical resource names that cross this decoupler. */
  allowedResources: string[];
}

export interface DockingSpec {
  /** Stock-style `nodeType` string (e.g. "size0", "size1", "size2"). Two
   *  ports only dock together when their node types match. */
  nodeType: string;
}

export interface CrewSpec {
  crewCapacity: number;
}

export interface StorageSpec {
  capacityBytes: number;
}

export interface ThermometerSpec {
  instrumentName: string;
  /** EC draw while the instrument is sampling, W. */
  ecRateW: number;
}

export interface NovaPartInfo {
  /** KSP internal name, e.g. "fuelTankS3-7200". */
  internalName: string;
  /** Player-facing title. */
  title: string;
  manufacturer: string;
  description: string;
  /** Dry mass in kilograms; UI formats kg / t at render time. */
  dryMassKg: number;
  /** Career-mode funds price; sandbox still surfaces it as a relative
   *  cost. */
  costFunds: number;
  /** Part icon's screen-space rect in browser coords (top-down Y).
   *  Used for the vertical anchor and as a fallback if the catalog
   *  rect is unavailable. */
  iconX: number;
  iconY: number;
  iconW: number;
  iconH: number;
  /** Parts catalog panel's left/right edges in the same coord space.
   *  The popup anchors flush against `catalogRightX` (with a small gap)
   *  and flips to `catalogLeftX - popupWidth` if the right side would
   *  clip the viewport — so neighbouring icons stay visible while the
   *  popup is open. */
  catalogLeftX: number;
  catalogRightX: number;
  /** Sticky flag toggled by right-clicking the part icon (in C#). The
   *  UI mirrors it as a pin glyph in the title bar; no other UI-side
   *  effect — the C# side owns the close decision. */
  isPinned: boolean;

  engine:       EngineSpec[];
  nuclear:      NuclearSpec[];
  rcs:          RcsSpec[];
  tank:         TankSpec[];
  battery:      BatterySpec[];
  fuelCell:     FuelCellSpec[];
  solar:        SolarSpec[];
  rtg:          RtgSpec[];
  wheel:        WheelSpec[];
  radiator:     RadiatorSpec[];
  light:        LightSpec[];
  command:      CommandSpec[];
  probe:        ProbeSpec[];
  antenna:      AntennaSpec[];
  decoupler:    DecouplerSpec[];
  docking:      DockingSpec[];
  crew:         CrewSpec[];
  storage:      StorageSpec[];
  thermometer:  ThermometerSpec[];
}

// The HUD is purely driven by this topic — no ops cross back. The mod
// decides when to clear the topic state (see `NovaPartInfoCloser`
// C#-side, which polls the cursor against the parts catalog rect and
// Dragonglass's CEF-UI raycast filter).
export const NovaPartInfoTopic: Topic<NovaPartInfoFrame> =
  topic<NovaPartInfoFrame>('NovaPartInfo');

export function decodePartInfo(f: NovaPartInfoFrame): NovaPartInfo | null {
  if (f.length === 0) return null;
  const [internalName, title, manufacturer, description,
         dryMassKg, costFunds,
         iconX, iconY, iconW, iconH,
         catalogLeftX, catalogRightX, isPinned,
         components] = f;
  const out: NovaPartInfo = {
    internalName, title, manufacturer, description,
    dryMassKg, costFunds,
    iconX, iconY, iconW, iconH,
    catalogLeftX, catalogRightX, isPinned: isPinned === 1,
    engine: [], nuclear: [], rcs: [], tank: [], battery: [],
    fuelCell: [], solar: [], rtg: [], wheel: [], radiator: [],
    light: [], command: [], probe: [], antenna: [], decoupler: [],
    docking: [], crew: [], storage: [], thermometer: [],
  };
  const decodePropellants = (raw: [string, number][]): PropellantSpec[] =>
    raw.map(([resource, ratio]) => ({ resource, ratio }));
  for (const c of components) {
    switch (c[0]) {
      case 'E':
        out.engine.push({
          class: c[1],
          thrustKn: c[2], ispS: c[3], gimbalDeg: c[4],
          propellants: decodePropellants(c[5]),
        });
        break;
      case 'N':
        out.nuclear.push({
          thrustKn: c[1], ispS: c[2],
          idleTempK: c[3], opTempK: c[4],
          idlePowerW: c[5], maxPowerW: c[6],
          warmupSec: c[7], slewPerSec: c[8],
          gimbalDeg: 0,
          propellants: decodePropellants(c[9]),
        });
        break;
      case 'M':
        out.rcs.push({
          thrusterPowerKn: c[1], thrusterCount: c[2], ispS: c[3],
          propellants: decodePropellants(c[4]),
        });
        break;
      case 'T':
        out.tank.push({
          volumeL: c[1], maxRateLps: c[2],
          slices: c[3].map(([resource, capacityL, tier]) => ({
            resource, capacityL, tier: tier as InsulationTier,
          })),
        });
        break;
      case 'B':
        out.battery.push({ capacityJ: c[1], maxRateW: c[2] });
        break;
      case 'F':
        out.fuelCell.push({
          maxOutputW: c[1], lh2RateKgs: c[2], loxRateKgs: c[3],
        });
        break;
      case 'S':
        out.solar.push({
          chargeRateW: c[1],
          isTracking: c[2] === 1, isDeployable: c[3] === 1,
        });
        break;
      case 'R':
        out.rtg.push({
          referencePowerW: c[1], halfLifeDays: c[2],
          thermalOutputW: c[3], maxOpTempC: c[4],
          vacuumRejectionW: c[5], atmRejectionW: c[6],
        });
        break;
      case 'W':
        out.wheel.push({
          pitchTorqueKnm: c[1], yawTorqueKnm: c[2], rollTorqueKnm: c[3],
          electricRateW: c[4],
        });
        break;
      case 'X':
        out.radiator.push({
          vacuumCoolingW: c[1], atmCoolingW: c[2],
          ecPerWattCooling: c[3], isDeployable: c[4] === 1,
        });
        break;
      case 'L':
        out.light.push({ drawW: c[1] });
        break;
      case 'C':
        out.command.push({
          idleDrawW: c[1], testLoadRateW: c[2],
        });
        break;
      case 'P':
        out.probe.push({
          idleDrawW: c[1], testLoadRateW: c[2], sasLevel: c[3],
          commandCapBytes: c[4], commandDecayBps: c[5],
          commandReceiveBps: c[6], inputCostBps: c[7],
        });
        break;
      case 'A':
        out.antenna.push({
          txPowerW: c[1], gain: c[2],
          maxRateBps: c[3], refDistanceM: c[4],
        });
        break;
      case 'D':
        out.decoupler.push({
          ejectionForceKn: c[1], canFullSeparate: c[2] === 1,
          allowedResources: c[3],
        });
        break;
      case 'K':
        out.docking.push({ nodeType: c[1] });
        break;
      case 'Y':
        out.crew.push({ crewCapacity: c[1] });
        break;
      case 'Z':
        out.storage.push({ capacityBytes: c[1] });
        break;
      case 'H':
        out.thermometer.push({ instrumentName: c[1], ecRateW: c[2] });
        break;
    }
  }
  return out;
}

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
    parts: parts.map(([id, partName, partTitle, parentId]) => ({
      id,
      name: partName,
      title: shortenPartTitle(partTitle),
      parentId,
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
    nuclear: [],
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
          slices: c[2].map(([
            resource, capacity, contents,
            tier, stage, maxStage,
            boiloffFractionPerDay, coolerEcW, coolerHeatW,
          ]) => ({
            resource, capacity, contents,
            tier: tier as InsulationTier,
            stage, maxStage,
            boiloffFractionPerDay,
            coolerEcW, coolerHeatW,
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
          currentEcW:      c[5],
          maxEcW:          c[6],
        });
        break;
      case 'D':
        out.decoupler.push({
          fullSeparation:  c[1] === 1,
          canFullSeparate: c[2] === 1,
          ejectionForce:   c[3],
        });
        break;
      case 'N':
        out.nuclear.push({
          state:             c[1] as ReactorState,
          coreTempK:         c[2],
          throttleActual:    c[3],
          throttleSetpoint:  c[4],
          currentThrustKn:   c[5],
          maxThrustKn:       c[6],
          lh2FlowKgs:        c[7],
          thermalPowerW:     c[8],
          shutdownRequested: c[9] === 1,
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
