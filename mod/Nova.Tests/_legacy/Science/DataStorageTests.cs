using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components.Science;
using Nova.Core.Persistence.Protos;

namespace Nova.Tests.Science;

[TestClass]
public class DataStorageTests {

  private static ScienceFile MakeFile(string subject = "atm-profile@Kerbin:troposphere",
                                       string exp = "atm-profile") =>
      new ScienceFile {
        SubjectId    = subject,
        ExperimentId = exp,
        Fidelity     = 1.0,
        ProducedAt   = 100,
      };

  [TestMethod]
  public void Upsert_RespectsCapacity() {
    var s = new DataStorage { CapacityBytes = 2500 };
    // Distinct subjects → all three would normally deposit; capacity
    // check rejects the third.
    Assert.IsTrue (s.Upsert(MakeFile("atm-profile@Kerbin:troposphere"),  1000));
    Assert.IsTrue (s.Upsert(MakeFile("atm-profile@Kerbin:stratosphere"), 1000));
    Assert.IsFalse(s.Upsert(MakeFile("atm-profile@Kerbin:mesosphere"),   1000));
    Assert.AreEqual(2, s.Files.Count);
    Assert.AreEqual(2000, s.UsedBytes);
    Assert.AreEqual(500,  s.FreeBytes);
  }

  [TestMethod]
  public void Upsert_UpdatesExistingFileInPlace() {
    var s = new DataStorage { CapacityBytes = 2500 };
    s.Upsert(new ScienceFile {
      SubjectId    = "atm-profile@Kerbin:troposphere",
      ExperimentId = "atm-profile",
      Fidelity     = 0.3,
    }, 1000);
    s.Upsert(new ScienceFile {
      SubjectId    = "atm-profile@Kerbin:troposphere",
      ExperimentId = "atm-profile",
      Fidelity     = 0.7,
    }, 1000);
    Assert.AreEqual(1, s.Files.Count, "second upsert with same subject must not add a new file");
    Assert.AreEqual(0.7, s.Files[0].Fidelity, 1e-9);
    Assert.AreEqual(1000, s.UsedBytes, "byte count is reserved on first deposit, not re-charged on update");
  }

  [TestMethod]
  public void RoundTrip_ThroughProto_RestoresFiles() {
    var src = new DataStorage { CapacityBytes = 10_000 };
    src.Upsert(MakeFile("atm-profile@Kerbin:troposphere"),  1000);
    src.Upsert(MakeFile("atm-profile@Kerbin:stratosphere"), 1000);

    var partState = new PartState();
    src.Save(partState);

    var dst = new DataStorage { CapacityBytes = 10_000 };
    dst.Load(partState);

    Assert.AreEqual(2, dst.Files.Count);
    Assert.AreEqual("atm-profile@Kerbin:troposphere",  dst.Files[0].SubjectId);
    Assert.AreEqual("atm-profile@Kerbin:stratosphere", dst.Files[1].SubjectId);
    Assert.AreEqual(2000, dst.UsedBytes);
  }

  [TestMethod]
  public void StructureRoundTrip_PreservesCapacity() {
    var src = new DataStorage { CapacityBytes = 51200 };
    var ps = new PartStructure();
    src.SaveStructure(ps);

    var dst = new DataStorage();
    dst.LoadStructure(ps);
    Assert.AreEqual(51200, dst.CapacityBytes);
  }

  [TestMethod]
  public void Clone_DeepCopiesFiles() {
    var src = new DataStorage { CapacityBytes = 5000 };
    src.Upsert(MakeFile("atm-profile@Kerbin:troposphere"), 1000);
    var clone = (DataStorage)src.Clone();

    src.Upsert(MakeFile("atm-profile@Eve:troposphere"), 1000);
    Assert.AreEqual(1, clone.Files.Count, "Clone must not share Files list");
    Assert.AreEqual(2, src.Files.Count);
  }
}
