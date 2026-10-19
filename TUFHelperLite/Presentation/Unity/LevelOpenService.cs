using TUFHelperLite.Presentation.Ipc;
using System;
using TUFHelperLite.App;
using UnityEngine.SceneManagement;

namespace TUFHelperLite.Presentation.Unity;

public static class LevelOpenService
{
  public static void Open(string levelPath)
  {
    if (string.IsNullOrWhiteSpace(levelPath))
    {
      throw new ArgumentException("Level path is required.", nameof(levelPath));
    }

    PendingLevelOpen.Reserve(levelPath);
    try
    {
      IpcRuntime.Current.RunOnGameThread(() =>
      {
        try
        {
          void LoadEditor()
          {
            try
            {
              GCS.sceneToLoad = "scnEditor";
              GCS.worldEntrance = null;
              scnEditor.levelToOpenOnLoad = levelPath;
              SceneManager.LoadScene("scnEditor");
            }
            catch { PendingLevelOpen.Release(levelPath); throw; }
          }

          if (scrUIController.instance == null)
          {
            LoadEditor();
            return;
          }

          scrUIController.instance.WipeToBlack(WipeDirection.StartsFromRight, LoadEditor);
        }
        catch { PendingLevelOpen.Release(levelPath); throw; }
      });
    }
    catch { PendingLevelOpen.Release(levelPath); throw; }
  }

  internal static void ObserveOpenedLevel()
  {
    try { if (scnGame.instance != null) PendingLevelOpen.Release(ADOBase.levelPath); }
    catch { }
  }
}
