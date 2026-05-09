//! Nova simulation engine.
//!
//! Pure Rust, no FFI, no KSP/Unity dependencies. Authoring scenarios
//! in tests goes through [`World`] — see `tests/` and the `fixtures`
//! module for stock-Kerbol scaffolding.
//!
//! Frame conventions: right-handed, Z-up. Distances in metres,
//! masses in kg, times in seconds, angles in radians (unless noted).
//! Position-at-UT queries return body-relative (parent-frame) vectors
//! at the appropriate level — [`Ephemeris::body_position_absolute`]
//! and [`World::vessel_position_absolute`] walk to the root frame.

pub mod atmosphere;
pub mod buffer;
pub mod comms;
pub mod components;
pub mod ephem;
pub mod fixtures;
pub mod math;
pub mod orbit;
pub mod resource;
pub mod resources;
pub mod sim_clock;
pub mod situation;
pub mod systems;
pub mod world;
pub mod world_context;

pub use atmosphere::{Atmosphere, FloatCurve, FloatCurveKey};
pub use buffer::Buffer;
pub use comms::{
    Antenna, CommsSystem, Endpoint, EndpointId, EndpointKind, GraphSnapshot, GroundStationSpec,
    Job, JobId, JobStatus, Link, MotionModel, PathSummary, TopicKey,
};
pub use components::{
    Accumulator, Battery, Command, Comms, Component, Engine, FuelCell, Part, Propellant,
    SolarPanel, TankSpec, TankVolume,
};
pub use ephem::{Body, BodyId, BodyRotation, Ephemeris};
pub use math::Vec3d;
pub use orbit::OrbitalElements;
pub use resource::{Resource, ResourceDomain};
pub use resources::{Orbit, PanelGeometry, SolarEvent, SolarForecaster};
pub use sim_clock::SimClock;
pub use situation::Situation;
pub use systems::{
    BufferId, Consumer, ConsumerId, ConsumerInput, Device, DeviceHandle, DeviceId, Node, NodeId,
    Priority, ProcessFlowSystem, StagingFlowSystem, VesselSystems,
};
pub use world::{Vessel, VesselId, World, WorldBuilder};
pub use world_context::WorldContext;
