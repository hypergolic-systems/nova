//! Resource catalogue. Rust is the source of truth — KSP doesn't push
//! resource definitions across the FFI; the wire format references
//! resources by name only, and `from_name` resolves them. New
//! resources require a code change here, not a config push.
//!
//! Density is kg / unit-volume (litres for propellants, joules for
//! Electric Charge — see comment on the EC entry).

#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash)]
pub enum Resource {
    ElectricCharge,
    LiquidHydrogen,
    LiquidOxygen,
    Rp1,
    Hydrazine,
    Xenon,
}

#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash)]
pub enum ResourceDomain {
    /// Flows along vessel topology (pipes, decouplers, drain priority).
    /// Solved by StagingFlowSystem (water-fill).
    Topological,
    /// Single vessel-wide pool — no topology distinctions, may be
    /// cyclic. Solved by ProcessFlowSystem (LP).
    Uniform,
}

impl Resource {
    /// Long form, e.g. "Liquid Hydrogen". Matches the C#
    /// `Resource.Name` registry key — used as the wire-format string.
    pub const fn name(self) -> &'static str {
        match self {
            Resource::ElectricCharge  => "Electric Charge",
            Resource::LiquidHydrogen  => "Liquid Hydrogen",
            Resource::LiquidOxygen    => "Liquid Oxygen",
            Resource::Rp1             => "RP-1",
            Resource::Hydrazine       => "Hydrazine",
            Resource::Xenon           => "Xenon",
        }
    }

    pub const fn abbreviation(self) -> &'static str {
        match self {
            Resource::ElectricCharge  => "EC",
            Resource::LiquidHydrogen  => "LH2",
            Resource::LiquidOxygen    => "LOX",
            Resource::Rp1             => "RP-1",
            Resource::Hydrazine       => "N2H4",
            Resource::Xenon           => "Xe",
        }
    }

    /// kg per unit-volume. EC has zero density (energy, not mass).
    /// Xenon uses the supercritical-storage value (~2 kg/L) to match
    /// real-world Dawn-style ion engines, not KSP's stock 1 kg/L.
    pub const fn density(self) -> f64 {
        match self {
            Resource::ElectricCharge  => 0.0,
            Resource::LiquidHydrogen  => 0.07,
            Resource::LiquidOxygen    => 1.2,
            Resource::Rp1             => 0.8,
            Resource::Hydrazine       => 1.0,
            Resource::Xenon           => 2.0,
        }
    }

    pub const fn domain(self) -> ResourceDomain {
        match self {
            Resource::ElectricCharge  => ResourceDomain::Uniform,
            Resource::LiquidHydrogen  |
            Resource::LiquidOxygen    |
            Resource::Rp1             |
            Resource::Hydrazine       |
            Resource::Xenon           => ResourceDomain::Topological,
        }
    }

    /// Resolve a wire-format resource name to its enum variant.
    /// Accepts the same long-form names as C#'s `Resource.Get(...)`.
    pub fn from_name(name: &str) -> Option<Self> {
        match name {
            "Electric Charge" => Some(Resource::ElectricCharge),
            "Liquid Hydrogen" => Some(Resource::LiquidHydrogen),
            "Liquid Oxygen"   => Some(Resource::LiquidOxygen),
            "RP-1"            => Some(Resource::Rp1),
            "Hydrazine"       => Some(Resource::Hydrazine),
            "Xenon"           => Some(Resource::Xenon),
            _                 => None,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const ALL: &[Resource] = &[
        Resource::ElectricCharge,
        Resource::LiquidHydrogen,
        Resource::LiquidOxygen,
        Resource::Rp1,
        Resource::Hydrazine,
        Resource::Xenon,
    ];

    #[test]
    fn name_round_trips_through_from_name() {
        for &r in ALL {
            assert_eq!(Resource::from_name(r.name()), Some(r));
        }
    }

    #[test]
    fn unknown_name_resolves_to_none() {
        assert_eq!(Resource::from_name("Unobtanium"), None);
        assert_eq!(Resource::from_name(""), None);
    }

    #[test]
    fn names_are_unique() {
        let mut names: Vec<&str> = ALL.iter().map(|r| r.name()).collect();
        names.sort();
        names.dedup();
        assert_eq!(names.len(), ALL.len());
    }

    #[test]
    fn electric_charge_is_uniform_others_are_topological() {
        assert_eq!(Resource::ElectricCharge.domain(), ResourceDomain::Uniform);
        for &r in ALL {
            if r == Resource::ElectricCharge { continue; }
            assert_eq!(r.domain(), ResourceDomain::Topological,
                       "{:?} should be Topological", r);
        }
    }

    #[test]
    fn densities_are_positive_for_propellants_zero_for_ec() {
        assert_eq!(Resource::ElectricCharge.density(), 0.0);
        for &r in ALL {
            if r == Resource::ElectricCharge { continue; }
            assert!(r.density() > 0.0, "{:?} density should be > 0", r);
        }
    }
}
