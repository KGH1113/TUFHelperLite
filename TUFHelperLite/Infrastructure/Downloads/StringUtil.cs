using System;

namespace TUFHelperLite.Infrastructure.Downloads;

public static class StringUtil
{
  public static string GetValue(string value, string start, string end)
  {
    string[] afterStart = value.Split(new[] { start }, StringSplitOptions.None);
    if (afterStart.Length < 2) return "";

    return afterStart[1].Split(new[] { end }, StringSplitOptions.None)[0].Trim();
  }

  public static int GetNextIndexOf(char value, string source, int start)
  {
    if (start < 0 || start > source.Length - 1) return -1;

    for (int i = start; i < source.Length; i++)
    {
      if (source[i] == value) return i;
    }

    return -1;
  }
}
