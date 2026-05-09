//! Directed-edge data plus the steady-state graph snapshot it lives
//! in. Mirrors `mod/Nova.Core/Communications/Link.cs` and
//! `GraphSnapshot.cs`.

use super::endpoint::EndpointId;

/// One directed edge in the comms graph: `from` transmits to `to`.
/// A bidirectional link surfaces as two `Link` records (rate may
/// differ per direction when antenna specs differ).
#[derive(Clone, Debug)]
pub struct Link {
    pub from: EndpointId,
    pub to: EndpointId,
    pub distance_m: f64,
    pub snr: f64,
    /// Effective rate after both bucket quantisation and occlusion
    /// gating. 0 when `blocked == true`. `MaxRatePath` filters edges
    /// with `rate_bps <= 0`, so blocked links automatically drop out
    /// of routing.
    pub rate_bps: f64,
    /// True iff some occluder body is intersecting the chord.
    /// Surfaced for telemetry; routing already excludes the edge via
    /// the rate-bps filter.
    pub blocked: bool,
    /// Demand allocated on this edge after max-min fair allocation.
    /// 0 if no jobs use the edge; never exceeds `rate_bps`.
    pub used_bps: f64,
    /// Forecast UT at which this link's effective state changes —
    /// earliest of (next bucket transition, next occlusion enter/exit).
    /// `+∞` when the link is rate-stable across the full search horizon.
    pub next_event_ut: f64,
}

impl Link {
    pub fn new(
        from: EndpointId,
        to: EndpointId,
        distance_m: f64,
        snr: f64,
        rate_bps: f64,
    ) -> Self {
        Link {
            from,
            to,
            distance_m,
            snr,
            rate_bps,
            blocked: false,
            used_bps: 0.0,
            next_event_ut: f64::INFINITY,
        }
    }
}

/// Steady-state graph produced by `CommsSystem::solve`. Read-only —
/// callers receive a fresh snapshot from each solve.
#[derive(Clone, Debug, Default)]
pub struct GraphSnapshot {
    pub links: Vec<Link>,
    /// UT at which positions were evaluated. NaN before the first
    /// solve.
    pub solved_ut: f64,
}

impl GraphSnapshot {
    pub fn empty() -> Self {
        GraphSnapshot { links: Vec::new(), solved_ut: f64::NAN }
    }
}
