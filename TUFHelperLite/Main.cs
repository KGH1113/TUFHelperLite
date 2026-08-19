using System;
using TUFHelperLite.Features;
using TUFHelperLite.App;
using TUFHelperLite.Infrastructure.Settings;
using TUFHelperLite.Infrastructure.Downloads;
using TUFHelperLite.Presentation.Unity;
using UnityModManagerNet;

namespace TUFHelperLite;

public sealed class Main
{
  public static Main Instance { get; private set; }

  public UnityModManager.ModEntry ModEntry { get; }
  public string Version => ModEntry.Info.Version;

  private IpcFeature _ipcFeature;
  private bool _enabled;

  private Main(UnityModManager.ModEntry modEntry)
  {
    ModEntry = modEntry;
  }

  public static bool Load(UnityModManager.ModEntry modEntry)
  {
    try
    {
      if (AdofaiIpcMigrationBridge.PrepareAndNotify(modEntry))
        return true;

      Instance = new Main(modEntry);
      DownloadStorageSettingsStore.Initialize(modEntry.Path);
      DownloadLibraryService.Initialize(modEntry.Path);
      DownloadStorageMigrationService.Initialize(modEntry.Path);
      LevelUpdateService.Initialize(modEntry.Path);
      modEntry.OnToggle = OnToggle;
      modEntry.OnUnload = OnUnload;
      Instance.Enable();
      return true;
    }
    catch (Exception e)
    {
      modEntry.Logger.Error(e.ToString());
      return false;
    }
  }

  public void Log(string message)
  {
    ModEntry.Logger.Log(message);
  }

  public void Warning(string message)
  {
    ModEntry.Logger.Warning(message);
  }

  public void LogException(Exception exception)
  {
    ModEntry.Logger.Error(exception.ToString());
  }

  private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
  {
    try
    {
      if (value) Instance.Enable();
      else Instance.Disable();
      return true;
    }
    catch (Exception e)
    {
      modEntry.Logger.Error(e.ToString());
      return false;
    }
  }

  private static bool OnUnload(UnityModManager.ModEntry modEntry)
  {
    try
    {
      Instance?.Shutdown();
      return true;
    }
    catch (Exception e)
    {
      modEntry.Logger.Error(e.ToString());
      return false;
    }
  }

  private void Enable()
  {
    if (_enabled) return;

    try
    {
      _ipcFeature = new IpcFeature();
      _ipcFeature.Enable();
      DownloadStatusOverlay.EnsureInstalled();
      _ipcFeature.MarkReady();
      _enabled = true;
      Log("TUFHelperLite initialized");
    }
    catch (Exception exception)
    {
      _ipcFeature?.MarkError(exception);
      throw;
    }
  }

  private void Disable()
  {
    if (!_enabled) return;

    _ipcFeature?.Disable();
    _ipcFeature = null;
    DownloadStatusOverlay.Uninstall();
    _enabled = false;
  }

  private void Shutdown()
  {
    DownloadFolderPickerCoordinator.Shutdown();
    Disable();
  }
}
