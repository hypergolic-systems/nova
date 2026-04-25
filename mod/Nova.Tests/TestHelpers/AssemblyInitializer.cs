namespace Nova.Tests.TestHelpers;

using System;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public static class AssemblyInitializer {
  [AssemblyInitialize]
  public static void AssemblyInit(TestContext context) {
    Console.WriteLine("=== Test Assembly Initialization ===");

    // Setup native library paths for OR-Tools
    SetupNativeLibraryPaths();

    // Verify OR-Tools can be loaded
    VerifyOrToolsLoading();

    // Register VirtualComponent types needed by tests (skip nested types —
    // Editor subtypes are discovered by MaybeRegister via GetNestedType).
    foreach (var type in typeof(Core.Components.VirtualComponent).Assembly.GetTypes()) {
      if (!type.IsNested)
        Core.Components.Registry.MaybeRegister(type);
    }

    Console.WriteLine("=== Initialization Complete ===");
  }

  private static void SetupNativeLibraryPaths() {
    try {
      string assemblyLocation = Assembly.GetExecutingAssembly().Location;
      string assemblyDir = Path.GetDirectoryName(assemblyLocation);

      Console.WriteLine($"Test assembly directory: {assemblyDir}");

      // Diagnostic: surface which native libs the host should pick up.
      // The csproj copies whatever the host RID's runtime package ships.
      string[] expectedLibraries = ExpectedNativeLibraries();
      foreach (var libName in expectedLibraries) {
        string libPath = Path.Combine(assemblyDir, libName);
        bool exists = File.Exists(libPath);
        Console.WriteLine($"  {libName}: {(exists ? "FOUND" : "NOT FOUND")}");
        if (exists) {
          Console.WriteLine($"    Size: {new FileInfo(libPath).Length} bytes");
        }
      }

      // Add assembly directory to PATH (Windows) and platform-specific
      // dylib search-path env var (DYLD on macOS, LD on Linux). Belt &
      // suspenders — Mono usually picks up native libs from the assembly
      // directory automatically, but the env vars cost nothing.
      PrependEnv("PATH", assemblyDir);
      switch (Environment.OSVersion.Platform) {
        case PlatformID.Unix:
          PrependEnv(IsMacOS() ? "DYLD_LIBRARY_PATH" : "LD_LIBRARY_PATH", assemblyDir);
          break;
      }
    } catch (Exception ex) {
      Console.WriteLine($"Error setting up native library paths: {ex.Message}");
    }
  }

  private static string[] ExpectedNativeLibraries() {
    if (IsMacOS())
      return new[] { "google-ortools-native.dylib", "libgoogle-ortools-native.dylib", "libortools.9.dylib" };
    if (Environment.OSVersion.Platform == PlatformID.Unix)
      return new[] { "libgoogle-ortools-native.so", "libortools.so.9" };
    return new[] { "google-ortools-native.dll", "ortools.dll" };
  }

  private static bool IsMacOS() {
    // .NET 4.x: PlatformID.MacOSX exists but is rarely reported; the
    // Mono runtime returns Unix even on macOS. Disambiguate by file.
    return Environment.OSVersion.Platform == PlatformID.MacOSX
        || (Environment.OSVersion.Platform == PlatformID.Unix && Directory.Exists("/Applications"));
  }

  private static void PrependEnv(string name, string dir) {
    string current = Environment.GetEnvironmentVariable(name) ?? "";
    if (!current.Contains(dir)) {
      Environment.SetEnvironmentVariable(name, dir + Path.PathSeparator + current);
      Console.WriteLine($"Added {dir} to {name}");
    }
  }

  private static void VerifyOrToolsLoading() {
    try {
      Console.WriteLine("Verifying OR-Tools can be loaded...");

      // Try to create a simple solver to verify OR-Tools works
      var solver = Google.OrTools.LinearSolver.Solver.CreateSolver("GLOP");

      if (solver != null) {
        Console.WriteLine("OR-Tools loaded successfully!");
        solver.Dispose();
      } else {
        Console.WriteLine("OR-Tools solver creation returned null");
      }
    } catch (Exception ex) {
      Console.WriteLine($"OR-Tools loading failed: {ex.GetType().Name}: {ex.Message}");
      Console.WriteLine("Note: Tests may fail if they depend on OR-Tools");

      // Log inner exceptions
      var inner = ex.InnerException;
      int depth = 1;
      while (inner != null) {
        Console.WriteLine($"  Inner exception {depth}: {inner.GetType().Name}: {inner.Message}");
        inner = inner.InnerException;
        depth++;
      }
    }
  }

  [AssemblyCleanup]
  public static void AssemblyCleanup() {
    Console.WriteLine("Test assembly cleanup");
  }
}
