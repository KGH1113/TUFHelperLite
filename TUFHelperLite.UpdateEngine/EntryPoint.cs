using System;
using Newtonsoft.Json;
using UnityModManagerNet;

namespace TUFHelperLite.UpdateEngine;

public static class EntryPoint
{
  public static string Resolve(UnityModManager.ModEntry modEntry, string requestJson)
  {
    try
    {
      UpdateRequest request = JsonConvert.DeserializeObject<UpdateRequest>(requestJson)
        ?? throw new InvalidOperationException("The update request is empty.");
      UpdateResult result = new UpdateManager(request.InstallPath).Resolve(request.CurrentVersion);
      return JsonConvert.SerializeObject(result);
    }
    catch (Exception exception)
    {
      modEntry.Logger.Warning("[AutoUpdate] " + exception);
      return JsonConvert.SerializeObject(new UpdateResult
      {
        Outcome = UpdateOutcomes.Error,
        Message = exception.Message,
      });
    }
  }
}

internal static class UpdateOutcomes
{
  public const string None = "none";
  public const string Candidate = "candidate";
  public const string Error = "error";
}

internal sealed class UpdateRequest
{
  public string InstallPath { get; set; }
  public string CurrentVersion { get; set; }
}

internal sealed class UpdateResult
{
  public string Outcome { get; set; }
  public string Version { get; set; }
  public string RuntimePath { get; set; }
  public string DependencyBootstrapPath { get; set; }
  public string Message { get; set; }
}
