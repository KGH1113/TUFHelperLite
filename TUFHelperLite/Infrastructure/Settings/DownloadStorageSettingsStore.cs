using System;
using System.IO;
using Newtonsoft.Json;

namespace TUFHelperLite.Infrastructure.Settings;

internal static class DownloadStorageSettingsStore
{
  private static readonly object Gate = new();
  private static string _settingsPath;
  private static string _defaultRoot;
  private static DownloadStorageSettings _settings;

  public static void Initialize(string installPath)
  {
    if (string.IsNullOrWhiteSpace(installPath))
      installPath = AppDomain.CurrentDomain.BaseDirectory;

    lock (Gate)
    {
      _settingsPath = Path.Combine(installPath, "Settings.json");
      _defaultRoot = Path.GetFullPath(Path.Combine(installPath, "Downloads"));
      _settings = Load(_settingsPath);
    }
  }

  public static string GetDefaultRoot()
  {
    EnsureInitialized();
    lock (Gate) return _defaultRoot;
  }

  public static string GetDownloadRoot()
  {
    EnsureInitialized();
    lock (Gate)
    {
      string configured = _settings?.DownloadRoot;
      if (string.IsNullOrWhiteSpace(configured)) return _defaultRoot;

      try
      {
        return Path.GetFullPath(configured);
      }
      catch
      {
        return _defaultRoot;
      }
    }
  }

  public static void SetDownloadRoot(string path)
  {
    if (string.IsNullOrWhiteSpace(path))
      throw new ArgumentException("A download root is required.", nameof(path));

    EnsureInitialized();
    string canonical = Path.GetFullPath(path);
    lock (Gate)
    {
      DownloadStorageSettings next = new()
      {
        DownloadRoot = PathsEqual(canonical, _defaultRoot) ? null : canonical
      };
      SaveAtomic(_settingsPath, next);
      _settings = next;
    }
  }

  private static DownloadStorageSettings Load(string path)
  {
    try
    {
      if (!File.Exists(path)) return new DownloadStorageSettings();
      return JsonConvert.DeserializeObject<DownloadStorageSettings>(File.ReadAllText(path)) ??
             new DownloadStorageSettings();
    }
    catch (Exception exception)
    {
      Main.Instance?.Warning("Failed to read Settings.json; using the default download directory: " + exception.Message);
      return new DownloadStorageSettings();
    }
  }

  private static void SaveAtomic(string path, DownloadStorageSettings settings)
  {
    string temporaryPath = path + ".tmp";
    File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(settings, Formatting.Indented));
    if (!File.Exists(path))
    {
      File.Move(temporaryPath, path);
      return;
    }

    string backupPath = path + ".bak";
    try
    {
      File.Replace(temporaryPath, path, backupPath, true);
      if (File.Exists(backupPath)) File.Delete(backupPath);
    }
    catch
    {
      if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
      throw;
    }
  }

  private static void EnsureInitialized()
  {
    if (_settings != null) return;
    Initialize(Main.Instance?.ModEntry?.Path ?? AppDomain.CurrentDomain.BaseDirectory);
  }

  private static bool PathsEqual(string left, string right) =>
    string.Equals(
      Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
      Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
      StringComparison.OrdinalIgnoreCase);
}
