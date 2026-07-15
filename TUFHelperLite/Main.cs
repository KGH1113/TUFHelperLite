using System;
using System.Threading;
using TUFHelperLite.Features;
using TUFHelperLite.Infrastructure.Updates;
using TUFHelperLite.Presentation.Unity;
using UnityModManagerNet;

namespace TUFHelperLite;

public sealed class Main
{
  public static Main Instance { get; private set; }

  public UnityModManager.ModEntry ModEntry { get; }
  public string Version => ModEntry.Info.Version;

  private IpcFeature _ipcFeature;
  private readonly SynchronizationContext _mainThread;
  private readonly string _displayName;
  private AutoUpdateService _autoUpdateService;
  private bool _enabled;

  private Main(UnityModManager.ModEntry modEntry, SynchronizationContext mainThread)
  {
    ModEntry = modEntry;
    _mainThread = mainThread;
    _displayName = modEntry.Info.DisplayName;
  }

  public static bool Load(UnityModManager.ModEntry modEntry)
  {
    try
    {
      Instance = new Main(modEntry, SynchronizationContext.Current);
      modEntry.OnToggle = OnToggle;
      modEntry.OnUnload = OnUnload;
      Instance.Enable();
      Instance.StartAutomaticUpdates();
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

    ModStatus.IpcDependencyState ipcState = ModStatus.GetAdofaiIpcState();
    ModStatus.SetAdofaiIpcState(ipcState);

    if (ipcState != ModStatus.IpcDependencyState.Available)
    {
      Warning("TUFHelperLite needs AdofaiIpc. Install and enable AdofaiIpc to use browser IPC.");
      return;
    }

    _ipcFeature = new IpcFeature();
    _ipcFeature.Enable();
    DownloadStatusOverlay.EnsureInstalled();
    _enabled = true;
    Log("TUFHelperLite initialized");
  }

  private void Disable()
  {
    if (!_enabled) return;

    _ipcFeature?.Disable();
    _ipcFeature = null;
    DownloadStatusOverlay.Uninstall();
    _enabled = false;
  }

  private void StartAutomaticUpdates()
  {
    if (_autoUpdateService != null) return;
    _autoUpdateService = new AutoUpdateService(
      Version,
      ModEntry.Path,
      Log,
      Warning,
      version => RunOnMainThread(() =>
      {
        ModEntry.Info.DisplayName = $"{_displayName} <color=grey>[Update {version} ready - restart]</color>";
        Log($"TUFHelperLite {version} update is ready and will be applied on the next launch.");
      }));
    _autoUpdateService.Start();
  }

  private void Shutdown()
  {
    _autoUpdateService?.Dispose();
    _autoUpdateService = null;
    Disable();
  }

  private void RunOnMainThread(Action action)
  {
    if (_mainThread == null || SynchronizationContext.Current == _mainThread)
    {
      action();
      return;
    }

    _mainThread.Post(_ => action(), null);
  }
}
