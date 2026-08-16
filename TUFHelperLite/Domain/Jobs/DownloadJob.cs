using System;
using System.Linq;
using System.Threading;
using TUFHelperLite.Domain.Errors;
using TUFHelperLite.Domain.Levels;

namespace TUFHelperLite.Domain.Jobs;

public sealed class DownloadJob
{
  private readonly object _lock = new();
  private readonly CancellationTokenSource _cts = new();

  private string _status = "queued";
  private string _stage = "queued";
  private string _message = "Queued";
  private int _queuePosition = -1;
  private double _progress = -1;
  private long _bytesReceived = -1;
  private long _totalBytes = -1;
  private string _song;
  private string _artist;
  private string _creator;
  private int _difficultyId = -1;
  private string _directUrl;
  private string _directory;
  private string _selectedLevelPath;
  private string[] _levelPaths = Array.Empty<string>();
  private bool _waitingForSelection;
  private bool _opened;
  private bool _fromCache;
  private string _error;
  private string _errorCode;
  private long _errorAvailableBytes;
  private long _errorRequiredBytes;
  private long _updatedAtUnixMs;

  public DownloadJob(string kind, string levelId, string sourceUrl, string cacheKey, bool openAfterDownload)
  {
    JobId = Guid.NewGuid().ToString("D");
    Kind = kind;
    LevelId = levelId;
    SourceUrl = sourceUrl;
    CacheKey = cacheKey;
    OpenAfterDownload = openAfterDownload;
    CreatedAtUnixMs = Now();
    _updatedAtUnixMs = CreatedAtUnixMs;
  }

  public string JobId { get; }
  public string Kind { get; }
  public string CacheKey { get; }
  public string LevelId { get; private set; }
  public string SourceUrl { get; private set; }
  public bool OpenAfterDownload { get; }
  public CancellationToken Token => _cts.Token;

  public bool IsDone
  {
    get
    {
      lock (_lock)
      {
        return IsTerminalStatus(_status);
      }
    }
  }

  public bool IsQueued
  {
    get
    {
      lock (_lock)
      {
        return _status == "queued";
      }
    }
  }

  public void SetQueuePosition(int queuePosition)
  {
    lock (_lock)
    {
      _queuePosition = queuePosition;
      if (_status == "queued")
      {
        _message = queuePosition > 0 ? $"Queued #{queuePosition}" : "Queued";
      }
      Touch();
    }
  }

  public void BeginRunning()
  {
    lock (_lock)
    {
      if (_status == "queued")
      {
        _status = "running";
        _stage = "starting";
        _message = "Starting download";
      }

      _queuePosition = 0;
      Touch();
    }
  }

  public void SetLevel(string levelId, string sourceUrl)
  {
    lock (_lock)
    {
      LevelId = levelId;
      SourceUrl = sourceUrl;
      Touch();
    }
  }

  public void SetLevelInfo(string song, string artist, string creator)
  {
    lock (_lock)
    {
      _song = song;
      _artist = artist;
      _creator = creator;
      Touch();
    }
  }

  public void SetDifficultyId(int difficultyId)
  {
    lock (_lock)
    {
      _difficultyId = difficultyId;
      Touch();
    }
  }

  public void Report(string stage, string message, double progress = -1, long bytesReceived = -1, long totalBytes = -1)
  {
    lock (_lock)
    {
      if (IsTerminalStatus(_status)) return;

      _status = "running";
      _stage = stage;
      _message = message;
      _progress = progress;
      _bytesReceived = bytesReceived;
      _totalBytes = totalBytes;
      Touch();
    }
  }

  public void Report(LevelDownloadProgress update)
  {
    if (update == null) return;
    Report(update.Stage, update.Message, update.Progress, update.BytesReceived, update.TotalBytes);
  }

  public void Complete(LevelDownloadResult result, bool opened)
  {
    lock (_lock)
    {
      if (IsTerminalStatus(_status)) return;

      _status = "completed";
      _stage = opened ? "opened" : "downloaded";
      _message = opened ? "Level opened" : "Download completed";
      _progress = 1;
      _bytesReceived = _totalBytes;
      _directUrl = result.DirectUrl;
      _directory = result.Directory;
      _selectedLevelPath = result.SelectedLevelPath;
      _levelPaths = result.LevelPaths.ToArray();
      _opened = opened;
      _fromCache = result.FromCache;
      Touch();
    }
  }

  public void WaitForSelection(LevelDownloadResult result)
  {
    lock (_lock)
    {
      if (IsTerminalStatus(_status)) return;

      _status = "waiting_selection";
      _stage = "selecting";
      _message = "Select a level to open";
      _progress = 1;
      _directUrl = result.DirectUrl;
      _directory = result.Directory;
      _selectedLevelPath = null;
      _levelPaths = result.LevelPaths.ToArray();
      _fromCache = result.FromCache;
      _waitingForSelection = true;
      Touch();
    }
  }

  public bool SelectLevel(string levelPath)
  {
    lock (_lock)
    {
      if (_status != "waiting_selection") return false;
      if (!_levelPaths.Contains(levelPath)) return false;

      _selectedLevelPath = levelPath;
      _waitingForSelection = false;
      _status = "completed";
      _stage = "opened";
      _message = "Level opened";
      _opened = true;
      Touch();
      return true;
    }
  }

  public void Fail(Exception exception)
  {
    lock (_lock)
    {
      if (IsTerminalStatus(_status)) return;

      _status = "failed";
      _stage = "failed";
      _message = "Download failed";
      _error = exception.Message;
      if (exception is InsufficientDiskSpaceException diskSpace)
      {
        _errorCode = "insufficient_disk_space";
        _errorAvailableBytes = diskSpace.AvailableBytes;
        _errorRequiredBytes = diskSpace.RequiredBytes;
      }
      Touch();
    }
  }

  public void Cancel()
  {
    TryCancel();
  }

  public bool TryCancel()
  {
    lock (_lock)
    {
      if (IsTerminalStatus(_status)) return false;

      _status = "cancelled";
      _stage = "cancelled";
      _message = "Cancelled";
      _waitingForSelection = false;
      Touch();
    }

    _cts.Cancel();
    return true;
  }

  public DownloadJobSnapshot Snapshot()
  {
    lock (_lock)
    {
      return new DownloadJobSnapshot
      {
        JobId = JobId,
        Kind = Kind,
        Status = _status,
        Stage = _stage,
        Message = _message,
        CacheKey = CacheKey,
        QueuePosition = _queuePosition,
        Progress = _progress,
        BytesReceived = _bytesReceived,
        TotalBytes = _totalBytes,
        LevelId = LevelId,
        Song = _song,
        Artist = _artist,
        Creator = _creator,
        DifficultyId = _difficultyId,
        SourceUrl = SourceUrl,
        DirectUrl = _directUrl,
        Directory = _directory,
        SelectedLevelPath = _selectedLevelPath,
        LevelPaths = _levelPaths,
        OpenAfterDownload = OpenAfterDownload,
        WaitingForSelection = _waitingForSelection,
        Opened = _opened,
        FromCache = _fromCache,
        Error = _error,
        ErrorCode = _errorCode,
        ErrorAvailableBytes = _errorAvailableBytes,
        ErrorRequiredBytes = _errorRequiredBytes,
        CreatedAtUnixMs = CreatedAtUnixMs,
        UpdatedAtUnixMs = _updatedAtUnixMs,
        Done = IsTerminalStatus(_status)
      };
    }
  }

  public long CreatedAtUnixMs { get; }

  private void Touch()
  {
    _updatedAtUnixMs = Now();
  }

  private static long Now()
  {
    return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
  }

  private static bool IsTerminalStatus(string status)
  {
    return status is "completed" or "failed" or "cancelled";
  }
}
