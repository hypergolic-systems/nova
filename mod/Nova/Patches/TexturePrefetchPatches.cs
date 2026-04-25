using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using DDSHeaders;
using HarmonyLib;
using Nova;
using UnityEngine;

namespace Nova.Patches;

[HarmonyPatch(typeof(DatabaseLoaderTexture_DDS), "Read")]
static class TexturePrefetchPatch {
  static ConcurrentDictionary<string, Task<byte[]>> prefetchTasks;
  static bool prefetchStarted;
  static readonly FieldInfo isNormalMapField =
    AccessTools.Field(typeof(DatabaseLoaderTexture_DDS), "isNormalMap");

  // Timing
  static readonly Stopwatch waitTimer = new();
  static readonly Stopwatch parseTimer = new();
  static readonly Stopwatch gpuTimer = new();
  static int loadCount;
  static bool reported;

  static bool Prefix(DatabaseLoaderTexture_DDS __instance, string filename, ref Texture2D __result) {
    if (!prefetchStarted) {
      prefetchStarted = true;
      prefetchTasks = new ConcurrentDictionary<string, Task<byte[]>>();
      int count = 0;
      foreach (var file in GameDatabase.Instance.root.GetFiles(UrlDir.FileType.Texture)) {
        if (file.fileExtension == "dds") {
          var path = file.fullPath;
          prefetchTasks[path] = Task.Run(() => File.ReadAllBytes(path));
          count++;
        }
      }
      NovaLog.Log($"[TexturePrefetch] Started prefetch of {count} DDS files");
    }

    waitTimer.Start();
    byte[] data;
    if (prefetchTasks.TryRemove(filename, out var task)) {
      data = task.Result;
    } else {
      data = File.ReadAllBytes(filename);
    }
    waitTimer.Stop();

    __result = ParseDDS(__instance, data);
    loadCount++;

    if (prefetchTasks.IsEmpty && !reported) {
      reported = true;
      NovaLog.Log($"[TexturePrefetch] Loaded {loadCount} DDS textures:");
      NovaLog.Log($"[TexturePrefetch]   Wait/read: {waitTimer.Elapsed.TotalSeconds:F2}s");
      NovaLog.Log($"[TexturePrefetch]   Parse:     {parseTimer.Elapsed.TotalSeconds:F2}s");
      NovaLog.Log($"[TexturePrefetch]   GPU:       {gpuTimer.Elapsed.TotalSeconds:F2}s");
    }

    return false;
  }

  static Texture2D ParseDDS(DatabaseLoaderTexture_DDS instance, byte[] data) {
    parseTimer.Start();
    var reader = new BinaryReader(new MemoryStream(data));

    if (reader.ReadUInt32() != DDSValues.uintMagic) {
      parseTimer.Stop();
      UnityEngine.Debug.LogError("DDS: File is not a DDS format file!");
      return null;
    }

    var header = new DDSHeader(reader);
    if (header.ddspf.dwFourCC == DDSValues.uintDX10)
      new DDSHeaderDX10(reader);

    bool mipChain = (header.dwCaps & DDSPixelFormatCaps.MIPMAP) != 0;

    bool isNormal = (header.ddspf.dwFlags & 0x80000) != 0 ||
                    (header.ddspf.dwFlags & 0x80000000u) != 0;
    isNormalMapField.SetValue(instance, isNormal);

    byte[] texData = reader.ReadBytes((int)(reader.BaseStream.Length - reader.BaseStream.Position));
    parseTimer.Stop();

    Texture2D texture = null;

    gpuTimer.Start();
    if (header.ddspf.dwFourCC == DDSValues.uintDXT1) {
      texture = new Texture2D((int)header.dwWidth, (int)header.dwHeight, TextureFormat.DXT1, mipChain);
      texture.LoadRawTextureData(texData);
      texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
    } else if (header.ddspf.dwFourCC == DDSValues.uintDXT3) {
      UnityEngine.Debug.LogError($"DDS: DXT3({header.dwWidth}x{header.dwHeight}, MipMap={mipChain}) - DXT3 format is NOT supported. Use DXT5");
    } else if (header.ddspf.dwFourCC == DDSValues.uintDXT5) {
      texture = new Texture2D((int)header.dwWidth, (int)header.dwHeight, TextureFormat.DXT5, mipChain);
      texture.LoadRawTextureData(texData);
      texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
    } else if (header.ddspf.dwFourCC == DDSValues.uintDXT2) {
      UnityEngine.Debug.Log("DDS: DXT2 is not supported!");
    } else if (header.ddspf.dwFourCC == DDSValues.uintDXT4) {
      UnityEngine.Debug.Log("DDS: DXT4 is not supported!");
    } else if (header.ddspf.dwFourCC == DDSValues.uintDX10) {
      UnityEngine.Debug.Log("DDS: DX10 formats not supported");
    }
    gpuTimer.Stop();

    return texture;
  }
}
