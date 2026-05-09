/// Mirror of `nova_sim::Command` post-tick state. Output-only;
/// `IdleActivity` is the LP solver's allocation share (0..1) for the
/// avionics consumer.
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct CommandState {
    pub IdleActivity: f64,
}

impl CommandState {
    pub const ZERO: CommandState = CommandState {
        IdleActivity: 0.0,
    };
}

/// Reference shim for csbindgen layout discovery — see the matching
/// note on `nova_battery_state_zero`.
#[no_mangle]
pub extern "C" fn nova_command_state_zero() -> CommandState {
    CommandState::ZERO
}
