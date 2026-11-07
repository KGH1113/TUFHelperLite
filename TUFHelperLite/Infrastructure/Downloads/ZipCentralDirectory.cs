using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TUFHelperLite.Infrastructure.Downloads;

internal sealed class ZipRawEntryName
{
  public ZipRawEntryName(byte[] bytes, bool utf8Flag, string unicodePath)
  {
    Bytes = bytes;
    Utf8Flag = utf8Flag;
    UnicodePath = unicodePath;
  }

  public byte[] Bytes { get; }
  public bool Utf8Flag { get; }

  // UTF-8 name from the Info-ZIP Unicode Path field (0x7075), when present and current.
  public string UnicodePath { get; }
}

// Reads entry names straight from the zip central directory. ZipArchive hides the
// raw name bytes and the Info-ZIP Unicode Path field, where Windows archivers
// such as Bandizip and 7-Zip keep the real name next to the code page bytes.
internal static class ZipCentralDirectory
{
  private const uint EndRecordSignature = 0x06054b50;
  private const uint Zip64LocatorSignature = 0x07064b50;
  private const uint Zip64EndRecordSignature = 0x06064b50;
  private const uint EntrySignature = 0x02014b50;
  private const ushort UnicodePathExtraId = 0x7075;
  private const int EndRecordSize = 22;
  private const int Zip64LocatorSize = 20;
  private const int Zip64EndRecordSize = 56;
  private const int EntryHeaderSize = 46;
  private const long MaxDirectorySize = 64L * 1024 * 1024;

  private static readonly UTF8Encoding StrictUtf8 = new(false, true);
  private static readonly uint[] CrcTable = CreateCrcTable();

  // Returns null when the directory can't be read, so callers keep ZipArchive's names.
  public static List<ZipRawEntryName> TryRead(Stream stream)
  {
    try
    {
      return Read(stream);
    }
    catch (Exception e) when (e is IOException || e is ArgumentException || e is OverflowException)
    {
      return null;
    }
  }

  private static List<ZipRawEntryName> Read(Stream stream)
  {
    long length = stream.Length;
    if (length < EndRecordSize) return null;

    int tailLength = (int)Math.Min(length, EndRecordSize + ushort.MaxValue);
    long tailStart = length - tailLength;
    byte[] tail = ReadAt(stream, tailStart, tailLength);
    int end = -1;
    for (int i = tail.Length - EndRecordSize; i >= 0; i--)
    {
      if (ReadUInt32(tail, i) == EndRecordSignature)
      {
        end = i;
        break;
      }
    }

    if (end < 0) return null;

    long entryCount = ReadUInt16(tail, end + 10);
    long directorySize = ReadUInt32(tail, end + 12);
    long directoryOffset = ReadUInt32(tail, end + 16);

    if (entryCount == ushort.MaxValue || directorySize == uint.MaxValue || directoryOffset == uint.MaxValue)
    {
      long locatorOffset = tailStart + end - Zip64LocatorSize;
      if (locatorOffset < 0) return null;

      byte[] locator = ReadAt(stream, locatorOffset, Zip64LocatorSize);
      if (ReadUInt32(locator, 0) != Zip64LocatorSignature) return null;

      long recordOffset = checked((long)ReadUInt64(locator, 8));
      if (recordOffset < 0 || recordOffset > length - Zip64EndRecordSize) return null;

      byte[] record = ReadAt(stream, recordOffset, Zip64EndRecordSize);
      if (ReadUInt32(record, 0) != Zip64EndRecordSignature) return null;

      entryCount = checked((long)ReadUInt64(record, 32));
      directorySize = checked((long)ReadUInt64(record, 40));
      directoryOffset = checked((long)ReadUInt64(record, 48));
    }

    if (directorySize > MaxDirectorySize || directoryOffset < 0 || directoryOffset > length - directorySize) return null;

    byte[] directory = ReadAt(stream, directoryOffset, (int)directorySize);
    List<ZipRawEntryName> names = new();
    int position = 0;
    for (long index = 0; index < entryCount; index++)
    {
      if (position > directory.Length - EntryHeaderSize || ReadUInt32(directory, position) != EntrySignature) return null;

      ushort flags = ReadUInt16(directory, position + 8);
      int nameLength = ReadUInt16(directory, position + 28);
      int extraLength = ReadUInt16(directory, position + 30);
      int commentLength = ReadUInt16(directory, position + 32);
      int nameStart = position + EntryHeaderSize;
      int extraStart = nameStart + nameLength;
      int next = extraStart + extraLength + commentLength;
      if (next > directory.Length) return null;

      byte[] name = new byte[nameLength];
      Buffer.BlockCopy(directory, nameStart, name, 0, nameLength);
      names.Add(new ZipRawEntryName(name, (flags & 0x800) != 0, ReadUnicodePath(directory, extraStart, extraLength, name)));
      position = next;
    }

    return names;
  }

  private static string ReadUnicodePath(byte[] data, int start, int length, byte[] headerName)
  {
    int position = start;
    int end = start + length;
    while (position <= end - 4)
    {
      ushort id = ReadUInt16(data, position);
      int size = ReadUInt16(data, position + 2);
      int body = position + 4;
      if (size > end - body) return null;

      // Layout: version (1), CRC-32 of the header name (4), UTF-8 name. A CRC
      // mismatch means the entry was renamed by a tool that ignored this field.
      if (id == UnicodePathExtraId && size > 5 && data[body] == 1 && ReadUInt32(data, body + 1) == Crc32(headerName))
      {
        try
        {
          return StrictUtf8.GetString(data, body + 5, size - 5);
        }
        catch (ArgumentException)
        {
          return null;
        }
      }

      position = body + size;
    }

    return null;
  }

  private static byte[] ReadAt(Stream stream, long offset, int count)
  {
    byte[] buffer = new byte[count];
    stream.Seek(offset, SeekOrigin.Begin);
    int read = 0;
    while (read < count)
    {
      int bytesRead = stream.Read(buffer, read, count - read);
      if (bytesRead <= 0) throw new EndOfStreamException();
      read += bytesRead;
    }

    return buffer;
  }

  private static ushort ReadUInt16(byte[] data, int offset) => (ushort)(data[offset] | data[offset + 1] << 8);

  private static uint ReadUInt32(byte[] data, int offset) => (uint)(ReadUInt16(data, offset) | ReadUInt16(data, offset + 2) << 16);

  private static ulong ReadUInt64(byte[] data, int offset) => ReadUInt32(data, offset) | (ulong)ReadUInt32(data, offset + 4) << 32;

  private static uint Crc32(byte[] data)
  {
    uint crc = uint.MaxValue;
    foreach (byte value in data) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
    return ~crc;
  }

  private static uint[] CreateCrcTable()
  {
    uint[] table = new uint[256];
    for (uint i = 0; i < table.Length; i++)
    {
      uint value = i;
      for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320 : value >> 1;
      table[i] = value;
    }

    return table;
  }
}
