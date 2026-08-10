using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityModManagerNet;

namespace TUFHelperLite.Launcher;

internal static class UpdateEngineLoader
{
  private const string EntryType = "TUFHelperLite.UpdateEngine.EntryPoint";
  private const string EntryMethod = "Resolve";

  public static UpdateResolution Resolve(UnityModManager.ModEntry modEntry, RuntimeCandidate current)
  {
    if (!File.Exists(current.UpdateEnginePath))
      throw new FileNotFoundException("The versioned update engine is missing.", current.UpdateEnginePath);
    Assembly assembly = Assembly.LoadFrom(current.UpdateEnginePath);
    Type type = assembly.GetType(EntryType, true);
    MethodInfo method = type.GetMethod(
      EntryMethod,
      BindingFlags.Public | BindingFlags.Static,
      null,
      new[] { typeof(UnityModManager.ModEntry), typeof(string) },
      null) ?? throw new MissingMethodException(EntryType, EntryMethod);
    string request = JsonConvert.SerializeObject(new
    {
      InstallPath = modEntry.Path,
      CurrentVersion = current.Version,
    });
    object raw;
    try
    {
      raw = method.Invoke(null, new object[] { modEntry, request });
    }
    catch (TargetInvocationException exception) when (exception.InnerException != null)
    {
      throw exception.InnerException;
    }
    return UpdateResolution.Parse(raw as string);
  }
}

internal sealed class UpdateResolution
{
  public bool HasCandidate { get; private set; }
  public string Version { get; private set; }
  public string RuntimePath { get; private set; }
  public string DependencyBootstrapPath { get; private set; }

  public static UpdateResolution None() => new();

  public static UpdateResolution Parse(string json)
  {
    JObject root = JObject.Parse(json ?? throw new InvalidDataException("The update engine returned no result."));
    string outcome = root.Value<string>("Outcome") ?? root.Value<string>("outcome");
    if (outcome == "none" || outcome == "error")
      return None();
    if (outcome != "candidate")
      throw new InvalidDataException("The update engine returned an invalid outcome.");
    return new UpdateResolution
    {
      HasCandidate = true,
      Version = root.Value<string>("Version") ?? root.Value<string>("version"),
      RuntimePath = root.Value<string>("RuntimePath") ?? root.Value<string>("runtimePath"),
      DependencyBootstrapPath = root.Value<string>("DependencyBootstrapPath") ??
        root.Value<string>("dependencyBootstrapPath"),
    };
  }
}
