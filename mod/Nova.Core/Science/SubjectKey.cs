using System;

namespace Nova.Core.Science;

// Stable, value-equal identifier for "what this data is about". Round-trips
// to a deterministic string ID so equality is preserved across save/load.
//
// Format: "<expId>@<bodyName>:<variant>[:<sliceIndex>]"
//   atm-profile@Kerbin:troposphere
//   lts@Kerbin:SrfLanded:7
//
// Variant carries the experiment-specific situation (a layer name for atm
// profile, a Situation enum name for long-term study). SliceIndex is
// optional and only used by experiments that subdivide a subject in time.
public readonly struct SubjectKey : IEquatable<SubjectKey> {
  public string ExperimentId { get; }
  public string BodyName     { get; }
  public string Variant      { get; }
  public int?   SliceIndex   { get; }

  public SubjectKey(string experimentId, string bodyName, string variant, int? sliceIndex = null) {
    if (string.IsNullOrEmpty(experimentId)) throw new ArgumentException("experimentId required", nameof(experimentId));
    if (string.IsNullOrEmpty(bodyName))     throw new ArgumentException("bodyName required",     nameof(bodyName));
    if (string.IsNullOrEmpty(variant))      throw new ArgumentException("variant required",      nameof(variant));
    if (experimentId.IndexOfAny(new[] { '@', ':' }) >= 0)
      throw new ArgumentException("experimentId must not contain '@' or ':'", nameof(experimentId));
    if (bodyName.IndexOfAny(new[] { '@', ':' }) >= 0)
      throw new ArgumentException("bodyName must not contain '@' or ':'", nameof(bodyName));
    if (variant.IndexOfAny(new[] { '@', ':' }) >= 0)
      throw new ArgumentException("variant must not contain '@' or ':'", nameof(variant));
    ExperimentId = experimentId;
    BodyName     = bodyName;
    Variant      = variant;
    SliceIndex   = sliceIndex;
  }

  public override string ToString() {
    return SliceIndex.HasValue
        ? $"{ExperimentId}@{BodyName}:{Variant}:{SliceIndex.Value}"
        : $"{ExperimentId}@{BodyName}:{Variant}";
  }

  public static bool TryParse(string s, out SubjectKey key) {
    key = default;
    if (string.IsNullOrEmpty(s)) return false;
    int at = s.IndexOf('@');
    if (at <= 0 || at == s.Length - 1) return false;

    string exp = s.Substring(0, at);
    string rest = s.Substring(at + 1);
    int firstColon = rest.IndexOf(':');
    if (firstColon <= 0 || firstColon == rest.Length - 1) return false;

    string body = rest.Substring(0, firstColon);
    string tail = rest.Substring(firstColon + 1);

    int secondColon = tail.IndexOf(':');
    string variant;
    int? slice = null;
    if (secondColon < 0) {
      variant = tail;
    } else {
      variant = tail.Substring(0, secondColon);
      string sliceStr = tail.Substring(secondColon + 1);
      if (!int.TryParse(sliceStr, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var i)) return false;
      slice = i;
    }
    if (string.IsNullOrEmpty(variant)) return false;
    try {
      key = new SubjectKey(exp, body, variant, slice);
      return true;
    } catch (ArgumentException) {
      return false;
    }
  }

  public bool Equals(SubjectKey o) =>
      ExperimentId == o.ExperimentId &&
      BodyName     == o.BodyName     &&
      Variant      == o.Variant      &&
      SliceIndex   == o.SliceIndex;

  public override bool Equals(object obj) => obj is SubjectKey k && Equals(k);

  public override int GetHashCode() {
    unchecked {
      int h = ExperimentId?.GetHashCode() ?? 0;
      h = h * 397 ^ (BodyName?.GetHashCode() ?? 0);
      h = h * 397 ^ (Variant?.GetHashCode() ?? 0);
      h = h * 397 ^ (SliceIndex?.GetHashCode() ?? 0);
      return h;
    }
  }

  public static bool operator ==(SubjectKey a, SubjectKey b) =>  a.Equals(b);
  public static bool operator !=(SubjectKey a, SubjectKey b) => !a.Equals(b);
}
