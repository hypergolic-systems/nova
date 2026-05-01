using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Science;

namespace Nova.Tests.Science;

[TestClass]
public class SubjectKeyTests {

  [TestMethod]
  public void AtmProfile_RoundTrips() {
    var key = new SubjectKey("atm-profile", "Kerbin", "troposphere");
    Assert.AreEqual("atm-profile@Kerbin:troposphere", key.ToString());
    Assert.IsTrue(SubjectKey.TryParse(key.ToString(), out var parsed));
    Assert.AreEqual(key, parsed);
  }

  [TestMethod]
  public void Lts_WithSlice_RoundTrips() {
    var key = new SubjectKey("lts", "Kerbin", "SrfLanded", 7);
    Assert.AreEqual("lts@Kerbin:SrfLanded:7", key.ToString());
    Assert.IsTrue(SubjectKey.TryParse(key.ToString(), out var parsed));
    Assert.AreEqual(key, parsed);
    Assert.AreEqual(7, parsed.SliceIndex);
  }

  [TestMethod]
  public void Lts_SliceZero_RoundTrips() {
    var key = new SubjectKey("lts", "Mun", "InSpaceLow", 0);
    Assert.AreEqual("lts@Mun:InSpaceLow:0", key.ToString());
    Assert.IsTrue(SubjectKey.TryParse(key.ToString(), out var parsed));
    Assert.AreEqual(key, parsed);
  }

  [TestMethod]
  public void Equality_ByValue() {
    var a = new SubjectKey("lts", "Kerbin", "SrfLanded", 3);
    var b = new SubjectKey("lts", "Kerbin", "SrfLanded", 3);
    var c = new SubjectKey("lts", "Kerbin", "SrfLanded", 4);
    Assert.AreEqual(a, b);
    Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    Assert.AreNotEqual(a, c);
  }

  [TestMethod]
  public void TryParse_RejectsMalformed() {
    Assert.IsFalse(SubjectKey.TryParse("", out _));
    Assert.IsFalse(SubjectKey.TryParse("noatsign", out _));
    Assert.IsFalse(SubjectKey.TryParse("@noexp:Kerbin:x", out _));
    Assert.IsFalse(SubjectKey.TryParse("exp@", out _));
    Assert.IsFalse(SubjectKey.TryParse("exp@body", out _));     // no variant
    Assert.IsFalse(SubjectKey.TryParse("exp@body:", out _));    // empty variant
    Assert.IsFalse(SubjectKey.TryParse("exp@body:variant:notnumeric", out _));
  }

  [TestMethod]
  public void Constructor_RejectsReservedChars() {
    Assert.ThrowsException<System.ArgumentException>(
        () => new SubjectKey("ex@p", "Kerbin", "x"));
    Assert.ThrowsException<System.ArgumentException>(
        () => new SubjectKey("exp", "Ker:bin", "x"));
    Assert.ThrowsException<System.ArgumentException>(
        () => new SubjectKey("exp", "Kerbin", "x:y"));
  }
}
