using System;
using System.Net;
using System.Text;
using Newtonsoft.Json;

namespace TUFHelperLite.Infrastructure.Tuforums;

public static class TuforumsClient
{
  private const string LevelByIdUrl = "https://api.tuforums.com/v2/database/levels/byId/{0}";
  public static TufLevelInfo GetLevelById(string id)
  {
    if (string.IsNullOrWhiteSpace(id))
    {
      throw new ArgumentException("Level id is required.", nameof(id));
    }

    using WebClient client = CreateClient();
    string json = client.DownloadString(
      string.Format(LevelByIdUrl, Uri.EscapeDataString(id.TrimStart('#'))));
    TufLevelInfo level = JsonConvert.DeserializeObject<TufLevelInfo>(json);

    if (level == null)
    {
      throw new InvalidOperationException("TUF API returned an empty level response.");
    }

    if (string.IsNullOrWhiteSpace(level.DownloadLink))
    {
      throw new InvalidOperationException($"TUF level #{level.Id} does not have a download link.");
    }

    return level;
  }

  private static WebClient CreateClient()
  {
    WebClient client = new()
    {
      Encoding = Encoding.UTF8,
      Proxy = null
    };

    client.Headers[HttpRequestHeader.UserAgent] = $"TUFHelperLite/{ModStatus.Version}";
    return client;
  }
}
