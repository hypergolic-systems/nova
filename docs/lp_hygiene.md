# LP hygiene — keeping the resource solver well-conditioned

Nova's resource flow is solved as a linear program (`Nova.Core/Resources/ResourceSolver.cs`). GLOP — OR-Tools' simplex implementation — handles the LP, with simplex warm-starting between solves so per-solve cost stays small. The solver runs event-driven (on `Invalidate()`, on buffer/device expiry, on topology rebuild) rather than every physics tick, but a busy frame can fire several solves: the new architecture is an **iterative max-min α/β-LP** that does multiple LP solves per `Solve()` call (Phase A device fairness + Phase B per-drain-priority pool fairness, each with bottleneck-pinning iteration). This document defines the numerical contract Nova components must respect to keep GLOP in its working envelope.

The penalty for violating the contract is real and observable: GLOP returns `MPSOLVER_ABNORMAL`, the LP basis is corrupted, simplex cold-starts on subsequent solves, and a complex vessel can chain enough re-solves to spike the physics budget noticeably.

## Working envelope

| Quantity | Range | Where it lives |
| --- | --- | --- |
| **Conservation row coefficients** (rates) | 0.1 – 10 000 | `device.AddInput(...)`, `device.AddOutput(...)` |
| **LP variable bounds** (mostly buffer flow rates) | 0.1 – ~10 000, max 100 000 | `Buffer.MaxRateIn`, `Buffer.MaxRateOut` |
| **Buffer state** (capacity, contents) | unbounded | `Buffer.Capacity`, `Buffer.Contents` — not in LP |
| **Time deltas** (forecasts, valid-until) | unbounded | `Device.ValidUntil`, `ValidUntilSeconds` — not in LP |

Strict for the first row; aim-for-it for the second; don't care for the bottom two.

## Why these numbers

GLOP's absolute coefficient tolerance is ~10⁻⁶. Per-row scaling at `BuildLP` divides each conservation row by its largest absolute coefficient, so the largest entry in any row is 1.0 after scaling. The smallest entry is `min_rate_in_row / max_rate_in_row`. With both bounded to `[0.1, 10 000]`, the worst within-row spread is 10⁵ — leaving the smallest scaled coefficient at 10⁻⁵, one order of magnitude above GLOP's tolerance floor. Comfortable but not luxurious.

Push the upper end to 100 000 and the smallest scaled coefficient drops to 10⁻⁶, *at* the tolerance floor — risky. Push to 10⁶ and you're below it; ABNORMAL on contact.

Variable bounds matter less than coefficients (they're a one-shot pivot scale, not an in-row condition number), but the simplex's basis numerics still degrade as bounds widen. ~10⁵ is the practical limit.

## Unit convention

EC quantity = Joules. EC rate = Watts. `1 EC = 1 J`, `1 EC/s = 1 W`. `Buffer.Capacity` × `Buffer.Rate` integrates to Joules over time as expected. Resource densities are kg/L; volumetric flows in L/s for fluid resources.

The unit convention is what makes the envelope numerically meaningful: 1.0 in the LP means "1 W" or "1 L/s" in the same physical scale across every component.

## Patterns for staying in envelope

When a component's natural rate falls outside [0.1, 10 000], use one of these patterns rather than putting the raw rate in the LP.

### 1. Buffer pattern (timescale decoupling)

Use when a component's consumption is either much faster than the LP cadence (RCS pulsing per physics step) or much slower than the envelope's lower bound (fuel cell µL/s reactant flow).

The component owns a small internal buffer. Consumption against the buffer happens *outside* the LP, debiting `Buffer.Contents` directly each tick. The LP only sees the *refill* flow from the main tank to the buffer, governed by hysteresis on buffer fill state.

```
main tank   ──refill──►   manifold   ──drain (off-LP)──►   component
                          (small)
                            ▲
                            │
                  hysteresis: refill at <50%
                              stop  at >95%
```

The refill rate is the LP-visible quantity and lives at envelope-friendly scale (~0.1–10 L/s). The component-internal consumption can be anything — micro-flows for fuel cells, burst flows for RCS — and the LP never sees it.

Cost: each buffered component carries persistent buffer state (saved to `PartState`) and per-tick control logic.

Examples in scope:
- **Fuel cells** consume LH₂/LOx at µL/s; refill manifold at 0.1 L/s when below threshold.
- **RCS thrusters** burst-consume Hydrazine per physics tick; refill manifold at ~1 L/s averaged over duty cycle.

Examples that don't need it:
- **Engines** — continuous flow at envelope scale (10–100 L/s). Direct LP consumption is fine.
- **Solar panels** — already aggregated into a single vessel-level device.
- **Lights, reaction wheels** — steady consumers within envelope.

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

- **Don't put inputs and outputs at vastly different scales on a single hybrid `Device`.** GLOP's column scaler can't fix a column whose entries span 10⁷. Split into input + output devices linked by a parent constraint (and accept the asymmetry — see `FuelCell.cs` for the worked example) or use the buffer pattern to bring everything to one scale.
- **Don't rely on `±∞` for `MaxRateIn`/`MaxRateOut`.** GLOP tolerates them but they make the simplex's pivot numerics worse and can leak into ABNORMAL on adjacent constraints. Pick a finite cap (10× the worst plausible flow is fine).
- **Don't mix component-internal rates into LP coefficients.** If a fuel cell consumes 5×10⁻⁴ L/s reactant per kW of EC, that 5×10⁻⁴ does not belong in any conservation row — that's what the buffer pattern is for.
- **Don't read `Variable.SolutionValue()` after a bound mutation without re-solving.** GLOP invalidates its basis on any `SetBounds` / `SetCoefficient` call; subsequent `SolutionValue()` reads return 0 until the next `Solve()`. The solver works around this by snapshotting values into local maps immediately after each successful solve and calling `ExtractResults` (which writes through to `Device.Activity` / `Buffer.Rate`) *before* any pinning mutation. Component code that drives the solver shouldn't need to worry about this, but anyone touching the Phase A / Phase B iteration loop must.
- **Don't put unnormalized `Buffer.Contents` into LP coefficients.** The fairness phase puts `amount / maxAmount_r` (per-resource normalized, clamped at `FairnessFloor = 0.1`) into the pool-α constraint coefficient — keeping it in `[0.1, 1]` regardless of pool magnitude. Without normalization, a 144 MJ battery against a litre-scale tank would yield coefficients around 1e-8, well below GLOP's tolerance. The clamp costs the very smallest pools a slightly looser fair share, but nothing breaks the envelope.

## Diagnostics

When you suspect a numerical problem:

1. **Run the suspect vessel at full physics rate**, watch for `MPSOLVER_ABNORMAL` from GLOP's stderr. It's noisy but unmistakable: `linear_solver.cc:NNNN] No solution exists. MPSolverInterface::result_status_ = MPSOLVER_ABNORMAL`.
2. **Print the conservation matrix** at `BuildLP` time — log each row's max and min absolute coefficient. Anything with a within-row ratio above 10⁵ is a smell. After per-row scaling, re-log.
3. **Check `Device.Activity` after solve.** If it's 0 when Demand is positive and the LP should be feasible, the priority-extract path failed — inspect the priority loop's `Solve()` result status.
4. **Check buffer flow bounds.** A buffer with `MaxRateIn = 10` left from the legacy Battery default cripples high-power flow paths silently — the LP solves fine but produces tiny Activities because the buffer can't sink the output.

## Solve flow at a glance

For each device priority `P` (Critical → High → Low):

1. **Phase A** — `max α + ε·Σ activity` with `activity[d] ≥ α × Demand[d]` for active devices at `P`. The α term is the max-min fairness target; the ε-weighted sum fills slack deterministically (so a supply-blocked device doesn't drag fairness to zero for its peers). Iterates: devices at their physical UB get pinned, residual α is re-solved.
2. **Phase B** (per drain priority `DP`, descending) — `max β` with `supply[p] ≥ β × normalized_amount[p]` for active pools at `DP`. Pools at `MaxSupplyRate` get pinned and excluded; conservation-bound pools all bind at the same β (lockstep drain). The pool-α coefficient is normalized per-resource and floored at `FairnessFloor = 0.1` to stay in envelope.

After all priorities: `Device.Activity` and `Buffer.Rate` reflect the final LP solution (written through by `ExtractResults` after each successful solve, before any bound mutation).

## Reference

- `mod/Nova.Core/Resources/ResourceSolver.cs` — LP construction, per-tick reset, iterative max-min α/β-LP (Phase A device fairness, Phase B per-drain-priority pool fairness), bottleneck-pinning iteration.
- `mod/Nova.Core/Components/VirtualVessel.cs` — pre/post-solve orchestration (`UpdateSolarDeviceDemand`, `UpdateFuelCellDevices`, `DistributeFuelCellState`).
- `mod/Nova.Core/Resources/Resource.cs` — unit convention.
- `mod/Nova.Core/Components/Electrical/FuelCell.cs` — worked example of the split input/output device pattern (until we add the buffer).

When the rules in this document conflict with what the LP actually does, fix the LP first and update this document second. The numerical envelope is empirical, not aspirational.
