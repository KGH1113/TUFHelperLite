using System;
using System.Threading;
using System.Threading.Tasks;
using TUFHelperLite.App.Ports;
using TUFHelperLite.Presentation.Ipc;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace TUFHelperLite.Presentation.Unity;

internal sealed class TempLevelRelease : ITempLevelRelease, IDisposable
{
  private readonly CancellationTokenSource _lifetime = new();

  public async Task ReleaseAsync(string directory, CancellationToken cancellationToken)
  {
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
    CancellationToken token = linked.Token;
    var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    UnityAction<Scene> unloaded = null;
    UnityAction<Scene, LoadSceneMode> loaded = null;
    bool subscribed = false;
    void Cleanup()
    {
      if (!subscribed) return;
      SceneManager.sceneUnloaded -= unloaded;
      SceneManager.sceneLoaded -= loaded;
      subscribed = false;
    }
    using var cancellation = token.Register(() => completed.TrySetCanceled());
    try
    {
      IpcRuntime.Current.RunOnGameThread(() => {
        if (token.IsCancellationRequested || completed.Task.IsCompleted) return;
        try
        {
          if (!TUFHelperLite.App.DownloadStorageMigrationService.IsDirectoryInUse(directory))
          { completed.TrySetResult(true); return; }
          Scene previous = SceneManager.GetActiveScene();
          bool previousUnloaded = false;
          string target = null;
          string loadedScene = null;
          void TryComplete()
          {
            if (!previousUnloaded || target == null || loadedScene != target) return;
            Cleanup();
            completed.TrySetResult(true);
          }
          unloaded = scene => { if (scene.handle == previous.handle) { previousUnloaded = true; TryComplete(); } };
          loaded = (scene, _) => { loadedScene = scene.name; TryComplete(); };
          void Quit()
          {
            // An unsaved-changes popup may call this long after cancellation.
            if (token.IsCancellationRequested || completed.Task.IsCompleted) return;
            try
            {
              SceneManager.sceneUnloaded += unloaded;
              SceneManager.sceneLoaded += loaded;
              subscribed = true;
              ADOBase.controller.QuitToMainMenu();
              target = GCS.sceneToLoad;
              TryComplete();
            }
            catch (Exception error) { Cleanup(); completed.TrySetException(error); }
          }
          if (scnEditor.instance != null) scnEditor.instance.CheckUnsavedChanges(Quit);
          else Quit();
        }
        catch (Exception error) { Cleanup(); completed.TrySetException(error); }
      });
      await completed.Task.ConfigureAwait(false);
      token.ThrowIfCancellationRequested();
    }
    finally
    {
      IpcRuntime.Current.RunOnGameThread(Cleanup);
    }
  }

  public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
