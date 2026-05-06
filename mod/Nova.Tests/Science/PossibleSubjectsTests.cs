using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Science;

namespace Nova.Tests.Science;

// Coverage for the `AllSubjects` / `AllSubjectsFor` enumerators that
// drive the science-archive topic's gap rendering. The topic itself
// reads `FlightGlobals.Bodies` and isn't unit-testable, but the
// per-experiment subject inventory is — keep it exercised here.
[TestClass]
public class PossibleSubjectsTests {

  [TestMethod]
  public void AtmosphericProfile_AllSubjects_CoversEveryBodyLayerPair() {
    var entries = AtmosphericProfileExperiment.AllSubjects().ToList();

    foreach (var body in AtmosphericProfileExperiment.KnownBodies) {
      var layers = AtmosphericProfileExperiment.LayersFor(body);
      Assert.IsNotNull(layers, "atm body without layers: " + body);
      foreach (var l in layers) {
        Assert.IsTrue(entries.Contains((body, l.name)),
            "missing (body, layer): " + body + "/" + l.name);
      }
    }

    int expected = AtmosphericProfileExperiment.KnownBodies
        .Sum(b => AtmosphericProfileExperiment.LayersFor(b).Length);
    Assert.AreEqual(expected, entries.Count);
  }

  [TestMethod]
  public void LongTermStudy_SupportedSituations_ExcludesNone() {
    Assert.IsFalse(
        LongTermStudyExperiment.SupportedSituations.Contains(Situation.None),
        "Situation.None must not be in SupportedSituations");
    Assert.AreEqual(6, LongTermStudyExperiment.SupportedSituations.Length);
  }

  [TestMethod]
  public void LongTermStudy_AllSubjectsFor_HasSixTimesTwelveEntries() {
    var subjects = LongTermStudyExperiment.AllSubjectsFor("Kerbin").ToList();
    Assert.AreEqual(
        LongTermStudyExperiment.SupportedSituations.Length
            * LongTermStudyExperiment.SlicesPerYear,
        subjects.Count);

    foreach (var s in LongTermStudyExperiment.SupportedSituations) {
      for (int i = 0; i < LongTermStudyExperiment.SlicesPerYear; i++) {
        Assert.IsTrue(subjects.Contains((s, i)),
            "missing (situation, slice): " + s + "/" + i);
      }
    }
  }

  [TestMethod]
  public void LongTermStudy_AllSubjectsFor_BodyAgnosticToday() {
    // Body filter is a future-proofing seam; today every body yields
    // the same subject inventory. Lock it down so a regression on
    // either side is loud.
    var kerbin = LongTermStudyExperiment.AllSubjectsFor("Kerbin").ToList();
    var jool   = LongTermStudyExperiment.AllSubjectsFor("Jool").ToList();
    CollectionAssert.AreEqual(kerbin, jool);
  }
}
