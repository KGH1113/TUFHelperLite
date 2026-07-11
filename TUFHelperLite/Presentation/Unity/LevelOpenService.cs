using System;
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

    global::AdofaiIpc.AdofaiIpc.RunOnMainThread(() =>
    {
      void LoadEditor()
      {
        GCS.sceneToLoad = "scnEditor";
        GCS.worldEntrance = null;
        scnEditor.levelToOpenOnLoad = levelPath;
        SceneManager.LoadScene("scnEditor");
      }

      if (scrUIController.instance == null)
      {
        LoadEditor();
        return;
      }

      scrUIController.instance.WipeToBlack(WipeDirection.StartsFromRight, LoadEditor);
    });
  }
}
