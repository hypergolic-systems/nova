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
  public void Deposit_RespectsCapacity() {
    var s = new DataStorage { CapacityBytes = 2500 };
    Assert.IsTrue(s.Deposit(MakeFile(), 1000));
    Assert.IsTrue(s.Deposit(MakeFile(), 1000));
    Assert.IsFalse(s.Deposit(MakeFile(), 1000));
    Assert.AreEqual(2, s.Files.Count);
    Assert.AreEqual(2000, s.UsedBytes);
    Assert.AreEqual(500,  s.FreeBytes);
  }

  [TestMethod]
  public void RoundTrip_ThroughProto_RestoresFiles() {
    var src = new DataStorage { CapacityBytes = 10_000 };
    src.Deposit(MakeFile("atm-profile@Kerbin:troposphere"),  1000);
    src.Deposit(MakeFile("atm-profile@Kerbin:stratosphere"), 1000);

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
    src.Deposit(MakeFile(), 1000);
    var clone = (DataStorage)src.Clone();

    src.Deposit(MakeFile("atm-profile@Eve:troposphere"), 1000);
    Assert.AreEqual(1, clone.Files.Count, "Clone must not share Files list");
    Assert.AreEqual(2, src.Files.Count);
  }
}
