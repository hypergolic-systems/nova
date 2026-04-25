using System.Collections.Generic;
using System.Linq;
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

  public override VirtualComponent Clone() {
    return new Decoupler {
      AllowedResources = new HashSet<Resource>(AllowedResources),
      UpOnlyResources = new HashSet<Resource>(UpOnlyResources),
      Priority = Priority,
    };
  }
}
