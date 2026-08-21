namespace TUFHelperLite.Domain.Levels;

public sealed class LevelUpdateCheckBatchSnapshot
{
  public string State { get; set; } = "idle";
  public string OperationId { get; set; }
  public string CurrentLevelId { get; set; }
  public string CurrentStage { get; set; }
  public double CurrentProgress { get; set; }
  public int LevelsProcessed { get; set; }
  public int LevelsTotal { get; set; }
  public int UpdatesAvailable { get; set; }
  public int LevelsUpToDate { get; set; }
  public int LevelsFailed { get; set; }
  public string ErrorCode { get; set; }
  public string Message { get; set; }
}
