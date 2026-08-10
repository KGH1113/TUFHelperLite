using System;
using System.IO;
using System.Reflection;
using UnityModManagerNet;

namespace TUFHelperLite.Launcher;

internal static class PayloadLoader
{
  private static readonly object Sync = new();
  private static string _payloadDirectory;
  private static bool _resolverRegistered;

  public static void Load(
    string assemblyPath,
    string entryMethod,
    UnityModManager.ModEntry modEntry)
  {
    bool assemblyLoaded = false;
    try
    {
      if (!File.Exists(assemblyPath))
        throw new FileNotFoundException("TUFHelperLite payload was not found.", assemblyPath);

      int separator = entryMethod.LastIndexOf('.');
      if (separator <= 0 || separator == entryMethod.Length - 1)
        throw new InvalidDataException("EntryMethod must contain a type and method name.");

      ConfigureResolver(Path.GetDirectoryName(Path.GetFullPath(assemblyPath)));

      Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
      assemblyLoaded = true;
      string typeName = entryMethod.Substring(0, separator);
      string methodName = entryMethod.Substring(separator + 1);
      Type type = assembly.GetType(typeName, throwOnError: true);
      MethodInfo method = type.GetMethod(
        methodName,
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
        null,
        new[] { typeof(UnityModManager.ModEntry) },
        null);

      if (method == null)
        throw new MissingMethodException(typeName, methodName);

      try
      {
        object result = method.Invoke(null, new object[] { modEntry });
        if (method.ReturnType == typeof(bool) && result is bool loaded && !loaded)
          throw new InvalidOperationException(entryMethod + " returned false.");
      }
      catch (TargetInvocationException exception) when (exception.InnerException != null)
      {
        throw exception.InnerException;
      }
    }
    catch (PayloadLoadException)
    {
      throw;
    }
    catch (Exception exception)
    {
      throw new PayloadLoadException(assemblyLoaded, exception);
    }
  }

  private static void ConfigureResolver(string payloadDirectory)
  {
    lock (Sync)
    {
      _payloadDirectory = payloadDirectory;
      if (_resolverRegistered)
        return;

      AppDomain.CurrentDomain.AssemblyResolve += ResolvePayloadDependency;
      _resolverRegistered = true;
    }
  }

  private static Assembly ResolvePayloadDependency(object sender, ResolveEventArgs args)
  {
    string directory;
    lock (Sync)
      directory = _payloadDirectory;

    if (string.IsNullOrEmpty(directory))
      return null;

    string assemblyName = new AssemblyName(args.Name).Name;
    string path = Path.Combine(directory, assemblyName + ".dll");
    return File.Exists(path) ? Assembly.LoadFrom(path) : null;
  }
}

internal sealed class PayloadLoadException : Exception
{
  public PayloadLoadException(bool assemblyLoaded, Exception innerException)
    : base(innerException.Message, innerException) => AssemblyLoaded = assemblyLoaded;

  public bool AssemblyLoaded { get; }
}
