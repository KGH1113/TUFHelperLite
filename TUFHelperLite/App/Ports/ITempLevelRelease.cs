using System.Threading;
using System.Threading.Tasks;

namespace TUFHelperLite.App.Ports;

public interface ITempLevelRelease
{
  // Completes only once the game has stopped using the previous temporary level.
  Task ReleaseAsync(string directory, CancellationToken cancellationToken);
}
