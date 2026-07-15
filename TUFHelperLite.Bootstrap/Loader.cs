using System;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityModManagerNet;

namespace TUFHelperLite.Bootstrap;

public static class Loader
{
  private const string CoreAssemblyName = "TUFHelperLite.Core.dll";
  private const string CoreTypeName = "TUFHelperLite.Main";
  private const string CoreMethodName = "Load";
  internal static readonly TimeSpan UpdateNetworkTimeout = TimeSpan.FromSeconds(20);
  private static readonly object Sync = new();
  private static bool _loaded;

  public static bool Load(UnityModManager.ModEntry modEntry)
  {
    lock (Sync)
    {
      if (_loaded) return true;

      PendingUpdateInstaller installer = new(
        modEntry.Path,
        modEntry.Logger.Log,
        modEntry.Logger.Warning);
      AppliedUpdate applied = null;
      string originalVersion = modEntry.Info.Version;

      try
      {
        installer.RecoverInterruptedApply();
        TryStageLatestUpdate(modEntry, installer, originalVersion);
        applied = installer.ApplyPending(originalVersion);
        if (applied != null) modEntry.Info.Version = applied.Version;

        bool result = LoadCore(modEntry);
        if (!result) throw new InvalidOperationException("TUFHelperLite.Core returned a load failure.");

        installer.Commit(applied);
        _loaded = true;
        return true;
      }
      catch (Exception exception)
      {
        modEntry.Info.Version = originalVersion;
        try
        {
          installer.Rollback(applied);
        }
        catch (Exception rollbackException)
        {
          modEntry.Logger.Error("TUFHelperLite update rollback failed: " + rollbackException);
        }

        modEntry.Logger.Error(exception.ToString());
        return false;
      }
    }
  }

  private static void TryStageLatestUpdate(
    UnityModManager.ModEntry modEntry,
    PendingUpdateInstaller installer,
    string currentVersion)
  {
    using CancellationTokenSource timeout = new(UpdateNetworkTimeout);
    try
    {
      using BootstrapUpdateService updater = new(modEntry.Path, modEntry.Logger.Log);
      updater.CheckAndStageAsync(currentVersion, installer, timeout.Token).GetAwaiter().GetResult();
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
    {
      modEntry.Logger.Warning(
        $"TUFHelperLite automatic update timed out after {UpdateNetworkTimeout.TotalSeconds:0} seconds; loading the installed version.");
    }
    catch (Exception exception)
    {
      modEntry.Logger.Warning(
        "TUFHelperLite automatic update failed; loading the installed version: " + exception.Message);
    }
  }

  private static bool LoadCore(UnityModManager.ModEntry modEntry)
  {
    string modRoot = EnsureTrailingSeparator(Path.GetFullPath(modEntry.Path));
    string assemblyPath = Path.GetFullPath(Path.Combine(modRoot, CoreAssemblyName));
    if (!assemblyPath.StartsWith(modRoot, StringComparison.Ordinal) || !File.Exists(assemblyPath))
      throw new FileNotFoundException("TUFHelperLite.Core assembly was not found.", assemblyPath);

    Assembly assembly = Assembly.LoadFrom(assemblyPath);
    Type type = assembly.GetType(CoreTypeName, true);
    MethodInfo method = type.GetMethod(
      CoreMethodName,
      BindingFlags.Public | BindingFlags.Static,
      null,
      new[] { typeof(UnityModManager.ModEntry) },
      null);
    if (method == null) throw new MissingMethodException(CoreTypeName, CoreMethodName);

    object result = method.Invoke(null, new object[] { modEntry });
    return result is bool loaded && loaded;
  }

  private static string EnsureTrailingSeparator(string path)
  {
    return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
      ? path
      : path + Path.DirectorySeparatorChar;
  }
}
