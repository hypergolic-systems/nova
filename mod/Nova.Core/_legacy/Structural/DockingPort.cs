using System.Collections.Generic;
using System.Linq;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;
namespace Nova.Core.Components.Structural;

public class DockingPort : VirtualComponent {
  /// <summary>
  /// Whitelist of resources allowed to flow through this docking port edge.
  /// Empty = allow all (null edge). Populated = only those resources flow.
  /// </summary>
  public HashSet<Resource> AllowedResources = new();

  /// <summary>
  /// Resources that can only flow upward (child to parent) through this port.
  /// </summary>
  public HashSet<Resource> UpOnlyResources = new();

  /// <summary>
  /// Drain priority for the child topology node below this port.
  /// Default 0 (no preference, unlike decouplers which default to 1).
  /// </summary>
  public int Priority = 0;

  public override VirtualComponent Clone() {
    return new DockingPort {
      AllowedResources = new HashSet<Resource>(AllowedResources),
      UpOnlyResources = new HashSet<Resource>(UpOnlyResources),
      Priority = Priority,
    };
  }
}
