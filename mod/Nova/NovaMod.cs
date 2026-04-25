namespace Nova;

using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using Google.OrTools.LinearSolver;
using Nova;
using Nova.Core.Components;
using System.Linq;

[KSPAddon(KSPAddon.Startup.Instantly, true)]
public class HypergolicSystemsMod : MonoBehaviour {
  private static HypergolicSystemsMod instance;

  public static HypergolicSystemsMod Instance {
    get {
      if (instance == null) {
        instance = FindObjectOfType<HypergolicSystemsMod>();
        if (instance == null) {
          GameObject go = new GameObject("HypergolicSystemsMod");
          instance = go.AddComponent<HypergolicSystemsMod>();
          DontDestroyOnLoad(go);
        }
      }
      return instance;
    }
  }

  public static bool IsInitialized => instance != null;

  private bool initialized = false;

  void Awake() {
    if (instance != null && instance != this) {
      Destroy(gameObject);
      return;
    }

    instance = this;
    DontDestroyOnLoad(gameObject);

    Initialize();
  }

  private void Initialize() {
    if (initialized) {
      return;
    }


    initialized = true;

    NovaLog.Log("Initializing...");

    InitializeSystems();

    foreach (var type in Assembly.GetAssembly(typeof(VirtualComponent)).GetTypes()) {
      if (!type.IsNested)
        Registry.MaybeRegister(type);
    }


    NovaLog.Log("Online!");
  }

  private void InitializeSystems() {
    HarmonyPatcher.Initialize();
    TestOrTools();
  }

  private void TestOrTools() {
    // Create a simple linear programming solver
    Solver solver = Solver.CreateSolver("GLOP");
    if (solver == null) {
      throw new Exception("OrTools initialization failed");
    }

    // Create variables: x >= 0, y >= 0
    Variable x = solver.MakeNumVar(0.0, double.PositiveInfinity, "x");
    Variable y = solver.MakeNumVar(0.0, double.PositiveInfinity, "y");

    // Constraint: x + 2y <= 14
    Constraint ct1 = solver.MakeConstraint(double.NegativeInfinity, 14.0, "ct1");
    ct1.SetCoefficient(x, 1);
    ct1.SetCoefficient(y, 2);

    // Constraint: 3x - y >= 0
    Constraint ct2 = solver.MakeConstraint(0.0, double.PositiveInfinity, "ct2");
    ct2.SetCoefficient(x, 3);
    ct2.SetCoefficient(y, -1);

    // Constraint: x - y <= 2
    Constraint ct3 = solver.MakeConstraint(double.NegativeInfinity, 2.0, "ct3");
    ct3.SetCoefficient(x, 1);
    ct3.SetCoefficient(y, -1);

    // Objective: maximize 3x + 4y
    Objective objective = solver.Objective();
    objective.SetCoefficient(x, 3);
    objective.SetCoefficient(y, 4);
    objective.SetMaximization();

    // Solve
    Solver.ResultStatus resultStatus = solver.Solve();

    if (resultStatus == Solver.ResultStatus.OPTIMAL) {
      NovaLog.Log($"OrTools successfully loaded");
    } else {
      throw new Exception($"OrTools test failed with status: {resultStatus}");
    }
  }
}
