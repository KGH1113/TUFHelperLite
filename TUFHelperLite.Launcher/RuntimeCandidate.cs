using System.IO;

namespace TUFHelperLite.Launcher;

internal sealed class RuntimeCandidate
{
  public RuntimeCandidate(string version, string runtimePath)
  {
    Version = version;
    RuntimePath = runtimePath;
  }

  public string Version { get; }
  public string RuntimePath { get; }
  public string AssemblyPath => Path.Combine(RuntimePath, "TUFHelperLite.Core.dll");
  public string UpdateEnginePath => Path.Combine(RuntimePath, "TUFHelperLite.UpdateEngine.dll");
}
