using System.IO;

namespace TUFHelperLite.Domain.Errors;

public sealed class InsufficientDiskSpaceException : IOException
{
  public InsufficientDiskSpaceException(
    string message,
    long availableBytes,
    long requiredBytes) : base(message)
  {
    AvailableBytes = availableBytes;
    RequiredBytes = requiredBytes;
  }

  public long AvailableBytes { get; }
  public long RequiredBytes { get; }
}
