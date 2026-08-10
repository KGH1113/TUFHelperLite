using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TUFHelperLite.UpdateEngine;

internal sealed class UpdateManager
{
  private const string LatestReleaseUrl =
    "https://api.github.com/repos/KGH1113/TUFHelperLite/releases/latest";
  internal const string PackageAssetName = "TUFHelperLite.zip";
  internal const string ChecksumAssetName = "TUFHelperLite.zip.sha256";
  private const int BufferSize = 128 * 1024;
  private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(20);
  private readonly string _modRoot;
  private readonly HttpClient _client;

  public UpdateManager(string modRoot)
    : this(modRoot, null)
  {
  }

  internal UpdateManager(string modRoot, HttpClient client)
  {
    _modRoot = Path.GetFullPath(modRoot ?? throw new ArgumentNullException(nameof(modRoot)));
    _client = client;
  }

  public UpdateResult Resolve(string currentVersion)
  {
    using CancellationTokenSource timeout = new(NetworkTimeout);
    Task<UpdateResult> operation = ResolveAsync(currentVersion, timeout.Token);
    Task deadline = Task.Delay(NetworkTimeout);
    if (Task.WhenAny(operation, deadline).GetAwaiter().GetResult() != operation)
    {
      timeout.Cancel();
      _ = operation.ContinueWith(
        completed => { if (completed.IsFaulted) _ = completed.Exception; },
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
      throw new TimeoutException("TUFHelperLite update operations timed out after 20 seconds.");
    }
    return operation.GetAwaiter().GetResult();
  }

  private async Task<UpdateResult> ResolveAsync(string currentVersion, CancellationToken cancellationToken)
  {
    using HttpClient ownedClient = _client == null ? CreateClient() : null;
    HttpClient client = _client ?? ownedClient;
    string releaseJson = await GetTextAsync(client, LatestReleaseUrl, 4 * 1024 * 1024, cancellationToken)
      .ConfigureAwait(false);
    UpdateReleaseSelection release = SelectRelease(releaseJson, currentVersion);
    if (release == null)
      return new UpdateResult { Outcome = UpdateOutcomes.None };

    string target = Path.Combine(_modRoot, "Runtime", "versions", release.Version);
    try
    {
      RuntimePackageInstaller.ValidateCandidate(target, release.Version);
      return Candidate(release.Version, target);
    }
    catch
    {
      // A missing or incomplete unreferenced candidate is replaced after the package is verified.
    }

    string updatesRoot = Path.Combine(_modRoot, "Data", "updates");
    Directory.CreateDirectory(updatesRoot);
    string archivePath = Path.Combine(updatesRoot, "download-" + Guid.NewGuid().ToString("N") + ".zip");
    try
    {
      string checksumText = await GetTextAsync(
        client,
        release.Checksum.BrowserDownloadUrl,
        64 * 1024,
        cancellationToken).ConfigureAwait(false);
      string checksum = RuntimePackageInstaller.ParseChecksum(checksumText);
      await DownloadFileAsync(
        client,
        release.Package.BrowserDownloadUrl,
        archivePath,
        release.Package.Size,
        cancellationToken).ConfigureAwait(false);
      string runtime = RuntimePackageInstaller.Install(
        archivePath,
        _modRoot,
        release.Version,
        checksum);
      return Candidate(release.Version, runtime);
    }
    finally
    {
      TryDeleteFile(archivePath);
    }
  }

  internal static UpdateReleaseSelection SelectRelease(string json, string currentVersion)
  {
    GitHubRelease release = JsonConvert.DeserializeObject<GitHubRelease>(json);
    if (release == null || string.IsNullOrWhiteSpace(release.TagName))
      throw new InvalidDataException("GitHub returned invalid TUFHelperLite release metadata.");
    if (release.Draft || release.Prerelease)
      return null;

    SemanticVersion current = SemanticVersion.Parse(currentVersion);
    SemanticVersion available = SemanticVersion.Parse(release.TagName);
    if (available.CompareTo(current) <= 0)
      return null;
    GitHubAsset package = FindAsset(release, PackageAssetName);
    GitHubAsset checksum = FindAsset(release, ChecksumAssetName);
    ValidateAsset(package, PackageAssetName, RuntimePackageInstaller.MaximumArchiveBytes);
    ValidateAsset(checksum, ChecksumAssetName, 64 * 1024);
    return new UpdateReleaseSelection(available.ToString(), package, checksum);
  }

  private static UpdateResult Candidate(string version, string runtime) =>
    new()
    {
      Outcome = UpdateOutcomes.Candidate,
      Version = version,
      RuntimePath = runtime,
      DependencyBootstrapPath = Path.Combine(runtime, "AdofaiIpc.Bootstrap.dll"),
    };

  private static HttpClient CreateClient()
  {
    HttpClient client = new() { Timeout = Timeout.InfiniteTimeSpan };
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TUFHelperLite", "1.0"));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    return client;
  }

  private static GitHubAsset FindAsset(GitHubRelease release, string name) =>
    release.Assets?.SingleOrDefault(asset => string.Equals(asset.Name, name, StringComparison.Ordinal));

  private static void ValidateAsset(GitHubAsset asset, string name, long maximumBytes)
  {
    if (asset == null)
      throw new InvalidDataException("GitHub release is missing " + name + ".");
    if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out Uri uri) ||
        uri.Scheme != Uri.UriSchemeHttps ||
        !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
        !uri.AbsolutePath.StartsWith(
          "/KGH1113/TUFHelperLite/releases/download/",
          StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("GitHub release asset URL is invalid.");
    if (asset.Size <= 0 || asset.Size > maximumBytes)
      throw new InvalidDataException("GitHub release asset size is invalid.");
  }

  private static async Task<string> GetTextAsync(
    HttpClient client,
    string url,
    int maximumBytes,
    CancellationToken cancellationToken)
  {
    using HttpRequestMessage request = new(HttpMethod.Get, url);
    using HttpResponseMessage response = await client.SendAsync(
      request,
      HttpCompletionOption.ResponseHeadersRead,
      cancellationToken).ConfigureAwait(false);
    response.EnsureSuccessStatusCode();
    if (response.Content.Headers.ContentLength is long length &&
        (length <= 0 || length > maximumBytes))
      throw new InvalidDataException("TUFHelperLite update metadata size is invalid.");

    using Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
    using MemoryStream output = new();
    byte[] buffer = new byte[16 * 1024];
    int read;
    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
    {
      if (output.Length + read > maximumBytes)
        throw new InvalidDataException("TUFHelperLite update metadata size is invalid.");
      output.Write(buffer, 0, read);
    }
    if (output.Length == 0)
      throw new InvalidDataException("TUFHelperLite update metadata size is invalid.");
    return System.Text.Encoding.UTF8.GetString(output.ToArray());
  }

  private static async Task DownloadFileAsync(
    HttpClient client,
    string url,
    string destination,
    long expectedBytes,
    CancellationToken cancellationToken)
  {
    UpdateDiskSpacePolicy.EnsureSufficientSpace(
      destination,
      expectedBytes,
      "Not enough free disk space to download the TUFHelperLite update.");
    using HttpRequestMessage request = new(HttpMethod.Get, url);
    using HttpResponseMessage response = await client.SendAsync(
      request,
      HttpCompletionOption.ResponseHeadersRead,
      cancellationToken).ConfigureAwait(false);
    response.EnsureSuccessStatusCode();
    if (response.Content.Headers.ContentLength is long length && length != expectedBytes)
      throw new InvalidDataException("TUFHelperLite update download size does not match the release.");

    using Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
    using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    byte[] buffer = new byte[BufferSize];
    long received = 0;
    int read;
    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
    {
      received = checked(received + read);
      if (received > expectedBytes || received > RuntimePackageInstaller.MaximumArchiveBytes)
        throw new InvalidDataException("TUFHelperLite update exceeds its declared size.");
      await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
    }
    if (received != expectedBytes)
      throw new InvalidDataException("TUFHelperLite update download is incomplete.");
  }

  private static void TryDeleteFile(string path)
  {
    try { if (File.Exists(path)) File.Delete(path); } catch { }
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
