using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Science;

namespace Nova.Tests.Science;

[TestClass]
public class ExperimentRegistryTests {

  private static AtmosphereLayers KerbinLayers() {
    var l = new AtmosphereLayers();
    l.AddLayer("Kerbin", "troposphere", 18000);
    l.AddLayer("Kerbin", "stratosphere", 45000);
    l.AddLayer("Kerbin", "mesosphere", 70000);
    return l;
  }

  private static ExperimentRegistry RegistryWith(AtmosphereLayers layers) {
    var reg = new ExperimentRegistry();
    reg.Register(new AtmosphericProfileExperiment(layers));
    reg.Register(new LongTermStudyExperiment());
    return reg;
  }

  private static SubjectContext Ctx(
      string body, Situation sit, double altitude, double pressure = 1.0,
      double ut = 100, double bodyYear = 9_203_545) =>
      new SubjectContext(0, body, sit, altitude, pressure, ut, bodyYear);

  [TestMethod]
  public void AtmProfile_AppliesInsideAtmosphere() {
    var reg = RegistryWith(KerbinLayers());
    var ctx = Ctx("Kerbin", Situation.FlyingLow, 5000);
    var applicable = reg.Applicable(ctx).Select(e => e.Id).ToList();
    CollectionAssert.Contains(applicable, "atm-profile");
  }

  [TestMethod]
  public void AtmProfile_DoesNotApplyAboveAtmosphere() {
    var reg = RegistryWith(KerbinLayers());
    var ctx = Ctx("Kerbin", Situation.InSpaceLow, 200_000);
    var applicable = reg.Applicable(ctx).Select(e => e.Id).ToList();
    CollectionAssert.DoesNotContain(applicable, "atm-profile");
  }

  [TestMethod]
  public void AtmProfile_ResolvesToCurrentLayer() {
    var exp = new AtmosphericProfileExperiment(KerbinLayers());
    var subj = exp.ResolveSubject(Ctx("Kerbin", Situation.FlyingLow, 30_000)).Value;
    Assert.AreEqual("atm-profile", subj.ExperimentId);
    Assert.AreEqual("stratosphere", subj.Variant);
    Assert.IsNull(subj.SliceIndex);
  }

  [TestMethod]
  public void Lts_AppliesEverywhereWithBodyYear() {
    var reg = RegistryWith(KerbinLayers());
    var landed = Ctx("Kerbin", Situation.SrfLanded, 0);
    var orbit  = Ctx("Kerbin", Situation.InSpaceHigh, 200_000);
    var moon   = Ctx("Mun",    Situation.SrfLanded,   0, bodyYear: 9_203_545);
    Assert.IsTrue(reg.Applicable(landed).Any(e => e.Id == "lts"));
    Assert.IsTrue(reg.Applicable(orbit) .Any(e => e.Id == "lts"));
    Assert.IsTrue(reg.Applicable(moon)  .Any(e => e.Id == "lts"));
  }

  [TestMethod]
  public void Lts_DoesNotApplyWithUnknownSituation() {
    var reg = RegistryWith(KerbinLayers());
    var ctx = Ctx("Kerbin", Situation.None, 0);
    Assert.IsFalse(reg.Applicable(ctx).Any(e => e.Id == "lts"));
  }

  [TestMethod]
  public void Lts_SubjectIncludesSliceIndex() {
    var exp = new LongTermStudyExperiment();
    // UT is exactly 1/4 of a Kerbin year → slice 3.
    double ut = 9_203_545.0 * 3 / 12;
    var subj = exp.ResolveSubject(Ctx("Kerbin", Situation.SrfLanded, 0, ut: ut)).Value;
    Assert.AreEqual("lts", subj.ExperimentId);
    Assert.AreEqual("Kerbin", subj.BodyName);
    Assert.AreEqual("SrfLanded", subj.Variant);
    Assert.AreEqual(3, subj.SliceIndex);
  }

  [TestMethod]
  public void Lts_SliceIndex_BoundaryCases() {
    double year = 12_000;  // 12 slices → 1000 s each
    Assert.AreEqual(0,  LongTermStudyExperiment.SliceIndexAt(0,         year));
    Assert.AreEqual(0,  LongTermStudyExperiment.SliceIndexAt(999.99,    year));
    Assert.AreEqual(1,  LongTermStudyExperiment.SliceIndexAt(1000,      year));
    Assert.AreEqual(11, LongTermStudyExperiment.SliceIndexAt(11500,     year));
    // Wraps at year boundary.
    Assert.AreEqual(0,  LongTermStudyExperiment.SliceIndexAt(year,      year));
    Assert.AreEqual(0,  LongTermStudyExperiment.SliceIndexAt(year * 5,  year));
    Assert.AreEqual(7,  LongTermStudyExperiment.SliceIndexAt(year * 5 + 7250, year));
  }

  [TestMethod]
  public void Lts_NextSliceBoundary_StrictlyAfterNow() {
    double year = 12_000;
    Assert.AreEqual(1000,  LongTermStudyExperiment.NextSliceBoundary(0,    year), 1e-6);
    Assert.AreEqual(1000,  LongTermStudyExperiment.NextSliceBoundary(500,  year), 1e-6);
    Assert.AreEqual(2000,  LongTermStudyExperiment.NextSliceBoundary(1000, year), 1e-6);
    Assert.AreEqual(12000, LongTermStudyExperiment.NextSliceBoundary(11500, year), 1e-6);
    Assert.AreEqual(13000, LongTermStudyExperiment.NextSliceBoundary(12000, year), 1e-6);
  }
}
