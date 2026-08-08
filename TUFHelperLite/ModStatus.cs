namespace TUFHelperLite;

public static class ModStatus
{
  public const string DisplayName = "TUFHelperLite";
  public static string Version => Main.Instance?.Version ?? "0.0.0";

  public static void SetNormal()
  {
    if (Main.Instance?.ModEntry?.Info != null)
      Main.Instance.ModEntry.Info.DisplayName = DisplayName;
  }
}
