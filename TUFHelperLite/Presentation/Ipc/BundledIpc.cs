using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AdofaiIpc.Contracts;
using AdofaiIpc.Loader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TUFHelperLite.Presentation.Ipc;

// JSON stays in this mod; the shared ABI carries only JSON strings and contract interfaces.
public static class IpcRuntime
{
  public static IIpcRuntime Current { get; private set; }
  public static void Connect(string modRoot)
  {
    string mods = Directory.GetParent(Path.GetFullPath(modRoot.TrimEnd(
      Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))).FullName;
    Current = IpcLoader.GetRuntime(mods, new RuntimeRequirements(
      minimumRuntimeVersion: "2.0.0",
      requiredCapabilities: new[] { "commands", "game-thread", "snapshots", "downloads" }));
  }
}

public sealed class JsonCommand
{
  private readonly IIncomingMessage _message;
  private JToken _payload;
  private bool _decoded;
  public JsonCommand(IIncomingMessage message) => _message = message;
  public string Id => _message.Id;
  public string Name => _message.Name;
  public string PeerId => _message.Client.PeerId;
  public CancellationToken CancellationToken => _message.CancellationToken;
  public JToken Payload
  {
    get
    {
      if (!_decoded) { _payload = JsonConvert.DeserializeObject<JToken>(_message.Json); _decoded = true; }
      return _payload;
    }
  }
  public void Reply(string name, object payload) => _message.Reply(name, JsonConvert.SerializeObject(payload));
  public void Reject(string code, string message) => _message.Reject(code, message);
  internal void Download(string name, DownloadSource source, object metadata)
  {
    string json = metadata == null ? null : JsonConvert.SerializeObject(metadata);
    if (source.Stream != null)
      _message.ReplyDownload(name, source.Stream, source.ByteLength, source.FileName, source.ContentType, json);
    else
      _message.ReplyDownload(name, source.Writer, source.ByteLength, source.FileName, source.ContentType, json);
  }
}

public sealed class DownloadSource : IDisposable
{
  internal Stream Stream { get; }
  internal Action<Stream> Writer { get; }
  public long ByteLength { get; }
  public string FileName { get; }
  public string ContentType { get; }
  public DownloadSource(Stream stream, long length, string name, string type)
  { Stream = stream; ByteLength = length; FileName = name; ContentType = type; }
  public DownloadSource(Action<Stream> writer, long length, string name, string type)
  { Writer = writer; ByteLength = length; FileName = name; ContentType = type; }
  public void WriteTo(Stream destination)
  {
    if (Stream != null) Stream.CopyTo(destination);
    else Writer(destination);
  }
  public void Dispose() => Stream?.Dispose();
}

public sealed class DownloadFailure
{
  public string Code { get; }
  public string Message { get; }
  public DownloadFailure(string code, string message, int statusCode = 400)
  { Code = code; Message = message; }
}

public sealed class JsonFeature : IDisposable
{
  private IFeatureBuilder _builder;
  private IFeatureRegistration _registration;
  public event Action<IClientConnection> PeerSubscribed;
  public event Action<IClientConnection> PeerDisconnected;
  private JsonFeature() { }
  public static JsonFeature Register(FeatureDescription description, Action<JsonFeature> configure)
  {
    var feature = new JsonFeature();
    feature._registration = IpcRuntime.Current.RegisterFeature(description, builder =>
    {
      feature._builder = builder;
      builder.OnClientAttached(peer => feature.PeerSubscribed?.Invoke(peer));
      builder.OnClientDetached(peer => feature.PeerDisconnected?.Invoke(peer));
      configure(feature);
    });
    feature._builder = null;
    return feature;
  }
  public void RegisterCommand(string name, Action<JsonCommand> handler) =>
    Register(name, IpcExecutionContext.Background, command => { handler(command); return Task.CompletedTask; });
  public void RegisterMainThreadCommand(string name, Action<JsonCommand> handler) =>
    Register(name, IpcExecutionContext.GameThread, command => { handler(command); return Task.CompletedTask; });
  public void RegisterAsyncCommand(string name, Func<JsonCommand, Task> handler) =>
    Register(name, IpcExecutionContext.Background, handler);
  private void Register(string name, IpcExecutionContext context, Func<JsonCommand, Task> handler)
  {
    if (_builder == null) throw new InvalidOperationException("Configure commands during feature registration.");
    _builder.OnCommand(name, context, message => handler(new JsonCommand(message)));
  }
  public void RegisterDownloadCommand(string name, Func<JsonCommand, object> handler) =>
    RegisterCommand(name, command =>
    {
      object result = handler(command);
      if (result is DownloadFailure error) command.Reject(error.Code, error.Message);
      else if (result is DownloadSource source) command.Download("download.ready", source, null);
      else command.Reject("download_invalid_result", "The mod could not prepare the file.");
    });
  public void ReplyDownload(JsonCommand command, string name, DownloadSource source, object metadata = null) =>
    command.Download(name, source, metadata);
  public void MarkReady() => _registration.MarkReady();
  public void MarkError(string code, string message) => _registration.MarkError(code, message);
  public void Publish(string name, object payload) => _registration.Publish(name, JsonConvert.SerializeObject(payload));
  public void SendToPeer(string peer, string name, object payload, string correlationId = null) =>
    _registration.SendToPeer(peer, name, JsonConvert.SerializeObject(payload), correlationId);
  public void Dispose() { _registration?.Dispose(); _registration = null; }
}
