using System.Collections.Generic;
using System.Linq;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;
namespace Nova.Core.Components.Structural;

public class Decoupler : VirtualComponent {
  /// <summary>
  /// Whitelist of resources allowed to flow through this decoupler edge.
  /// Empty = block all (default). Populated = only those resources flow.
  /// </summary>
  public HashSet<Resource> AllowedResources = new();

  /// <summary>
  /// Resources that can only flow upward (child → parent) through this decoupler.
  /// Resources in AllowedResources but not here flow bidirectionally.
  /// </summary>
  public HashSet<Resource> UpOnlyResources = new();

  /// <summary>
  /// Drain priority for the child topology node below this decoupler.
  /// Higher = drains first. Default 1 (drain before root which is 0).
  /// </summary>
  public int Priority = 1;

  /// <summary>
  /// When true, firing this decoupler releases every attached neighbour
  /// (children + parent) at once — stock "separator" semantics. When
  /// false, only the explosive-node neighbour is released. Editor-time
  /// toggle, surfaced via the Dragonglass UI's per-part popover.
  /// </summary>
  public bool FullSeparation;

  public override VirtualComponent Clone() {
    return new Decoupler {
      AllowedResources = new HashSet<Resource>(AllowedResources),
      UpOnlyResources = new HashSet<Resource>(UpOnlyResources),
      Priority = Priority,
      FullSeparation = FullSeparation,
    };
  }

  public override void Save(PartState state) {
    state.Decoupler = new DecouplerState { FullSeparation = FullSeparation };
  }

  public override void Load(PartState state) {
    if (state.Decoupler == null) return;
    FullSeparation = state.Decoupler.FullSeparation;
  }
}
