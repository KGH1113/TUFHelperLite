using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityModManagerNet;

namespace TUFHelperLite.Launcher;

public static class DependencyEntryPoint
{
  private const string ShimTypeName = "AdofaiIpc.DependencyShim.DependencyShim";
  private static readonly Regex CanonicalVersion = new(
    @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?$",
    RegexOptions.CultureInvariant);

  public static bool Load(UnityModManager.ModEntry owner)
  {
    try
    {
      string controls = Path.GetDirectoryName(typeof(DependencyEntryPoint).Assembly.Location);
      if (RepairMissingCandidate(owner.Path, controls))
        owner.Logger.Warning("[DependencyBootstrap] Restored the missing bootstrap candidate from packaged assets.");

      Type shim = LoadShim(controls);
      MethodInfo load = shim.GetMethod("Load", BindingFlags.Public | BindingFlags.Static, null,
        new[] { typeof(UnityModManager.ModEntry) }, null)
        ?? throw new MissingMethodException(ShimTypeName, "Load");
      try { return load.Invoke(null, new object[] { owner }) is bool result && result; }
      catch (TargetInvocationException exception) when (exception.InnerException != null)
      { throw exception.InnerException; }
    }
    catch (Exception exception)
    {
      owner.Logger.Error("Dependency bootstrap entrypoint failed: " + exception);
      return false;
    }
  }

  internal static bool RepairMissingCandidate(string modRoot, string controls)
  {
    string statePath = Path.Combine(modRoot, "DependencyBootstrap", "state.json");
    if (!File.Exists(statePath)) return false;

    string current = JObject.Parse(File.ReadAllText(statePath)).Value<string>("Current");
    if (string.IsNullOrWhiteSpace(current) || !CanonicalVersion.IsMatch(current)) return false;

    string candidate = Path.Combine(modRoot, "DependencyBootstrap", "versions", current,
      "AdofaiIpc.Bootstrap.dll");
    if (File.Exists(candidate)) return false;

    string source = Path.Combine(controls, "AdofaiIpc.Bootstrap.dll");
    if (!File.Exists(source) || AssemblyName.GetAssemblyName(source).Name != "AdofaiIpc.Bootstrap" ||
        FileVersionInfo.GetVersionInfo(source).ProductVersion != current)
      return false;

    Directory.CreateDirectory(Path.GetDirectoryName(candidate));
    string temporary = candidate + ".tmp-" + Guid.NewGuid().ToString("N");
    File.Copy(source, temporary, false);
    try
    {
      if (AssemblyName.GetAssemblyName(temporary).Name != "AdofaiIpc.Bootstrap" ||
          FileVersionInfo.GetVersionInfo(temporary).ProductVersion != current)
        throw new InvalidDataException("Packaged dependency bootstrap failed verification.");
      if (File.Exists(candidate)) return false;
      File.Move(temporary, candidate);
      temporary = null;
      return true;
    }
    finally
    {
      if (temporary != null && File.Exists(temporary)) File.Delete(temporary);
    }
  }

  private static Type LoadShim(string controls)
  {
    Type loaded = AppDomain.CurrentDomain.GetAssemblies()
      .Select(assembly => assembly.GetType(ShimTypeName, false))
      .FirstOrDefault(type => type != null);
    if (loaded != null) return loaded;

    string path = Path.Combine(controls, "AdofaiIpc.DependencyShim.dll");
    if (!File.Exists(path)) throw new FileNotFoundException("Dependency shim is missing.", path);
    return Assembly.LoadFrom(path).GetType(ShimTypeName, true);
  }
}
