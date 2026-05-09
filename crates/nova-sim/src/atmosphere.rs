/// Cubic Hermite spline matching KSP's FloatCurve evaluation.
/// Tangents are baked in (extracted from the live curve at
/// serialization time); the evaluator does not auto-derive them.
#[derive(Clone, Debug, Default)]
pub struct FloatCurve {
    pub keys: Vec<FloatCurveKey>,
}

#[derive(Copy, Clone, Debug, PartialEq)]
pub struct FloatCurveKey {
    pub time: f64,
    pub value: f64,
    pub in_tangent: f64,
    pub out_tangent: f64,
}

impl FloatCurve {
    pub fn new(keys: Vec<FloatCurveKey>) -> Self {
        FloatCurve { keys }
    }

    /// Evaluate at `x`. Outside the curve domain, returns the first /
    /// last value (clamped, matching KSP's FloatCurve behavior).
    pub fn evaluate(&self, x: f64) -> f64 {
        let keys = &self.keys;
        if keys.is_empty() { return 0.0; }
        if keys.len() == 1 || x <= keys[0].time { return keys[0].value; }
        let last = &keys[keys.len() - 1];
        if x >= last.time { return last.value; }

        // Binary search for the segment containing x.
        let i = keys.partition_point(|k| k.time <= x) - 1;
        let k0 = &keys[i];
        let k1 = &keys[i + 1];
        let dt = k1.time - k0.time;
        if dt == 0.0 { return k0.value; }
        let t = (x - k0.time) / dt;

        let h00 = (1.0 + 2.0 * t) * (1.0 - t).powi(2);
        let h10 = t * (1.0 - t).powi(2);
        let h01 = t * t * (3.0 - 2.0 * t);
        let h11 = t * t * (t - 1.0);

        h00 * k0.value
            + h10 * dt * k0.out_tangent
            + h01 * k1.value
            + h11 * dt * k1.in_tangent
    }
}

#[derive(Clone, Debug, Default)]
pub struct Atmosphere {
    pub depth_m: f64,
    pub pressure_curve_atm: FloatCurve,
    pub temperature_curve_k: FloatCurve,
}

impl Atmosphere {
    /// Pressure at altitude (m), in atm. Returns 0 above the
    /// atmosphere top.
    pub fn pressure_atm(&self, altitude_m: f64) -> f64 {
        if altitude_m >= self.depth_m { return 0.0; }
        self.pressure_curve_atm.evaluate(altitude_m).max(0.0)
    }

    /// Temperature at altitude (m), in K.
    pub fn temperature_k(&self, altitude_m: f64) -> f64 {
        self.temperature_curve_k.evaluate(altitude_m)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use approx::assert_relative_eq;

    fn linear_curve() -> FloatCurve {
        // Straight line from (0, 1) to (10, 0).
        FloatCurve::new(vec![
            FloatCurveKey { time: 0.0, value: 1.0, in_tangent: -0.1, out_tangent: -0.1 },
            FloatCurveKey { time: 10.0, value: 0.0, in_tangent: -0.1, out_tangent: -0.1 },
        ])
    }

    #[test]
    fn endpoint_values_are_exact() {
        let c = linear_curve();
        assert_relative_eq!(c.evaluate(0.0), 1.0);
        assert_relative_eq!(c.evaluate(10.0), 0.0);
    }

    #[test]
    fn linear_curve_midpoint() {
        let c = linear_curve();
        // Hermite with matching slopes on both ends == straight line.
        assert_relative_eq!(c.evaluate(5.0), 0.5, max_relative = 1e-12);
    }

    #[test]
    fn out_of_domain_clamps() {
        let c = linear_curve();
        assert_relative_eq!(c.evaluate(-1.0), 1.0);
        assert_relative_eq!(c.evaluate(20.0), 0.0);
    }

    #[test]
    fn empty_curve_returns_zero() {
        let c = FloatCurve::default();
        assert_relative_eq!(c.evaluate(5.0), 0.0);
    }

    #[test]
    fn atmosphere_above_top_is_vacuum() {
        let atm = Atmosphere {
            depth_m: 70_000.0,
            pressure_curve_atm: linear_curve(),
            temperature_curve_k: linear_curve(),
        };
        assert_relative_eq!(atm.pressure_atm(70_000.0), 0.0);
        assert_relative_eq!(atm.pressure_atm(100_000.0), 0.0);
    }
}
