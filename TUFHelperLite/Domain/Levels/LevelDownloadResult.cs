using System.Collections.Generic;

namespace TUFHelperLite.Domain.Levels;

public sealed class LevelDownloadResult
{
  public string SourceUrl { get; set; }
  public string DirectUrl { get; set; }
  public string Directory { get; set; }
  public string SelectedLevelPath { get; set; }
  public List<string> LevelPaths { get; set; } = new();
  public bool FromCache { get; set; }
}
