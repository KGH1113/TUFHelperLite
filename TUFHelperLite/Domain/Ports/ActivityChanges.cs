using System;
using System.Threading;

namespace TUFHelperLite.Domain.Ports;

[Flags]
public enum ActivityTopic { None = 0, Jobs = 1, Library = 2, Storage = 4, Batch = 8, Folder = 16 }

/// <summary>Non-blocking application change port. Implementations must never read application state inline.</summary>
public interface IActivityChangeSink { void Changed(ActivityTopic topic); }

public static class ActivityChanges
{
  private sealed class SilentSink : IActivityChangeSink { public void Changed(ActivityTopic topic) { } }
  private static IActivityChangeSink _sink = new SilentSink();
  public static IActivityChangeSink Sink { get => Volatile.Read(ref _sink); set => Volatile.Write(ref _sink, value ?? new SilentSink()); }
  public static void Notify(ActivityTopic topic) => Sink.Changed(topic);
}

/// <summary>Coalesces mutations without invoking listeners while application locks are held.</summary>
public sealed class ActivityChangeBuffer : IActivityChangeSink
{
  private int _pending;
  public void Changed(ActivityTopic topic)
  {
    int previous;
    do { previous = Volatile.Read(ref _pending); }
    while (Interlocked.CompareExchange(ref _pending, previous | (int)topic, previous) != previous);
  }
  public ActivityTopic Drain() => (ActivityTopic)Interlocked.Exchange(ref _pending, 0);
}
