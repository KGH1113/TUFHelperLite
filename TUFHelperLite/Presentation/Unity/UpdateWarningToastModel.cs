namespace TUFHelperLite.Presentation.Unity;

internal sealed class UpdateWarningToastModel
{
  public const string DefaultTitle = "Update Available";
  public const string DefaultMessage =
    "A newer version of this level is available. Clears submitted with this version may not be accepted.";

  public UpdateWarningToastModel(string title = null, string message = null)
  {
    Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle : title.Trim();
    Message = string.IsNullOrWhiteSpace(message) ? DefaultMessage : message.Trim();
  }

  public string Title { get; }
  public string Message { get; }
}
