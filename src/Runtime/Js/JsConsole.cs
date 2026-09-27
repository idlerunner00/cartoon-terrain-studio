using System;

namespace Fluitown.Runtime;

/// <summary>
/// <c>console.*</c> for ported domain code. The domain layer is engine-free, so it writes through
/// replaceable sinks; the Godot layer points them at GD.PushWarning / GD.PushError at startup.
/// </summary>
public static class JsConsole
{
    public static Action<string> Error = message => Console.Error.WriteLine(message);
    public static Action<string> Warn = message => Console.Error.WriteLine(message);

    public static void error(string message) => Error(message);
    public static void error(string message, object? detail) => Error(detail is null ? message : $"{message} {detail}");
    public static void warn(string message) => Warn(message);
}
