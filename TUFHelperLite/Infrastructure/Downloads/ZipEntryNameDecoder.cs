using System;
using System.Collections.Generic;
using System.Text;

namespace TUFHelperLite.Infrastructure.Downloads;

// Zip entries without the UTF-8 flag store names in the code page of the
// archiver. Level archives come from Korean and Chinese Windows, and from
// Japanese titles zipped on either, so a fixed code page garbles many of them.
internal static class ZipEntryNameDecoder
{
  private const int InvalidSequencePenalty = 20;

  private static readonly object Gate = new();
  private static volatile LegacyCodePage[] _codePages;

  // utf8Names come from ZipArchive opened with UTF-8; rawNames from ZipCentralDirectory.
  public static string[] Decode(IList<string> utf8Names, IList<ZipRawEntryName> rawNames)
  {
    if (utf8Names == null) throw new ArgumentNullException(nameof(utf8Names));

    string[] names = new string[utf8Names.Count];
    utf8Names.CopyTo(names, 0);
    if (rawNames == null || rawNames.Count != names.Length) return names;

    for (int i = 0; i < names.Length; i++)
    {
      // ZipArchive reads every name as UTF-8 here, so a mismatch means the two
      // readers disagree about the archive; keep ZipArchive's view.
      if (!string.Equals(Encoding.UTF8.GetString(rawNames[i].Bytes), names[i], StringComparison.Ordinal)) return names;
    }

    List<int> legacyIndexes = new();
    for (int i = 0; i < names.Length; i++)
    {
      ZipRawEntryName raw = rawNames[i];
      if (raw.Utf8Flag || IsAscii(raw.Bytes)) continue;

      if (raw.UnicodePath != null)
      {
        names[i] = raw.UnicodePath;
        continue;
      }

      legacyIndexes.Add(i);
    }

    if (legacyIndexes.Count == 0) return names;

    List<byte[]> legacyNames = new(legacyIndexes.Count);
    foreach (int index in legacyIndexes) legacyNames.Add(rawNames[index].Bytes);

    string[] decoded = DecodeLegacy(legacyNames);
    for (int i = 0; i < legacyIndexes.Count; i++) names[legacyIndexes[i]] = decoded[i];
    return names;
  }

  // Picks one code page for all names of an archive, since one tool wrote them.
  internal static string[] DecodeLegacy(IList<byte[]> names)
  {
    LegacyCodePage best = null;
    string[] bestNames = null;
    long bestScore = long.MinValue;

    foreach (LegacyCodePage codePage in GetCodePages())
    {
      string[] decoded = new string[names.Count];
      long score = 0;
      for (int i = 0; i < names.Count; i++)
      {
        decoded[i] = codePage.Decode(names[i], out int invalidSequences);
        score += Score(codePage, decoded[i]) - (long)InvalidSequencePenalty * invalidSequences;
      }

      if (best == null || score > bestScore)
      {
        best = codePage;
        bestNames = decoded;
        bestScore = score;
      }
    }

    return bestNames;
  }

  private static int Score(LegacyCodePage codePage, string text)
  {
    int score = 0;
    foreach (char character in text)
    {
      if (character < 0x80 || character == '�') continue;

      if (character >= '぀' && character <= 'ヿ') score += 2; // kana
      else if (character >= '가' && character <= '힣') score += codePage.IsRareHangul(character) ? -1 : 2;
      else if (character >= '㄰' && character <= '㆏') score -= 2; // lone jamo: Chinese kana bytes read as CP949
      else if (character >= '一' && character <= '鿿') score += codePage.IsKorean ? -1 : 1; // Hanja are rare in Korean names
      else if (character >= '｡' && character <= 'ﾟ') score -= 1; // half-width katakana
      else if (character >= '─' && character <= '╿') score -= 1; // box drawing: Greek read as CP949
      else if ((character >= '‐' && character <= '➿') ||
               (character >= '　' && character <= '〿') ||
               (character >= '！' && character <= '～')) score += 1; // symbols and CJK punctuation
      else score -= 1;
    }

    return score;
  }

  private static bool IsAscii(byte[] bytes)
  {
    foreach (byte value in bytes)
    {
      if (value >= 0x80) return false;
    }

    return true;
  }

  private static LegacyCodePage[] GetCodePages()
  {
    LegacyCodePage[] codePages = _codePages;
    if (codePages != null) return codePages;

    lock (Gate)
    {
      if (_codePages != null) return _codePages;

      // Korean first so ties keep the previous CP949 behavior.
      List<LegacyCodePage> available = new();
      foreach (int codePage in new[] { 949, 936, 932 })
      {
        Encoding encoding = TryGetEncoding(codePage);
        if (encoding != null) available.Add(new LegacyCodePage(codePage, encoding));
      }

      available.Add(new LegacyCodePage(65001, new UTF8Encoding(false, false)));
      _codePages = available.ToArray();
      return _codePages;
    }
  }

  private static Encoding TryGetEncoding(int codePage)
  {
    try
    {
      return Encoding.GetEncoding(codePage);
    }
    catch (Exception e) when (e is ArgumentException || e is NotSupportedException)
    {
      Main.Instance?.Warning(string.Concat("Zip name code page ", codePage.ToString(), " is unavailable: ", e.Message));
      return null;
    }
  }

  private sealed class LegacyCodePage
  {
    private readonly int _codePage;
    private readonly Encoding _encoding;

    public LegacyCodePage(int codePage, Encoding encoding)
    {
      _codePage = codePage;
      _encoding = encoding;
    }

    public bool IsKorean => _codePage == 949;

    // Outside the 2,350 KS X 1001 syllables of everyday Korean; Chinese and
    // Japanese bytes read as CP949 mostly land there.
    public bool IsRareHangul(char character)
    {
      if (!IsKorean) return false;

      byte[] bytes = _encoding.GetBytes(new[] { character });
      return bytes.Length != 2 || bytes[0] < 0xB0 || bytes[0] > 0xC8 || bytes[1] < 0xA1;
    }

    // Decodes one character at a time. Mono's CJK decoders swallow the byte
    // after an invalid lead byte, which can eat a '/' or '.'; bytes below 0x40
    // are never trail bytes, so separators and extensions survive here.
    public string Decode(byte[] bytes, out int invalidSequences)
    {
      invalidSequences = 0;
      if (_codePage == 65001)
      {
        string text = _encoding.GetString(bytes);
        foreach (char character in text)
        {
          if (character == '�') invalidSequences++;
        }

        return text;
      }

      StringBuilder builder = new(bytes.Length);
      for (int i = 0; i < bytes.Length; i++)
      {
        byte value = bytes[i];
        if (value < 0x80)
        {
          builder.Append((char)value);
          continue;
        }

        int width = IsSingleByte(value) ? 1 : IsLeadByte(value) && i + 1 < bytes.Length && IsTrailByte(bytes[i + 1]) ? 2 : 0;
        string character = width == 0 ? null : _encoding.GetString(bytes, i, width);
        if (string.IsNullOrEmpty(character) || character.IndexOf('?') >= 0 || character.IndexOf('�') >= 0)
        {
          invalidSequences++;
          builder.Append('�');
          continue;
        }

        builder.Append(character);
        i += width - 1;
      }

      return builder.ToString();
    }

    private bool IsSingleByte(byte value) => _codePage == 932 && value >= 0xA1 && value <= 0xDF;

    private bool IsLeadByte(byte value) => _codePage == 932
      ? (value >= 0x81 && value <= 0x9F) || (value >= 0xE0 && value <= 0xFC)
      : value >= 0x81 && value <= 0xFE;

    private bool IsTrailByte(byte value) => _codePage switch
    {
      949 => (value >= 0x41 && value <= 0x5A) || (value >= 0x61 && value <= 0x7A) || (value >= 0x81 && value <= 0xFE),
      932 => (value >= 0x40 && value <= 0x7E) || (value >= 0x80 && value <= 0xFC),
      _ => (value >= 0x40 && value <= 0x7E) || (value >= 0x80 && value <= 0xFE)
    };
  }
}
