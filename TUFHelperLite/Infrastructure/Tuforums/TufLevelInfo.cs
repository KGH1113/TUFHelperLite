using Newtonsoft.Json;

namespace TUFHelperLite.Infrastructure.Tuforums;

public sealed class TufLevelInfo
{
  [JsonProperty("id")]
  public int Id { get; set; }

  [JsonProperty("song")]
  public string Song { get; set; }

  [JsonProperty("artist")]
  public string Artist { get; set; }

  [JsonProperty("diffId")]
  public int DiffId { get; set; }

  [JsonProperty("creator")]
  public string Creator { get; set; }

  [JsonProperty("charter")]
  public string Charter { get; set; }

  [JsonProperty("team")]
  public string Team { get; set; }

  [JsonProperty("dlLink")]
  public string DownloadLink { get; set; }
}
