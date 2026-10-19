using System;
using System.Collections.Generic;
using System.IO;
using TUFHelperLite.Domain.Errors;

namespace TUFHelperLite.Infrastructure.Downloads;

internal static class ManagedDownloadFiles
{
  // Validate the complete tree before destructive work. Never follow directory links
  // into user files outside a managed download directory.
  internal static void EnsureSafeTree(string root, string directory)
  {
    string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
    string canonical = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
    if (!canonical.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
      throw new LevelUpdateException("download_path_invalid", "The download directory is outside managed storage.");
    RejectLink(canonicalRoot);
    string current = canonical;
    while (current.Length > canonicalRoot.Length)
    {
      RejectLink(current);
      current = Path.GetDirectoryName(current);
    }
    if (!Directory.Exists(canonical)) return;
    var pending = new Stack<string>();
    pending.Push(canonical);
    while (pending.Count > 0)
    {
      foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
      {
        FileAttributes attributes = File.GetAttributes(entry);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
          throw new LevelUpdateException("download_path_invalid", "Linked files and directories cannot be removed by TUFHelperLite.");
        if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
      }
    }
  }

  private static void RejectLink(string path)
  {
    if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
      throw new LevelUpdateException("download_path_invalid", "The managed download directory cannot be a filesystem link.");
  }
}
