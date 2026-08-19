using System;

namespace TUFHelperLite.Domain.Errors;

public sealed class LevelUpdateException : Exception
{
  public LevelUpdateException(string code, string message) : base(message)
  {
    Code = code;
  }

  public string Code { get; }
}
