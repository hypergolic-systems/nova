# LP hygiene — keeping ProcessFlowSystem well-conditioned

Nova's resource flow is solved by two systems (see `PLAN.md` for the architectural rationale):

- **`StagingFlowSystem`** — water-fill on vessel topology. Topological resources (RP-1, LOX, LH₂, Hydrazine, Xenon). Not an LP. No envelope concerns; just arithmetic over the connectivity graph.
- **`ProcessFlowSystem`** — slim LP via OR-Tools GLOP. Uniform resources (ElectricCharge today; O₂, CO₂, H₂O, food, waste, heat tomorrow). This document is about its numerical contract.

GLOP — OR-Tools' simplex implementation — handles the LP, with simplex warm-starting between solves so per-solve cost stays small. The solver runs event-driven (on `Invalidate()`, on buffer/device expiry, on topology rebuild) rather than every physics tick, but a busy frame can fire several solves: each `Solve()` call iterates max-min α-LP rounds across device priorities (Critical → High → Low) plus a final lex-2 cleanup that minimizes Σ supply + Σ fill to suppress LP-cycling artefacts.

The penalty for violating the contract is real and observable: GLOP returns `MPSOLVER_ABNORMAL`, the LP basis is corrupted, simplex cold-starts on subsequent solves, and a complex vessel can chain enough re-solves to spike the physics budget noticeably.

## Working envelope

| Quantity | Range | Where it lives |
| --- | --- | --- |
| **Conservation row coefficients** (rates) | 0.1 – 10 000 | `inputs` / `outputs` arrays passed to `systems.AddDevice(...)` |
| **LP variable bounds** (mostly buffer flow rates) | 0.1 – ~10 000, max 100 000 | `Buffer.MaxRateIn`, `Buffer.MaxRateOut` |
| **Buffer state** (capacity, contents) | unbounded | `Buffer.Capacity`, `Buffer.Contents` — not in LP |
| **Time deltas** (forecasts, valid-until) | unbounded | `Device.ValidUntil` — not in LP |

Strict for the first row; aim-for-it for the second; don't care for the bottom two. These limits apply only to Uniform resources flowing through ProcessFlowSystem — the staging side has no analogous concerns.

## Why these numbers

GLOP's absolute coefficient tolerance is ~10⁻⁶. Per-row scaling at `BuildLP` divides each conservation row by its largest absolute coefficient, so the largest entry in any row is 1.0 after scaling. The smallest entry is `min_rate_in_row / max_rate_in_row`. With both bounded to `[0.1, 10 000]`, the worst within-row spread is 10⁵ — leaving the smallest scaled coefficient at 10⁻⁵, one order of magnitude above GLOP's tolerance floor. Comfortable but not luxurious.

Push the upper end to 100 000 and the smallest scaled coefficient drops to 10⁻⁶, *at* the tolerance floor — risky. Push to 10⁶ and you're below it; ABNORMAL on contact.

Variable bounds matter less than coefficients (they're a one-shot pivot scale, not an in-row condition number), but the simplex's basis numerics still degrade as bounds widen. ~10⁵ is the practical limit.

## Unit convention

EC quantity = Joules. EC rate = Watts. `1 EC = 1 J`, `1 EC/s = 1 W`. `Buffer.Capacity` × `Buffer.Rate` integrates to Joules over time as expected. Resource densities are kg/L; volumetric flows in L/s for fluid resources.

The unit convention is what makes the envelope numerically meaningful: 1.0 in the LP means "1 W" or "1 L/s" in the same physical scale across every component.

## Device construction

Components declare resource flow through `VesselSystems.AddDevice`:

```csharp
device = systems.AddDevice(node,
    inputs:   new[] { (Resource.LiquidHydrogen, lh2Rate),
                      (Resource.LiquidOxygen,   loxRate) },
    outputs:  new[] { (Resource.ElectricCharge, ecOutput) },
    priority: ProcessFlowSystem.Priority.Low);
```

The factory routes the whole device to one solver based on the resources' `Domain`:

- **Topological** inputs (RP-1, LOX, LH₂, Hydrazine, Xenon) → `StagingFlowSystem.Consumer`.
- **Uniform** inputs/outputs (ElectricCharge, future O₂/CO₂/H₂O/heat) → `ProcessFlowSystem.Device`.

Hard rules, validated at factory time:

- **Single domain.** A Device's `Activity` is managed by exactly one solver, so all of its inputs and outputs must share a domain. Mixed-domain flow (e.g. ISRU consuming water and producing O₂/H₂ across the two systems) is modelled with two devices coupled via an Accumulator — the same way FuelCell already bridges Staging refill ↔ Process EC production.
- **Topological resources have no LP-visible producers.** Only tanks (`Buffer` on a Staging node) store them. `outputs` of a Topological resource throws.
- **Inputs/outputs are immutable after construction.** No `AddInput` / `AddOutput` mutators on the Device — declare resources upfront, validate once.

Within the Process side, the rest of this document still applies: keep within-row spread under 10⁵, avoid degenerate column scales, etc.

## Patterns for staying in envelope

When a component's natural rate falls outside [0.1, 10 000], use one of these patterns rather than putting the raw rate in the LP.

### 1. Buffer pattern (timescale decoupling)

Use when a component's consumption is either much faster than the LP cadence (per-frame intensity changes on reaction wheels) or much slower than the envelope's lower bound (fuel cell µL/s reactant flow).

The component owns a small internal `Accumulator`. Consumption is *off-LP*: the component sets `Accumulator.TapRate` (continuous drain rate), which combines with the refill activity in the Accumulator's lerp — `Contents(t) = clamp(BaselineContents + Rate × (t − BaselineUT), 0, Capacity)` where `Rate = RefillActivity·RefillRate − TapRate`. No per-tick integration, just rate updates at solve boundaries. The LP only sees the *refill* flow into the buffer, governed by hysteresis on buffer fill state.

```
main tank   ──refill──►   manifold   ──drain (off-LP)──►   component
                          (small)
                            ▲
                            │
                  hysteresis: refill at <10%
                              stop  at >100%
```

The refill rate is the LP-visible quantity and lives at envelope-friendly scale (~0.1–10 L/s or W). The component-internal consumption can be anything — micro-flows for fuel cells, burst flows for wheel torque commands — and the LP never sees it.

Cost: each buffered component carries persistent buffer state (saved to `PartState`) and small amounts of control logic on the standard `OnPreSolve` / `OnPostSolve` / `OnTickBegin` hooks. The `Accumulator` itself owns the refill `Device` + hysteresis bands, so component code reduces to "set TapRate, read Contents".

Examples in scope:
- **Fuel cells** consume LH₂/LOx at µL/s; refill the mix-manifold at ~0.15 L/s when below threshold (refill is on the *staging* side, but the same pattern applies — buffer between two solver domains).
- **Reaction wheels** burst-consume EC per per-frame attitude command; refill the energy buffer at the rated wattage when below threshold.

Examples that don't need it:
- **Engines** — continuous flow at envelope scale (10–100 L/s). Direct staging consumption is fine.
- **Solar panels** — already aggregated into a single vessel-level device.
- **Lights, command pods** — steady consumers within envelope.

### 2. Bus separation (magnitude decoupling)

Use when components want to participate in the same conservation row at vastly different rates — typically when MW/GW reactor-class content is added alongside W-scale stock parts.

Define a separate `Resource` (e.g. `ElectricCharge_HV`) and a `PowerConverter` component that consumes one and produces the other at ~90-95% efficiency. Each resource has its own conservation row with self-consistent scale.

```
solar (W) ─┐                              ┌─ ion engine (kW–MW)
           ├──► ElectricCharge ◄─converter─► ElectricCharge_HV ─┤
fuel cell ─┘    (LV bus)                    (HV bus)            └─ life support
           ▲                                                  ▲
           └─────────── battery (LV) ──┐  ┌── battery (HV) ───┘
                                       │  │
                                  reactor produces HV
```

The matrix dynamic range stays bounded by the envelope on each row. The converter is the only device touching both rows, contributing one entry per row at *its own* design rate (intermediate scale by construction).

Cost: more resource registry entries; a converter is a real component with mass and efficiency tax. This is gameplay-positive — bus architecture becomes a player decision.

### 3. Battery flow caps

Real batteries have C-rate limits well below capacity-implied flow. We respect the envelope upper bound by formula:

```
MaxRateIn = MaxRateOut = min(C × Capacity / 3600, 10000)
```

with `C` the chosen C-rate (typically 0.5–2). For Z-100 (3.6 MJ) at 1C → 1 000 W (in envelope). For Z-4k (144 MJ) at 1C → 40 000 W (clipped to 10 000). Big packs charge at the envelope limit rather than at their physical max — a defensible simulation choice that matches real DCB rate-limiting for thermal reasons.

## Things to never do

- **Don't put inputs and outputs at vastly different scales on a single `Device`.** GLOP's column scaler can't fix a column whose entries span 10⁷. The single-domain rule already keeps Topological/Uniform from co-existing on one Device, but within a domain you can still construct a Device whose inputs and outputs span far too much: an EC consumer at 1 W *and* a side EC output at 10 MW, say. Split into two devices, or use the buffer pattern to bring everything to one scale.
- **Don't rely on `±∞` for `MaxRateIn`/`MaxRateOut`.** GLOP tolerates them but they make the simplex's pivot numerics worse and can leak into ABNORMAL on adjacent constraints. Pick a finite cap (10× the worst plausible flow is fine).
- **Don't mix component-internal rates into LP coefficients.** If a fuel cell consumes 5×10⁻⁴ L/s reactant per kW of EC, that 5×10⁻⁴ does not belong in any conservation row — that's what the buffer pattern is for.
- **Don't read `Variable.SolutionValue()` after a bound mutation without re-solving.** GLOP invalidates its basis on any `SetBounds` / `SetCoefficient` call; subsequent `SolutionValue()` reads return 0 until the next `Solve()`. ProcessFlowSystem snapshots values into local maps immediately after each successful solve, so callers don't need to worry about this — but anyone touching the priority loop must.

## Diagnostics

When you suspect a numerical problem:

1. **Run the suspect vessel at full physics rate**, watch for `MPSOLVER_ABNORMAL` from GLOP's stderr. It's noisy but unmistakable: `linear_solver.cc:NNNN] No solution exists. MPSolverInterface::result_status_ = MPSOLVER_ABNORMAL`.
2. **Print the conservation matrix** at `BuildLP` time — log each row's max and min absolute coefficient. Anything with a within-row ratio above 10⁵ is a smell.
3. **Check `Device.Activity` after solve.** If it's 0 when `Demand` is positive and the LP should be feasible, the priority-extract path failed — inspect the priority loop's `Solve()` result status.
4. **Check buffer flow bounds.** A buffer with `MaxRateIn = 10` left from the legacy Battery default cripples high-power flow paths silently — the LP solves fine but produces tiny Activities because the buffer can't sink the output.

## Solve flow at a glance

For each device priority `P` (Critical → High → Low):

- **Iterate** `max α + ε·Σ activity` with `activity[d] ≥ α × Demand[d]` for active devices at `P`. The α term is the max-min fairness target; the ε-weighted sum fills slack deterministically (so a supply-blocked device doesn't drag fairness to zero for its peers). Devices at their physical UB get pinned, residual α is re-solved.

After all priorities:

- **Lex-2 cleanup**: with every device pinned, `min Σ supply + Σ fill` collapses any residual feasible cycling (supply = X, fill = X − ε scenarios where the LP basis is otherwise free to invent flow that doesn't change the objective). Without this, the buffer-rate distribution downstream can pick arbitrarily-large supply/fill values that net out to the same Δbuffer but report nonsense rates to telemetry.

## Reference

- `mod/Nova.Core/Systems/ProcessFlowSystem.cs` — LP construction, per-tick reset, priority loop, lex-2 cleanup, buffer-rate distribution.
- `mod/Nova.Core/Systems/StagingFlowSystem.cs` — water-fill solver (no LP envelope concerns).
- `mod/Nova.Core/Systems/VesselSystems.cs` — `AddDevice` factory, same-domain validation, cross-system orchestration.
- `mod/Nova.Core/Systems/Device.cs` — unified Device facade over `Staging.Consumer` / `Process.Device`.
- `mod/Nova.Core/Components/VirtualVessel.cs` — Tick scheduler + generic `OnPreSolve` / `OnPostSolve` / `OnTickBegin` / `OnAdvance` dispatch over components. Vessel-aggregate solar handling lives here too (`UpdateSolarDeviceDemand`).
- `mod/Nova.Core/Components/Accumulator.cs` — lerp-based off-LP cell, owns its own refill `Device` + hysteresis.
- `mod/Nova.Core/Resources/Buffer.cs` — lerp-based LP-visible storage primitive.
- `mod/Nova.Core/Resources/Resource.cs` — unit convention; `ResourceDomain` (Topological / Uniform) tags which solver owns the resource.
- `mod/Nova.Core/Components/Electrical/FuelCell.cs` — worked example of the buffer pattern bridging staging refill ↔ process production.

When the rules in this document conflict with what the LP actually does, fix the LP first and update this document second. The numerical envelope is empirical, not aspirational.
