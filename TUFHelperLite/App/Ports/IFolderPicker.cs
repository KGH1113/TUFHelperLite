using System;
namespace TUFHelperLite.App.Ports;
public interface IFolderPicker { void Pick(string initialDirectory, bool allowExisting, Action<string> selected, Action<Exception> failed); }
