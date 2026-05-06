using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Communications;
using Nova.Core.Components;
using Nova.Core.Components.Communications;
using Nova.Core.Components.Science;
using Nova.Core.Persistence.Protos;
using Nova.Core.Science;
using Nova.Core.Systems;
using Nova.Core.Utils;

namespace Nova.Tests.Systems;

[TestClass]
public class ScienceTransmissionSystemTests {

  // --- Test scaffolding ----------------------------------------------------

  private class CapturingArchive : IScienceArchive {
    public readonly List<(ScienceFile file, uint vesselId, double ut)> Received = new();
    public void Receive(ScienceFile file, uint vesselId, double ut) {
      Received.Add((file, vesselId, ut));
    }
  }

  private static Antenna FlatAntenna(double maxRate = 1000) => new() {
    TxPower = 1, Gain = 1, MaxRate = maxRate, RefDistance = 1e6,
  };

  private static Endpoint MakeEndpoint(string id, Vec3d pos, params Antenna[] antennas) {
    var ep = new Endpoint { Id = id, PositionAt = _ => pos };
    ep.Antennas.AddRange(antennas);
    return ep;
  }

  // Minimal vessel with one part hosting a DataStorage component, so the
  // transmission system has somewhere to pull files from.
  private static (VirtualVessel vessel, DataStorage storage) MakeVessel(double startUT = 0) {
    var storage = new DataStorage { CapacityBytes = 100_000 };
    var vessel = new VirtualVessel();
    vessel.AddPart(1, "probe", 100, new List<VirtualComponent> { storage });
    vessel.UpdatePartTree(new Dictionary<uint, uint?> { { 1u, null } });
    vessel.InitializeSolver(startUT);
    return (vessel, storage);
  }

  private static ScienceFile MakeAtmFile(double fidelity, bool complete) => new() {
    SubjectId       = "atm-profile@Kerbin:troposphere",
    ExperimentId    = AtmosphericProfileExperiment.ExperimentId,
    Fidelity        = fidelity,
    RecordedMinAltM = 1_000,
    RecordedMaxAltM = 1_000 + fidelity * (18_000 - 1_000),
    IsComplete      = complete,
  };

  private const long AtmFileBytes = AtmosphericProfileExperiment.FileSizeBytes;

  // --- Tests ---------------------------------------------------------------

  [TestMethod]
  public void NoFiles_NoPacketSubmitted() {
    var net = new CommunicationsNetwork();
    var vEp = MakeEndpoint("V", Vec3d.Zero, FlatAntenna());
    var kEp = MakeEndpoint("K", new Vec3d(10, 0, 0), FlatAntenna());
    net.AddEndpoint(vEp); net.AddEndpoint(kEp);

    var (vessel, _) = MakeVessel();
    var sys = vessel.Systems.Transmission;
    sys.SetCommNetwork(net, vEp, kEp, new CapturingArchive(), 42);

    sys.Solve();
    net.Solve(0);

    Assert.IsNull(sys.ActivePacket);
    Assert.AreEqual(0, net.Jobs.Count);
  }

  [TestMethod]
  public void IncompleteFile_NotEnqueued() {
    var net = new CommunicationsNetwork();
    var vEp = MakeEndpoint("V", Vec3d.Zero, FlatAntenna());
    var kEp = MakeEndpoint("K", new Vec3d(10, 0, 0), FlatAntenna());
    net.AddEndpoint(vEp); net.AddEndpoint(kEp);

    var (vessel, storage) = MakeVessel();
    storage.Upsert(MakeAtmFile(fidelity: 0.5, complete: false), AtmFileBytes);

    var sys = vessel.Systems.Transmission;
    sys.SetCommNetwork(net, vEp, kEp, new CapturingArchive(), 42);

    sys.Solve();
    Assert.IsNull(sys.ActivePacket);
    Assert.AreEqual(0, net.Jobs.Count);
  }

  [TestMethod]
  public void CompleteFile_SubmitsPacketWithCorrectSize() {
    var net = new CommunicationsNetwork();
    var vEp = MakeEndpoint("V", Vec3d.Zero, FlatAntenna(500));
    var kEp = MakeEndpoint("K", new Vec3d(10, 0, 0), FlatAntenna(500));
    net.AddEndpoint(vEp); net.AddEndpoint(kEp);

    var (vessel, storage) = MakeVessel();
    storage.Upsert(MakeAtmFile(fidelity: 1.0, complete: true), AtmFileBytes);

    var sys = vessel.Systems.Transmission;
    sys.SetCommNetwork(net, vEp, kEp, new CapturingArchive(), 42);

    sys.Solve();

    Assert.IsNotNull(sys.ActivePacket);
    Assert.AreEqual(AtmFileBytes, sys.ActivePacket.TotalBytes);
    Assert.AreSame(vEp, sys.ActivePacket.Source);
    Assert.AreSame(kEp, sys.ActivePacket.Destination);
    Assert.AreEqual(1, net.Jobs.Count);
  }

  [TestMethod]
  public void PacketCompletes_FileRemovedAndArchived() {
    var net = new CommunicationsNetwork();
    var vEp = MakeEndpoint("V", Vec3d.Zero, FlatAntenna(AtmFileBytes));
    var kEp = MakeEndpoint("K", new Vec3d(10, 0, 0), FlatAntenna(AtmFileBytes));
    net.AddEndpoint(vEp); net.AddEndpoint(kEp);

    var (vessel, storage) = MakeVessel();
    storage.Upsert(MakeAtmFile(fidelity: 1.0, complete: true), AtmFileBytes);

    var archive = new CapturingArchive();
    var sys = vessel.Systems.Transmission;
    sys.SetCommNetwork(net, vEp, kEp, archive, 42);

    // Tick 0: enqueue + submit packet.
    sys.Solve();
    net.Solve(0);
    Assert.AreEqual(JobStatus.Active, sys.ActivePacket.Status);

    // Tick 2: 1 s × FileBytes bps = full delivery.
    net.Solve(2);
    Assert.AreEqual(JobStatus.Completed, sys.ActivePacket?.Status ?? JobStatus.Completed);

    // Next system Solve reaps the completed packet.
    vessel.Systems.Clock.UT = 2;
    sys.Solve();

    Assert.IsNull(sys.ActivePacket);
    Assert.AreEqual(0, storage.Files.Count, "file should be removed locally");
    Assert.AreEqual(1, archive.Received.Count, "archive should have received one file");
    Assert.AreEqual("atm-profile@Kerbin:troposphere", archive.Received[0].file.SubjectId);
    Assert.AreEqual(42u, archive.Received[0].vesselId);
    Assert.AreEqual(2.0, archive.Received[0].ut, 1e-9);
  }

  [TestMethod]
  public void OneAtATime_SecondFileWaitsForFirstToComplete() {
    var net = new CommunicationsNetwork();
    var vEp = MakeEndpoint("V", Vec3d.Zero, FlatAntenna(AtmFileBytes));
    var kEp = MakeEndpoint("K", new Vec3d(10, 0, 0), FlatAntenna(AtmFileBytes));
    net.AddEndpoint(vEp); net.AddEndpoint(kEp);

    var (vessel, storage) = MakeVessel();
    storage.Upsert(MakeAtmFile(fidelity: 1.0, complete: true), AtmFileBytes);
    var f2 = MakeAtmFile(fidelity: 1.0, complete: true);
    f2.SubjectId = "atm-profile@Kerbin:stratosphere";
    storage.Upsert(f2, AtmFileBytes);

    var archive = new CapturingArchive();
    var sys = vessel.Systems.Transmission;
    sys.SetCommNetwork(net, vEp, kEp, archive, 42);

    sys.Solve();
    Assert.AreEqual(1, net.Jobs.Count(j => j.Status == JobStatus.Active));

    net.Solve(0);
    net.Solve(2);                          // first packet finishes
    vessel.Systems.Clock.UT = 2;
    sys.Solve();                           // reap + submit next

    Assert.IsNotNull(sys.ActivePacket, "second packet should now be in flight");
    net.Solve(4);                          // second finishes
    vessel.Systems.Clock.UT = 4;
    sys.Solve();

    Assert.IsNull(sys.ActivePacket);
    Assert.AreEqual(0, storage.Files.Count);
    Assert.AreEqual(2, archive.Received.Count);
  }

  [TestMethod]
  public void CancelActive_AbortsPacketAndPermitsResubmit() {
    var net = new CommunicationsNetwork();
    var vEp = MakeEndpoint("V", Vec3d.Zero, FlatAntenna(100));
    var kEp = MakeEndpoint("K", new Vec3d(10, 0, 0), FlatAntenna(100));
    net.AddEndpoint(vEp); net.AddEndpoint(kEp);

    var (vessel, storage) = MakeVessel();
    storage.Upsert(MakeAtmFile(fidelity: 1.0, complete: true), AtmFileBytes);

    var sys = vessel.Systems.Transmission;
    sys.SetCommNetwork(net, vEp, kEp, new CapturingArchive(), 42);

    sys.Solve();
    net.Solve(0);
    var first = sys.ActivePacket;
    Assert.IsNotNull(first);

    sys.CancelActive();
    Assert.AreEqual(JobStatus.Cancelled, first.Status);
    Assert.IsNull(sys.ActivePacket);

    // File still in storage; next Solve re-enqueues + submits a fresh packet.
    Assert.AreEqual(1, storage.Files.Count);
    sys.Solve();
    Assert.IsNotNull(sys.ActivePacket);
    Assert.AreNotSame(first, sys.ActivePacket);
  }

  [TestMethod]
  public void NoNetworkWired_QueuesButDoesNotSubmit() {
    var (vessel, storage) = MakeVessel();
    storage.Upsert(MakeAtmFile(fidelity: 1.0, complete: true), AtmFileBytes);

    var sys = vessel.Systems.Transmission;
    sys.Solve();

    Assert.IsNull(sys.ActivePacket);
    Assert.AreEqual(1, storage.Files.Count);
    Assert.IsTrue(sys.QueuedSubjects.Contains("atm-profile@Kerbin:troposphere"));
  }
}
