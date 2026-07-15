using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using TUFHelperLite.Domain.Levels;

namespace TUFHelperLite.Infrastructure.Downloads;

public static class LevelArchiveDownloader
{
  public static LevelDownloadResult Download(
    string url,
    string cacheKey,
    CancellationToken cancellationToken,
    Action<LevelDownloadProgress> onProgress = null)
  {
    if (string.IsNullOrWhiteSpace(url))
    {
      throw new ArgumentException("Download URL is required.", nameof(url));
    }

    string downloadRoot = DownloadCachePaths.GetDownloadRoot();
    string key = SanitizeCacheKey(cacheKey);
    string extractPath = Path.Combine(downloadRoot, key);
    string zipPath = extractPath + ".zip";

    Directory.CreateDirectory(downloadRoot);

    if (Directory.Exists(extractPath) && FindAdofaiFiles(extractPath).Count > 0)
    {
      onProgress?.Invoke(new LevelDownloadProgress
      {
        Stage = "cached",
        Progress = 1,
        Message = "Using cached level"
      });

      return CreateResult(url, url, extractPath, true);
    }

    using CookieWebClient client = new();
    onProgress?.Invoke(new LevelDownloadProgress
    {
      Stage = "resolving",
      Progress = -1,
      Message = "Resolving download URL"
    });

    string directUrl = DownloadUrlResolver.Resolve(url, client);

    if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
    if (File.Exists(zipPath)) File.Delete(zipPath);

    try
    {
      DownloadArchive(client, directUrl, zipPath, cancellationToken, onProgress);

      cancellationToken.ThrowIfCancellationRequested();

      onProgress?.Invoke(new LevelDownloadProgress
      {
        Stage = "extracting",
        Progress = -1,
        Message = "Extracting level archive"
      });

      ZipExtractor.Extract(zipPath, extractPath, cancellationToken);

      onProgress?.Invoke(new LevelDownloadProgress
      {
        Stage = "scanning",
        Progress = -1,
        Message = "Finding .adofai files"
      });

      DirectoryFlattener.MoveLastDirectoryFilesToRoot(extractPath, extractPath);

      return CreateResult(url, directUrl, extractPath, false);
    }
    catch
    {
      TryDeleteDirectory(extractPath);
      throw;
    }
    finally
    {
      TryDeleteFile(zipPath);
    }
  }

  public static string[] GetDownloadedLevelIds()
  {
    string downloadRoot = DownloadCachePaths.GetDownloadRoot();
    if (!Directory.Exists(downloadRoot)) return Array.Empty<string>();

    return Directory.GetDirectories(downloadRoot, "tuf-*", SearchOption.TopDirectoryOnly)
      .Where(directory => FindAdofaiFiles(directory).Count > 0)
      .Select(Path.GetFileName)
      .Where(name => name != null && name.StartsWith("tuf-", StringComparison.OrdinalIgnoreCase))
      .Select(name => name.Substring(4))
      .Where(id => id.All(char.IsDigit))
      .Distinct()
      .OrderBy(id => id)
      .ToArray();
  }

  private static LevelDownloadResult CreateResult(string sourceUrl, string directUrl, string extractPath, bool fromCache)
  {
    List<string> levelPaths = FindAdofaiFiles(extractPath);

    return new LevelDownloadResult
    {
      SourceUrl = sourceUrl,
      DirectUrl = directUrl,
      Directory = extractPath,
      SelectedLevelPath = levelPaths.FirstOrDefault(),
      LevelPaths = levelPaths,
      FromCache = fromCache
    };
  }

  private static string SanitizeCacheKey(string cacheKey)
  {
    string key = string.IsNullOrWhiteSpace(cacheKey) ? Guid.NewGuid().ToString("N") : cacheKey;

    foreach (char invalid in Path.GetInvalidFileNameChars())
    {
      key = key.Replace(invalid, '_');
    }

    return key;
  }

  private static List<string> FindAdofaiFiles(string path)
  {
    return Directory.GetFiles(path, "*.adofai", SearchOption.AllDirectories)
      .Where(file => !Path.GetFileName(file).ToLowerInvariant().Contains("backup"))
      .OrderByDescending(file => new FileInfo(file).Length)
      .ToList();
  }

  private static void DownloadArchive(
    CookieWebClient client,
    string directUrl,
    string zipPath,
    CancellationToken cancellationToken,
    Action<LevelDownloadProgress> onProgress)
  {
    const int bufferSize = 128 * 1024;
    const long diskCheckInterval = 64L * 1024 * 1024;
    byte[] buffer = new byte[bufferSize];
    long bytesReceived = 0;
    long nextDiskCheck = diskCheckInterval;
    Stopwatch progressTimer = Stopwatch.StartNew();

    try
    {
      using CancellationTokenRegistration registration = cancellationToken.Register(client.CancelAsync);
      using Stream input = client.OpenReadTaskAsync(directUrl).GetAwaiter().GetResult();
      long totalBytes = ReadContentLength(client);
      long initialRequiredBytes = DiskSpacePolicy.CalculateRemainingBytes(totalBytes, bytesReceived);
      ZipExtractor.EnsureAvailableSpace(zipPath, initialRequiredBytes);
      using FileStream output = new(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
      int bytesRead;

      while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
      {
        cancellationToken.ThrowIfCancellationRequested();
        output.Write(buffer, 0, bytesRead);
        bytesReceived = checked(bytesReceived + bytesRead);

        if (bytesReceived >= nextDiskCheck)
        {
          long remainingBytes = DiskSpacePolicy.CalculateRemainingBytes(totalBytes, bytesReceived);
          ZipExtractor.EnsureAvailableSpace(zipPath, remainingBytes);
          nextDiskCheck = checked(bytesReceived + diskCheckInterval);
        }

        if (progressTimer.ElapsedMilliseconds >= 100)
        {
          ReportDownloadProgress(onProgress, bytesReceived, totalBytes);
          progressTimer.Restart();
        }
      }

      ZipExtractor.EnsureDiskReserve(zipPath);
      ReportDownloadProgress(onProgress, bytesReceived, totalBytes);
    }
    catch (WebException) when (cancellationToken.IsCancellationRequested)
    {
      throw new OperationCanceledException(cancellationToken);
    }
  }

  private static long ReadContentLength(WebClient client)
  {
    string value = client.ResponseHeaders?[HttpResponseHeader.ContentLength];
    return long.TryParse(value, out long length) && length > 0 ? length : -1;
  }

  private static void ReportDownloadProgress(
    Action<LevelDownloadProgress> onProgress,
    long bytesReceived,
    long totalBytes)
  {
    onProgress?.Invoke(new LevelDownloadProgress
    {
      Stage = "downloading",
      Progress = totalBytes > 0 ? bytesReceived / (double)totalBytes : -1,
      BytesReceived = bytesReceived,
      TotalBytes = totalBytes,
      Message = "Downloading level archive"
    });
  }

  private static void TryDeleteDirectory(string path)
  {
    try
    {
      if (Directory.Exists(path)) Directory.Delete(path, true);
    }
    catch (Exception e)
    {
      Main.Instance?.Warning($"Failed to clean partial download directory: {e.Message}");
    }
  }

  private static void TryDeleteFile(string path)
  {
    try
    {
      if (File.Exists(path)) File.Delete(path);
    }
    catch (Exception e)
    {
      Main.Instance?.Warning($"Failed to clean temporary archive: {e.Message}");
    }
  }

  private sealed class CookieWebClient : WebClient
  {
    public CookieWebClient()
    {
      Encoding = Encoding.UTF8;
      Proxy = null;
      Headers[HttpRequestHeader.UserAgent] = $"TUFHelperLite/{ModStatus.Version}";
    }

    protected override WebRequest GetWebRequest(Uri address)
    {
      DownloadUrlPolicy.Validate(address);
      WebRequest request = base.GetWebRequest(address);

      if (request is HttpWebRequest httpRequest)
      {
        httpRequest.Timeout = 30000;
        httpRequest.ReadWriteTimeout = 300000;
      }

      return request;
    }
  }
}
