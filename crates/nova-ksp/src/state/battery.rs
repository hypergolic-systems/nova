/// Mirror of `nova_sim::Battery` post-tick state. Output-only; no
/// fields are C#-writable in Phase-1.
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct BatteryState {
    /// Current EC contents in the buffer.
    pub Contents: f64,
    /// Capacity (constant for the vessel's lifetime; mirrored once at
    /// finalize for C# convenience).
    pub Capacity: f64,
}

impl BatteryState {
    pub const ZERO: BatteryState = BatteryState {
        Contents: 0.0,
        Capacity: 0.0,
    };
}

/// Reference shim — exists solely so csbindgen emits the matching
/// `[StructLayout]` for `BatteryState` (csbindgen only walks types
/// reachable from `extern "C"` fns). C# may call this once at startup
/// to assert layout parity, but the per-frame read path goes through
/// the arena pointer with `Unsafe.AsRef`, never this function.
#[no_mangle]
pub extern "C" fn nova_battery_state_zero() -> BatteryState {
    BatteryState::ZERO
}
