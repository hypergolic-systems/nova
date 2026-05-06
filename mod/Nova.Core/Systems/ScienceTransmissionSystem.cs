using System;
using System.Collections.Generic;
using System.Linq;
using Nova.Core.Communications;
using Nova.Core.Components;
using Nova.Core.Components.Science;
using Nova.Core.Persistence.Protos;
using Nova.Core.Science;

namespace Nova.Core.Systems;

// Per-vessel transmission queue. Polls every DataStorage on the vessel
// for files marked IsComplete by the producing experiment, ships them
// to KSC one at a time as Packets through the comm network, and on
// completion both archives the file at KSC and removes the local copy.
//
// Network access (CommunicationsNetwork + endpoints + archive) is
// injected post-construction via SetCommNetwork — VesselSystems is
// built before NovaCommunicationsAddon has registered this vessel's
// endpoint, so the wiring lands in a separate call from the mod side.
// Until SetCommNetwork is called the system is inert (Solve no-ops).
//
// Idle by design: when the queue is empty no Packet is held, so the
// link to KSC carries no traffic until the next file completes.
public class ScienceTransmissionSystem : BackgroundSystem {

  private readonly VirtualVessel vessel;
  private readonly SimClock clock;

  private CommunicationsNetwork network;
  private Endpoint vesselEndpoint;
  private Endpoint kscEndpoint;
  private IScienceArchive archive;
  private uint sourceVesselPersistentId;

  // Queue of (storage, subjectId) refs awaiting transmission. We don't
  // store ScienceFile copies because the file lives in the storage and
  // can update in place until the moment we submit; the dequeue path
  // re-resolves via FindBySubject.
  private readonly Queue<QueueEntry> queue = new();
  private readonly HashSet<string> tracked = new();

  private Packet active;
  private QueueEntry? activeRef;

  public ScienceTransmissionSystem(VirtualVessel vessel, SimClock clock) {
    this.vessel = vessel;
    this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
  }

  // Mod-side wiring entry point. May be called multiple times (e.g. on
  // dock/undock when the endpoint is rebuilt). Cancels any in-flight
  // packet because its endpoints are about to become stale.
  public void SetCommNetwork(CommunicationsNetwork network,
                             Endpoint vesselEndpoint,
                             Endpoint kscEndpoint,
                             IScienceArchive archive,
                             uint sourceVesselPersistentId) {
    CancelActive();
    this.network = network;
    this.vesselEndpoint = vesselEndpoint;
    this.kscEndpoint = kscEndpoint;
    this.archive = archive;
    this.sourceVesselPersistentId = sourceVesselPersistentId;
  }

  public IReadOnlyCollection<string> QueuedSubjects => tracked;
  public Packet ActivePacket => active;

  public override void Solve() {
    needsSolve = false;

    // Reap any terminal packet from the prior tick.
    if (active != null) {
      switch (active.Status) {
        case JobStatus.Completed:
          var ar = activeRef.Value;
          var file = ar.Storage.FindBySubject(ar.SubjectId);
          if (file != null) {
            archive?.Receive(file, sourceVesselPersistentId, clock.UT);
            ar.Storage.RemoveBySubject(ar.SubjectId);
            vessel?.Log?.Invoke(
                $"[Science] archive received subject={ar.SubjectId} fidelity={file.Fidelity:F3} ut={clock.UT:F1}");
          }
          tracked.Remove(ar.SubjectId);
          active = null;
          activeRef = null;
          break;
        case JobStatus.Cancelled:
          tracked.Remove(activeRef.Value.SubjectId);
          active = null;
          activeRef = null;
          break;
        case JobStatus.Active:
          // Still in flight; nothing to do.
          break;
      }
    }

    // Enqueue any newly-complete files.
    if (vessel == null) return;
    foreach (var storage in vessel.AllComponents().OfType<DataStorage>()) {
      foreach (var f in storage.Files) {
        if (!f.IsComplete) continue;
        if (tracked.Contains(f.SubjectId)) continue;
        queue.Enqueue(new QueueEntry(storage, f.SubjectId));
        tracked.Add(f.SubjectId);
      }
    }

    if (network == null || vesselEndpoint == null || kscEndpoint == null) return;

    // Submit next if idle and the queue has work.
    while (active == null && queue.Count > 0) {
      var entry = queue.Dequeue();
      var file = entry.Storage.FindBySubject(entry.SubjectId);
      if (file == null) {
        // File evaporated between enqueue and dequeue (player discard,
        // vessel rebuild). Drop and try the next one.
        tracked.Remove(entry.SubjectId);
        continue;
      }
      var bytes = ExperimentSpecs.FileSizeFor(file.ExperimentId);
      active = new Packet(vesselEndpoint, kscEndpoint, bytes);
      activeRef = entry;
      network.Submit(active);
      vessel?.Log?.Invoke(
          $"[Science] transmit subject={entry.SubjectId} bytes={bytes}");
    }
  }

  // Cancel any in-flight transmission. Used on vessel teardown / endpoint
  // rebuild — the packet's endpoints are about to become stale.
  public void CancelActive() {
    if (active != null && active.Status == JobStatus.Active && network != null) {
      network.Cancel(active);
    }
    if (activeRef.HasValue) tracked.Remove(activeRef.Value.SubjectId);
    active = null;
    activeRef = null;
  }

  private readonly struct QueueEntry {
    public readonly DataStorage Storage;
    public readonly string SubjectId;
    public QueueEntry(DataStorage storage, string subjectId) {
      Storage = storage;
      SubjectId = subjectId;
    }
  }
}
