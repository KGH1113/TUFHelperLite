using System;
using System.Linq;
using System.Reflection;

namespace TUFHelperLite.Launcher;

internal static class DependencyBootstrapShim
{
  private const string TypeName = "AdofaiIpc.DependencyShim.DependencyShim";

  public static string Stage(string modRoot, string candidatePath) =>
    (string)Invoke("StageCandidate", new[] { typeof(string), typeof(string) },
      new object[] { modRoot, candidatePath });

  public static void Discard(string modRoot, string version) =>
    Invoke("DiscardTrial", new[] { typeof(string), typeof(string) }, new object[] { modRoot, version });

  private static object Invoke(string name, Type[] parameterTypes, object[] arguments)
  {
    Type type = AppDomain.CurrentDomain.GetAssemblies()
      .Select(assembly => assembly.GetType(TypeName, false))
      .FirstOrDefault(candidate => candidate != null)
      ?? throw new InvalidOperationException("AdofaiIpc dependency shim is not loaded.");
    MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null,
      parameterTypes, null) ?? throw new MissingMethodException(TypeName, name);
    try { return method.Invoke(null, arguments); }
    catch (TargetInvocationException exception) when (exception.InnerException != null)
    { throw exception.InnerException; }
  }
}
