using System.IO;

namespace TUFHelperLite.Infrastructure.Downloads;

public static class DirectoryFlattener
{
  public static void MoveLastDirectoryFilesToRoot(string startPath, string rootPath)
  {
    string[] directories = Directory.GetDirectories(startPath);

    if (directories.Length > 0)
    {
      foreach (string directory in directories)
      {
        MoveLastDirectoryFilesToRoot(directory, rootPath);
      }

      return;
    }

    if (startPath == rootPath) return;

    foreach (string file in Directory.GetFiles(startPath))
    {
      string destination = Path.Combine(rootPath, Path.GetFileName(file));
      if (File.Exists(destination)) continue;

      File.Move(file, destination);
    }
  }
}
