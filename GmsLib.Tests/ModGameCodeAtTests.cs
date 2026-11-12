using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using GmsLib;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Models;

namespace GmsLib.Tests;

/// <summary>
/// The <c>at</c> tokens the summary hands a mod, and whether they name the instruction they claim.
///
/// <para>
/// This is the seam between two halves written apart: the mapper says where a statement is, and the
/// detour acts on that. If they disagree the game is edited in the wrong place, and since the detour
/// keeps every address valid, the result is a game that still runs and does something else.
/// </para>
/// </summary>
[TestClass]
public class ModGameCodeAtTests
{
    private static UndertaleData GameWith(string source)
    {
        var data = UndertaleData.CreateNew();
        data.GeneralInfo.BytecodeVersion = 16;

        var declare = new CodeImportGroup(data);
        declare.QueueReplace("gml_Script_target", "");
        declare.QueueReplace("gml_Script_m_ins", "");
        declare.Import();

        var bodies = new CodeImportGroup(data);
        bodies.QueueReplace("gml_Script_target", source);
        bodies.QueueReplace("gml_Script_m_ins", "global.hits += 1;");
        bodies.Import();

        return data;
    }

    /// <summary>Every statement of `target` that carries a token, as (kind, token).</summary>
    private static List<(string Kind, string At)> TokensOf(UndertaleData data)
    {
        var json = JsonDocument.Parse(GameCode.Extract(data));
        var found = new List<(string, string)>();

        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray())
                {
                    Walk(item);
                }

                return;
            }

            if (node.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (node.TryGetProperty("at", out var at) && node.TryGetProperty("k", out var kind))
            {
                found.Add((kind.GetString()!, at.GetString()!));
            }

            foreach (var key in new[] { "then", "else", "body", "bodies" })
            {
                if (node.TryGetProperty(key, out var nested))
                {
                    Walk(nested);
                }
            }
        }

        foreach (var script in json.RootElement.GetProperty("scripts").EnumerateArray())
        {
            if (script.GetProperty("name").GetString() == "target")
            {
                Walk(script.GetProperty("body"));
            }
        }

        return found;
    }

    private static (uint Before, uint After) AddressesIn(string token)
    {
        var parts = token[(token.IndexOf('#') + 1)..].Split(',');
        return (uint.Parse(parts[0]), uint.Parse(parts[1]));
    }

    private static UndertaleInstruction InstructionAt(UndertaleCode code, uint address)
    {
        uint at = 0;
        foreach (var instruction in code.Instructions)
        {
            if (at == address)
            {
                return instruction;
            }

            at += instruction.CalculateInstructionSize() * 4;
        }

        Assert.Fail($"no instruction begins at {address}");
        return null!;
    }

    /// <summary>
    /// The kind check: an assignment ends on the <c>pop</c> that stores it, a call on the
    /// <c>popz</c> that throws its result away. A token naming anything else is a mapper that has
    /// drifted from the instruction stream.
    /// </summary>
    [TestMethod]
    public void EveryTokenNamesTheInstructionItsStatementEndsOn()
    {
        string[] sources =
        [
            "global.a = 1;",
            "global.a = 1; global.b = 2;",
            "show_debug_message(\"x\");",
            "if (argument0) { global.a = 1; } else { global.b = 2; }",
            "while (global.i < 3) { global.i += 1; }",
            "for (var i = 0; i < 3; i += 1) { global.a += i; }",
            "with (obj) { x = 1; }",
            "switch (argument0) { case 0: global.a = 1; break; default: global.a = 2; }",
            "repeat (3) { global.a += 1; }",
        ];

        foreach (var source in sources)
        {
            var data = GameWith(source);
            var code = data.Code.ByName("gml_Script_target")!;
            var tokens = TokensOf(data);
            Assert.IsTrue(tokens.Count > 0, $"no statement carried a token in: {source}");

            foreach (var (kind, token) in tokens)
            {
                var (before, after) = AddressesIn(token);
                StringAssert.StartsWith(token, "target#", $"token names the wrong entry in: {source}");

                var last = InstructionAt(code, after);
                var expected = kind switch
                {
                    "assign" => UndertaleInstruction.Opcode.Pop,
                    "call" => UndertaleInstruction.Opcode.Popz,
                    _ => throw new InvalidOperationException($"unexpected kind {kind}"),
                };
                Assert.AreEqual(expected, last.Kind, $"{kind} at {after} in: {source}");

                // The "before" address must be a real instruction too, and never after the last.
                InstructionAt(code, before);
                Assert.IsTrue(before <= after, $"{kind} starts after it ends in: {source}");
            }
        }
    }

    /// <summary>
    /// Every token the mapper writes is one the installer accepts. These are the two halves of the
    /// feature, and this is the only test that runs them against each other.
    /// </summary>
    [TestMethod]
    public void EveryTokenTheMapperWritesCanBeInsertedAt()
    {
        const string source = "global.a = 1; if (argument0) { global.b = 2; } while (global.i < 3) { global.i += 1; }";

        foreach (var place in new[] { "before", "after" })
        {
            var data = GameWith(source);
            var tokens = TokensOf(data);
            var inserts = tokens.Select(t => new ModInstaller.Insert(t.At, place, "m_ins")).ToList();

            // Throws if any address is not an instruction, or is one that may not be moved.
            Detour.Install(data, inserts);

            var code = data.Code.ByName("gml_Script_target")!;
            var calls = code.Instructions.Count(i =>
                i.Kind == UndertaleInstruction.Opcode.Call
                && i.ValueFunction?.Name?.Content == "m_ins");
            Assert.AreEqual(tokens.Count, calls, $"not every insertion landed, {place}");
        }
    }

    /// <summary>
    /// Statements the mapper cannot place carry no token rather than a wrong one. A `for` header's
    /// two halves are the case that matters: the decompiler reports them as assignments, and they
    /// are not statements a mod may insert into.
    /// </summary>
    [TestMethod]
    public void StatementsWithoutAnInstructionOfTheirOwnCarryNoToken()
    {
        var data = GameWith("if (argument0) { global.a = 1; }");
        var json = JsonDocument.Parse(GameCode.Extract(data));
        var target = json.RootElement.GetProperty("scripts").EnumerateArray()
            .First(s => s.GetProperty("name").GetString() == "target");
        var ifNode = target.GetProperty("body").EnumerateArray().First();

        Assert.AreEqual("if", ifNode.GetProperty("k").GetString());
        Assert.IsFalse(
            ifNode.TryGetProperty("at", out _),
            "an `if` is assembled from control flow and has no instruction of its own to move");
    }

    /// <summary>
    /// The same two checks, on real games: every token names the instruction its statement ends on,
    /// and every token the mapper writes is one the installer accepts.
    ///
    /// <para>
    /// Ignored by default and driven by <c>GMS_CHECK_GAMES</c> (<c>:</c>-separated paths to
    /// <c>data.win</c> files), because the games are not ours to check in and every machine keeps
    /// them somewhere else. Synthetic scripts cover the shapes a compiler emits for source someone
    /// wrote by hand; a real fangame is where the shapes nobody would write turn up.
    /// </para>
    /// </summary>
    [TestMethod]
    public void EveryTokenOfARealGameIsSoundAndInsertable()
    {
        var paths = (Environment.GetEnvironmentVariable("GMS_CHECK_GAMES") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length == 0)
        {
            // Inconclusive rather than ignored, so it runs the moment the variable is set without
            // anyone having to remember a second flag.
            Assert.Inconclusive("set GMS_CHECK_GAMES to `:`-separated paths of data.win files");
        }

        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"skipped (not here): {path}");
                continue;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            var data = UndertaleModLib.UndertaleIO.Read(stream);

            // Real game code nests past System.Text.Json's default 64. Only this test's reader
            // cares -- the loader's own side is JavaScript's JSON.parse, which has no such limit.
            var json = JsonDocument.Parse(GameCode.Extract(data), new JsonDocumentOptions { MaxDepth = 512 });
            var tokens = 0;
            var checkedKinds = 0;
            var inserts = new List<ModInstaller.Insert>();

            foreach (var script in json.RootElement.GetProperty("scripts").EnumerateArray())
            {
                var name = script.GetProperty("name").GetString()!;
                var code = data.Code.ByName($"gml_Script_{name}");
                if (code is null)
                {
                    continue;
                }

                var found = new List<(string Kind, string At)>();
                CollectTokens(script.GetProperty("body"), found);
                foreach (var (kind, token) in found)
                {
                    tokens++;
                    StringAssert.StartsWith(token, $"{name}#", "a token names the wrong entry");
                    var (before, after) = AddressesIn(token);

                    // The kind check: the address a statement ends on must be the instruction that
                    // finishes it. A token pointing anywhere else is a mapper out of step with the
                    // instruction stream, and the detour would edit the wrong line.
                    var expected = kind switch
                    {
                        "assign" => UndertaleInstruction.Opcode.Pop,
                        "call" => UndertaleInstruction.Opcode.Popz,
                        _ => (UndertaleInstruction.Opcode?)null,
                    };
                    if (expected is not null)
                    {
                        Assert.AreEqual(expected, InstructionAt(code, after).Kind, $"{kind} at {token}");
                        checkedKinds++;
                    }

                    InstructionAt(code, before);
                    inserts.Add(new ModInstaller.Insert(token, "after", "m_probe"));
                }
            }

            // And the installer takes every one of them. Declared first, the way the patcher does.
            var declare = new CodeImportGroup(data);
            declare.QueueReplace("gml_Script_m_probe", "");
            declare.Import();
            var body = new CodeImportGroup(data);
            body.QueueReplace("gml_Script_m_probe", "global.__probe += 1;");
            body.Import();

            Detour.Install(data, inserts);

            Console.WriteLine(
                $"{Path.GetFileName(Path.GetDirectoryName(path))}: {tokens} tokens, "
                    + $"{checkedKinds} kind-checked, {inserts.Count} installed");
        }
    }

    private static void CollectTokens(JsonElement node, List<(string, string)> found)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                CollectTokens(item, found);
            }

            return;
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (node.TryGetProperty("at", out var at) && node.TryGetProperty("k", out var kind))
        {
            found.Add((kind.GetString()!, at.GetString()!));
        }

        foreach (var key in new[] { "then", "else", "body", "bodies" })
        {
            if (node.TryGetProperty(key, out var nested))
            {
                CollectTokens(nested, found);
            }
        }
    }

    /// <summary>
    /// An object's event carries no token: nothing can install into one yet, and a token would be a
    /// promise the installer cannot keep.
    /// </summary>
    [TestMethod]
    public void AnObjectEventCarriesNoToken()
    {
        var data = GameWith("global.a = 1;");
        var objects = new CodeImportGroup(data);
        objects.QueueReplace("gml_Object_obj_thing_Create_0", "global.b = 2;");
        objects.Import();

        var json = JsonDocument.Parse(GameCode.Extract(data));
        foreach (var obj in json.RootElement.GetProperty("objects").EnumerateArray())
        {
            foreach (var ev in obj.GetProperty("events").EnumerateObject())
            {
                foreach (var statement in ev.Value.EnumerateArray())
                {
                    Assert.IsFalse(statement.TryGetProperty("at", out _), "an event statement carried a token");
                }
            }
        }
    }
}
