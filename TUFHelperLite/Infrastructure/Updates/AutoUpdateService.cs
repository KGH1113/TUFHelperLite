using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TUFHelperLite.Infrastructure.Downloads;

namespace TUFHelperLite.Infrastructure.Updates;

internal sealed class AutoUpdateService : IDisposable
{
  private const string LatestReleaseUrl = "https://api.github.com/repos/KGH1113/TUFHelperLite/releases/latest";
  private const string PackageAssetName = "TUFHelperLite.zip";
  private const string ChecksumAssetName = "TUFHelperLite.zip.sha256";
  private const int BufferSize = 128 * 1024;
  private const long DiskCheckInterval = 64L * 1024 * 1024;
  private readonly string _currentVersion;
  private readonly string _modRoot;
  private readonly Action<string> _log;
  private readonly Action<string> _warning;
  private readonly Action<string> _ready;
  private readonly CancellationTokenSource _cancellation = new();
  private Task _task;

  public AutoUpdateService(
    string currentVersion,
    string modRoot,
    Action<string> log,
    Action<string> warning,
    Action<string> ready)
  {
    _currentVersion = currentVersion;
    _modRoot = Path.GetFullPath(modRoot);
    _log = log ?? (_ => { });
    _warning = warning ?? (_ => { });
    _ready = ready ?? (_ => { });
  }

  public void Start()
  {
    if (_task != null) return;
    _task = Task.Run(() => CheckForUpdateAsync(_cancellation.Token));
  }

  public void Dispose()
  {
    _cancellation.Cancel();
    _cancellation.Dispose();
  }

  private async Task CheckForUpdateAsync(CancellationToken cancellationToken)
  {
    try
    {
      using HttpClient client = CreateClient();
      string releaseJson = await GetLatestReleaseJsonAsync(client, cancellationToken).ConfigureAwait(false);
      UpdateReleaseSelection release = SelectRelease(releaseJson, _currentVersion);
      if (release == null) return;

      GitHubAsset package = release.Package;
      GitHubAsset checksum = release.Checksum;

      string updatesRoot = Path.Combine(_modRoot, "Data", "updates");
      Directory.CreateDirectory(updatesRoot);
      string archivePath = Path.Combine(updatesRoot, "download-" + Guid.NewGuid().ToString("N") + ".zip");

      try
      {
        string checksumText = await client.GetStringAsync(checksum.BrowserDownloadUrl).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        string expectedSha256 = UpdatePackageStager.ParseChecksum(checksumText);

        await DownloadFileAsync(
          client,
          package.BrowserDownloadUrl,
          archivePath,
          package.Size,
          cancellationToken).ConfigureAwait(false);
        string actualSha256 = UpdatePackageStager.ComputeSha256(archivePath);
        if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
          throw new InvalidDataException("TUFHelperLite update checksum does not match.");

        string version = release.Version;
        UpdatePackageStager.Stage(archivePath, _modRoot, version, actualSha256);
        _log($"TUFHelperLite {version} update downloaded and verified.");
        _ready(version);
      }
      finally
      {
        if (File.Exists(archivePath)) File.Delete(archivePath);
      }
    }
    catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
    {
    }
    catch (Exception exception)
    {
      _warning("TUFHelperLite automatic update failed: " + exception.Message);
    }
  }

  private static HttpClient CreateClient()
  {
    HttpClient client = new()
    {
      Timeout = TimeSpan.FromSeconds(30)
    };
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TUFHelperLite", "1.0"));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    return client;
  }

  internal static UpdateReleaseSelection SelectRelease(string json, string currentVersion)
  {
    GitHubRelease release = JsonConvert.DeserializeObject<GitHubRelease>(json);
    if (release == null || string.IsNullOrWhiteSpace(release.TagName))
      throw new InvalidDataException("GitHub returned invalid release metadata.");
    if (release.Draft || release.Prerelease || !UpdateVersion.IsNewer(release.TagName, currentVersion)) return null;

    GitHubAsset package = FindAsset(release, PackageAssetName);
    GitHubAsset checksum = FindAsset(release, ChecksumAssetName);
    ValidateAsset(package, PackageAssetName, true);
    ValidateAsset(checksum, ChecksumAssetName, false);
    return new UpdateReleaseSelection(UpdateVersion.Normalize(release.TagName), package, checksum);
  }

  private static async Task<string> GetLatestReleaseJsonAsync(
    HttpClient client,
    CancellationToken cancellationToken)
  {
    using HttpResponseMessage response = await client.GetAsync(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
  }

  private static GitHubAsset FindAsset(GitHubRelease release, string name)
  {
    return release.Assets?.SingleOrDefault(asset => asset.Name.Equals(name, StringComparison.Ordinal));
  }

  private static void ValidateAsset(GitHubAsset asset, string name, bool package)
  {
    if (asset == null) throw new InvalidDataException("GitHub release is missing " + name + ".");
    if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out Uri uri) ||
        uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
        !uri.AbsolutePath.StartsWith("/KGH1113/TUFHelperLite/releases/download/", StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("GitHub release asset URL is invalid.");
    long maximumSize = package ? UpdatePackageStager.MaximumArchiveBytes : 64L * 1024;
    if (asset.Size <= 0 || asset.Size > maximumSize)
      throw new InvalidDataException("GitHub release asset size is invalid.");
  }

  private static async Task DownloadFileAsync(
    HttpClient client,
    string url,
    string destination,
    long expectedBytes,
    CancellationToken cancellationToken)
  {
    DiskSpacePolicy.EnsureSufficientSpace(
      destination,
      expectedBytes,
      "Not enough free disk space to download the TUFHelperLite update.");

    using HttpRequestMessage request = new(HttpMethod.Get, url);
    using HttpResponseMessage response = await client.SendAsync(
      request,
      HttpCompletionOption.ResponseHeadersRead,
      cancellationToken).ConfigureAwait(false);
    response.EnsureSuccessStatusCode();
    if (response.Content.Headers.ContentLength is long contentLength &&
        (contentLength <= 0 || contentLength > UpdatePackageStager.MaximumArchiveBytes))
      throw new InvalidDataException("TUFHelperLite update download size is invalid.");

    using Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
    using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    byte[] buffer = new byte[BufferSize];
    long received = 0;
    long nextDiskCheck = DiskCheckInterval;
    int read;

    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
    {
      await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
      received = checked(received + read);
      if (received > UpdatePackageStager.MaximumArchiveBytes)
        throw new InvalidDataException("TUFHelperLite update exceeds the download size limit.");

      if (received >= nextDiskCheck)
      {
        long remaining = DiskSpacePolicy.CalculateRemainingBytes(expectedBytes, received);
        DiskSpacePolicy.EnsureSufficientSpace(
          destination,
          remaining,
          "Not enough free disk space to download the TUFHelperLite update.");
        nextDiskCheck = checked(received + DiskCheckInterval);
      }
    }

    if (expectedBytes > 0 && received != expectedBytes)
      throw new InvalidDataException("TUFHelperLite update download is incomplete.");
  }

  internal sealed class UpdateReleaseSelection
  {
    public UpdateReleaseSelection(string version, GitHubAsset package, GitHubAsset checksum)
    {
      Version = version;
      Package = package;
      Checksum = checksum;
    }

    public string Version { get; }
    public GitHubAsset Package { get; }
    public GitHubAsset Checksum { get; }
  }

  internal sealed class GitHubRelease
  {
    [JsonProperty("tag_name")]
    public string TagName { get; set; }

    [JsonProperty("draft")]
    public bool Draft { get; set; }

    [JsonProperty("prerelease")]
    public bool Prerelease { get; set; }

    [JsonProperty("assets")]
    public GitHubAsset[] Assets { get; set; }
  }

  internal sealed class GitHubAsset
  {
    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("browser_download_url")]
    public string BrowserDownloadUrl { get; set; }

    [JsonProperty("size")]
    public long Size { get; set; }
  }
}
