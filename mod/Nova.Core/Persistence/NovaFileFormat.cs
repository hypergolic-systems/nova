using System;
using Nova.Core.Persistence.Protos;
using System.IO;
using ProtoBuf;

namespace Nova.Core.Persistence;

/// <summary>
/// HGS binary file format prefix: 8 bytes.
/// Bytes 0-2: 'H' 'G' 'S'
/// Byte 3:    'C' (craft) or 'S' (save)
/// Bytes 4-7: version (uint32 little-endian)
/// </summary>
public static class NovaFileFormat {
  public const uint CurrentVersion = 2;

  public static void WritePrefix(Stream stream, char type) {
    stream.WriteByte((byte)'H');
    stream.WriteByte((byte)'G');
    stream.WriteByte((byte)'S');
    stream.WriteByte((byte)type);
    var versionBytes = BitConverter.GetBytes(CurrentVersion);
    stream.Write(versionBytes, 0, 4);
  }

  public static (char type, uint version) ReadPrefix(Stream stream) {
    var header = new byte[8];
    int read = stream.Read(header, 0, 8);
    if (read < 8)
      throw new InvalidDataException("File too short for HGS header");
    if (header[0] != 'H' || header[1] != 'G' || header[2] != 'S')
      throw new InvalidDataException($"Invalid HGS magic: {(char)header[0]}{(char)header[1]}{(char)header[2]}");

    char type = (char)header[3];
    if (type != 'C' && type != 'S')
      throw new InvalidDataException($"Unknown HGS file type: '{type}'");

    uint version = BitConverter.ToUInt32(header, 4);
    return (type, version);
  }

  /// <summary>
  /// Read only the CraftMetadata (field 1) from a craft file stream,
  /// without parsing the Vessel payload. Stream must be positioned
  /// after the 8-byte HGS header.
  /// </summary>
  public static CraftMetadata ReadCraftMetadata(Stream stream) {
    // Protobuf wire format: field 1, wire type 2 (length-delimited) = tag byte 0x0A.
    // Read tag, then varint length, then exactly that many bytes.
    int tag = stream.ReadByte();
    if (tag != 0x0A) // field 1, wire type 2
      throw new InvalidDataException($"Expected field 1 tag (0x0A), got 0x{tag:X2}");

    int length = ReadVarint(stream);
    var buf = new byte[length];
    int read = stream.Read(buf, 0, length);
    if (read < length)
      throw new InvalidDataException($"Expected {length} bytes for CraftMetadata, got {read}");

    using (var ms = new MemoryStream(buf))
      return Serializer.Deserialize<CraftMetadata>(ms);
  }

  static int ReadVarint(Stream stream) {
    int result = 0, shift = 0;
    while (true) {
      int b = stream.ReadByte();
      if (b < 0) throw new InvalidDataException("Unexpected end of stream in varint");
      result |= (b & 0x7F) << shift;
      if ((b & 0x80) == 0) return result;
      shift += 7;
    }
  }
}
