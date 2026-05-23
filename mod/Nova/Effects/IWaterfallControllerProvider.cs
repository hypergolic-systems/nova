using System.Collections.Generic;
using Waterfall;

namespace Nova.Effects;

// Implemented by any PartModule that wants to publish controllers to a
// sibling `ModuleWaterfallFX`. Resolved by `WaterfallInitializePatch`
// after `ModuleWaterfallFX.Initialize` completes; each yielded controller
// is registered via `AddController` (which calls `Initialize(host)` and
// re-binds modifier-controller references).
//
// Yield each controller fresh per call — `CreateWaterfallControllers`
// fires every time the FX module re-initializes (e.g. on save-load),
// and a stale closure over a replaced VirtualComponent would point at
// the wrong data. Closing over `this` (the PartModule) and dereferencing
// the typed component accessor inside the lambda keeps the binding live.
public interface IWaterfallControllerProvider {
  IEnumerable<WaterfallController> CreateWaterfallControllers();
}
