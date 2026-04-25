using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Nova;

public static class NovaLog {
  private static readonly StreamWriter logWriter;
  private static readonly string logFilePath;
  private static readonly Queue<string> recentMessages = new Queue<string>();
  private const int MaxRecentMessages = 1000;

  public static Queue<string> RecentMessages => recentMessages;

  static NovaLog() {
    try {
      string logDir = Path.Combine(KSPUtil.ApplicationRootPath, "Logs");
      if (!Directory.Exists(logDir))
        Directory.CreateDirectory(logDir);
        
      logFilePath = Path.Combine(logDir, $"HypergolicSystems_{DateTime.Now:yyyy-MM-dd_HHmmss}.log");
      logWriter = new StreamWriter(logFilePath, true) { AutoFlush = true };
      
      Log($"Hypergolic Systems Logger initialized - {logFilePath}");
    } catch (Exception ex) {
      Debug.LogError($"[Nova] Failed to initialize file logger: {ex.Message}");
    }
  }
  
  public static void Log(string message) {
    string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
    string formattedMessage = $"[{timestamp}] [Nova] {message}";

    // Write to Unity Debug Log (for in-game console)
    Debug.Log($"[Nova] {message}");

    // Write to our log file
    try {
      logWriter?.WriteLine(formattedMessage);
    } catch { }

    EnqueueRecent(formattedMessage);
  }
  
  public static void LogWarning(string message) {
    string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
    string formattedMessage = $"[{timestamp}] [Nova WARN] {message}";

    Debug.LogWarning($"[Nova] {message}");

    try {
      logWriter?.WriteLine(formattedMessage);
    } catch { }

    EnqueueRecent(formattedMessage);
  }
  
  public static void LogError(string message) {
    string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
    string formattedMessage = $"[{timestamp}] [Nova ERROR] {message}";

    Debug.LogError($"[Nova] {message}");

    try {
      logWriter?.WriteLine(formattedMessage);
    } catch { }

    EnqueueRecent(formattedMessage);
  }
  
  public static void LogException(Exception ex) {
    LogError($"Exception: {ex.Message}\n{ex.StackTrace}");
  }
  
  public static void Flush() {
    try {
      logWriter?.Flush();
    } catch { }
  }
  
  public static void Close() {
    try {
      logWriter?.Close();
      logWriter?.Dispose();
    } catch { }
  }

  private static void EnqueueRecent(string message) {
    recentMessages.Enqueue(message);
    while (recentMessages.Count > MaxRecentMessages)
      recentMessages.Dequeue();
  }
}