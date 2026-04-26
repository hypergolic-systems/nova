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
  | 'attitude';

// ---------- Wire (frame) types ---------------------------------

export type NovaResourceFrame = [
  resourceId: string,
  rate: number,
  satisfaction: number,
];

// One named tuple per kind so visualization code can import the
// specific frame type it cares about without re-narrowing the union.
export type NovaSolarFrame   = ['S', currentEcRate: number, deployed: 0 | 1, sunlit: 0 | 1, retractable: 0 | 1];
export type NovaBatteryFrame = ['B', soc: number, capacity: number, rate: number];
export type NovaWheelFrame   = ['W', maxEcRate: number, activity: number];
export type NovaLightFrame   = ['L', maxEcRate: number, activity: number];
export type NovaEngineFrame  = ['E', alternatorMaxRate: number, thrustFraction: number];

export type NovaComponentFrame =
  | NovaSolarFrame
  | NovaBatteryFrame
  | NovaWheelFrame
  | NovaLightFrame
  | NovaEngineFrame;

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
  resourceId: string;
  rate: number;
  satisfaction: number;
}

export interface SolarState {
  /** Current EC/s output (already gated on sunlit). */
  rate: number;
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
  maxEcRate: number;
  /** Saturation, 0..1. Multiply by `maxEcRate` for current draw. */
  activity: number;
}

export interface LightState {
  maxEcRate: number;
  activity: number;
}

export interface EngineState {
  alternatorMaxRate: number;
  /** Engine thrust fraction, 0..1. Doubles as alternator activity. */
  thrustFraction: number;
}

export interface NovaPart {
  id: string;
  resources: NovaResourceFlow[];
  solar: SolarState[];
  battery: BatteryState[];
  wheel: WheelState[];
  light: LightState[];
  engine: EngineState[];
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
   * state would retract a non-retractable panel in flight. Symmetry
   * cousins are dispatched by the mod side — fire one op for the
   * representative.
   */
  setSolarDeployed(deployed: boolean): void;
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
    resources: resources.map(([resourceId, rate, satisfaction]) => ({
      resourceId,
      rate,
      satisfaction,
    })),
    solar: [],
    battery: [],
    wheel: [],
    light: [],
    engine: [],
  };
  for (const c of components) {
    switch (c[0]) {
      case 'S':
        out.solar.push({
          rate: c[1],
          deployed: c[2] === 1,
          sunlit: c[3] === 1,
          retractable: c[4] === 1,
        });
        break;
      case 'B':
        out.battery.push({ soc: c[1], capacity: c[2], rate: c[3] });
        break;
      case 'W':
        out.wheel.push({ maxEcRate: c[1], activity: c[2] });
        break;
      case 'L':
        out.light.push({ maxEcRate: c[1], activity: c[2] });
        break;
      case 'E':
        out.engine.push({
          alternatorMaxRate: c[1],
          thrustFraction: c[2],
        });
        break;
    }
  }
  return out;
}
