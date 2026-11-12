using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Models;

namespace GmsLib;

/// <summary>
/// Running a mod's script at one statement inside one of the game's, without moving anything else.
///
/// <para>
/// GameMaker Studio code is bytecode, and its jumps are <b>relative offsets</b> that UndertaleModLib
/// stores as written -- nothing recomputes them. Inserting an instruction would therefore silently
/// change the meaning of every jump that spans the insertion, every <c>pushenv</c>/<c>popenv</c>
/// offset, and (in 2.3) every absolute <c>try</c> address stored as a plain integer. So nothing is
/// inserted. One instruction is <b>moved</b> instead, to a detour appended past the end of the code:
/// </para>
///
/// <code>
/// before:                          after:
///  ...compute value                 ...compute value
///  pop.v.v global.saveX             b detour         &lt;- same size, padded
///  next statement...                (filler)
///                                   next statement...
///                                   ...
///                                   exit.i           &lt;- old end of code
///                                   detour: pop.v.v global.saveX
///                                           call.i m_ins0(argc=0)
///                                           popz.v
///                                           b next statement
/// </code>
///
/// <para>
/// Why nothing else has to change: no existing instruction moves, so every relative jump still spans
/// the same instructions. A jump that landed on the moved instruction now lands on the <c>b</c>, and
/// the detour runs that instruction anyway. Nothing can land in the filler, because a jump targets
/// the start of an instruction and never its second word.
/// </para>
///
/// <para>
/// <b>The <c>exit.i</c> is the detail that must not be forgotten.</b> Code that ran off the end used
/// to stop; with detours appended it would fall into the first one. Jumps to the end of the entry
/// (which is what leaving a loop at the very end compiles to) landed there too. One <c>exit.i</c> at
/// the old end preserves both meanings, and it is added once per entry.
/// </para>
/// </summary>
public static class Detour
{
    /// <summary>One thing to run at one address, and which side of the moved instruction it goes.</summary>
    /// <param name="Address">Byte address of the instruction to move.</param>
    /// <param name="CallFirst">
    /// True to run the call and then the moved instruction, false for the other way round.
    ///
    /// This is what lets one mechanism serve both places. "Before a statement" moves the statement's
    /// first instruction and runs the call first. "After a simple statement" moves its last and runs
    /// the call second. "After a compound statement" -- an <c>if</c>, a loop -- has no last
    /// instruction of its own, since its branches meet at whatever follows; it moves that successor
    /// and runs the call first, which is the same moment.
    /// </param>
    /// <param name="Call">The script to call.</param>
    public readonly record struct Anchor(uint Address, bool CallFirst, string Call);

    /// <summary>The instruction pair a script call compiles to, learned once per script.</summary>
    private sealed record CallTemplate(UndertaleFunction Function, ushort ArgumentsCount);

    public static void Install(UndertaleData data, IReadOnlyList<ModInstaller.Insert> inserts)
    {
        if (inserts.Count == 0)
        {
            return;
        }

        // 2.3 made a script a file of global functions whose bodies are child code entries sharing
        // the parent's bytecode at an offset. The detour itself suits that fine -- nothing shifts --
        // but the addresses would have to be mapped within the parent and the detours appended
        // there. Refused rather than approximated, exactly like hooks.
        if (data.IsVersionAtLeast(2, 3))
        {
            throw new InvalidOperationException(
                "mod install: inserting into a statement is not supported on GameMaker 2.3 and later yet");
        }

        var templates = new Dictionary<string, CallTemplate>();
        foreach (var group in inserts.GroupBy(insert => EntryOf(insert.At)))
        {
            var code = data.Code.ByName($"gml_Script_{group.Key}")
                ?? throw new InvalidOperationException(
                    $"mod install: cannot insert into {group.Key}, this game has no script by that name");
            if (code.ParentEntry is not null)
            {
                throw new InvalidOperationException(
                    $"mod install: cannot insert into {group.Key}, its code is part of another entry");
            }

            var anchors = new List<Anchor>();
            foreach (var insert in group)
            {
                anchors.Add(AnchorOf(insert));
                if (!templates.ContainsKey(insert.Call))
                {
                    templates[insert.Call] = LearnCall(data, insert.Call);
                }
            }

            InstallInto(code, anchors, templates);
        }
    }

    /// <summary>The script name in front of a token's <c>#</c>.</summary>
    private static string EntryOf(string at)
    {
        var hash = at.IndexOf('#');
        return hash < 0 ? at : at[..hash];
    }

    /// <summary>
    /// Where one insertion goes, from its token and place.
    ///
    /// The token carries both anchors the mapper found -- <c>&lt;script&gt;#&lt;before&gt;,&lt;after&gt;,&lt;afterFirst&gt;</c>
    /// -- because only the mapper knows whether "after" means this statement's last instruction or
    /// the one it hands control to. An <c>after</c> of -1 is a statement nothing follows.
    /// </summary>
    private static Anchor AnchorOf(ModInstaller.Insert insert)
    {
        var hash = insert.At.IndexOf('#');
        var parts = hash < 0 ? [] : insert.At[(hash + 1)..].Split(',');
        if (parts.Length != 3
            || !uint.TryParse(parts[0], out var before)
            || !int.TryParse(parts[1], out var after)
            || !int.TryParse(parts[2], out var afterFirst))
        {
            throw new InvalidOperationException(
                $"mod install: {insert.At} is not a statement this loader can insert around");
        }

        return insert.Place switch
        {
            "before" => new Anchor(before, CallFirst: true, insert.Call),
            "after" when after >= 0 => new Anchor((uint)after, CallFirst: afterFirst != 0, insert.Call),
            "after" => throw new InvalidOperationException(
                $"mod install: nothing follows the statement at {insert.At}, so there is no \"after\" to insert at"),
            // Dropping a statement means branching over its instructions, and the token says where
            // the statement starts, not where it ends: this backend has no end to branch to.
            // Refused rather than guessed at, the way hooks on 2.3 are.
            "instead" => throw new InvalidOperationException(
                $"mod install: running something instead of the statement at {insert.At} is not supported on "
                    + "GameMaker Studio, where a statement's bytecode has no end this format records"),
            _ => throw new InvalidOperationException(
                $"mod install: an insertion's place is \"before\", \"after\" or \"instead\", not \"{insert.Place}\""),
        };
    }

    /// <summary>
    /// What calling <paramref name="script"/> compiles to, by compiling it and reading it back.
    ///
    /// Asked of the compiler rather than assumed, because how a script call is emitted is the
    /// compiler's business and has changed between bytecode versions. The scratch entry is deleted
    /// afterwards; only the function reference and argument count are kept.
    /// </summary>
    private static CallTemplate LearnCall(UndertaleData data, string script)
    {
        const string scratch = "gml_Script___opengm_mod_call_probe";
        var declare = new CodeImportGroup(data);
        declare.QueueReplace(scratch, "");
        declare.Import();
        var body = new CodeImportGroup(data);
        body.QueueReplace(scratch, $"{script}();");
        body.Import();

        var entry = data.Code.ByName(scratch)
            ?? throw new InvalidOperationException($"mod install: could not compile a call to {script}");
        var call = entry.Instructions.FirstOrDefault(i => i.Kind == UndertaleInstruction.Opcode.Call)
            ?? throw new InvalidOperationException(
                $"mod install: calling {script} did not compile to a call, so this game does not have it as a script");
        var template = new CallTemplate(call.ValueFunction, call.ArgumentsCount);

        data.Code.Remove(entry);
        if (data.Scripts.ByName(scratch[11..]) is UndertaleScript stray)
        {
            data.Scripts.Remove(stray);
        }

        return template;
    }

    /// <summary>The two instructions a call is: invoke it, then drop what it returned.</summary>
    private static IEnumerable<UndertaleInstruction> CallInstructions(CallTemplate template)
    {
        // Stack-neutral by construction, which is what makes a detour safe at any address: a
        // `repeat` counter, a `switch` value or a `with` context part-way through is untouched.
        yield return new UndertaleInstruction
        {
            Kind = UndertaleInstruction.Opcode.Call,
            Type1 = UndertaleInstruction.DataType.Int32,
            ArgumentsCount = template.ArgumentsCount,
            ValueFunction = template.Function,
        };
        yield return new UndertaleInstruction
        {
            Kind = UndertaleInstruction.Opcode.Popz,
            Type1 = UndertaleInstruction.DataType.Variable,
        };
    }

    private static bool IsBranch(UndertaleInstruction.Opcode kind) =>
        kind is UndertaleInstruction.Opcode.B
            or UndertaleInstruction.Opcode.Bt
            or UndertaleInstruction.Opcode.Bf;

    private static UndertaleInstruction Branch(int jumpOffsetWords) =>
        new() { Kind = UndertaleInstruction.Opcode.B, JumpOffset = jumpOffsetWords };

    /// <summary>
    /// The jump offset, in words, that reaches <paramref name="toAddress"/> from a branch sitting at
    /// <paramref name="fromAddress"/>.
    ///
    /// Signed, and that is the whole point of it being a function. Addresses are unsigned, and every
    /// branch a detour relocates jumps <i>backwards</i> -- the detour lives past the end of the code,
    /// and its target is in the body above. Subtracting unsigned there wraps, and the result divides
    /// into a large positive offset that lands somewhere arbitrary.
    /// </summary>
    private static int Offset(uint fromAddress, uint toAddress) =>
        ((int)toAddress - (int)fromAddress) / 4;

    private static void InstallInto(
        UndertaleCode code,
        List<Anchor> anchors,
        Dictionary<string, CallTemplate> templates)
    {
        var original = code.Instructions.ToList();

        // Byte address of every instruction, and the index at each address.
        var addressOf = new uint[original.Count];
        var indexAt = new Dictionary<uint, int>(original.Count);
        uint running = 0;
        for (var i = 0; i < original.Count; i++)
        {
            addressOf[i] = running;
            indexAt[running] = i;
            running += original[i].CalculateInstructionSize() * 4;
        }

        var endAddress = running;

        // Everything wanted at one address becomes one detour, in declaration order, so no
        // insertion ever moves another's branch.
        var byAddress = new SortedDictionary<uint, List<Anchor>>();
        foreach (var anchor in anchors)
        {
            if (!indexAt.TryGetValue(anchor.Address, out var index))
            {
                throw new InvalidOperationException(
                    $"mod install: {code.Name?.Content} has no instruction at {anchor.Address}. "
                        + "The game's code was read from a different copy of this game.");
            }

            var kind = original[index].Kind;
            // A `with` loop's own offsets drive its instance iteration; a moved one is a different
            // loop. Plain branches are relocatable, because their offset is recomputed below.
            if (kind is UndertaleInstruction.Opcode.PushEnv or UndertaleInstruction.Opcode.PopEnv)
            {
                throw new InvalidOperationException(
                    $"mod install: cannot insert at {code.Name?.Content}+{anchor.Address}, "
                        + $"which is a {kind} -- moving one would change the `with` it belongs to");
            }

            // "Run this after that instruction" has no meaning when the instruction is a jump: on
            // the path where it is taken, there is no "after". The mapper never asks for it -- a
            // simple statement ends in a `pop` or a `popz`, and a compound one anchors on its
            // successor with the call first -- so this is here to catch a mapper that has gone
            // wrong, loudly, instead of installing a call that silently only fires sometimes.
            if (!anchor.CallFirst && IsBranch(original[index].Kind))
            {
                throw new InvalidOperationException(
                    $"mod install: cannot run something after {code.Name?.Content}+{anchor.Address}, "
                        + $"which is a {original[index].Kind} -- on the path where it jumps there is "
                        + "no \"after\" for the call to happen in");
            }

            if (!byAddress.TryGetValue(anchor.Address, out var list))
            {
                byAddress[anchor.Address] = list = [];
            }

            list.Add(anchor);
        }

        // Pass one: how long each detour is, so every address is known before any jump is written.
        // The `exit.i` guard sits between the old end and the first detour.
        //
        // Measured from the instructions themselves, never from a constant. A call happens to be a
        // `call` (two words, since it carries a function reference) and a `popz` (one) -- but that
        // is the compiler's business, and counting it by hand is exactly how the first version of
        // this put every detour after the first one word out. A jump one word out still lands on a
        // real instruction, so the game runs, and runs something else.
        var detourAddress = new Dictionary<uint, uint>();
        var next = endAddress + 4;
        foreach (var (address, list) in byAddress)
        {
            detourAddress[address] = next;
            var words = original[indexAt[address]].CalculateInstructionSize();
            foreach (var anchor in list)
            {
                foreach (var instruction in CallInstructions(templates[anchor.Call]))
                {
                    words += instruction.CalculateInstructionSize();
                }
            }

            // Plus the branch home.
            next += (words + 1) * 4;
        }

        // Pass two: the body, with each anchor replaced by a branch of exactly the same size.
        var rebuilt = new List<UndertaleInstruction>(original.Count);
        var detours = new List<UndertaleInstruction>();
        for (var i = 0; i < original.Count; i++)
        {
            var address = addressOf[i];
            if (!byAddress.TryGetValue(address, out var list))
            {
                rebuilt.Add(original[i]);
                continue;
            }

            var moved = original[i];
            var words = moved.CalculateInstructionSize();
            var target = detourAddress[address];

            // The branch, then filler to the instruction's original width. The filler is the same
            // branch rather than a nop: nothing can reach it, and if anything ever did it would do
            // the right thing instead of something arbitrary.
            for (var word = 0; word < words; word++)
            {
                var at = address + (uint)(word * 4);
                rebuilt.Add(Branch(Offset(at, target)));
            }

            // The detour itself.
            var detourStart = target;
            var cursor = detourStart;
            var first = list.Where(a => a.CallFirst);
            var last = list.Where(a => !a.CallFirst);
            foreach (var anchor in first)
            {
                foreach (var instruction in CallInstructions(templates[anchor.Call]))
                {
                    detours.Add(instruction);
                    cursor += instruction.CalculateInstructionSize() * 4;
                }
            }

            if (IsBranch(moved.Kind))
            {
                // Relative, so moving it means recomputing it against where it now sits.
                var branchTarget = (uint)((int)address + (moved.JumpOffset * 4));
                moved.JumpOffset = Offset(cursor, branchTarget);
            }

            detours.Add(moved);
            cursor += words * 4;

            foreach (var anchor in last)
            {
                foreach (var instruction in CallInstructions(templates[anchor.Call]))
                {
                    detours.Add(instruction);
                    cursor += instruction.CalculateInstructionSize() * 4;
                }
            }

            // Home, to whatever followed the instruction that was moved.
            var home = address + (words * 4);
            detours.Add(Branch(Offset(cursor, home)));
        }

        // The guard, then the detours. Without it, code that ran off the end would fall into the
        // first detour, and every jump to the end of the entry would land there too.
        rebuilt.Add(new UndertaleInstruction
        {
            Kind = UndertaleInstruction.Opcode.Exit,
            Type1 = UndertaleInstruction.DataType.Int32,
        });
        rebuilt.AddRange(detours);

        code.Instructions.Clear();
        code.Instructions.AddRange(rebuilt);
        code.UpdateLength();
    }
}
