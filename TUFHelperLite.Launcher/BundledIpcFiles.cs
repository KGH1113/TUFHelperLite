using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace TUFHelperLite.BundledIpc;

// Package verification precedes this code. Assembly metadata is inspected without loading DLLs.
internal static class BundledIpcFiles
{
  internal static readonly string[] Files =
  {
    "AdofaiIpc.Contracts.dll", "AdofaiIpc.Loader.dll",
    "ipc/AdofaiIpc.Runtime.dll", "ipc/manifest.json"
  };
  public static void Validate(string root)
  {
    root = ResolveSource(root);
    foreach (string name in Files)
      if (!File.Exists(Path.Combine(root, name)))
        throw new InvalidDataException("The mod package has no complete IPC v2 bundle.");
    JObject manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "ipc", "manifest.json")));
    if ((int?)manifest["SchemaVersion"] != 1 || (int?)manifest["ContractMajor"] != 1 ||
        (string)manifest["ContractAssemblyVersion"] != "1.0.0.0" || (int?)manifest["WireMajor"] != 3)
      throw new InvalidDataException("The bundled IPC contract or wire protocol is incompatible.");
    string version = (string)manifest["RuntimeVersion"];
    if (version == null || version.Contains("-") || !Version.TryParse(version, out Version runtimeVersion) ||
        runtimeVersion < new Version(2, 0, 0))
      throw new InvalidDataException("A stable IPC v2 runtime is required.");
    ValidateIdentity(root, Files[0], "AdofaiIpc.Contracts", new Version(1, 0, 0, 0));
    ValidateIdentity(root, Files[1], "AdofaiIpc.Loader", new Version(1, 0, 0, 0));
    ValidateIdentity(root, Files[2], "AdofaiIpc.Runtime", new Version(version + ".0"));
  }
  private static void ValidateIdentity(string root, string file, string name, Version version)
  {
    AssemblyName identity = AssemblyName.GetAssemblyName(Path.Combine(root, file));
    if (identity.Name != name || identity.Version != version)
      throw new InvalidDataException("The IPC bundle assembly identity is invalid: " + file);
  }
  public static void Copy(string source, string destination)
  {
    Validate(source);
    source = ResolveSource(source);
    foreach (string name in Files)
    {
      string target = Path.Combine(destination, name);
      if (Path.GetFullPath(Path.Combine(source, name)) == Path.GetFullPath(target)) continue;
      if (FileContentsEqual(Path.Combine(source, name), target)) continue;
      Directory.CreateDirectory(Path.GetDirectoryName(target));
      string temporary = target + ".ipc-" + Guid.NewGuid().ToString("N");
      try
      {
        File.Copy(Path.Combine(source, name), temporary);
        if (File.Exists(target)) File.Replace(temporary, target, null);
        else File.Move(temporary, target);
      }
      finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
  }

  internal static bool FileContentsEqual(string source, string target)
  {
    if (!File.Exists(target) || new FileInfo(source).Length != new FileInfo(target).Length) return false;
    using SHA256 sha = SHA256.Create();
    using FileStream first = File.OpenRead(source);
    using FileStream second = File.OpenRead(target);
    return sha.ComputeHash(first).SequenceEqual(sha.ComputeHash(second));
  }

  private static string ResolveSource(string root) =>
    File.Exists(Path.Combine(root, "Assets", "IpcMigration", "ipc", "manifest.json"))
      ? Path.Combine(root, "Assets", "IpcMigration") : root;
}
