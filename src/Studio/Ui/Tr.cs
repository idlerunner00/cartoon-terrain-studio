using System.Globalization;

namespace TerrainStudio.Ui;

/// <summary>
/// The studio's texts. Every visible string goes through <see cref="T"/> or <see cref="F"/>, so wording lives in one
/// place per call and formatting is culture-independent.
/// </summary>
public static class Tr
{
    /// <summary>
    /// The text to show. A text may carry a context after "|" ("Save|place") to tell two uses of one word apart;
    /// the context is not shown.
    /// </summary>
    public static string T(string text)
    {
        int bar = text.IndexOf('|');
        return bar > 0 ? text[..bar] : text;
    }

    /// <summary>A format string filled with invariant-culture arguments.</summary>
    public static string F(string text, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, T(text), args);
}
