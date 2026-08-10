using System;
using System.IO;
using UnityModManagerNet;

namespace TUFHelperLite.Launcher;

public static class EntryPoint
{
  private const string PayloadEntryMethod = "TUFHelperLite.Main.Load";

  public static bool Load(UnityModManager.ModEntry modEntry)
  {
    string displayName = modEntry.Info.DisplayName;
    RuntimeStore store = new(modEntry.Path);
    try
    {
      RuntimeState state = store.LoadAndRepair();
      RuntimeCandidate current = store.GetCandidate(state.Current);
      modEntry.Info.Version = current.Version;
      modEntry.Info.DisplayName = Status(modEntry, "Checking for updates...");

      UpdateResolution resolution;
      try
      {
        resolution = UpdateEngineLoader.Resolve(modEntry, current);
      }
      catch (Exception exception)
      {
        Warn(modEntry, "Update check failed. Loading the current runtime.", exception);
        resolution = UpdateResolution.None();
      }

      modEntry.Info.DisplayName = displayName;
      if (!resolution.HasCandidate)
        return TryLoadCurrent(modEntry, current);

      string bootstrapTrial;
      try
      {
        if (string.IsNullOrWhiteSpace(resolution.DependencyBootstrapPath))
          throw new InvalidDataException("The update candidate has no dependency bootstrap.");
        bootstrapTrial = DependencyBootstrapShim.Stage(modEntry.Path,
          resolution.DependencyBootstrapPath);
      }
      catch (Exception exception)
      {
        Warn(modEntry, "The dependency bootstrap candidate could not be staged. Loading the current runtime.", exception);
        modEntry.Info.DisplayName = displayName;
        return TryLoadCurrent(modEntry, current);
      }

      RuntimeCandidate trial;
      try { trial = store.ValidateCandidate(resolution.Version, resolution.RuntimePath); }
      catch (Exception exception)
      {
        TryDiscardBootstrap(modEntry, bootstrapTrial);
        Warn(modEntry, "The runtime candidate failed validation. Loading the current runtime.", exception);
        return TryLoadCurrent(modEntry, current);
      }
      try
      {
        state.Trial = trial.Version;
        store.Save(state);
      }
      catch (Exception exception)
      {
        state.Trial = null;
        TryDiscardBootstrap(modEntry, bootstrapTrial);
        Warn(modEntry, "The runtime trial marker could not be saved. Loading the current runtime.", exception);
        return TryLoadCurrent(modEntry, current);
      }
      modEntry.Info.Version = trial.Version;
      if (TryLoad(modEntry, trial, out Exception loadException, out bool safeToFallback))
      {
        try
        {
          store.Promote(state, trial.Version);
        }
        catch (Exception exception)
        {
          Warn(modEntry, "The updated runtime loaded, but its active marker could not be saved.", exception);
        }
        return true;
      }

      state.Trial = null;
      store.Save(state);
      store.DeleteUnreferencedRuntime(trial.Version, state);
      TryDiscardBootstrap(modEntry, bootstrapTrial);
      modEntry.Info.Version = current.Version;
      modEntry.Info.DisplayName = displayName + " <color=red>[Failed to update!]</color>";
      if (safeToFallback)
      {
        Warn(modEntry, "The updated runtime failed before its assembly was loaded. Loading the current runtime.", loadException);
        return TryLoadCurrent(modEntry, current);
      }
      Warn(modEntry, "The updated runtime failed after its assembly was loaded. Loading stops until the next game launch.", loadException);
      return false;
    }
    catch (Exception exception)
    {
      modEntry.Info.DisplayName = displayName + " <color=red>[Failed to update!]</color>";
      Warn(modEntry, "The runtime launcher failed.", exception);
      return false;
    }
  }

  private static bool TryLoadCurrent(UnityModManager.ModEntry modEntry, RuntimeCandidate current)
  {
    modEntry.Info.Version = current.Version;
    if (TryLoad(modEntry, current, out Exception exception, out _))
      return true;
    modEntry.Logger.Error(exception?.ToString() ?? "The current TUFHelperLite runtime failed to load.");
    return false;
  }

  private static bool TryLoad(
    UnityModManager.ModEntry modEntry,
    RuntimeCandidate candidate,
    out Exception exception,
    out bool safeToFallback)
  {
    try
    {
      PayloadLoader.Load(candidate.AssemblyPath, PayloadEntryMethod, modEntry);
      exception = null;
      safeToFallback = false;
      return true;
    }
    catch (Exception caught)
    {
      exception = caught;
      safeToFallback = caught is PayloadLoadException loadFailure && !loadFailure.AssemblyLoaded;
      return false;
    }
  }

  private static void TryDiscardBootstrap(UnityModManager.ModEntry modEntry, string version)
  {
    try { DependencyBootstrapShim.Discard(modEntry.Path, version); }
    catch (Exception exception)
    {
      Warn(modEntry, "The dependency bootstrap trial could not be discarded.", exception);
    }
  }

  private static void Warn(UnityModManager.ModEntry modEntry, string message, Exception exception)
  {
    modEntry.Logger.Warning("[AutoUpdate] " + message);
    if (exception != null)
      modEntry.Logger.Warning("[AutoUpdate] " + exception);
  }

  private static string Status(UnityModManager.ModEntry modEntry, string status)
  {
    return modEntry.Info.Id + " <color=grey>[" + status + "]</color>";
  }
}
