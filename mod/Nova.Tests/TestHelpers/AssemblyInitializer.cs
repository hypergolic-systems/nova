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

      // Check for native libraries
      string[] expectedLibraries = new[]
      {
                    "google-ortools-native.dylib",
                    "libgoogle-ortools-native.dylib",
                    "libortools.9.dylib"
                };

      foreach (var libName in expectedLibraries) {
        string libPath = Path.Combine(assemblyDir, libName);
        bool exists = File.Exists(libPath);
        Console.WriteLine($"  {libName}: {(exists ? "FOUND" : "NOT FOUND")}");

        if (exists) {
          var info = new FileInfo(libPath);
          Console.WriteLine($"    Size: {info.Length} bytes");
        }
      }

      // Add assembly directory to PATH for native library resolution
      string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
      if (!currentPath.Contains(assemblyDir)) {
        Environment.SetEnvironmentVariable("PATH",
            assemblyDir + Path.PathSeparator + currentPath);
        Console.WriteLine($"Added {assemblyDir} to PATH");
      }

      // Also set DYLD_LIBRARY_PATH for macOS
      string dyldPath = Environment.GetEnvironmentVariable("DYLD_LIBRARY_PATH") ?? "";
      if (!dyldPath.Contains(assemblyDir)) {
        Environment.SetEnvironmentVariable("DYLD_LIBRARY_PATH",
            assemblyDir + Path.PathSeparator + dyldPath);
        Console.WriteLine($"Added {assemblyDir} to DYLD_LIBRARY_PATH");
      }
    } catch (Exception ex) {
      Console.WriteLine($"Error setting up native library paths: {ex.Message}");
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
