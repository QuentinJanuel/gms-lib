using System.Runtime.InteropServices.JavaScript;
using UndertaleModLib;

namespace GmsLib;

/// <summary>
/// The JavaScript surface: stage a mod's resources one call at a time, then patch a data.win with
/// them; or read a data.win's code.
///
/// Staged rather than handed over as one serialized blob because the shape is four fields deep and
/// a hand-rolled staging API needs no JSON dependency and nothing for the trimmer to keep alive
/// reflectively. One runtime serves every call: <see cref="PatchDataWin"/> clears what it used.
/// </summary>
public static partial class WasmEntry
{
    private static readonly List<ModInstaller.Script> Scripts = [];
    private static readonly List<ModInstaller.Insert> Inserts = [];
    private static readonly List<ModInstaller.Hook> Hooks = [];
    private static readonly List<ModInstaller.Object> Objects = [];

    // The runtime needs an entry point to boot; everything else is called through the exports.
    private static void Main() { }

    /// <summary>Drops everything staged, so a patch never carries a previous one's resources.</summary>
    [JSExport]
    internal static void ClearMods()
    {
        Scripts.Clear();
        Inserts.Clear();
        Hooks.Clear();
        Objects.Clear();
    }

    [JSExport]
    internal static void AddModScript(string name, string source) =>
        Scripts.Add(new ModInstaller.Script(name, source));

    [JSExport]
    internal static void AddModInsert(string at, string place, string call) =>
        Inserts.Add(new ModInstaller.Insert(at, place, call));

    [JSExport]
    internal static void AddModHook(string script, string original, string source) =>
        Hooks.Add(new ModInstaller.Hook(script, original, source));

    [JSExport]
    internal static void AddModObject(string name, int depth, bool persistent, bool visible) =>
        Objects.Add(new ModInstaller.Object(name, depth, persistent, visible, []));

    /// <summary>
    /// Attaches one event's GML to the most recently added object, by the mod contract's event name
    /// (see <see cref="ModInstaller.EventFor"/>). Positional rather than keyed by object name,
    /// because the caller has just added it and a lookup would only invent a way to get it wrong.
    /// </summary>
    [JSExport]
    internal static void AddModObjectEvent(string eventName, string source)
    {
        if (Objects.Count == 0)
        {
            throw new InvalidOperationException("AddModObjectEvent called before any AddModObject");
        }

        var (name, subtype) = ModInstaller.EventFor(eventName)
            ?? throw new ArgumentException($"mod object {Objects[^1].Name}: unknown event \"{eventName}\"");
        Objects[^1].Events.Add(new ModInstaller.Event(name, subtype, source));
    }

    /// <summary>
    /// Patches a raw data.win with everything staged and returns the rewritten bytes. With nothing
    /// staged this returns the input re-serialized, which is the correct answer rather than a
    /// special case.
    /// </summary>
    [JSExport]
    internal static byte[] PatchDataWin(byte[] dataWinBytes)
    {
        try
        {
            using var data = Read(dataWinBytes);
            ModInstaller.Install(data, new ModInstaller.Resources(Scripts, Inserts, Hooks, Objects));

            using var output = new MemoryStream();
            UndertaleIO.Write(output, data);
            return output.ToArray();
        }
        finally
        {
            ClearMods();
        }
    }

    /// <summary>The game's code in the mod loader's <c>GameCode</c> format, as JSON.</summary>
    [JSExport]
    internal static string GameCodeFromDataWin(byte[] dataWinBytes)
    {
        using var data = Read(dataWinBytes);
        return GameCode.Extract(data);
    }

    private static UndertaleData Read(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, writable: false);
        // UndertaleIO.Read throws on *any* warning when given no handler, including routine padding
        // quirks that the real tool only logs.
        return UndertaleIO.Read(input, (warning, isImportant) =>
            Console.Error.WriteLine($"[UndertaleModLib] {warning}"));
    }
}
