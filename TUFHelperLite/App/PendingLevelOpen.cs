using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TUFHelperLite.App;

// A queued game-thread open still owns its files before Unity publishes
// ADOBase.levelPath. Keep every queued path reserved through that interval.
internal static class PendingLevelOpen
{
  private static readonly object Gate = new();
  private static readonly HashSet<string> Paths = new(StringComparer.OrdinalIgnoreCase);

  internal static void Reserve(string path)
  {
    lock (Gate)
    {
      if (path == null) Paths.Clear();
      else Paths.Add(Path.GetFullPath(path));
    }
  }

  internal static void Release(string path)
  {
    if (string.IsNullOrWhiteSpace(path)) return;
    lock (Gate) Paths.Remove(Path.GetFullPath(path));
  }

  internal static bool IsInside(string directory)
  {
    string prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    lock (Gate) return Paths.Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
  }
}
