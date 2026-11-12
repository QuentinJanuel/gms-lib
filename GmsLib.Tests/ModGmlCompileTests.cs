using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UndertaleModLib;
using UndertaleModLib.Compiler;

namespace GmsLib.Tests;

/// <summary>
/// Compiles a mod's generated GML with the real GameMaker Studio compiler.
///
/// A mod's GML is written once and has to compile on both engines. GameMaker 8 only parses it at
/// game start, so a mistake there shows up as a game that will not launch; this catches it here.
/// Point QUICKSAVE_GML at a JSON array of <c>{name, source}</c>.
/// </summary>
[TestClass]
public class ModGmlCompileTests
{
    [TestMethod]
    public void AModsGeneratedGmlCompiles()
    {
        var path = Environment.GetEnvironmentVariable("QUICKSAVE_GML");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Assert.Inconclusive("set QUICKSAVE_GML to a JSON array of {name, source}");
        }

        var bodies = JsonDocument.Parse(File.ReadAllText(path!)).RootElement;
        var data = UndertaleData.CreateNew();
        data.GeneralInfo.BytecodeVersion = 16;
        data.BuiltinList.Functions.TryAdd("host_send", new FunctionInfo("host_send", 1));
        data.BuiltinList.Functions.TryAdd("host_receive", new FunctionInfo("host_receive", 0));

        // Declared empty first, so a call to one compiles as a script call -- the same two passes
        // ModInstaller makes.
        var declare = new CodeImportGroup(data);
        foreach (var body in bodies.EnumerateArray())
        {
            declare.QueueReplace($"gml_Script_{body.GetProperty("name").GetString()}", "");
        }
        declare.Import();

        var compile = new CodeImportGroup(data);
        foreach (var body in bodies.EnumerateArray())
        {
            compile.QueueReplace(
                $"gml_Script_{body.GetProperty("name").GetString()}",
                body.GetProperty("source").GetString());
        }
        compile.Import();

        foreach (var body in bodies.EnumerateArray())
        {
            var name = body.GetProperty("name").GetString();
            var code = data.Code.ByName($"gml_Script_{name}");
            Assert.IsNotNull(code, $"{name} did not compile");
            Console.WriteLine($"{name}: {code!.Instructions.Count} instructions");
        }
    }
}
