using System;
using System.Collections.Generic;
using System.IO;
using TUFHelperLite.Integration;

internal static class Program
{
  private static readonly List<string> Failures = new();

  private static int Main()
  {
    string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads");
    if (Directory.Exists(root)) Directory.Delete(root, true);

    try
    {
      string valid = CreateLevel(root, "tuf-12345", "nested/chart.adofai");
      Check("valid nested TUF cache path", 12345, LevelContextResolver.ResolveTufLevelId(valid));
      Check("case-insensitive extension", 42, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "tuf-42", "chart.ADOFAI")));

      Check("null", null, LevelContextResolver.ResolveTufLevelId(null));
      Check("blank", null, LevelContextResolver.ResolveTufLevelId("  "));
      Check("non-adofai", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "tuf-12345", "notes.txt")));
      Check("missing file", null, LevelContextResolver.ResolveTufLevelId(
        Path.Combine(root, "tuf-12345", "missing.adofai")));
      Check("outside root", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(AppDomain.CurrentDomain.BaseDirectory, "outside", "chart.adofai")));
      Check("root prefix confusion", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root + "-other", "tuf-12345", "chart.adofai")));
      Check("URL cache", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "url-abcdef", "chart.adofai")));
      Check("malformed missing ID", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "tuf-", "chart.adofai")));
      Check("malformed nonnumeric ID", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "tuf-12x", "chart.adofai")));
      Check("overflow ID", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "tuf-999999999999", "chart.adofai")));
      Check("nonpositive ID", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "tuf-0", "chart.adofai")));
      Check("file directly under root", null, LevelContextResolver.ResolveTufLevelId(
        CreateLevel(root, "", "chart.adofai")));

      if (Failures.Count == 0)
      {
        Console.WriteLine("All LevelContextResolver tests passed.");
        return 0;
      }

      foreach (string failure in Failures) Console.Error.WriteLine(failure);
      return 1;
    }
    finally
    {
      if (Directory.Exists(root)) Directory.Delete(root, true);
      string outside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "outside");
      if (Directory.Exists(outside)) Directory.Delete(outside, true);
      string sibling = root + "-other";
      if (Directory.Exists(sibling)) Directory.Delete(sibling, true);
    }
  }

  private static string CreateLevel(string root, string directory, string relativePath)
  {
    string path = Path.Combine(root, directory, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "{}");
    return Path.GetFullPath(path);
  }

  private static void Check(string name, int? expected, int? actual)
  {
    if (expected != actual) Failures.Add($"{name}: expected {expected?.ToString() ?? "null"}, got {actual?.ToString() ?? "null"}");
  }
}
