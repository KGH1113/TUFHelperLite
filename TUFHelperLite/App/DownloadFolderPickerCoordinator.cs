using System;
using TUFHelperLite.Infrastructure.Downloads;

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

  public static TUFHelperLite.App.Ports.IMainThreadDispatcher MainThread { private get; set; }
  public static TUFHelperLite.App.Ports.IFolderPicker FolderPicker { private get; set; }

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

    (MainThread ?? throw new InvalidOperationException("The folder picker dispatcher is not configured.")).Dispatch(() => BeginPick(operation));
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
    try
    {
      (FolderPicker ?? throw new InvalidOperationException("The native folder picker is not configured.")).Pick(
        DownloadCachePaths.GetDownloadRoot(), operation.AllowExisting,
        directory => CompleteSelection(operation, directory),
        exception => Complete(operation, Error(operation.Id, "folder_picker_failed", PickerFailure(exception))));
    }
    catch (Exception exception) { Complete(operation, Error(operation.Id, "folder_picker_failed", PickerFailure(exception))); }
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
        TUFHelperLite.Domain.Ports.ActivityChanges.Notify(TUFHelperLite.Domain.Ports.ActivityTopic.Folder);
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
      if (ReferenceEquals(_active, operation))
      {
        operation.Result = result;
        TUFHelperLite.Domain.Ports.ActivityChanges.Notify(TUFHelperLite.Domain.Ports.ActivityTopic.Folder);
      }
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
