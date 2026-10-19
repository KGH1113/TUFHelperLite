using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AdofaiIpc;
using AdofaiIpc.Contracts;
using AdofaiIpc.Core;
using AdofaiIpc.Server;
using Newtonsoft.Json.Linq;
using TUFHelperLite.App;
using TUFHelperLite.Domain.Downloads;
using TUFHelperLite.Domain.Jobs;
using TUFHelperLite.Infrastructure.Downloads;
using TUFHelperLite.Infrastructure.Settings;
using TUFHelperLite.Presentation.Ipc;
using Wire = TUFHelperLite.Presentation.Ipc.Generated;

internal static partial class Program
{
  private static async Task RunTypedIpcTests()
  {
    Wire.OpenFromIdRequest missing = Wire.TufHelperMethods.OpenFromId.Input.Read("{\"id\":\"123\"}");
    CheckTrue("typed open default distinguishes omitted flag", !missing.OpenAfterDownload.IsSpecified);
    Wire.OpenFromIdRequest explicitFalse = Wire.TufHelperMethods.OpenFromId.Input.Read("{\"id\":\"123\",\"openAfterDownload\":false,\"source\":null}");
    CheckTrue("typed optional false remains specified", explicitFalse.OpenAfterDownload.IsSpecified && !explicitFalse.OpenAfterDownload.Value);
    CheckTrue("typed optional null remains specified", explicitFalse.Source.IsSpecified && explicitFalse.Source.Value == null);
    CheckTrue("typed omitted fields remain absent on write", !JObject.Parse(Wire.TufHelperMethods.OpenFromId.Input.Write(missing)).ContainsKey("openAfterDownload"));
    try
    {
      Wire.TufHelperMethods.OpenFromId.Input.Read("{\"id\":\"123\",\"openAfterDownload\":null}");
      CheckTrue("typed nonnullable optional rejects null", false);
    }
    catch (IpcValidationException) { CheckTrue("typed nonnullable optional rejects null", true); }
    var backwards = new Wire.LibraryPageRequest { Direction = Wire.PageDirection.Previous, Cursor = new IpcOptional<string>(null) };
    JObject pageRequest = JObject.Parse(Wire.TufHelperMethods.LibraryPage.Input.Write(backwards));
    CheckTrue("typed direction writes exact string enum", pageRequest.Value<string>("direction") == "previous");
    CheckTrue("typed nullable cursor writes explicit null", pageRequest["cursor"]?.Type == JTokenType.Null);

    foreach (string status in new[] { "queued", "running", "waiting_selection", "completed", "failed", "cancelled" })
    {
      var domain = new DownloadJobSnapshot { JobId = "typed-" + status, Status = status, LevelPaths = new[] { "a.adofai", "b.adofai" }, Progress = 0.125, BytesReceived = 4_294_967_296L };
      Wire.Job mapped = TypedSnapshots.Job(domain);
      JObject wire = JObject.Parse(Wire.TufHelperMethods.OpenFromId.Output.Write(mapped));
      CheckTrue("typed domain status " + status, wire.Value<string>("status") == status);
      CheckTrue("typed job arrays survive mapping " + status, wire["levelPaths"] is JArray paths && paths.Count == 2);
      CheckTrue("typed job null survives mapping " + status, wire["artist"]?.Type == JTokenType.Null);
      CheckTrue("typed job float and safe integer " + status, wire.Value<double>("progress") == 0.125 && wire.Value<long>("bytesReceived") == 4_294_967_296L);
    }
    var invalid = TypedSnapshots.Job(new DownloadJobSnapshot { JobId = "too-large", Status = "queued", BytesReceived = 9_007_199_254_740_992L });
    try { Wire.TufHelperMethods.OpenFromId.Output.Write(invalid); CheckTrue("typed job rejects unsafe byte count", false); }
    catch (IpcValidationException) { CheckTrue("typed job rejects unsafe byte count", true); }
    var page = TypedSnapshots.Page(new DownloadedLevelPage { Items = Array.Empty<DownloadedLevelItem>(), Revision = 1 });
    JObject empty = JObject.Parse(Wire.TufHelperMethods.LibraryPage.Output.Write(page));
    CheckTrue("typed empty page has array and nullable cursors", empty["items"] is JArray items && items.Count == 0 && empty["nextCursor"]?.Type == JTokenType.Null);

    using var invoker = new NotificationInvoker();
    using var notifications = new AppNotifications(new Wire.TufHelperApp(invoker));
    notifications.Enqueue("snapshot", () => notifications.App.SnapshotAsync(new Wire.SnapshotRequest(), invoker.CancellationToken));
    await invoker.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
    for (int index = 0; index < 1000; index++)
    {
      int progress = index;
      notifications.Enqueue("jobs", () => notifications.App.JobsChangedAsync(new Wire.JobsChangedRequest {
        Jobs = new[] { new Wire.Job { JobId = progress.ToString() } }
      }, invoker.CancellationToken));
    }
    invoker.Release.TrySetResult();
    await invoker.Progress.Task.WaitAsync(TimeSpan.FromSeconds(3));
    CheckTrue("notification snapshot precedes progress", invoker.Calls[0] == "snapshot");
    CheckTrue("notification burst retains only latest progress", invoker.Calls.Count == 2 && invoker.Calls[1] == "jobsChanged:999");

    using var cancelled = new NotificationInvoker();
    using var stopped = new AppNotifications(new Wire.TufHelperApp(cancelled));
    stopped.Enqueue("snapshot", () => stopped.App.SnapshotAsync(new Wire.SnapshotRequest(), cancelled.CancellationToken));
    await cancelled.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
    stopped.Enqueue("jobs", () => stopped.App.JobsChangedAsync(new Wire.JobsChangedRequest(), cancelled.CancellationToken));
    stopped.Dispose();
    cancelled.Cancel();
    await cancelled.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
    CheckTrue("disconnected notification queue discards queued topics", cancelled.Calls.Count == 1);
  }

  private sealed class NotificationInvoker : IpcAppInvoker, IDisposable
  {
    private readonly CancellationTokenSource _lifetime = new();
    internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Progress = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly List<string> Calls = new();
    public override string AppId => "test-app";
    public override string DisplayName => "test";
    public override string SessionId => "test-session";
    public override CancellationToken CancellationToken => _lifetime.Token;
    public override async Task<TResult> InvokeAsync<TRequest, TResult>(IpcMethod<TRequest, TResult> method, TRequest request, CancellationToken cancellationToken = default)
    {
      if (method.Name == "snapshot")
      {
        Calls.Add("snapshot"); Started.TrySetResult();
        try { await Release.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
      }
      else if (request is Wire.JobsChangedRequest jobs)
      {
        Calls.Add("jobsChanged:" + jobs.Jobs[0].JobId); Progress.TrySetResult();
      }
      return (TResult)(object)IpcUnit.Value;
    }
    internal void Cancel() => _lifetime.Cancel();
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
  }

  private static int ServeIpc()
  {
    string root = Path.Combine(Path.GetTempPath(), "tufhelper-ipc-fixture-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    IpcServer server = null;
    try
    {
      DownloadStorageSettingsStore.Initialize(root);
      DownloadStorageMigrationService.Initialize(root);
      DownloadStorageMigrationService.SetLevelInUseProbeForTests(_ => false);
      DownloadLibraryService.Initialize(root);
      LevelUpdateCheckBatchService.Initialize(root);
      var picker = new ControlledPicker();
      DownloadFolderPickerCoordinator.MainThread = new ImmediateDispatcher();
      DownloadFolderPickerCoordinator.FolderPicker = picker;
      var registry = new IpcRegistry(action => action());
      server = new IpcServer(registry);
      Type type = typeof(IpcRegistry).Assembly.GetType("AdofaiIpc.Runtime.RoutedRuntime", throwOnError: true);
      var runtime = (IIpcRuntime)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
        new object[] { registry, new RuntimeInfo("2.0.0", 1, 3, "commands", "game-thread", "snapshots", "downloads"),
          (Action<Action>)(action => action()), (Func<bool>)(() => false) }, null);
      IpcRegistration.Register(runtime, configureNativePicker: false);
      IpcRegistration.MarkReady();
      server.Start();
      Console.WriteLine("IPC_TUFHELPER_SERVER_PORT=" + server.Port);
      Console.Out.Flush();
      string command;
      int selections = 0;
      DownloadJob progressJob = null;
      while ((command = Console.ReadLine()) != null && command != "stop")
      {
        switch (command)
        {
          case "folder.complete":
            string directory = Path.Combine(root, "selected-" + ++selections);
            Directory.CreateDirectory(directory); picker.Complete(directory); break;
          case "folder.immediate":
            string immediate = Path.Combine(root, "selected-" + ++selections);
            Directory.CreateDirectory(immediate); picker.CompleteNext(immediate); break;
          case "folder.cancel": picker.Complete(null); break;
          case "progress":
            if (progressJob == null)
            {
              progressJob = new DownloadJob("fixture.progress", "1", null, "fixture-progress", false);
              var jobGate = typeof(LevelJobService).GetField("Lock", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
              var jobs = (Dictionary<string, DownloadJob>)typeof(LevelJobService).GetField("Jobs", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
              lock (jobGate) jobs.Add(progressJob.JobId, progressJob);
              progressJob.BeginRunning();
            }
            progressJob.Report("downloading", "Fixture download progress", .5, 512, 1024);
            Console.WriteLine("IPC_TUFHELPER_PROGRESS_JOB_ID=" + progressJob.JobId);
            break;
          case "stats":
            Console.WriteLine("IPC_TUFHELPER_STATS=" + new JObject { ["folderStarts"] = picker.Starts, ["progressJobId"] = progressJob?.JobId }.ToString(Newtonsoft.Json.Formatting.None));
            break;
          case "jobs.changed": TUFHelperLite.Domain.Ports.ActivityChanges.Notify(TUFHelperLite.Domain.Ports.ActivityTopic.Jobs); break;
          case "library.changed": TUFHelperLite.Domain.Ports.ActivityChanges.Notify(TUFHelperLite.Domain.Ports.ActivityTopic.Library); break;
          case "all.changed": TUFHelperLite.Domain.Ports.ActivityChanges.Notify(TUFHelperLite.Domain.Ports.ActivityTopic.Jobs | TUFHelperLite.Domain.Ports.ActivityTopic.Library | TUFHelperLite.Domain.Ports.ActivityTopic.Storage | TUFHelperLite.Domain.Ports.ActivityTopic.Batch); break;
          default: throw new InvalidOperationException("Unknown fixture command: " + command);
        }
        Console.WriteLine("IPC_TUFHELPER_CONTROL_DONE=" + command); Console.Out.Flush();
      }
      return 0;
    }
    finally
    {
      IpcRegistration.Unregister(); server?.Stop(); DownloadFolderPickerCoordinator.Shutdown();
      DownloadStorageMigrationService.WaitForWorkerForTests();
      DownloadFolderPickerCoordinator.MainThread = null; DownloadFolderPickerCoordinator.FolderPicker = null;
      if (Directory.Exists(root)) Directory.Delete(root, true);
    }
  }

  private sealed class ImmediateDispatcher : TUFHelperLite.App.Ports.IMainThreadDispatcher
  { public void Dispatch(Action action) => action(); }

  private sealed class ControlledPicker : TUFHelperLite.App.Ports.IFolderPicker
  {
    private readonly object _gate = new();
    private Action<string> _selected;
    private string _nextDirectory;
    private int _starts;
    internal int Starts => Volatile.Read(ref _starts);
    public void Pick(string initialDirectory, bool allowExisting, Action<string> selected, Action<Exception> failed)
    {
      Interlocked.Increment(ref _starts);
      string immediate;
      lock (_gate)
      {
        immediate = _nextDirectory; _nextDirectory = null;
        if (immediate == null) _selected = selected;
      }
      if (immediate != null) selected(immediate);
    }
    internal void CompleteNext(string directory) { lock (_gate) _nextDirectory = directory; }
    internal void Complete(string directory)
    {
      Action<string> selected;
      lock (_gate) { selected = _selected; _selected = null; }
      if (selected == null) throw new InvalidOperationException("No picker is pending.");
      selected(directory);
    }
  }
}
