using UnityModManagerNet;

namespace TUFHelperLite;

public static class ModStatus
{
  public const string DisplayName = "TUFHelperLite";
  public const string MissingAdofaiIpcPostfix = " <color=red>[Needs AdofaiIpc]</color>";
  public const string ErrorAdofaiIpcPostfix = " <color=red>[AdofaiIpc Error!]</color>";
  public const string InactiveAdofaiIpcPostfix = " <color=yellow>[AdofaiIpc Inactive]</color>";

  public static string Version => Main.Instance?.Version.ToString() ?? "0.0.0";

  public static IpcDependencyState GetAdofaiIpcState()
  {
    UnityModManager.ModEntry entry = UnityModManager.FindMod("AdofaiIpc");

    if (entry == null) return IpcDependencyState.Missing;
    if (entry.ErrorOnLoading) return IpcDependencyState.Error;
    if (!entry.Active) return IpcDependencyState.Inactive;

    return IpcDependencyState.Available;
  }

  public static bool IsAdofaiIpcAvailable()
  {
    return GetAdofaiIpcState() == IpcDependencyState.Available;
  }

  public static void SetNormal()
  {
    SetDisplayName(DisplayName);
  }

  public static void SetNeedsAdofaiIpc()
  {
    SetDisplayName(DisplayName + MissingAdofaiIpcPostfix);
  }

  public static void SetAdofaiIpcState(IpcDependencyState state)
  {
    switch (state)
    {
      case IpcDependencyState.Available:
        SetNormal();
        break;
      case IpcDependencyState.Error:
        SetDisplayName(DisplayName + ErrorAdofaiIpcPostfix);
        break;
      case IpcDependencyState.Inactive:
        SetDisplayName(DisplayName + InactiveAdofaiIpcPostfix);
        break;
      default:
        SetNeedsAdofaiIpc();
        break;
    }
  }

  private static void SetDisplayName(string displayName)
  {
    if (Main.Instance?.ModEntry?.Info == null) return;
    Main.Instance.ModEntry.Info.DisplayName = displayName;
  }

  public enum IpcDependencyState
  {
    Available,
    Missing,
    Inactive,
    Error
  }
}
