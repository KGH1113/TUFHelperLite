using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TUFHelperLite.Presentation.Ipc.Generated;

namespace TUFHelperLite.Presentation.Ipc;

// One in-flight call per app; repeated progress updates retain the latest value for each topic.
internal sealed class AppNotifications : IDisposable
{
  private readonly object _gate = new();
  private readonly Dictionary<string, Func<Task>> _latest = new();
  private bool _running, _disposed;
  internal TufHelperApp App { get; }
  internal AppNotifications(TufHelperApp app) => App = app;
  internal void Enqueue(string topic, Func<Task> send)
  {
    lock (_gate)
    {
      if (_disposed) return;
      _latest[topic] = send;
      if (_running) return;
      _running = true;
    }
    _ = Task.Run(Drain);
  }
  private async Task Drain()
  {
    while (true)
    {
      Func<Task> send;
      lock (_gate)
      {
        if (_disposed || _latest.Count == 0) { _running = false; return; }
        using var entries = _latest.GetEnumerator(); entries.MoveNext();
        var next = entries.Current; send = next.Value; _latest.Remove(next.Key);
      }
      try { await send().ConfigureAwait(false); }
      catch (OperationCanceledException) { Dispose(); return; }
      catch (Exception error)
      {
        if (App.CancellationToken.IsCancellationRequested) { Dispose(); return; }
        Main.Instance?.LogException(error);
      }
    }
  }
  public void Dispose() { lock (_gate) { _disposed = true; _latest.Clear(); } }
}
