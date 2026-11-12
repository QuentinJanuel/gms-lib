using Microsoft.VisualStudio.TestTools.UnitTesting;
using GmsLib;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Models;

namespace GmsLib.Tests;

/// <summary>
/// The detour: a mod's script running at one statement inside one of the game's.
///
/// <para>
/// What these check is <b>control flow</b>, not text. A detour is only correct if, from every
/// instruction the game originally had, execution still reaches the same next instruction it did
/// before -- with the mod's call somewhere along the way. Every failure mode here is silent: a jump
/// whose offset is one word out lands on a real instruction and the game does something subtly
/// different, rather than crashing.
/// </para>
/// </summary>
[TestClass]
public class ModDetourTests
{
    /// <summary>A game with one script compiled from <paramref name="source"/>, plus a mod script.</summary>
    private static UndertaleData GameWith(string source, out UndertaleCode target)
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

        target = data.Code.ByName("gml_Script_target")!;
        return data;
    }

    /// <summary>Byte address of each instruction, in order.</summary>
    private static List<uint> Addresses(UndertaleCode code)
    {
        var result = new List<uint>();
        uint at = 0;
        foreach (var instruction in code.Instructions)
        {
            result.Add(at);
            at += instruction.CalculateInstructionSize() * 4;
        }

        return result;
    }

    /// <summary>
    /// Follows execution from <paramref name="from"/> until it reaches an address that existed in
    /// the original code, counting the mod calls passed on the way.
    ///
    /// Conditional branches are followed both ways and both must agree about where they end up --
    /// which is the property that matters: a detour must be transparent whichever way control went.
    /// </summary>
    private static (uint Landed, int Calls) FollowToOriginal(
        UndertaleCode code,
        uint from,
        ISet<uint> originalAddresses,
        bool takeBranch)
    {
        var addresses = Addresses(code);
        var indexAt = new Dictionary<uint, int>();
        for (var i = 0; i < addresses.Count; i++)
        {
            indexAt[addresses[i]] = i;
        }

        var calls = 0;
        var at = from;
        for (var step = 0; step < 1000; step++)
        {
            if (!indexAt.TryGetValue(at, out var index))
            {
                Assert.Fail($"landed at {at}, which is not the start of an instruction");
            }

            var instruction = code.Instructions[index];
            var size = instruction.CalculateInstructionSize() * 4;

            switch (instruction.Kind)
            {
                case UndertaleInstruction.Opcode.Call:
                    calls++;
                    at += size;
                    break;
                case UndertaleInstruction.Opcode.B:
                    at = (uint)((int)at + (instruction.JumpOffset * 4));
                    break;
                case UndertaleInstruction.Opcode.Bt:
                case UndertaleInstruction.Opcode.Bf:
                    at = takeBranch
                        ? (uint)((int)at + (instruction.JumpOffset * 4))
                        : at + size;
                    break;
                default:
                    at += size;
                    break;
            }

            if (originalAddresses.Contains(at))
            {
                return (at, calls);
            }
        }

        Assert.Fail($"execution from {from} never came back to the original code");
        return (0, 0);
    }

    /// <summary>
    /// The core property, over every instruction of a script: inserting anywhere leaves the path
    /// from every original instruction to its original successor unchanged.
    /// </summary>
    private static void AssertTransparent(string source, bool callFirst)
    {
        var data = GameWith(source, out var target);
        var before = Addresses(target);
        var beforeKinds = target.Instructions.Select(i => i.Kind).ToList();

        // Where each original instruction went, walking the untouched code.
        var originals = new HashSet<uint>(before);
        var expected = new Dictionary<(uint, bool), (uint, int)>();
        foreach (var address in before)
        {
            foreach (var branch in new[] { true, false })
            {
                var next = NextOf(target, address, branch);
                if (next is not null)
                {
                    expected[(address, branch)] = (next.Value, 0);
                }
            }
        }

        // One insertion at every instruction that may be moved. A `with`'s own `pushenv`/`popenv`
        // may not be; nor may a branch take a call *after* it, since on the path where it jumps
        // there is no after. Both are refused by the installer, and tested separately.
        var anchored = new HashSet<uint>();
        var anchors = new List<ModInstaller.Insert>();
        for (var i = 0; i < before.Count; i++)
        {
            if (beforeKinds[i] is UndertaleInstruction.Opcode.PushEnv or UndertaleInstruction.Opcode.PopEnv)
            {
                continue;
            }

            if (!callFirst && beforeKinds[i]
                is UndertaleInstruction.Opcode.B
                or UndertaleInstruction.Opcode.Bt
                or UndertaleInstruction.Opcode.Bf)
            {
                continue;
            }

            anchored.Add(before[i]);
            var token = callFirst
                ? $"target#{before[i]},-1,0"
                : $"target#0,{before[i]},0";
            anchors.Add(new ModInstaller.Insert(token, callFirst ? "before" : "after", "m_ins"));
        }

        Detour.Install(data, anchors);

        foreach (var ((address, branch), (next, _)) in expected)
        {
            var (landed, calls) = FollowToOriginal(target, address, originals, branch);
            Assert.AreEqual(next, landed, $"from {address} (branch={branch}) in: {source}");
            if (anchored.Contains(address))
            {
                Assert.IsTrue(calls >= 1, $"no mod call between {address} and {landed} in: {source}");
            }
        }
    }

    /// <summary>Where control goes after the instruction at <paramref name="address"/>, or null at the end.</summary>
    private static uint? NextOf(UndertaleCode code, uint address, bool takeBranch)
    {
        var addresses = Addresses(code);
        var index = addresses.IndexOf(address);
        var instruction = code.Instructions[index];
        var size = instruction.CalculateInstructionSize() * 4;
        var next = instruction.Kind switch
        {
            UndertaleInstruction.Opcode.B => (uint)((int)address + (instruction.JumpOffset * 4)),
            UndertaleInstruction.Opcode.Bt or UndertaleInstruction.Opcode.Bf =>
                takeBranch ? (uint)((int)address + (instruction.JumpOffset * 4)) : address + size,
            _ => address + size,
        };
        return addresses.Contains(next) ? next : null;
    }

    /// <summary>
    /// The shapes a fangame's save routine is actually made of, plus the ones most likely to break
    /// a naive detour: branches at the anchor, loops of every kind, and a `with` whose own offsets
    /// must not be touched.
    /// </summary>
    private static IEnumerable<string> Shapes() =>
    [
        "global.a = 1;",
        "global.a = 1; global.b = 2; global.c = 3;",
        "if (argument0) { global.a = 1; } else { global.a = 2; }",
        "if (argument0) { if (global.b) { global.a = 1; } }",
        "while (global.i < 10) { global.i += 1; }",
        "for (var i = 0; i < 10; i += 1) { global.a += i; }",
        "do { global.i += 1; } until (global.i > 3);",
        "repeat (4) { global.a += 1; }",
        "switch (argument0) { case 0: global.a = 1; break; case 1: global.a = 2; default: global.a = 3; }",
        "with (obj) { x = 1; }",
        "global.a = argument0 ? 1 : 2;",
        "if (global.a && global.b) { global.c = 1; }",
        "if (argument0) { exit; } global.a = 1;",
        "while (true) { if (global.a) { break; } else { continue; } }",
    ];

    [TestMethod]
    public void InsertingBeforeEveryInstructionKeepsControlFlow()
    {
        foreach (var shape in Shapes())
        {
            AssertTransparent(shape, callFirst: true);
        }
    }

    [TestMethod]
    public void InsertingAfterEveryInstructionKeepsControlFlow()
    {
        foreach (var shape in Shapes())
        {
            AssertTransparent(shape, callFirst: false);
        }
    }

    /// <summary>
    /// Nothing before the detours moves, which is what every existing jump depends on.
    /// </summary>
    [TestMethod]
    public void TheOriginalInstructionsKeepTheirAddresses()
    {
        var data = GameWith("global.a = 1; if (global.b) { global.c = 2; } global.d = 3;", out var target);
        var before = Addresses(target);
        var originalLength = before[^1] + target.Instructions[^1].CalculateInstructionSize() * 4;

        Detour.Install(data, [new ModInstaller.Insert($"target#{before[2]},-1,0", "before", "m_ins")]);

        var after = Addresses(target);
        CollectionAssert.IsSubsetOf(before, after, "an original instruction address moved");
        // The code grew only at the end.
        Assert.IsTrue(after[^1] > originalLength);
    }

    /// <summary>
    /// The guard that stops code running off the end into the first detour -- and that keeps a jump
    /// to the end of the entry meaning what it meant.
    /// </summary>
    [TestMethod]
    public void AnExitGuardsTheEndOfTheOriginalCode()
    {
        var data = GameWith("global.a = 1;", out var target);
        var before = Addresses(target);
        var endAddress = before[^1] + target.Instructions[^1].CalculateInstructionSize() * 4;

        Detour.Install(data, [new ModInstaller.Insert($"target#{before[0]},-1,0", "before", "m_ins")]);

        var after = Addresses(target);
        var guard = target.Instructions[after.IndexOf(endAddress)];
        Assert.AreEqual(UndertaleInstruction.Opcode.Exit, guard.Kind);
    }

    [TestMethod]
    public void SeveralInsertionsAtOneAddressAllRun()
    {
        var data = GameWith("global.a = 1; global.b = 2;", out var target);
        var before = Addresses(target);
        var originals = new HashSet<uint>(before);

        Detour.Install(data, [
            new ModInstaller.Insert($"target#{before[0]},-1,0", "before", "m_ins"),
            new ModInstaller.Insert($"target#{before[0]},-1,0", "before", "m_ins"),
            new ModInstaller.Insert($"target#{before[0]},-1,0", "before", "m_ins"),
        ]);

        var (landed, calls) = FollowToOriginal(target, before[0], originals, takeBranch: false);
        Assert.AreEqual(before[1], landed);
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public void AWrongAddressIsRefused()
    {
        var data = GameWith("global.a = 1;", out _);
        Assert.ThrowsException<InvalidOperationException>(
            () => Detour.Install(data, [new ModInstaller.Insert("target#9999,-1,0", "before", "m_ins")]));
    }

    [TestMethod]
    public void AnUnknownScriptIsRefused()
    {
        var data = GameWith("global.a = 1;", out _);
        Assert.ThrowsException<InvalidOperationException>(
            () => Detour.Install(data, [new ModInstaller.Insert("nope#0,-1,0", "before", "m_ins")]));
    }

    [TestMethod]
    public void AStatementWithNoAfterIsRefused()
    {
        var data = GameWith("global.a = 1;", out _);
        Assert.ThrowsException<InvalidOperationException>(
            () => Detour.Install(data, [new ModInstaller.Insert("target#0,-1,0", "after", "m_ins")]));
    }

    /// <summary>
    /// "After a jump" does not exist on the path where the jump is taken. The mapper never asks for
    /// it; refusing says so if one ever does.
    /// </summary>
    [TestMethod]
    public void RunningSomethingAfterABranchIsRefused()
    {
        var data = GameWith("if (argument0) { global.a = 1; }", out var target);
        var addresses = Addresses(target);
        var index = target.Instructions
            .Select((instruction, i) => (instruction, i))
            .First(pair => pair.instruction.Kind == UndertaleInstruction.Opcode.Bf)
            .i;

        Assert.ThrowsException<InvalidOperationException>(
            () => Detour.Install(
                data,
                [new ModInstaller.Insert($"target#0,{addresses[index]},0", "after", "m_ins")]));

        // But running something *before* it is fine: that is what "before a statement" is.
        var fresh = GameWith("if (argument0) { global.a = 1; }", out _);
        Detour.Install(fresh, [new ModInstaller.Insert($"target#{addresses[index]},-1,0", "before", "m_ins")]);
    }

    /// <summary>
    /// A `with` keeps its own loop: `pushenv` and `popenv` offsets are what step it through the
    /// instances, so a moved one is a different loop entirely.
    /// </summary>
    [TestMethod]
    public void MovingAWithsOwnInstructionIsRefused()
    {
        var data = GameWith("with (obj) { x = 1; }", out var target);
        var addresses = Addresses(target);
        var index = target.Instructions
            .Select((instruction, i) => (instruction, i))
            .First(pair => pair.instruction.Kind == UndertaleInstruction.Opcode.PushEnv)
            .i;

        Assert.ThrowsException<InvalidOperationException>(
            () => Detour.Install(
                data,
                [new ModInstaller.Insert($"target#{addresses[index]},-1,0", "before", "m_ins")]));
    }
}
