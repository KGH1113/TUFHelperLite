using System;
using System.IO;
using System.Reflection;
using UnityModManagerNet;

namespace TUFHelperLite;

internal static class AdofaiIpcMigrationBridge
{
  public static bool PrepareAndNotify(UnityModManager.ModEntry owner)
  {
    string path = Path.Combine(owner.Path, "Assets", "AdofaiIpc", "AdofaiIpc.Migration.dll");
    if (!File.Exists(path)) return false;
    Assembly assembly = Assembly.LoadFrom(path);
    Type type = assembly.GetType("AdofaiIpc.Migration.TransitionMigration", true);
    MethodInfo method = type.GetMethod("PrepareAndNotify", BindingFlags.Public | BindingFlags.Static,
      null, new[] { typeof(UnityModManager.ModEntry) }, null) ??
      throw new MissingMethodException(type.FullName, "PrepareAndNotify");
    try { return method.Invoke(null, new object[] { owner }) is bool required && required; }
    catch (TargetInvocationException exception) when (exception.InnerException != null)
    { throw exception.InnerException; }
  }
}
