using System;
namespace TUFHelperLite.App.Ports;
public interface IMainThreadDispatcher { void Dispatch(Action action); }
