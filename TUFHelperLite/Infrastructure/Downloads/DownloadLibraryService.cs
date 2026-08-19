using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TUFHelperLite.App;
using TUFHelperLite.Domain.Downloads;
using TUFHelperLite.Domain.Levels;
using TUFHelperLite.Infrastructure.Tuforums;

namespace TUFHelperLite.Infrastructure.Downloads;

public static class DownloadLibraryService
{
  internal const string ManifestFileName = ".tufhelperlite-level.json";
  private const int DefaultPageSize = 20;
  private const int MaximumPageSize = 50;
  private const int CursorVersion = 1;
  private const int SummaryVersion = 1;
  private const int MetadataFetchConcurrency = 4;
  private static readonly object Gate = new();
  private static Func<string, TufLevelInfo> _metadataProvider = TuforumsClient.GetLevelMetadataById;
  private static string _summaryPath;
  private static long _revision = 1;
  private static DownloadLibrarySummaryFile _summary;
  private static bool _summaryWorkerRunning;
  private static int _maximumCandidateCountObserved;

  public static void Initialize(string installPath)
  {
    string root = string.IsNullOrWhiteSpace(installPath)
      ? AppDomain.CurrentDomain.BaseDirectory
      : installPath;

    lock (Gate)
    {
      _summaryPath = Path.Combine(root, "DownloadLibrarySummary.json");
      _summary = LoadSummary(_summaryPath);
      _revision = Math.Max(1, _summary?.Revision ?? 1);
      if (_summary == null || !PathsEqual(_summary.DownloadRoot, DownloadCachePaths.GetDownloadRoot()))
      {
        _summary = new DownloadLibrarySummaryFile
        {
          Version = SummaryVersion,
          Revision = _revision,
          DownloadRoot = DownloadCachePaths.GetDownloadRoot(),
          State = "calculating"
        };
        StartSummaryRebuildLocked();
      }
      else if (!string.Equals(_summary.State, "ready", StringComparison.Ordinal))
      {
        _summary.State = "calculating";
        StartSummaryRebuildLocked();
      }
    }
  }

  public static DownloadedLevelPage GetPage(string cursorValue, string directionValue, int requestedLimit)
  {
    EnsureStorageAvailable();
    EnsureInitialized();
    int limit = requestedLimit <= 0 ? DefaultPageSize : Math.Min(requestedLimit, MaximumPageSize);
    string direction = string.Equals(directionValue, "previous", StringComparison.OrdinalIgnoreCase)
      ? "previous"
      : "next";
    CursorToken cursor = DecodeCursor(cursorValue);
    long revision;
    lock (Gate) revision = _revision;

    if (cursor != null && cursor.Revision != revision)
      throw new InvalidOperationException("download_library_cursor_stale");

    List<Candidate> candidates = SelectCandidates(
      DownloadCachePaths.GetDownloadRoot(), cursor, direction, limit + 1);
    bool hasExtra = candidates.Count > limit;

    if (hasExtra)
    {
      if (direction == "previous") candidates.RemoveAt(0);
      else candidates.RemoveAt(candidates.Count - 1);
    }

    DownloadedLevelItem[] items = CreateItems(candidates);
    bool hasPrevious = cursor != null && direction == "next" || direction == "previous" && hasExtra;
    bool hasNext = direction == "next" ? hasExtra : cursor != null;

    return new DownloadedLevelPage
    {
      Revision = revision,
      Items = items,
      PreviousCursor = items.Length > 0 ? EncodeCursor(revision, items[0]) : null,
      NextCursor = items.Length > 0 ? EncodeCursor(revision, items[items.Length - 1]) : null,
      HasPrevious = hasPrevious,
      HasNext = hasNext
    };
  }

  public static DownloadLibrarySummary GetSummary()
  {
    EnsureStorageAvailable();
    EnsureInitialized();
    lock (Gate)
    {
      if (_summary == null || !PathsEqual(_summary.DownloadRoot, DownloadCachePaths.GetDownloadRoot()))
      {
        _summary = new DownloadLibrarySummaryFile
        {
          Version = SummaryVersion,
          Revision = _revision,
          DownloadRoot = DownloadCachePaths.GetDownloadRoot(),
          State = "calculating"
        };
        StartSummaryRebuildLocked();
      }

      return ToSummary(_summary);
    }
  }

  public static void RecordDownload(LevelDownloadResult result, TufLevelInfo level, string levelId)
  {
    if (result == null || string.IsNullOrWhiteSpace(result.Directory)) return;
    if (!DownloadCachePaths.TryParseTufCacheKey(Path.GetFileName(result.Directory), out int parsedId))
    {
      if (!int.TryParse((levelId ?? "").Trim().TrimStart('#'), out parsedId) || parsedId <= 0) return;
    }

    EnsureInitialized();
    string manifestPath = Path.Combine(result.Directory, ManifestFileName);
    DownloadedLevelManifest existing = ReadManifest(manifestPath);
    long downloadedAt = existing?.DownloadedAtUnixMs > 0
      ? existing.DownloadedAtUnixMs
      : result.FromCache
        ? new DateTimeOffset(Directory.GetLastWriteTimeUtc(result.Directory)).ToUnixTimeMilliseconds()
        : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    long sizeBytes = CalculatePayloadSize(result.Directory);
    DownloadedLevelManifest manifest = BuildManifest(parsedId, downloadedAt, sizeBytes, level, result.Directory, existing);
    if (!result.FromCache)
    {
      manifest.DownloadedFileId = level?.FileId;
      manifest.InstalledPayloadHash = CalculatePayloadHash(result.Directory);
      ClearAvailableUpdate(manifest);
    }
    WriteAtomic(manifestPath, manifest);

    if (result.FromCache) return;

    lock (Gate)
    {
      _revision++;
      if (_summary != null && string.Equals(_summary.State, "ready", StringComparison.Ordinal))
      {
        _summary.Revision = _revision;
        _summary.LevelCount++;
        _summary.TotalSizeBytes = checked(_summary.TotalSizeBytes + sizeBytes);
        _summary.LastCalculatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        SaveSummaryLocked();
      }
      else if (_summary != null)
      {
        _summary.Revision = _revision;
        SaveSummaryLocked();
      }
    }
  }

  public static DownloadedLevelUpdateDescriptor GetUpdateDescriptor(int id)
  {
    EnsureStorageAvailable();
    EnsureInitialized();
    string directory = Path.Combine(DownloadCachePaths.GetDownloadRoot(), DownloadCachePaths.BuildTufCacheKey(id.ToString(CultureInfo.InvariantCulture)));
    if (!Directory.Exists(directory) || !HasLevelFile(directory))
      throw new InvalidOperationException("downloaded_level_not_found");

    string manifestPath = Path.Combine(directory, ManifestFileName);
    DownloadedLevelManifest manifest = ReadManifest(manifestPath);
    if (manifest?.Id != id)
    {
      long downloadedAt = new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory)).ToUnixTimeMilliseconds();
      manifest = BuildManifest(id, downloadedAt, CalculatePayloadSize(directory), null, directory);
    }
    if (string.IsNullOrWhiteSpace(manifest.InstalledPayloadHash))
    {
      manifest.Version = 2;
      manifest.InstalledPayloadHash = CalculatePayloadHash(directory);
      WriteAtomic(manifestPath, manifest);
    }

    return ToUpdateDescriptor(manifest, directory);
  }

  public static DownloadedLevelItem RecordUpdateCheck(
    int id,
    TufLevelInfo remote,
    string availablePayloadHash,
    bool upToDate)
  {
    DownloadedLevelUpdateDescriptor descriptor = GetUpdateDescriptor(id);
    string manifestPath = Path.Combine(descriptor.Directory, ManifestFileName);
    DownloadedLevelManifest manifest = ReadManifest(manifestPath) ??
      BuildManifest(id, descriptor.DownloadedAtUnixMs, descriptor.SizeBytes, remote, descriptor.Directory);
    manifest = BuildManifest(id, manifest.DownloadedAtUnixMs, manifest.SizeBytes, remote, descriptor.Directory, manifest);
    manifest.LastUpdateCheckedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    if (upToDate)
    {
      if (!string.IsNullOrWhiteSpace(remote?.FileId)) manifest.DownloadedFileId = remote.FileId;
      if (string.IsNullOrWhiteSpace(manifest.InstalledPayloadHash))
        manifest.InstalledPayloadHash = CalculatePayloadHash(descriptor.Directory);
      ClearAvailableUpdate(manifest);
    }
    else
    {
      manifest.AvailableFileId = remote?.FileId;
      manifest.AvailablePayloadHash = availablePayloadHash;
      manifest.AvailableUpdatedAtUtc = remote?.UpdatedAt;
    }
    WriteAtomic(manifestPath, manifest);
    return ToItem(manifest);
  }

  public static DownloadedLevelItem RecordActivatedUpdate(
    int id,
    string directory,
    DownloadedLevelUpdateDescriptor previous,
    TufLevelInfo remote,
    string installedPayloadHash,
    long sizeBytes)
  {
    DownloadedLevelManifest existing = ReadManifest(Path.Combine(directory, ManifestFileName));
    bool alreadyRecorded = existing != null &&
      string.Equals(existing.InstalledPayloadHash, installedPayloadHash, StringComparison.OrdinalIgnoreCase) &&
      (string.IsNullOrWhiteSpace(remote?.FileId) || string.Equals(existing.DownloadedFileId, remote.FileId, StringComparison.Ordinal));
    DownloadedLevelManifest manifest = BuildManifest(
      id,
      previous.DownloadedAtUnixMs,
      sizeBytes,
      remote,
      directory,
      existing);
    manifest.DownloadedFileId = remote?.FileId;
    manifest.InstalledPayloadHash = installedPayloadHash;
    manifest.LastUpdateCheckedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    ClearAvailableUpdate(manifest);
    WriteAtomic(Path.Combine(directory, ManifestFileName), manifest);

    lock (Gate)
    {
      if (!alreadyRecorded && _summary != null && string.Equals(_summary.State, "ready", StringComparison.Ordinal))
      {
        _summary.TotalSizeBytes = Math.Max(0, checked(_summary.TotalSizeBytes - previous.SizeBytes + sizeBytes));
        _summary.LastCalculatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        SaveSummaryLocked();
      }
    }
    return ToItem(manifest);
  }

  public static DownloadedLevelItem GetItem(int id)
  {
    DownloadedLevelUpdateDescriptor descriptor = GetUpdateDescriptor(id);
    DownloadedLevelManifest manifest = ReadManifest(Path.Combine(descriptor.Directory, ManifestFileName));
    if (manifest == null)
      throw new InvalidOperationException("downloaded_level_manifest_missing");
    return ToItem(manifest);
  }

  public static long GetPayloadSize(string directory) => CalculatePayloadSize(directory);

  public static string CalculatePayloadHash(string directory)
  {
    if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
    using SHA256 hash = SHA256.Create();
    string[] files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
      .Where(file => !IsManifestFile(file))
      .OrderBy(file => Path.GetRelativePath(directory, file).Replace('\\', '/'), StringComparer.Ordinal)
      .ToArray();
    byte[] buffer = new byte[128 * 1024];
    foreach (string file in files)
    {
      string relativePath = Path.GetRelativePath(directory, file).Replace('\\', '/');
      byte[] pathBytes = Encoding.UTF8.GetBytes(relativePath);
      byte[] pathLength = BitConverter.GetBytes(pathBytes.Length);
      hash.TransformBlock(pathLength, 0, pathLength.Length, null, 0);
      hash.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
      using FileStream input = File.OpenRead(file);
      int read;
      while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        hash.TransformBlock(buffer, 0, read, null, 0);
    }
    hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
    return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
  }

  public static void NotifyStorageRootChanged()
  {
    EnsureInitialized();
    lock (Gate)
    {
      _revision++;
      if (_summary == null)
      {
        _summary = new DownloadLibrarySummaryFile();
      }

      _summary.Version = SummaryVersion;
      _summary.Revision = _revision;
      _summary.DownloadRoot = DownloadCachePaths.GetDownloadRoot();
      if (string.IsNullOrWhiteSpace(_summary.State)) _summary.State = "calculating";
      SaveSummaryLocked();
      if (_summary.State != "ready") StartSummaryRebuildLocked();
    }
  }

  internal static int GetCandidateCapacityForTests(int limit) => Math.Min(Math.Max(limit, 1), MaximumPageSize) + 1;
  internal static int MaximumCandidateCountObservedForTests => _maximumCandidateCountObserved;
  internal static void ResetCandidateCountForTests() => _maximumCandidateCountObserved = 0;
  internal static void SetMetadataProviderForTests(Func<string, TufLevelInfo> provider) =>
    _metadataProvider = provider ?? TuforumsClient.GetLevelMetadataById;

  internal static void RebuildSummaryForTests()
  {
    EnsureInitialized();
    RebuildSummary();
  }

  private static List<Candidate> SelectCandidates(
    string downloadRoot,
    CursorToken cursor,
    string direction,
    int capacity)
  {
    List<Candidate> selected = new(capacity);
    if (!Directory.Exists(downloadRoot)) return selected;

    foreach (string directory in Directory.EnumerateDirectories(downloadRoot, "tuf-*", SearchOption.TopDirectoryOnly))
    {
      if (!DownloadCachePaths.TryParseTufCacheKey(Path.GetFileName(directory), out int id)) continue;
      if (!HasLevelFile(directory)) continue;
      DownloadedLevelManifest manifest = ReadManifest(Path.Combine(directory, ManifestFileName));
      long downloadedAt = manifest?.DownloadedAtUnixMs > 0
        ? manifest.DownloadedAtUnixMs
        : new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory)).ToUnixTimeMilliseconds();
      Candidate candidate = new(id, downloadedAt, directory);
      if (cursor != null)
      {
        int comparison = Compare(candidate.DownloadedAtUnixMs, candidate.Id, cursor.DownloadedAtUnixMs, cursor.Id);
        if (direction == "previous" ? comparison >= 0 : comparison <= 0) continue;
      }

      if (selected.Count < capacity)
      {
        selected.Add(candidate);
        selected.Sort(CandidateComparer.Instance);
      }
      else if (direction == "previous" && CandidateComparer.Instance.Compare(candidate, selected[0]) > 0)
      {
        selected[0] = candidate;
        selected.Sort(CandidateComparer.Instance);
      }
      else if (direction != "previous" && CandidateComparer.Instance.Compare(candidate, selected[selected.Count - 1]) < 0)
      {
        selected[selected.Count - 1] = candidate;
        selected.Sort(CandidateComparer.Instance);
      }

      _maximumCandidateCountObserved = Math.Max(_maximumCandidateCountObserved, selected.Count);
    }

    return selected;
  }

  private static DownloadedLevelItem[] CreateItems(List<Candidate> candidates)
  {
    DownloadedLevelItem[] items = new DownloadedLevelItem[candidates.Count];
    try
    {
      Parallel.For(
        0,
        candidates.Count,
        new ParallelOptions { MaxDegreeOfParallelism = MetadataFetchConcurrency },
        index => items[index] = CreateItem(candidates[index]));
      return items;
    }
    catch (AggregateException exception)
    {
      Exception cause = exception.Flatten().InnerExceptions.FirstOrDefault() ?? exception;
      throw new InvalidOperationException("downloaded_level_metadata_fetch_failed: " + cause.Message, cause);
    }
  }

  private static DownloadedLevelItem CreateItem(Candidate candidate)
  {
    string manifestPath = Path.Combine(candidate.Directory, ManifestFileName);
    DownloadedLevelManifest manifest = ReadManifest(manifestPath);
    if (manifest?.Id != candidate.Id) manifest = null;
    if (manifest == null)
    {
      long sizeBytes = CalculatePayloadSize(candidate.Directory);
      manifest = BuildManifest(candidate.Id, candidate.DownloadedAtUnixMs, sizeBytes, null, candidate.Directory);
    }

    if (!string.Equals(manifest.MetadataState, "ready", StringComparison.Ordinal))
    {
      TufLevelInfo remote = _metadataProvider(candidate.Id.ToString(CultureInfo.InvariantCulture));
      manifest = BuildManifest(
        candidate.Id,
        manifest.DownloadedAtUnixMs,
        manifest.SizeBytes,
        remote,
        candidate.Directory);
      if (!string.Equals(manifest.MetadataState, "ready", StringComparison.Ordinal))
        throw new InvalidOperationException($"Downloaded level #{candidate.Id} metadata is incomplete.");

      WriteAtomic(manifestPath, manifest);
      Directory.SetLastWriteTimeUtc(
        candidate.Directory,
        DateTimeOffset.FromUnixTimeMilliseconds(candidate.DownloadedAtUnixMs).UtcDateTime);
    }

    return ToItem(manifest);
  }

  private static DownloadedLevelManifest BuildManifest(
    int id,
    long downloadedAt,
    long sizeBytes,
    TufLevelInfo remote,
    string directory,
    DownloadedLevelManifest existing = null)
  {
    LocalLevelMetadata local = ReadLocalMetadata(directory);
    string levelName = FirstNonEmpty(remote?.Song, local.LevelName);
    string artist = FirstNonEmpty(remote?.Artist, local.Artist);
    string creator = FirstNonEmpty(remote?.Creator, remote?.Charter, remote?.Team, local.Creator);
    bool complete = remote != null && remote.DiffId > 0 &&
      !string.IsNullOrWhiteSpace(levelName) &&
      !string.IsNullOrWhiteSpace(artist) &&
      !string.IsNullOrWhiteSpace(creator);
    return new DownloadedLevelManifest
    {
      Version = 2,
      Id = id,
      DiffId = remote?.DiffId ?? 0,
      Artist = artist,
      LevelName = levelName,
      Creator = creator,
      SizeBytes = sizeBytes,
      DownloadedAtUnixMs = downloadedAt,
      MetadataState = complete ? "ready" : "partial",
      DownloadedFileId = existing?.DownloadedFileId,
      InstalledPayloadHash = existing?.InstalledPayloadHash,
      AvailableFileId = existing?.AvailableFileId,
      AvailablePayloadHash = existing?.AvailablePayloadHash,
      AvailableUpdatedAtUtc = existing?.AvailableUpdatedAtUtc,
      LastUpdateCheckedAtUtc = existing?.LastUpdateCheckedAtUtc
    };
  }

  private static DownloadedLevelItem ToItem(DownloadedLevelManifest manifest) => new()
  {
    Id = manifest.Id,
    DiffId = manifest.DiffId,
    Artist = manifest.Artist,
    LevelName = manifest.LevelName,
    Creator = manifest.Creator,
    SizeBytes = Math.Max(0, manifest.SizeBytes),
    DownloadedAtUnixMs = manifest.DownloadedAtUnixMs,
    DownloadedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(manifest.DownloadedAtUnixMs).UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
    MetadataState = manifest.MetadataState,
    UpdateState = HasAvailableUpdate(manifest) ? "update_available" : "idle"
  };

  private static DownloadedLevelUpdateDescriptor ToUpdateDescriptor(DownloadedLevelManifest manifest, string directory) => new()
  {
    Id = manifest.Id,
    Directory = directory,
    DownloadedAtUnixMs = manifest.DownloadedAtUnixMs,
    SizeBytes = Math.Max(0, manifest.SizeBytes),
    DownloadedFileId = manifest.DownloadedFileId,
    InstalledPayloadHash = manifest.InstalledPayloadHash,
    AvailableFileId = manifest.AvailableFileId,
    AvailablePayloadHash = manifest.AvailablePayloadHash,
    AvailableUpdatedAtUtc = manifest.AvailableUpdatedAtUtc,
    LastUpdateCheckedAtUtc = manifest.LastUpdateCheckedAtUtc
  };

  private static bool HasAvailableUpdate(DownloadedLevelManifest manifest) =>
    !string.IsNullOrWhiteSpace(manifest.AvailableFileId) ||
    !string.IsNullOrWhiteSpace(manifest.AvailablePayloadHash);

  private static void ClearAvailableUpdate(DownloadedLevelManifest manifest)
  {
    manifest.AvailableFileId = null;
    manifest.AvailablePayloadHash = null;
    manifest.AvailableUpdatedAtUtc = null;
  }

  private static LocalLevelMetadata ReadLocalMetadata(string directory)
  {
    try
    {
      string path = Directory.EnumerateFiles(directory, "*.adofai", SearchOption.AllDirectories).FirstOrDefault();
      if (path == null) return new LocalLevelMetadata();
      JObject root = JObject.Parse(File.ReadAllText(path));
      JObject settings = root["settings"] as JObject ?? root;
      return new LocalLevelMetadata
      {
        LevelName = (string)settings["song"] ?? Path.GetFileNameWithoutExtension(path),
        Artist = (string)settings["artist"],
        Creator = (string)(settings["author"] ?? settings["creator"])
      };
    }
    catch
    {
      return new LocalLevelMetadata();
    }
  }

  private static long CalculatePayloadSize(string directory)
  {
    long total = 0;
    if (!Directory.Exists(directory)) return total;
    foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
    {
      string name = Path.GetFileName(file);
      if (string.Equals(name, ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
          string.Equals(name, ManifestFileName + ".tmp", StringComparison.OrdinalIgnoreCase)) continue;
      try { total = checked(total + new FileInfo(file).Length); }
      catch (IOException) { }
      catch (UnauthorizedAccessException) { }
    }
    return total;
  }

  private static bool HasLevelFile(string directory)
  {
    try
    {
      return Directory.EnumerateFiles(directory, "*.adofai", SearchOption.AllDirectories)
        .Any(file => !Path.GetFileName(file).Contains("backup", StringComparison.OrdinalIgnoreCase));
    }
    catch
    {
      return false;
    }
  }

  private static void StartSummaryRebuildLocked()
  {
    if (_summaryWorkerRunning) return;
    _summaryWorkerRunning = true;
    Task.Run(RebuildSummary);
  }

  private static void RebuildSummary()
  {
    string root = DownloadCachePaths.GetDownloadRoot();
    long revision;
    lock (Gate) revision = _revision;
    int count = 0;
    long total = 0;
    string errorCode = null;
    string message = null;

    try
    {
      if (Directory.Exists(root))
      {
        foreach (string directory in Directory.EnumerateDirectories(root, "tuf-*", SearchOption.TopDirectoryOnly))
        {
          if (!DownloadCachePaths.TryParseTufCacheKey(Path.GetFileName(directory), out _)) continue;
          if (!HasLevelFile(directory)) continue;
          count++;
          DownloadedLevelManifest manifest = ReadManifest(Path.Combine(directory, ManifestFileName));
          total = checked(total + (manifest?.SizeBytes > 0 ? manifest.SizeBytes : CalculatePayloadSize(directory)));
        }
      }
    }
    catch (Exception exception)
    {
      errorCode = "download_library_summary_failed";
      message = exception.Message;
    }

    lock (Gate)
    {
      _summaryWorkerRunning = false;
      if (revision != _revision || !PathsEqual(root, DownloadCachePaths.GetDownloadRoot()))
      {
        StartSummaryRebuildLocked();
        return;
      }

      _summary = new DownloadLibrarySummaryFile
      {
        Version = SummaryVersion,
        Revision = _revision,
        DownloadRoot = root,
        State = errorCode == null ? "ready" : "failed",
        LevelCount = count,
        TotalSizeBytes = total,
        LastCalculatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        ErrorCode = errorCode,
        Message = message
      };
      SaveSummaryLocked();
    }
  }

  private static string EncodeCursor(long revision, DownloadedLevelItem item)
  {
    CursorToken cursor = new()
    {
      Version = CursorVersion,
      Revision = revision,
      DownloadedAtUnixMs = item.DownloadedAtUnixMs,
      Id = item.Id
    };
    return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(cursor)));
  }

  private static CursorToken DecodeCursor(string value)
  {
    if (string.IsNullOrWhiteSpace(value)) return null;
    try
    {
      CursorToken cursor = JsonConvert.DeserializeObject<CursorToken>(
        Encoding.UTF8.GetString(Convert.FromBase64String(value)));
      if (cursor == null || cursor.Version != CursorVersion || cursor.Id <= 0 || cursor.DownloadedAtUnixMs <= 0)
        throw new InvalidOperationException("download_library_cursor_invalid");
      return cursor;
    }
    catch (InvalidOperationException) { throw; }
    catch { throw new InvalidOperationException("download_library_cursor_invalid"); }
  }

  private static int Compare(long leftTime, int leftId, long rightTime, int rightId)
  {
    int time = rightTime.CompareTo(leftTime);
    return time != 0 ? time : rightId.CompareTo(leftId);
  }

  private static DownloadedLevelManifest ReadManifest(string path)
  {
    try
    {
      if (!File.Exists(path)) return null;
      DownloadedLevelManifest value = JsonConvert.DeserializeObject<DownloadedLevelManifest>(File.ReadAllText(path));
      return value != null && (value.Version == 1 || value.Version == 2) && value.Id > 0 && value.DownloadedAtUnixMs > 0 ? value : null;
    }
    catch { return null; }
  }

  private static DownloadLibrarySummaryFile LoadSummary(string path)
  {
    try
    {
      if (!File.Exists(path)) return null;
      DownloadLibrarySummaryFile value = JsonConvert.DeserializeObject<DownloadLibrarySummaryFile>(File.ReadAllText(path));
      return value?.Version == SummaryVersion && value.Revision > 0 ? value : null;
    }
    catch { return null; }
  }

  private static void WriteAtomic(string path, object value)
  {
    string temporary = path + ".tmp";
    File.WriteAllText(temporary, JsonConvert.SerializeObject(value, Formatting.Indented));
    if (File.Exists(path)) File.Replace(temporary, path, null);
    else File.Move(temporary, path);
  }

  private static bool IsManifestFile(string file)
  {
    string name = Path.GetFileName(file);
    return string.Equals(name, ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
      string.Equals(name, ManifestFileName + ".tmp", StringComparison.OrdinalIgnoreCase);
  }

  private static void SaveSummaryLocked()
  {
    if (string.IsNullOrWhiteSpace(_summaryPath) || _summary == null) return;
    try { WriteAtomic(_summaryPath, _summary); }
    catch (Exception exception) { Main.Instance?.Warning("Failed to save download library summary: " + exception.Message); }
  }

  private static DownloadLibrarySummary ToSummary(DownloadLibrarySummaryFile value) => new()
  {
    State = value.State,
    Revision = value.Revision,
    LevelCount = value.LevelCount,
    TotalSizeBytes = value.TotalSizeBytes,
    LastCalculatedAtUtc = value.LastCalculatedAtUtc,
    ErrorCode = value.ErrorCode,
    Message = value.Message
  };

  private static void EnsureInitialized()
  {
    if (_summaryPath != null) return;
    Initialize(Main.Instance?.ModEntry?.Path ?? AppDomain.CurrentDomain.BaseDirectory);
  }

  private static void EnsureStorageAvailable()
  {
    if (DownloadStorageMigrationService.IsMigrationActive)
      throw new InvalidOperationException("storage_migration_in_progress");
  }

  private static bool PathsEqual(string left, string right)
  {
    if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
    return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
      Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
  }

  private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

  private sealed class Candidate
  {
    public Candidate(int id, long downloadedAtUnixMs, string directory)
    {
      Id = id;
      DownloadedAtUnixMs = downloadedAtUnixMs;
      Directory = directory;
    }
    public int Id { get; }
    public long DownloadedAtUnixMs { get; }
    public string Directory { get; }
  }

  private sealed class CandidateComparer : IComparer<Candidate>
  {
    public static readonly CandidateComparer Instance = new();
    public int Compare(Candidate left, Candidate right) => DownloadLibraryService.Compare(
      left.DownloadedAtUnixMs, left.Id, right.DownloadedAtUnixMs, right.Id);
  }

  private sealed class CursorToken
  {
    public int Version { get; set; }
    public long Revision { get; set; }
    public long DownloadedAtUnixMs { get; set; }
    public int Id { get; set; }
  }

  private sealed class DownloadedLevelManifest
  {
    public int Version { get; set; }
    public int Id { get; set; }
    public int DiffId { get; set; }
    public string Artist { get; set; }
    public string LevelName { get; set; }
    public string Creator { get; set; }
    public long SizeBytes { get; set; }
    public long DownloadedAtUnixMs { get; set; }
    public string MetadataState { get; set; }
    public string DownloadedFileId { get; set; }
    public string InstalledPayloadHash { get; set; }
    public string AvailableFileId { get; set; }
    public string AvailablePayloadHash { get; set; }
    public string AvailableUpdatedAtUtc { get; set; }
    public string LastUpdateCheckedAtUtc { get; set; }
  }

  private sealed class DownloadLibrarySummaryFile
  {
    public int Version { get; set; }
    public long Revision { get; set; }
    public string DownloadRoot { get; set; }
    public string State { get; set; }
    public int LevelCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public string LastCalculatedAtUtc { get; set; }
    public string ErrorCode { get; set; }
    public string Message { get; set; }
  }

  private sealed class LocalLevelMetadata
  {
    public string Artist { get; set; }
    public string LevelName { get; set; }
    public string Creator { get; set; }
  }
}
