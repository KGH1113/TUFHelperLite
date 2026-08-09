namespace AdofaiIpc.DependencyShim;

public static class DependencyShim
{
  public static int StageCount { get; private set; }
  public static int DiscardCount { get; private set; }

  public static string StageCandidate(string modRoot, string sourceAssemblyPath)
  {
    StageCount++;
    return "0.3.0";
  }

  public static void DiscardTrial(string modRoot, string version) => DiscardCount++;

  public static void Reset()
  {
    StageCount = 0;
    DiscardCount = 0;
  }
}
