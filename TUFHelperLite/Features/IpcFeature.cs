using TUFHelperLite.Presentation.Ipc;

namespace TUFHelperLite.Features;

public sealed class IpcFeature
{
  private bool _enabled;

  public void Enable()
  {
    if (_enabled) return;

    IpcRegistration.Register();
    _enabled = true;
  }

  public void Disable()
  {
    if (!_enabled) return;

    IpcRegistration.Unregister();
    _enabled = false;
  }

  public void MarkReady()
  {
    if (!_enabled) throw new System.InvalidOperationException("TUFHelperLite IPC is not enabled.");
    IpcRegistration.MarkReady();
  }

  public void MarkError(System.Exception exception)
  {
    IpcRegistration.MarkError(exception);
  }
}
