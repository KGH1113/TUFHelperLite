using System;
using System.Diagnostics;
using System.Threading;
using TUFHelperLite.App.Ports;
using UnityEngine;
using UnityFileDialog;

namespace TUFHelperLite.Presentation.Unity;

public sealed class NativeFolderPicker : IFolderPicker
{
  public void Pick(string initialDirectory, bool allowExisting, Action<string> selected, Action<Exception> failed)
  {
    if (Application.platform == RuntimePlatform.OSXPlayer)
    {
      ThreadPool.QueueUserWorkItem(_ => PickOnMac(allowExisting, selected, failed));
      return;
    }
    try { selected(FileBrowser.PickFolder(initialDirectory, title: allowExisting ? "Choose a TUFHelperLite download folder" : "Choose an empty TUFHelperLite download folder")); }
    catch (Exception exception) { failed(exception); }
  }

  private static void PickOnMac(bool allowExisting, Action<string> selected, Action<Exception> failed)
  {
    try
    {
      string title = allowExisting ? "Choose a TUFHelperLite download folder" : "Choose an empty TUFHelperLite download folder";
      using Process process = new()
      {
        StartInfo = new ProcessStartInfo
        {
          FileName = "/usr/bin/osascript",
          Arguments = "-e \"POSIX path of (choose folder with prompt \\\"" + title + "\\\")\"",
          UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        }
      };
      process.Start();
      string output = process.StandardOutput.ReadToEnd();
      string error = process.StandardError.ReadToEnd();
      process.WaitForExit();
      if (process.ExitCode == 0) selected(output.Trim());
      else if (error.Contains("(-128)")) selected(null);
      else failed(new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "The macOS folder picker failed." : error.Trim()));
    }
    catch (Exception exception) { failed(exception); }
  }
}
