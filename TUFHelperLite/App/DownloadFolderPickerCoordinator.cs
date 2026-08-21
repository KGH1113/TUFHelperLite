using System;
using System.Diagnostics;
using System.Threading;
using TUFHelperLite.Infrastructure.Downloads;
using UnityEngine;
using UnityFileDialog;

namespace TUFHelperLite.App;

public sealed class DownloadFolderPickerSnapshot
{
  public string OperationId;
  public string State;
  public string SelectionToken;
  public string Directory;
  public string SelectionKind;
  public string ErrorCode;
  public string Message;
}

public static class DownloadFolderPickerCoordinator
{
  private sealed class PickOperation
  {
    public PickOperation(bool allowExisting) => AllowExisting = allowExisting;
    public readonly string Id = Guid.NewGuid().ToString("N");
    public bool AllowExisting { get; }
    public DownloadFolderPickerSnapshot Result;
  }

  private static readonly object Gate = new();
  private static PickOperation _active;
  private static string _selectedToken;
  private static string _selectedDirectory;
  private static string _selectedKind;

  public static DownloadFolderPickerSnapshot Start(bool allowExisting = false)
  {
    PickOperation operation;
    lock (Gate)
    {
      if (_active != null && _active.Result == null)
        return Error(_active.Id, "folder_picker_busy", "Another folder picker is already open.");

      operation = new PickOperation(allowExisting);
      _active = operation;
      _selectedToken = null;
      _selectedDirectory = null;
      _selectedKind = null;
    }

    global::AdofaiIpc.AdofaiIpc.RunOnMainThread(() => BeginPick(operation));
    return Pending(operation.Id);
  }

  public static DownloadFolderPickerSnapshot GetStatus(string operationId)
  {
    lock (Gate)
    {
      if (_active == null || !string.Equals(_active.Id, operationId, StringComparison.Ordinal))
        return Error(operationId, "folder_picker_not_found", "The folder picker operation was not found.");
      return _active.Result ?? Pending(_active.Id);
    }
  }

  public static bool TryConsumeSelection(string token, out string directory)
  {
    return TryConsumeSelection(token, out directory, out _);
  }

  public static bool TryConsumeSelection(string token, out string directory, out string selectionKind)
  {
    lock (Gate)
    {
      directory = null;
      selectionKind = null;
      if (string.IsNullOrWhiteSpace(token) || !string.Equals(token, _selectedToken, StringComparison.Ordinal))
        return false;
      directory = _selectedDirectory;
      selectionKind = _selectedKind;
      _selectedToken = null;
      _selectedDirectory = null;
      _selectedKind = null;
      return !string.IsNullOrWhiteSpace(directory);
    }
  }

  public static void Shutdown()
  {
    lock (Gate)
    {
      _active = null;
      _selectedToken = null;
      _selectedDirectory = null;
      _selectedKind = null;
    }
  }

  private static void BeginPick(PickOperation operation)
  {
    if (!IsCurrent(operation)) return;
    if (Application.platform == RuntimePlatform.OSXPlayer)
    {
      ThreadPool.QueueUserWorkItem(_ => PickOnMac(operation));
      return;
    }

    try
    {
      CompleteSelection(operation, FileBrowser.PickFolder(
        DownloadCachePaths.GetDownloadRoot(),
        title: operation.AllowExisting
          ? "Choose a TUFHelperLite download folder"
          : "Choose an empty TUFHelperLite download folder"));
    }
    catch (Exception exception)
    {
      Complete(operation, Error(operation.Id, "folder_picker_failed", PickerFailure(exception)));
    }
  }

  private static void PickOnMac(PickOperation operation)
  {
    try
    {
      using Process process = new()
      {
        StartInfo = new ProcessStartInfo
        {
          FileName = "/usr/bin/osascript",
          Arguments = operation.AllowExisting
            ? "-e \"POSIX path of (choose folder with prompt \\\"Choose a TUFHelperLite download folder\\\")\""
            : "-e \"POSIX path of (choose folder with prompt \\\"Choose an empty TUFHelperLite download folder\\\")\"",
          UseShellExecute = false,
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          CreateNoWindow = true,
        }
      };
      process.Start();
      string output = process.StandardOutput.ReadToEnd();
      string error = process.StandardError.ReadToEnd();
      process.WaitForExit();
      if (process.ExitCode == 0)
        CompleteSelection(operation, output.Trim());
      else if (error.Contains("(-128)"))
        Complete(operation, Cancelled(operation.Id));
      else
        Complete(operation, Error(operation.Id, "folder_picker_failed",
          string.IsNullOrWhiteSpace(error) ? "The macOS folder picker failed." : error.Trim()));
    }
    catch (Exception exception)
    {
      Complete(operation, Error(operation.Id, "folder_picker_failed", PickerFailure(exception)));
    }
  }

  private static void CompleteSelection(PickOperation operation, string directory)
  {
    if (string.IsNullOrWhiteSpace(directory))
    {
      Complete(operation, Cancelled(operation.Id));
      return;
    }

    try
    {
      string kind = "migration";
      string canonical = operation.AllowExisting
        ? DownloadStorageMigrationService.ValidateChangeTarget(directory, out kind)
        : DownloadStorageMigrationService.ValidateSelectedTarget(directory);
      string token = Guid.NewGuid().ToString("N");
      lock (Gate)
      {
        if (!ReferenceEquals(_active, operation)) return;
        _selectedToken = token;
        _selectedDirectory = canonical;
        _selectedKind = kind ?? "migration";
        operation.Result = new DownloadFolderPickerSnapshot
        {
          OperationId = operation.Id,
          State = "selected",
          SelectionToken = token,
          Directory = canonical,
          SelectionKind = _selectedKind,
          Message = "Folder selected."
        };
      }
    }
    catch (DownloadStorageMigrationException exception)
    {
      Complete(operation, Error(operation.Id, exception.Code, exception.Message));
    }
  }

  private static bool IsCurrent(PickOperation operation)
  {
    lock (Gate) return ReferenceEquals(_active, operation);
  }

  private static void Complete(PickOperation operation, DownloadFolderPickerSnapshot result)
  {
    lock (Gate)
    {
      if (ReferenceEquals(_active, operation)) operation.Result = result;
    }
  }

  private static DownloadFolderPickerSnapshot Pending(string id) => new()
  {
    OperationId = id,
    State = "picking",
    Message = "Waiting for a folder selection."
  };

  private static DownloadFolderPickerSnapshot Cancelled(string id) => new()
  {
    OperationId = id,
    State = "cancelled",
    Message = "Folder selection was cancelled."
  };

  private static DownloadFolderPickerSnapshot Error(string id, string code, string message) => new()
  {
    OperationId = id,
    State = "failed",
    ErrorCode = code,
    Message = message
  };

  private static string PickerFailure(Exception exception) =>
    "The folder picker failed: " + exception.GetType().Name + ": " + exception.Message;
}
