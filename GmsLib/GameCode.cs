using System.Text;
using System.Text.Json;
using Underanalyzer;
using Underanalyzer.Decompiler;
using Underanalyzer.Decompiler.AST;
using Underanalyzer.Decompiler.ControlFlow;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using static Underanalyzer.IGMInstruction;
using UAssetType = Underanalyzer.AssetType;

namespace GmsLib;

/// <summary>
/// A GameMaker Studio game's code, summarized into the mod loader's engine-neutral format.
///
/// That format's own definition, not this file, is the authority
/// on what every node means. This is one of its two producers -- GameMaker 8's is
/// <c>gm8exe/src/game_code.rs</c> -- and the two are held to the same fixtures.
///
/// <b>For understanding, never for rebuilding.</b> A mod reads this to find something in the game
/// and never prints it back into GML, so the mapping keeps assignments, calls, references and
/// control flow, normalizes what differs from GameMaker 8, and turns everything else into
/// <c>unknown</c> rather than guessing.
///
/// Built from Underanalyzer's AST rather than from decompiled text, so nothing is parsed twice and
/// the decompiler's own knowledge -- which variable is local, which number is an object -- is used
/// as it stands.
/// </summary>
public static class GameCode
{
    /// <summary>Bumped with <c>GAME_CODE_FORMAT</c> in the protocol, never on its own.</summary>
    /// <remarks>
    /// Format 2 added <c>at</c> on statements: where a statement is, so a mod can ask for its own GML
    /// to run there. Emitted for the assignments and calls the block simulator produces, which are
    /// the statements that have an instruction of their own to detour. An <c>if</c> or a loop is
    /// assembled from control flow and has none, so it carries no token -- which is a thing the
    /// format allows and a mod is told about when it tries to point at one.
    ///
    /// Format 3 added <c>events</c> and <c>sounds</c>. Both are emitted here, and <c>events</c> is
    /// empty: this backend addresses a statement by the instruction it moves (see
    /// <see cref="Detour"/>), and an object event's code entry is not something it can move a
    /// mod's call into yet. An empty list is what the format says to emit when a backend cannot
    /// address those statements, and a mod that finds nothing there says so rather than concluding
    /// the game's objects do nothing. <c>sounds</c> is real, and every sound in it is
    /// non-exclusive: GameMaker Studio mixes as many sounds as a game asks for, so the single
    /// multimedia slot <c>exclusive</c> describes is a GameMaker 8 thing and nothing here holds it.
    ///
    /// Format 4 added <c>rooms</c>: the code a room runs when it loads. Empty here for the same
    /// reason <c>events</c> is -- a room's and an instance's creation code are compiled into code
    /// entries this backend does not walk, and nothing it can detour into. A mod whose job is room
    /// code will find none and should say so.
    /// </remarks>
    public const int Format = 4;

    /// <summary>The protocol's event names, by GameMaker's event type and subtype.</summary>
    private static readonly (string Name, EventType Type, uint Subtype)[] Events =
    [
        ("create", EventType.Create, 0),
        ("destroy", EventType.Destroy, 0),
        ("step", EventType.Step, 0),
        ("beginStep", EventType.Step, 1),
        ("endStep", EventType.Step, 2),
        ("draw", EventType.Draw, 0),
    ];

    /// <summary>The game's code in the protocol's <c>GameCode</c> shape, as JSON.</summary>
    public static string Extract(UndertaleData data)
    {
        // No macro resolution while summarizing, so every build resolves assets the same way -- by
        // ArgumentAssets below -- rather than natively by name and in wasm by number.
        var registry = data.GameSpecificRegistry;
        data.GameSpecificRegistry = new();
        try
        {
            return ExtractUnresolved(data);
        }
        finally
        {
            data.GameSpecificRegistry = registry;
        }
    }

    private static string ExtractUnresolved(UndertaleData data)
    {
        var context = new GlobalDecompileContext(data);
        var builtins = data.BuiltinList ?? new BuiltinList(data);
        var mapper = new Mapper(data, context, builtins);

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("format", Format);

            json.WriteStartArray("scripts");
            foreach (var script in data.Scripts)
            {
                // A GameMaker 2.3+ function lives inside another script's code as a child entry; it
                // has no body of its own to summarize, and the parent's summary already covers it.
                if (script?.Name?.Content is not string name || script.Code is null || script.Code.ParentEntry is not null)
                {
                    continue;
                }

                json.WriteStartObject();
                json.WriteString("name", name);
                json.WritePropertyName("body");
                mapper.Code(json, script.Code, addressableAs: name);
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteStartArray("objects");
            foreach (var obj in data.GameObjects)
            {
                if (obj?.Name?.Content is not string name)
                {
                    continue;
                }

                json.WriteStartObject();
                json.WriteString("name", name);
                json.WriteStartObject("events");
                foreach (var (eventName, type, subtype) in Events)
                {
                    if ((int)type >= obj.Events.Count)
                    {
                        continue;
                    }

                    var ev = obj.Events[(int)type].FirstOrDefault(e => e.EventSubtype == subtype);
                    if (ev is null)
                    {
                        continue;
                    }

                    json.WritePropertyName(eventName);
                    json.WriteStartArray();
                    foreach (var action in ev.Actions)
                    {
                        if (action.CodeId is UndertaleCode code)
                        {
                            mapper.CodeStatements(json, code);
                        }
                    }
                    json.WriteEndArray();
                }
                json.WriteEndObject();
                json.WriteEndObject();
            }
            json.WriteEndArray();

            // Addressable object events: none here, see Format's remarks.
            json.WriteStartArray("events");
            json.WriteEndArray();

            json.WriteStartArray("sounds");
            foreach (var sound in data.Sounds)
            {
                if (sound?.Name?.Content is not string soundName)
                {
                    continue;
                }

                json.WriteStartObject();
                json.WriteString("name", soundName);
                json.WriteBoolean("exclusive", false);
                json.WriteEndObject();
            }
            json.WriteEndArray();

            // Addressable room and instance creation code: none here, see Format's remarks.
            json.WriteStartArray("rooms");
            json.WriteEndArray();

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Which arguments of GameMaker's own functions take an asset, by position: argument 2 of
    /// <c>instance_create</c> is an object. What turns <c>instance_create(0, 0, 159)</c> back into
    /// <c>instance_create(0, 0, objPlayer)</c>.
    ///
    /// Read from the decompiler's own signature file, embedded, with <see cref="JsonDocument"/>. Not
    /// through the decompiler's macro resolver: that loads the file from beside the app (absent in
    /// the browser and under Node) and deserializes it by reflection (disabled in the trimmed build).
    /// The resolver is switched off while summarizing instead, so a native build and the wasm one
    /// produce the same summary.
    /// </summary>
    private static readonly Lazy<Dictionary<string, UAssetType?[]>> ArgumentAssets = new(() =>
    {
        var table = new Dictionary<string, UAssetType?[]>();
        using var stream = typeof(GameCode).Assembly.GetManifestResourceStream("GmsLib.gamemaker.json");
        if (stream is null)
        {
            return table;
        }

        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        if (!document.RootElement.TryGetProperty("GlobalNames", out var names)
            || !names.TryGetProperty("FunctionArguments", out var functions))
        {
            return table;
        }

        foreach (var function in functions.EnumerateObject())
        {
            // A union lists several signatures; the first is the common one.
            var signature = function.Value;
            if (signature.ValueKind == JsonValueKind.Object
                && signature.TryGetProperty("Macros", out var macros)
                && macros.ValueKind == JsonValueKind.Array
                && macros.GetArrayLength() > 0)
            {
                signature = macros[0];
            }
            if (signature.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            table[function.Name] = signature.EnumerateArray()
                .Select(argument => argument.ValueKind == JsonValueKind.String ? AssetTypeNamed(argument.GetString()!) : null)
                .ToArray();
        }
        return table;
    });

    private static UAssetType? AssetTypeNamed(string macro) => macro switch
    {
        "Asset.Object" => UAssetType.Object,
        "Asset.Sprite" => UAssetType.Sprite,
        "Asset.Sound" => UAssetType.Sound,
        "Asset.Room" => UAssetType.Room,
        "Asset.Script" => UAssetType.Script,
        "Asset.Background" or "Asset.Tileset" => UAssetType.Background,
        "Asset.Path" => UAssetType.Path,
        "Asset.Font" => UAssetType.Font,
        "Asset.Timeline" => UAssetType.Timeline,
        _ => null,
    };

    private sealed class Mapper(UndertaleData data, GlobalDecompileContext context, BuiltinList builtins)
    {
        private readonly DecompileSettings settings = new();

        /// <summary>
        /// The code entry whose statements may be addressed, and where each of its instructions is.
        ///
        /// Set only for scripts: an object's event has no way to be inserted into yet, and a token
        /// naming one would promise something the installer cannot do. Null elsewhere, which is what
        /// makes <see cref="At"/> write nothing.
        /// </summary>
        private string? entry;
        private Dictionary<IGMInstruction, uint>? addresses;
        private DecompileContext? decompiled;

        /// <summary>One code entry's statements as a JSON array.</summary>
        public void Code(Utf8JsonWriter json, UndertaleCode code, string? addressableAs = null)
        {
            entry = addressableAs;
            addresses = addressableAs is null ? null : AddressesOf(code);
            json.WriteStartArray();
            CodeStatements(json, code);
            json.WriteEndArray();
            entry = null;
            addresses = null;
            decompiled = null;
        }

        /// <summary>
        /// Byte address of every instruction, by identity.
        ///
        /// Identity works because the decompiler hands back the very instruction objects this list
        /// holds -- <c>UndertaleInstruction</c> is what implements <c>IGMInstruction</c> -- so a
        /// statement's first and last instruction can be looked up here without the decompiler ever
        /// having to carry an address.
        /// </summary>
        private static Dictionary<IGMInstruction, uint> AddressesOf(UndertaleCode code)
        {
            var result = new Dictionary<IGMInstruction, uint>(code.Instructions.Count);
            uint at = 0;
            foreach (var instruction in code.Instructions)
            {
                result[instruction] = at;
                at += instruction.CalculateInstructionSize() * 4;
            }

            return result;
        }

        /// <summary>
        /// Writes the <c>at</c> token for one statement, when it has one.
        ///
        /// <c>&lt;script&gt;#&lt;before&gt;,&lt;after&gt;,&lt;afterFirst&gt;</c>, where the two
        /// addresses are the instruction to detour for each place and <c>afterFirst</c> says which
        /// side of it the call goes. See <c>Detour</c>, which reads this back.
        ///
        /// Written only for the statements the block simulator emitted, which is assignments and
        /// calls. An <c>if</c> or a loop is assembled from control flow and has no instruction of its
        /// own to move; the parts of a <c>for</c> header and the temporaries the compiler invents are
        /// not statements of this summary at all. All of those simply get no token, which a mod is
        /// told about when it tries to point at one.
        /// </summary>
        private void At(Utf8JsonWriter json, IStatementNode node)
        {
            if (entry is null || addresses is null || decompiled is null)
            {
                return;
            }

            if (!decompiled.StatementInstructions.TryGetValue(node, out var span))
            {
                return;
            }

            if (!addresses.TryGetValue(span.First, out var first)
                || !addresses.TryGetValue(span.Last, out var last))
            {
                return;
            }

            // "after" moves the statement's own last instruction and runs the call behind it, so the
            // call goes second -- afterFirst is 0.
            json.WriteString("at", $"{entry}#{first},{last},0");
        }

        /// <summary>
        /// One code entry's statements, written into an array the caller has open. Code the
        /// decompiler cannot handle is one <c>unknown</c>: the game still runs it, so a mod should
        /// see that something is there.
        /// </summary>
        public void CodeStatements(Utf8JsonWriter json, UndertaleCode code)
        {
            IStatementNode root;
            try
            {
                var one = new DecompileContext(context, code, settings);
                root = one.DecompileToAST();
                decompiled = one;
            }
            catch (Exception)
            {
                Unknown(json);
                return;
            }

            Statement(json, root);
        }

        private static void Unknown(Utf8JsonWriter json)
        {
            json.WriteStartObject();
            json.WriteString("k", "unknown");
            json.WriteEndObject();
        }

        private void Block(Utf8JsonWriter json, string property, IStatementNode? node)
        {
            json.WriteStartArray(property);
            if (node is not null)
            {
                Statement(json, node);
            }
            json.WriteEndArray();
        }

        /// <summary>One statement, written into an open array. A block flattens into its statements.</summary>
        private void Statement(Utf8JsonWriter json, IStatementNode node)
        {
            switch (node)
            {
                case BlockNode block:
                    foreach (var child in block.Children)
                    {
                        Statement(json, child);
                    }
                    break;

                case AssignNode assign:
                    json.WriteStartObject();
                    json.WriteString("k", "assign");
                    At(json, node);
                    json.WritePropertyName("target");
                    Expression(json, assign.Variable);
                    switch (assign.AssignKind)
                    {
                        case AssignNode.AssignType.Normal:
                            json.WriteString("op", "=");
                            json.WritePropertyName("value");
                            Expression(json, assign.Value);
                            break;
                        case AssignNode.AssignType.Compound:
                            json.WriteString("op", assign.BinaryInstruction?.Kind switch
                            {
                                Opcode.Add => "+=",
                                Opcode.Subtract => "-=",
                                Opcode.Multiply => "*=",
                                Opcode.Divide => "/=",
                                _ => "other",
                            });
                            json.WritePropertyName("value");
                            Expression(json, assign.Value);
                            break;
                        default:
                            // `x++` and `--x` are `x += 1` and `x -= 1` as far as anyone reading is concerned.
                            json.WriteString("op", assign.BinaryInstruction?.Kind == Opcode.Add ? "+=" : "-=");
                            json.WritePropertyName("value");
                            json.WriteStartObject();
                            json.WriteString("k", "number");
                            json.WriteNumber("value", 1);
                            json.WriteEndObject();
                            break;
                    }
                    json.WriteEndObject();
                    break;

                case FunctionCallNode call:
                    json.WriteStartObject();
                    json.WriteString("k", "call");
                    At(json, node);
                    json.WritePropertyName("call");
                    Expression(json, call);
                    json.WriteEndObject();
                    break;

                case IfNode ifNode:
                    json.WriteStartObject();
                    json.WriteString("k", "if");
                    json.WritePropertyName("cond");
                    Expression(json, ifNode.Condition);
                    Block(json, "then", ifNode.TrueBlock);
                    Block(json, "else", ifNode.ElseBlock);
                    json.WriteEndObject();
                    break;

                case WithLoopNode with:
                    json.WriteStartObject();
                    json.WriteString("k", "with");
                    json.WritePropertyName("target");
                    // `with (3)` runs as object 3, as `with (objPlayer)` was written.
                    if (NonNegativeInteger(with.Target) is int withObject)
                    {
                        Asset(json, UAssetType.Object, withObject);
                    }
                    else
                    {
                        Expression(json, with.Target);
                    }
                    Block(json, "body", with.Body);
                    json.WriteEndObject();
                    break;

                case ForLoopNode forLoop:
                    // The initialiser runs once, before the loop; the incrementor runs inside it.
                    if (forLoop.Initializer is not null)
                    {
                        Statement(json, forLoop.Initializer);
                    }
                    json.WriteStartObject();
                    json.WriteString("k", "loop");
                    json.WriteStartArray("body");
                    Statement(json, forLoop.Body);
                    if (forLoop.Incrementor is not null)
                    {
                        Statement(json, forLoop.Incrementor);
                    }
                    json.WriteEndArray();
                    json.WriteEndObject();
                    break;

                case WhileLoopNode whileLoop:
                    Loop(json, whileLoop.Body);
                    break;
                case RepeatLoopNode repeat:
                    Loop(json, repeat.Body);
                    break;
                case DoUntilLoopNode doUntil:
                    Loop(json, doUntil.Body);
                    break;

                case SwitchNode switchNode:
                    json.WriteStartObject();
                    json.WriteString("k", "switch");
                    json.WritePropertyName("value");
                    Expression(json, switchNode.Expression);
                    json.WriteStartArray("bodies");
                    var open = false;
                    foreach (var child in switchNode.Body.Children)
                    {
                        if (child is SwitchCaseNode)
                        {
                            if (open)
                            {
                                json.WriteEndArray();
                            }
                            json.WriteStartArray();
                            open = true;
                            continue;
                        }
                        if (!open)
                        {
                            json.WriteStartArray();
                            open = true;
                        }
                        Statement(json, child);
                    }
                    if (open)
                    {
                        json.WriteEndArray();
                    }
                    json.WriteEndArray();
                    json.WriteEndObject();
                    break;

                case ReturnNode returnNode:
                    json.WriteStartObject();
                    json.WriteString("k", "exit");
                    json.WritePropertyName("value");
                    Expression(json, returnNode.Value);
                    json.WriteEndObject();
                    break;
                case ExitNode:
                    json.WriteStartObject();
                    json.WriteString("k", "exit");
                    json.WriteNull("value");
                    json.WriteEndObject();
                    break;

                // Declarations only say a name is local; the variable nodes themselves already know.
                case LocalVarDeclNode:
                case BlockLocalVarDeclNode:
                    break;

                default:
                    Unknown(json);
                    break;
            }
        }

        private void Loop(Utf8JsonWriter json, BlockNode body)
        {
            json.WriteStartObject();
            json.WriteString("k", "loop");
            Block(json, "body", body);
            json.WriteEndObject();
        }

        private void Constant(Utf8JsonWriter json, string name)
        {
            // The decompiler resolves some numbers to names on its own -- `with (objPlayer)` arrives
            // as a macro. A name that is one of the game's assets is an asset, as in GameMaker 8.
            if (AssetKindByName(name) is string kind)
            {
                json.WriteStartObject();
                json.WriteString("k", "asset");
                json.WriteString("kind", kind);
                json.WriteString("name", name);
                json.WriteEndObject();
                return;
            }
            json.WriteStartObject();
            json.WriteString("k", "constant");
            json.WriteString("name", name);
            json.WriteEndObject();
        }

        private static void Number(Utf8JsonWriter json, double value)
        {
            json.WriteStartObject();
            json.WriteString("k", "number");
            json.WriteNumber("value", value);
            json.WriteEndObject();
        }

        private void Var(Utf8JsonWriter json, string scope, string name)
        {
            json.WriteStartObject();
            json.WriteString("k", "var");
            json.WriteString("scope", scope);
            json.WriteString("name", name);
            json.WriteEndObject();
        }

        private Dictionary<string, string>? assetKinds;

        private string? AssetKindByName(string name)
        {
            if (assetKinds is null)
            {
                assetKinds = [];
                void Add<T>(IEnumerable<T> assets, string kind) where T : UndertaleNamedResource
                {
                    foreach (var asset in assets)
                    {
                        if (asset?.Name?.Content is string assetName)
                        {
                            assetKinds.TryAdd(assetName, kind);
                        }
                    }
                }
                Add(data.GameObjects, "object");
                Add(data.Scripts, "script");
                Add(data.Sprites, "sprite");
                Add(data.Rooms, "room");
                Add(data.Sounds, "sound");
                Add(data.Backgrounds, "other");
                Add(data.Paths, "other");
                Add(data.Fonts, "other");
                Add(data.Timelines, "other");
            }
            return assetKinds.TryGetValue(name, out var kind) ? kind : null;
        }

        private bool IsBuiltinVariable(string name) =>
            builtins.InstanceVars.ContainsKey(name)
            || builtins.InstanceLimitedVars.ContainsKey(name)
            || builtins.GlobalVars.ContainsKey(name)
            || builtins.GlobalArrayVars.ContainsKey(name);

        private static string? AssetKind(UAssetType type) => type switch
        {
            UAssetType.Object => "object",
            UAssetType.Script => "script",
            UAssetType.Sprite => "sprite",
            UAssetType.Room => "room",
            UAssetType.Sound => "sound",
            _ => "other",
        };

        private void Asset(Utf8JsonWriter json, UAssetType type, int index)
        {
            var name = context.GetAssetName(type, index);
            if (name is null)
            {
                Number(json, index);
                return;
            }
            json.WriteStartObject();
            json.WriteString("k", "asset");
            json.WriteString("kind", AssetKind(type));
            json.WriteString("name", name);
            json.WriteEndObject();
        }

        private void Expression(Utf8JsonWriter json, IExpressionNode? node)
        {
            switch (node)
            {
                case Int16Node n:
                    Number(json, n.Value);
                    break;
                case Int32Node n:
                    Number(json, n.Value);
                    break;
                case Int64Node n:
                    Number(json, n.Value);
                    break;
                case DoubleNode n:
                    Number(json, n.Value);
                    break;
                case BooleanNode b:
                    Constant(json, b.Value ? "true" : "false");
                    break;
                case PredefinedDoubleSingleNode p:
                    Constant(json, p.Value);
                    break;
                case MacroValueNode m:
                    Constant(json, m.ValueName);
                    break;
                case StringNode s:
                    json.WriteStartObject();
                    json.WriteString("k", "string");
                    json.WriteString("value", s.Value.Content);
                    json.WriteEndObject();
                    break;
                case AssetReferenceNode asset:
                    Asset(json, asset.AssetType, asset.AssetId);
                    break;
                case InstanceTypeNode inst when (int)inst.InstanceType >= 0:
                    Asset(json, UAssetType.Object, (int)inst.InstanceType);
                    break;
                case InstanceTypeNode inst:
                    Constant(json, inst.InstanceType switch
                    {
                        InstanceType.Other => "other",
                        InstanceType.All => "all",
                        InstanceType.Noone => "noone",
                        InstanceType.Global => "global",
                        _ => "self",
                    });
                    break;
                case VariableNode variable:
                    Variable(json, variable);
                    break;
                case FunctionCallNode call:
                    json.WriteStartObject();
                    json.WriteString("k", "call");
                    // No `at` here: this is a call used as an *expression*, and tokens go on
                    // statements only. Two identical expressions have to compare equal -- mods key
                    // on that -- and there is nothing smaller than a statement to insert around.
                    json.WriteString("kind", data.Scripts.ByName(call.FunctionName) is not null ? "script" : "builtin");
                    json.WriteString("name", call.FunctionName);
                    json.WriteStartArray("args");
                    ArgumentAssets.Value.TryGetValue(call.FunctionName, out var assetArguments);
                    for (var i = 0; i < call.Arguments.Count; i++)
                    {
                        var assetType = assetArguments is not null && i < assetArguments.Length ? assetArguments[i] : null;
                        if (assetType is UAssetType type && NonNegativeInteger(call.Arguments[i]) is int index)
                        {
                            Asset(json, type, index);
                        }
                        else
                        {
                            Expression(json, call.Arguments[i]);
                        }
                    }
                    json.WriteEndArray();
                    json.WriteEndObject();
                    break;
                case BinaryNode binary:
                    var op = binary.Instruction switch
                    {
                        { Kind: Opcode.Add } => "+",
                        { Kind: Opcode.Subtract } => "-",
                        { Kind: Opcode.Multiply } => "*",
                        { Kind: Opcode.Divide } => "/",
                        { Kind: Opcode.GMLDivRemainder } => "div",
                        { Kind: Opcode.GMLModulo } => "mod",
                        { Kind: Opcode.And, Type1: DataType.Boolean, Type2: DataType.Boolean } => "&&",
                        { Kind: Opcode.And } => "&",
                        { Kind: Opcode.Or, Type1: DataType.Boolean, Type2: DataType.Boolean } => "||",
                        { Kind: Opcode.Or } => "|",
                        { Kind: Opcode.Xor, Type1: DataType.Boolean, Type2: DataType.Boolean } => "^^",
                        { Kind: Opcode.Xor } => "^",
                        { Kind: Opcode.ShiftLeft } => "<<",
                        { Kind: Opcode.ShiftRight } => ">>",
                        { Kind: Opcode.Compare, ComparisonKind: ComparisonType.LesserThan } => "<",
                        { Kind: Opcode.Compare, ComparisonKind: ComparisonType.LesserEqualThan } => "<=",
                        { Kind: Opcode.Compare, ComparisonKind: ComparisonType.EqualTo } => "==",
                        { Kind: Opcode.Compare, ComparisonKind: ComparisonType.NotEqualTo } => "!=",
                        { Kind: Opcode.Compare, ComparisonKind: ComparisonType.GreaterEqualThan } => ">=",
                        { Kind: Opcode.Compare, ComparisonKind: ComparisonType.GreaterThan } => ">",
                        _ => null,
                    };
                    if (op is null)
                    {
                        Unknown(json);
                        break;
                    }
                    Operator(json, op, binary.Left, binary.Right);
                    break;
                case UnaryNode unary:
                    var unaryOp = unary.Instruction switch
                    {
                        { Kind: Opcode.Negate } => "-",
                        { Kind: Opcode.Not, Type1: DataType.Boolean } => "!",
                        { Kind: Opcode.Not } => "~",
                        _ => null,
                    };
                    if (unaryOp is null)
                    {
                        Unknown(json);
                        break;
                    }
                    Operator(json, unaryOp, unary.Value);
                    break;
                case ShortCircuitNode shortCircuit:
                    ShortCircuit(json, shortCircuit.LogicType == ShortCircuitType.And ? "&&" : "||", shortCircuit.Conditions, 0);
                    break;
                default:
                    Unknown(json);
                    break;
            }
        }

        private void Operator(Utf8JsonWriter json, string op, params IExpressionNode[] args)
        {
            json.WriteStartObject();
            json.WriteString("k", "op");
            json.WriteString("op", op);
            json.WriteStartArray("args");
            foreach (var arg in args)
            {
                Expression(json, arg);
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }

        /// <summary>`a && b && c` as nested binary operators, which is what GameMaker 8 parses it to.</summary>
        private void ShortCircuit(Utf8JsonWriter json, string op, List<IExpressionNode> conditions, int from)
        {
            if (from == conditions.Count - 1)
            {
                Expression(json, conditions[from]);
                return;
            }
            json.WriteStartObject();
            json.WriteString("k", "op");
            json.WriteString("op", op);
            json.WriteStartArray("args");
            Expression(json, conditions[from]);
            ShortCircuit(json, op, conditions, from + 1);
            json.WriteEndArray();
            json.WriteEndObject();
        }

        private static int? NonNegativeInteger(IExpressionNode node) => node switch
        {
            Int16Node { Value: >= 0 } n => n.Value,
            Int32Node { Value: >= 0 } n => n.Value,
            InstanceTypeNode inst when (int)inst.InstanceType >= 0 => (int)inst.InstanceType,
            _ => null,
        };

        private static int? ArgumentIndex(string name)
        {
            if (!name.StartsWith("argument", StringComparison.Ordinal) || name.Length is < 9 or > 10)
            {
                return null;
            }
            return int.TryParse(name.AsSpan(8), out var index) && index is >= 0 and <= 15 ? index : null;
        }

        private void Variable(Utf8JsonWriter json, VariableNode variable)
        {
            var name = variable.Variable.Name.Content;
            var left = variable.Left;
            var instanceType = left switch
            {
                // A non-negative "instance type" is an object index: `objPlayer.x` for object 0.
                InstanceTypeNode inst when (int)inst.InstanceType >= 0 => (int?)null,
                InstanceTypeNode inst => (int)inst.InstanceType,
                Int16Node i16 when i16.Value < 0 && variable.ReferenceType != VariableType.Instance => i16.Value,
                _ => (int?)null,
            };
            var onSelf = instanceType is (int)InstanceType.Self or (int)InstanceType.Builtin or (int)InstanceType.Argument;

            // `argument0` and `argument[0]` are the same thing to anyone reading.
            if (onSelf && ArgumentIndex(name) is int argIndex && variable.ArrayIndices is null)
            {
                json.WriteStartObject();
                json.WriteString("k", "arg");
                json.WriteNumber("index", argIndex);
                json.WriteEndObject();
                return;
            }
            if (onSelf && name == "argument" && variable.ArrayIndices is { Count: 1 } argumentIndices)
            {
                json.WriteStartObject();
                json.WriteString("k", "arg");
                if (argumentIndices[0] is Int16Node { Value: >= 0 } constantIndex)
                {
                    json.WriteNumber("index", constantIndex.Value);
                }
                else
                {
                    json.WriteNull("index");
                }
                json.WriteEndObject();
                return;
            }

            var indexed = variable.ArrayIndices is { Count: > 0 };
            if (indexed)
            {
                json.WriteStartObject();
                json.WriteString("k", "index");
                json.WritePropertyName("of");
            }

            switch (instanceType)
            {
                case (int)InstanceType.Local:
                    Var(json, "local", name);
                    break;
                case (int)InstanceType.Global:
                    Var(json, "global", name);
                    break;
                case (int)InstanceType.Self:
                case (int)InstanceType.Builtin:
                case (int)InstanceType.Argument:
                case (int)InstanceType.StackTop:
                    Var(json, IsBuiltinVariable(name) ? "builtin" : "self", name);
                    break;
                case int special:
                    // `other.x`, `all.x`, `noone.x`: a field of that instance constant.
                    json.WriteStartObject();
                    json.WriteString("k", "field");
                    json.WritePropertyName("of");
                    Constant(json, special switch
                    {
                        (int)InstanceType.Other => "other",
                        (int)InstanceType.All => "all",
                        (int)InstanceType.Noone => "noone",
                        _ => "self",
                    });
                    json.WriteString("name", name);
                    json.WriteEndObject();
                    break;
                default:
                    json.WriteStartObject();
                    json.WriteString("k", "field");
                    json.WritePropertyName("of");
                    if (left is InstanceTypeNode objectType)
                    {
                        Asset(json, UAssetType.Object, (int)objectType.InstanceType);
                    }
                    else if (left is Int16Node objectIndex && variable.ReferenceType != VariableType.Instance)
                    {
                        // The decompiler prints a non-negative number here as the object it indexes,
                        // and so does this: in source it was almost always `objPlayer.x`.
                        Asset(json, UAssetType.Object, objectIndex.Value);
                    }
                    else if (left is Int16Node roomInstance)
                    {
                        Number(json, (ushort)roomInstance.Value + 100000);
                    }
                    else
                    {
                        Expression(json, left);
                    }
                    json.WriteString("name", name);
                    json.WriteEndObject();
                    break;
            }

            if (indexed)
            {
                json.WriteStartArray("at");
                foreach (var index in variable.ArrayIndices!)
                {
                    Expression(json, index);
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
        }
    }
}
