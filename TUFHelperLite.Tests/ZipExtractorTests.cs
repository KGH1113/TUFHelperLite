using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using TUFHelperLite.Infrastructure.Downloads;

internal static partial class Program
{
  private const byte ZipHostWindows = 0;
  private const byte ZipHostUnix = 3;

  private static void RunZipExtractorTests()
  {
    string root = Path.Combine(Path.GetTempPath(), "tufhelper-zip-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      Encoding shiftJis = Encoding.GetEncoding(932);
      Encoding korean = Encoding.GetEncoding(949);
      byte[] level = Encoding.UTF8.GetBytes("{\"settings\":{}}");

      string japanese = "初音ミク - 千本桜";
      string destination = ExtractTestZip(root, "sjis", new[]
      {
        Entry(japanese + "/" + japanese + ".adofai", shiftJis, false, level),
        // ソ and 表 end in 0x5C, the byte for '\'.
        Entry(japanese + "/ソング表示.ogg", shiftJis, false, new byte[] { 1 }),
        Entry(japanese + "/ソ", shiftJis, false, new byte[] { 2 })
      });
      CheckTrue("Shift_JIS names without the UTF-8 flag are decoded", File.Exists(Path.Combine(destination, japanese, japanese + ".adofai")));
      CheckTrue("Shift_JIS 0x5C trail bytes are not path separators", File.Exists(Path.Combine(destination, japanese, "ソング表示.ogg")));
      CheckTrue("Shift_JIS name ending in 0x5C stays a file", File.Exists(Path.Combine(destination, japanese, "ソ")));

      destination = ExtractTestZip(root, "cp949", new[]
      {
        Entry("한글 레벨/레벨.adofai", korean, false, level),
        Entry("한글 레벨/노래 ♪.ogg", korean, false, new byte[] { 1 })
      });
      CheckTrue("CP949 names without the UTF-8 flag are decoded", File.Exists(Path.Combine(destination, "한글 레벨", "레벨.adofai")));
      CheckTrue("CP949 symbols are decoded", File.Exists(Path.Combine(destination, "한글 레벨", "노래 ♪.ogg")));

      destination = ExtractTestZip(root, "utf8-noflag", new[]
      {
        Entry("おみくじ/いめ44 「おみくじ」 feat. 初音ミク.ogg", Encoding.UTF8, false, new byte[] { 1 }),
        Entry("おみくじ/level.adofai", Encoding.UTF8, false, level)
      });
      CheckTrue("UTF-8 names without the flag are decoded",
        File.Exists(Path.Combine(destination, "おみくじ", "いめ44 「おみくじ」 feat. 初音ミク.ogg")));

      destination = ExtractTestZip(root, "utf8-flag", new[]
      {
        Entry("REFLECTION»«REFRACTION/level.adofai", Encoding.UTF8, true, level),
        Entry("readme.txt", Encoding.ASCII, false, new byte[] { 1 })
      });
      CheckTrue("flagged UTF-8 names in the Latin-1 range are kept",
        File.Exists(Path.Combine(destination, "REFLECTION»«REFRACTION", "level.adofai")));

      Encoding chinese = Encoding.GetEncoding(936);
      string remix = "はいよろこんで (Magens Remix)";
      destination = ExtractTestZip(root, "gbk-japanese", new[]
      {
        Entry(remix + "/level.adofai", chinese, false, level),
        Entry(remix + "/" + remix + ".ogg", chinese, false, new byte[] { 1 })
      });
      CheckTrue("Japanese names zipped on Chinese Windows are decoded",
        File.Exists(Path.Combine(destination, remix, remix + ".ogg")));

      destination = ExtractTestZip(root, "gbk-chinese", new[]
      {
        Entry("十字光/十字光.png", chinese, false, new byte[] { 1 }),
        Entry("十字光/level.adofai", chinese, false, level)
      });
      CheckTrue("GBK Chinese names are decoded", File.Exists(Path.Combine(destination, "十字光", "十字光.png")));

      byte[] gbkName = chinese.GetBytes("风屿.ogg");
      destination = ExtractTestZip(root, "unicode-path", new[]
      {
        new TestZipEntry(gbkName, false, ZipHostWindows, new byte[] { 1 }, UnicodePathExtra("风屿.ogg", gbkName)),
        Entry("level.adofai", Encoding.ASCII, false, level)
      });
      CheckTrue("Info-ZIP Unicode Path names are used", File.Exists(Path.Combine(destination, "风屿.ogg")));

      // CP949 bytes for 캡처 are also valid UTF-8 (ĸó), and the stale Unicode Path must be ignored.
      byte[] captureName = korean.GetBytes("캡처.PNG");
      destination = ExtractTestZip(root, "stale-unicode-path", new[]
      {
        new TestZipEntry(captureName, false, ZipHostWindows, new byte[] { 1 }, UnicodePathExtra("old.PNG", korean.GetBytes("old.PNG"))),
        Entry("level.adofai", Encoding.ASCII, false, level)
      });
      CheckTrue("CP949 names that are also valid UTF-8 stay Korean", File.Exists(Path.Combine(destination, "캡처.PNG")));
      CheckFalse("Unicode Path with a stale CRC is ignored", File.Exists(Path.Combine(destination, "old.PNG")));

      destination = ExtractTestZip(root, "invalid-lead", new[]
      {
        new TestZipEntry(new byte[] { 0xB1, (byte)'/', (byte)'x', (byte)'.', (byte)'o', (byte)'g', (byte)'g' }, false, ZipHostWindows, new byte[] { 1 }, null),
        Entry("level.adofai", Encoding.ASCII, false, level)
      });
      CheckTrue("an invalid lead byte does not swallow '/'",
        Directory.GetFiles(destination, "x.ogg", SearchOption.AllDirectories).Any(path => Path.GetDirectoryName(path) != destination));

      string rar = Path.Combine(root, "level.rar");
      File.WriteAllBytes(rar, new byte[] { (byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x01, 0x00 });
      string rarError = null;
      try { ZipExtractor.Extract(rar, Path.Combine(root, "rar"), CancellationToken.None); }
      catch (InvalidDataException exception) { rarError = exception.Message; }
      CheckTrue("RAR archives get a clear error", rarError != null && rarError.Contains("RAR"));

      string longName = new string('A', 300) + ".adofai";
      destination = ExtractTestZip(root, "long-name", new[] { Entry(longName, Encoding.UTF8, true, level) });
      string[] extracted = Directory.GetFiles(destination);
      CheckTrue("names over 255 characters are shortened", extracted.Length == 1 &&
        Path.GetFileName(extracted[0]).Length <= ZipEntryPaths.MaxComponentLength &&
        extracted[0].EndsWith(".adofai", StringComparison.Ordinal));

      // Longer than PATH_MAX on macOS (1024) and Linux (4096).
      string deepFolder = string.Join("/", Enumerable.Repeat(new string('B', 200), 25));
      destination = ExtractTestZip(root, "long-path", new[]
      {
        Entry(deepFolder + "/level.adofai", Encoding.UTF8, true, level),
        Entry(deepFolder + "/song.ogg", Encoding.UTF8, true, new byte[] { 1 })
      });
      string[] fallbackLevels = Directory.GetFiles(destination, "level.adofai", SearchOption.AllDirectories);
      CheckTrue("paths over the OS limit fall back to a short folder", fallbackLevels.Length == 1 &&
        Path.GetDirectoryName(fallbackLevels[0]) != destination &&
        Path.GetFileName(Path.GetDirectoryName(fallbackLevels[0])).StartsWith("~", StringComparison.Ordinal));
      CheckTrue("fallback keeps sibling files together",
        fallbackLevels.Length == 1 && File.Exists(Path.Combine(Path.GetDirectoryName(fallbackLevels[0]), "song.ogg")));

      destination = ExtractTestZip(root, "dot-segments", new[]
      {
        Entry("a/../b.adofai", Encoding.UTF8, true, level),
        Entry("./c.adofai", Encoding.UTF8, true, level)
      });
      CheckTrue("'..' inside the archive resolves", File.Exists(Path.Combine(destination, "b.adofai")));
      CheckTrue("'.' segments are ignored", File.Exists(Path.Combine(destination, "c.adofai")));

      bool escaped = false;
      try { ExtractTestZip(root, "escape", new[] { Entry("../evil.adofai", Encoding.UTF8, true, level) }); }
      catch (InvalidDataException) { escaped = true; }
      CheckTrue("entries escaping the destination are rejected", escaped);
      CheckFalse("rejected entry is not written", File.Exists(Path.Combine(root, "evil.adofai")));

      CheckString("Windows trims trailing dots and spaces", "Song", ZipEntryPaths.SanitizeComponent("Song. ", true));
      CheckString("Windows keeps an empty name valid", "_", ZipEntryPaths.SanitizeComponent("...", true));
      CheckString("Windows reserved names are prefixed", "_con.png", ZipEntryPaths.SanitizeComponent("con.png", true));
      CheckString("Windows reserved COM names are prefixed", "_COM1", ZipEntryPaths.SanitizeComponent("COM1", true));
      CheckString("Windows invalid characters are replaced", "Song_ _Title_ _x_", ZipEntryPaths.SanitizeComponent("Song: \"Title\" <x>", true));
      CheckString("non-Windows keeps valid characters", "Song: \"Title\". ", ZipEntryPaths.SanitizeComponent("Song: \"Title\". ", false));
      CheckString("control characters are replaced", "a_b", ZipEntryPaths.SanitizeComponent("a\u0001b", false));

      string shortened = ZipEntryPaths.Shorten(new string('가', 100) + ".ogg", ZipEntryPaths.FallbackFileNameLength, false);
      CheckTrue("shortened names keep the extension", shortened.Length <= ZipEntryPaths.FallbackFileNameLength &&
        shortened.EndsWith(".ogg", StringComparison.Ordinal));
      CheckTrue("shortened names fit 255 bytes", Encoding.UTF8.GetByteCount(shortened) <= 255);
      CheckFalse("different long names stay distinct", shortened ==
        ZipEntryPaths.Shorten(new string('가', 101) + ".ogg", ZipEntryPaths.FallbackFileNameLength, false));
    }
    catch (Exception exception)
    {
      Failures.Add("zip extractor tests threw: " + exception);
    }
    finally
    {
      try { Directory.Delete(root, true); } catch { }
    }
  }

  private static string ExtractTestZip(string root, string name, TestZipEntry[] entries)
  {
    string archive = Path.Combine(root, name + ".zip");
    string destination = Path.Combine(root, name);
    File.WriteAllBytes(archive, BuildTestZip(entries));
    ZipExtractor.Extract(archive, destination, CancellationToken.None);
    return destination;
  }

  private static TestZipEntry Entry(string name, Encoding encoding, bool utf8Flag, byte[] data) =>
    new(encoding.GetBytes(name), utf8Flag, utf8Flag ? ZipHostUnix : ZipHostWindows, data, null);

  // Info-ZIP Unicode Path extra field (0x7075), as written by Bandizip and 7-Zip.
  private static byte[] UnicodePathExtra(string name, byte[] headerName)
  {
    byte[] utf8 = Encoding.UTF8.GetBytes(name);
    using MemoryStream output = new();
    using BinaryWriter writer = new(output);
    writer.Write((ushort)0x7075);
    writer.Write((ushort)(5 + utf8.Length));
    writer.Write((byte)1);
    writer.Write(Crc32(headerName));
    writer.Write(utf8);
    writer.Flush();
    return output.ToArray();
  }

  // Writes stored entries directly so tests control the name bytes and the UTF-8 flag.
  private static byte[] BuildTestZip(IEnumerable<TestZipEntry> entries)
  {
    using MemoryStream output = new();
    using BinaryWriter writer = new(output);
    List<(TestZipEntry Entry, uint Offset, uint Crc)> written = new();

    foreach (TestZipEntry entry in entries)
    {
      uint offset = (uint)output.Position;
      uint crc = Crc32(entry.Data);
      writer.Write(0x04034b50u);
      WriteEntryHeader(writer, entry, crc);
      writer.Write((ushort)0);
      writer.Write(entry.Name);
      writer.Write(entry.Data);
      written.Add((entry, offset, crc));
    }

    uint directoryOffset = (uint)output.Position;
    foreach ((TestZipEntry entry, uint offset, uint crc) in written)
    {
      writer.Write(0x02014b50u);
      writer.Write((ushort)((entry.Host << 8) | 20));
      WriteEntryHeader(writer, entry, crc);
      writer.Write((ushort)(entry.Extra?.Length ?? 0));
      writer.Write((ushort)0);
      writer.Write((ushort)0);
      writer.Write((ushort)0);
      writer.Write(0u);
      writer.Write(offset);
      writer.Write(entry.Name);
      if (entry.Extra != null) writer.Write(entry.Extra);
    }

    uint directorySize = (uint)output.Position - directoryOffset;
    writer.Write(0x06054b50u);
    writer.Write((ushort)0);
    writer.Write((ushort)0);
    writer.Write((ushort)written.Count);
    writer.Write((ushort)written.Count);
    writer.Write(directorySize);
    writer.Write(directoryOffset);
    writer.Write((ushort)0);
    writer.Flush();
    return output.ToArray();
  }

  private static void WriteEntryHeader(BinaryWriter writer, TestZipEntry entry, uint crc)
  {
    writer.Write((ushort)20);
    writer.Write((ushort)(entry.Utf8Flag ? 0x800 : 0));
    writer.Write((ushort)0);
    writer.Write((ushort)0);
    writer.Write((ushort)0x21);
    writer.Write(crc);
    writer.Write((uint)entry.Data.Length);
    writer.Write((uint)entry.Data.Length);
    writer.Write((ushort)entry.Name.Length);
  }

  private static uint Crc32(byte[] data)
  {
    uint crc = 0xFFFFFFFF;
    foreach (byte value in data)
    {
      crc ^= value;
      for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
    }

    return ~crc;
  }

  private sealed record TestZipEntry(byte[] Name, bool Utf8Flag, byte Host, byte[] Data, byte[] Extra);
}
