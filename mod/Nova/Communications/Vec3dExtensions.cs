using UnityEngine;
using Nova.Core.Utils;

namespace Nova.Communications;

public static class Vec3dExtensions {
  public static Vec3d ToNova(this Vector3d v) => new Vec3d(v.x, v.y, v.z);
}
