using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

    string downloadRoot = GetDownloadRoot();
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
    Directory.CreateDirectory(extractPath);

    client.DownloadProgressChanged += (_, args) =>
    {
      onProgress?.Invoke(new LevelDownloadProgress
      {
        Stage = "downloading",
        Progress = args.TotalBytesToReceive > 0 ? args.BytesReceived / (double)args.TotalBytesToReceive : -1,
        BytesReceived = args.BytesReceived,
        TotalBytes = args.TotalBytesToReceive,
        Message = "Downloading level archive"
      });
    };

    byte[] zipBytes;
    using (cancellationToken.Register(client.CancelAsync))
    {
      zipBytes = client.DownloadDataTaskAsync(directUrl).GetAwaiter().GetResult();
    }

    cancellationToken.ThrowIfCancellationRequested();

    onProgress?.Invoke(new LevelDownloadProgress
    {
      Stage = "extracting",
      Progress = -1,
      Message = "Extracting level archive"
    });

    ZipExtractor.Extract(zipBytes, extractPath);

    onProgress?.Invoke(new LevelDownloadProgress
    {
      Stage = "scanning",
      Progress = -1,
      Message = "Finding .adofai files"
    });

    DirectoryFlattener.MoveLastDirectoryFilesToRoot(extractPath, extractPath);

    return CreateResult(url, directUrl, extractPath, false);
  }

  public static string[] GetDownloadedLevelIds()
  {
    string downloadRoot = GetDownloadRoot();
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

  private static string GetDownloadRoot()
  {
    string modPath = Main.Instance?.ModEntry?.Path;
    if (string.IsNullOrWhiteSpace(modPath))
    {
      modPath = AppDomain.CurrentDomain.BaseDirectory;
    }

    return Path.Combine(modPath, "Downloads");
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
