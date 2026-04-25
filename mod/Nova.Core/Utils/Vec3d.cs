using System;

namespace Nova.Core.Utils;

public struct Vec3d {
  public double X, Y, Z;

  public Vec3d(double x, double y, double z) {
    X = x; Y = y; Z = z;
  }

  public static Vec3d Zero => new(0, 0, 0);

  public double Magnitude => Math.Sqrt(X * X + Y * Y + Z * Z);
  public double SqrMagnitude => X * X + Y * Y + Z * Z;

  public Vec3d Normalized {
    get {
      var m = Magnitude;
      if (m < 1e-15) return Zero;
      return new Vec3d(X / m, Y / m, Z / m);
    }
  }

  public static Vec3d operator +(Vec3d a, Vec3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
  public static Vec3d operator -(Vec3d a, Vec3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
  public static Vec3d operator -(Vec3d a) => new(-a.X, -a.Y, -a.Z);
  public static Vec3d operator *(double s, Vec3d v) => new(s * v.X, s * v.Y, s * v.Z);
  public static Vec3d operator *(Vec3d v, double s) => new(s * v.X, s * v.Y, s * v.Z);

  public static double Dot(Vec3d a, Vec3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

  public static Vec3d Cross(Vec3d a, Vec3d b) => new(
    a.Y * b.Z - a.Z * b.Y,
    a.Z * b.X - a.X * b.Z,
    a.X * b.Y - a.Y * b.X
  );

  public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
}
