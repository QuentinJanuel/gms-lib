using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Models;

namespace GmsLib;

/// <summary>
/// Installs a mod's resources into a parsed <see cref="UndertaleData"/>: scripts, insertions,
/// hooks and objects, plus the message channel when the game is going to a real GameMaker runner.
///
/// A pure function of its inputs. The emulator calls it between parsing a game and converting it,
/// and a patcher calls it between a read and a write, so a mod installs identically either way.
/// </summary>
public static class ModInstaller
{
    /// <summary>One event's worth of GML on a mod object.</summary>
    /// <param name="EventName">
    /// GameMaker's own event name as it appears in a code entry name -- "Create", "Step", "Draw".
    /// See <see cref="EventFor"/> for the mod contract's names.
    /// </param>
    public sealed record Event(string EventName, uint Subtype, string Source);

    /// <summary>
    /// A mod event's name, as the mod contract spells it ("create", "beginStep", ...), as GameMaker
    /// Studio's code entry name and subtype. Naming the entry <c>gml_Object_&lt;object&gt;_&lt;Event&gt;_&lt;subtype&gt;</c>
    /// is what installs the event. GameMaker 8 numbers these differently, which is why the contract
    /// uses names.
    /// </summary>
    public static (string EventName, uint Subtype)? EventFor(string name) => name switch
    {
        "create" => ("Create", 0),
        "destroy" => ("Destroy", 0),
        "step" => ("Step", 0),
        "beginStep" => ("Step", 1),
        "endStep" => ("Step", 2),
        "draw" => ("Draw", 0),
        _ => null,
    };

    /// <summary>One object a mod wants installed. Field names match mod-protocol's ObjectResource.</summary>
    public sealed record Object(string Name, int Depth, bool Persistent, bool Visible, List<Event> Events);

    /// <summary>One script a mod wants installed. Arguments arrive as <c>argument0</c>, ...</summary>
    public sealed record Script(string Name, string Source);

    /// <summary>One hook a mod wants installed. Field names match mod-protocol's HookResource.</summary>
    public sealed record Hook(string Script, string Original, string Source);

    /// <summary>
    /// One insertion a mod wants installed. Field names match mod-protocol's InsertResource.
    /// </summary>
    /// <param name="At">
    /// The statement to run around, as <c>&lt;script&gt;#&lt;before&gt;,&lt;after&gt;,&lt;afterFirst&gt;</c>:
    /// the instruction to detour for each place, and which side of it the call goes. Written by
    /// <c>GameCode</c>, read back by <c>Detour</c>, and opaque everywhere in between.
    /// </param>
    /// <param name="Place">"before" or "after".</param>
    /// <param name="Call">The mod script to call there. Declared already, by DeclareScripts.</param>
    public sealed record Insert(string At, string Place, string Call);

    /// <summary>Everything one install puts into a game, in the order the lists are installed.</summary>
    public sealed record Resources(
        IReadOnlyList<Script> Scripts,
        IReadOnlyList<Insert> Inserts,
        IReadOnlyList<Hook> Hooks,
        IReadOnlyList<Object> Objects);

    /// <summary>
    /// Installs every staged object into <paramref name="data"/> and places one instance of each in
    /// the game's first room. A no-op when no mods are enabled, so an unmodded load takes exactly
    /// the path it always did.
    /// </summary>
    /// <summary>
    /// The channel, as GML, for when it has to live *inside* the game -- see
    /// <see cref="Install"/>.
    ///
    /// A real GameMaker runner has no <c>host_send</c>, so the whole channel is built out of stock
    /// GML: a TCP socket to the host on loopback, opened by a controller object the loader installs
    /// alongside the mods' own objects. Messages are NUL-terminated, which is what lets one read
    /// recover several messages that TCP happened to coalesce.
    ///
    /// The port is passed on the **command line** rather than written to a file next to the game.
    /// A file there would be indistinguishable from the player's save data.
    /// </summary>
    /// <summary>The loader's own controller object: it owns the socket, so mods never see one.</summary>
    private const string ChannelObjectName = "mod_channel";

    /// <summary>The launch argument carrying the host's port. See <see cref="ModChannelCreate"/>.</summary>
    public const string ChannelPortPrefix = "--emu-mod-port=";

    /// <summary>The launch argument carrying the game id, so one server can serve every install.</summary>
    public const string ChannelGamePrefix = "--emu-mod-game=";

    // **No `var` anywhere below, and that is not style.** These code entries are created by the
    // importer, which registers no CodeLocals for them, so a `local.x` the runner cannot resolve
    // fails as "trying to index variable that is not an array" -- inside someone else's game, at
    // runtime. Globals need no such table. They are safe here because GML is single-threaded and
    // none of this recurses.
    private const string ModChannelCreate = """
        global.__mod_inbox = ds_list_create();
        global.__mod_socket = -1;
        global.__mod_port = -1;
        global.__mod_id = "";
        for (global.__mod_i = 0; global.__mod_i < parameter_count(); global.__mod_i += 1) {
            global.__mod_arg = parameter_string(global.__mod_i);
            if (string_pos("--emu-mod-port=", global.__mod_arg) == 1) {
                global.__mod_port = real(string_delete(global.__mod_arg, 1, string_length("--emu-mod-port=")));
            }
            if (string_pos("--emu-mod-game=", global.__mod_arg) == 1) {
                global.__mod_id = string_delete(global.__mod_arg, 1, string_length("--emu-mod-game="));
            }
        }
        if (global.__mod_port > 0) {
            global.__mod_socket = network_create_socket(network_socket_tcp);
            if (network_connect_raw(global.__mod_socket, "127.0.0.1", global.__mod_port) < 0) {
                network_destroy(global.__mod_socket);
                global.__mod_socket = -1;
            } else {
                // One server serves every install, so the first thing down the wire says which game
                // this is. Sent through host_send so there is one place that frames a message.
                host_send("hello:" + global.__mod_id);
            }
        }
        """;

    // Reads every NUL-terminated string the packet carried, not just the first: TCP is a stream, and
    // two sends from the host arrive as one read whenever they are close enough together.
    private const string ModChannelAsync = """
        if (ds_map_find_value(async_load, "type") == network_type_data) {
            global.__mod_rxbuf = ds_map_find_value(async_load, "buffer");
            global.__mod_rxsize = ds_map_find_value(async_load, "size");
            buffer_seek(global.__mod_rxbuf, buffer_seek_start, 0);
            while (buffer_tell(global.__mod_rxbuf) < global.__mod_rxsize) {
                ds_list_add(global.__mod_inbox, buffer_read(global.__mod_rxbuf, buffer_string));
            }
        }
        """;

    private const string ModChannelCleanUp = """
        if (global.__mod_socket >= 0) {
            network_destroy(global.__mod_socket);
            global.__mod_socket = -1;
        }
        """;

    private const string ModSendScript = """
        // No existence guard on these globals, deliberately. The obvious one is not callable in a
        // GameMaker Studio 1.4 runner -- it takes the process down rather than raising a GML error,
        // which reads as a crash in whatever code happened to call host_send. It is also unnecessary:
        // the channel object is placed first in the first room, so its Create event has defined
        // these globals before any mod object exists to call this.
        if (global.__mod_socket >= 0) {
            global.__mod_txbuf = buffer_create(string_byte_length(argument0) + 1, buffer_grow, 1);
            buffer_write(global.__mod_txbuf, buffer_string, argument0);
            network_send_raw(global.__mod_socket, global.__mod_txbuf, buffer_tell(global.__mod_txbuf));
            buffer_delete(global.__mod_txbuf);
        }
        """;

    private const string ModReceiveScript = """
        global.__mod_rxmsg = "";
        if (ds_list_size(global.__mod_inbox) > 0) {
            global.__mod_rxmsg = ds_list_find_value(global.__mod_inbox, 0);
            ds_list_delete(global.__mod_inbox, 0);
        }
        return global.__mod_rxmsg;
        """;


    /// <summary>
    /// Puts one instance of <paramref name="obj"/> at the front of the room, in both the flat list
    /// and the instance layer a GameMaker Studio 2 room draws from.
    ///
    /// First, not last, and that is load-bearing: instances get their Create events in list order,
    /// and a fangame's controller -- first in its own room -- routinely wipes the room on the way to
    /// the menu. Appended last, a mod instance is created, marked for destruction by that Create,
    /// and then skipped by every event dispatch afterwards.
    /// </summary>


    /// <summary>
    /// Declares the staged scripts as empty assets, refusing any name that is already taken.
    ///
    /// <b>The refusal is the point.</b> <c>CodeImportGroup</c> finds an existing code entry by name
    /// and replaces its contents, so a mod script named after one of the game's own would overwrite
    /// the game's code -- and since GameMaker resolves a call against scripts before builtins, one
    /// named <c>sprite_add</c> would replace the engine's for the whole game. Neither is something
    /// to find out about while playing.
    ///
    /// Two passes for the same reason the channel's own scripts need two: a script body is compiled
    /// in the import that creates the asset, and until it exists the compiler has no reason to read
    /// the entry as a script, so <c>argument0</c> compiles to <c>self.argument0</c> and the game
    /// dies reading it. The empty first pass creates the assets; the second compiles against them.
    /// </summary>
    private static void DeclareScripts(UndertaleData data, IReadOnlyList<Script> scripts)
    {
        if (scripts.Count == 0)
        {
            return;
        }

        foreach (var script in scripts)
        {
            if (data.Scripts.ByName(script.Name) is not null || data.Code.ByName($"gml_Script_{script.Name}") is not null)
            {
                throw new InvalidOperationException(
                    $"mod install: this game already has a script called {script.Name}, "
                        + "and a mod may not replace one");
            }

            // A script also shadows the *builtin* of the same name, for the whole game and the
            // game's own code with it -- which is how a mod that meant to define a helper called
            // `sprite_add` takes the host game's sprite loading with it.
            if (data.BuiltinList.Functions.ContainsKey(script.Name))
            {
                throw new InvalidOperationException(
                    $"mod install: a script called {script.Name} would shadow the GameMaker "
                        + "function of that name for the whole game");
            }
        }

        var declare = new CodeImportGroup(data);
        foreach (var script in scripts)
        {
            declare.QueueReplace($"gml_Script_{script.Name}", "");
        }
        declare.Import();
    }

    /// <summary>
    /// Installs the staged insertions: each runs one mod script at one statement inside one of the
    /// game's, by detouring a single instruction. See <see cref="Detour"/> for how and why.
    ///
    /// Before the hooks, because an insertion's addresses name the game's original body and a hook
    /// moves that whole entry aside -- taking the detours with it, which is what makes an insertion
    /// still fire when a hook calls the original.
    /// </summary>
    private static void InstallInserts(UndertaleData data, IReadOnlyList<Insert> inserts) =>
        Detour.Install(data, inserts);

    /// <summary>
    /// Compiles the staged scripts' bodies, into the assets <see cref="DeclareScripts"/> made.
    ///
    /// Separate from declaring, and after the hooks, so that every name a mod's GML can call exists
    /// before any of it is compiled: a mod script may call a hook's <c>original</c>, and a hook's
    /// body may call a mod script.
    /// </summary>
    private static void CompileScripts(UndertaleData data, IReadOnlyList<Script> scripts)
    {
        if (scripts.Count == 0)
        {
            return;
        }

        var bodies = new CodeImportGroup(data);
        foreach (var script in scripts)
        {
            bodies.QueueReplace($"gml_Script_{script.Name}", script.Source);
        }
        bodies.Import();
    }

    /// <summary>
    /// Installs the staged hooks, in chain order.
    ///
    /// <b>The original body is moved, never decompiled.</b> Its code entry -- bytecode, locals and
    /// all -- is renamed to the <c>original</c> script, and the hooked script gets a fresh entry
    /// compiled from the hook's source. Recompiling decompiled code is where a faithful game quietly
    /// stops being one, and moving it means that never happens. The script asset keeps its index, so
    /// every call site reaches the new body.
    ///
    /// A second hook on the same script finds the first hook's body there and moves that, which is
    /// what makes hooks chain rather than overwrite each other.
    ///
    /// The hooked script's new entry is declared empty first and compiled second, for the same reason
    /// <see cref="DeclareScripts"/> exists: a script body compiled in the import that creates its
    /// entry reads <c>argument0</c> as an instance variable.
    /// </summary>
    private static void InstallHooks(UndertaleData data, IReadOnlyList<Hook> hooks)
    {
        if (hooks.Count == 0)
        {
            return;
        }

        // GameMaker 2.3 turned scripts into files of global functions whose bodies are child code
        // entries, and moving one of those is a different operation entirely. Refused until a game
        // needs it rather than done approximately.
        if (data.IsVersionAtLeast(2, 3))
        {
            throw new InvalidOperationException(
                "mod install: hooks are not supported on GameMaker 2.3 and later yet");
        }

        foreach (var hook in hooks)
        {
            var script = data.Scripts.ByName(hook.Script)
                ?? throw new InvalidOperationException($"mod install: cannot hook {hook.Script}, this game has no script by that name");
            var code = script.Code
                ?? throw new InvalidOperationException($"mod install: cannot hook {hook.Script}, it has no code");
            if (data.Scripts.ByName(hook.Original) is not null || data.Code.ByName($"gml_Script_{hook.Original}") is not null)
            {
                throw new InvalidOperationException(
                    $"mod install: cannot keep the original of {hook.Script} as {hook.Original}, "
                        + "a script by that name already exists");
            }

            // Move the body: the code entry and its locals table are renamed together, since the
            // runner finds a code entry's locals by name.
            var movedName = data.Strings.MakeString($"gml_Script_{hook.Original}");
            if (data.CodeLocals?.For(code) is UndertaleCodeLocals locals)
            {
                locals.Name = movedName;
            }
            code.Name = movedName;
            data.Scripts.Add(new UndertaleScript { Name = data.Strings.MakeString(hook.Original), Code = code });

            // A fresh entry for the hooked script. The importer attaches it to the existing script
            // asset of that name, which is what keeps the script's index.
            script.Code = null;
            var declare = new CodeImportGroup(data);
            declare.QueueReplace($"gml_Script_{hook.Script}", "");
            declare.Import();

            // Compiled before the next hook runs, not batched with it: a second hook on this script
            // moves *this* body into its own original, so this body has to exist by then.
            var body = new CodeImportGroup(data);
            body.QueueReplace($"gml_Script_{hook.Script}", hook.Source);
            body.Import();
        }
    }

    private static void PlaceFirst(
        UndertaleData data,
        UndertaleRoom room,
        UndertaleGameObject obj,
        ref int insertAt)
    {
        data.GeneralInfo.LastObj++;
        var instance = new UndertaleRoom.GameObject
        {
            X = 0,
            Y = 0,
            ObjectDefinition = obj,
            InstanceID = data.GeneralInfo.LastObj,
        };
        room.GameObjects.Insert(insertAt, instance);
        room.Layers
            ?.FirstOrDefault(layer => layer.LayerType == UndertaleRoom.LayerType.Instances)
            ?.InstancesData.Instances.Insert(insertAt, instance);
        insertAt++;
    }

    /// <summary>
    /// How many event lists this game's objects carry, read off the game rather than assumed.
    ///
    /// **A GameMaker Studio 1.4 object has 12 event types; a GameMaker 2 object has 15.** The
    /// runner reads exactly as many lists as its own version defines, so an object carrying more
    /// leaves the reader mid-object and every field after it is garbage -- which surfaces not as a
    /// parse error but as a page fault at room entry, or a GML error blaming whatever code happens
    /// to run first. UndertaleModLib creates new objects with the GM2 count regardless of the data
    /// it is editing, so a freshly created object has to be cut back down to the game's own shape.
    ///
    /// Taken from an object the game already had rather than from the bytecode version, because it
    /// is the same question asked of the actual answer.
    /// </summary>
    private static int EventListCount(UndertaleData data)
    {
        foreach (var existing in data.GameObjects)
        {
            if (existing.Events.Count > 0)
            {
                return existing.Events.Count;
            }
        }

        return data.GeneralInfo?.BytecodeVersion >= 15 ? Gm2EventListCount : Gm1EventListCount;
    }

    private const int Gm1EventListCount = 12;
    private const int Gm2EventListCount = 15;

    /// <summary>
    /// Cuts a newly created object back to the game's own event-list count.
    ///
    /// Only ever drops empty trailing lists: an event we actually installed landing out of range is
    /// a bug in the caller's event choice, and silently discarding it would turn that into a mod
    /// that loads and then quietly does nothing.
    /// </summary>
    private static void NormalizeEvents(UndertaleGameObject obj, int count)
    {
        while (obj.Events.Count > count)
        {
            var last = obj.Events[^1];
            if (last.Count > 0)
            {
                throw new InvalidOperationException(
                    $"mod install: object {obj.Name?.Content} has an event at index {obj.Events.Count - 1}, "
                        + $"but this game's objects only have {count} event lists");
            }

            obj.Events.RemoveAt(obj.Events.Count - 1);
        }
    }

    /// <summary>
    /// Installs the channel and the mods into <paramref name="data"/>. The game always carries the
    /// channel itself, as GML over a loopback socket, so the same patched data.win runs on a real
    /// runner and on an emulator that answers <c>network_*</c> on loopback.
    /// </summary>
    public static void Install(UndertaleData data, Resources mods)
    {
        if (mods.Objects.Count == 0 && mods.Scripts.Count == 0 && mods.Hooks.Count == 0
            && mods.Inserts.Count == 0)
        {
            return;
        }

        // The first room in play order is where an instance can be placed such that it exists from
        // the very first frame -- not necessarily the first room in the chunk.
        var firstRoom = data.GeneralInfo?.RoomOrder.FirstOrDefault()?.Resource
            ?? throw new InvalidOperationException("mod install: this game has no rooms");

        var insertAt = 0;
        var eventLists = EventListCount(data);

        // Imported *before* the objects below, so the compiler resolves `host_send(...)` in mod
        // GML to these scripts rather than leaving it an unknown name.
        // Two passes, and the split is the difference between working and not. A script's body
        // is compiled in the same import that *creates* the script asset, and until that asset
        // exists the compiler has no reason to think the entry is a script -- so `argument0`
        // compiles to `self.argument0`, an instance variable, and the game dies reading it. An
        // empty first pass creates the assets; the second compiles against them.
        var declare = new CodeImportGroup(data);
        declare.QueueReplace("gml_Script_host_send", "");
        declare.QueueReplace("gml_Script_host_receive", "");
        declare.Import();

        var channel = new CodeImportGroup(data);
        channel.QueueReplace("gml_Script_host_send", ModSendScript);
        channel.QueueReplace("gml_Script_host_receive", ModReceiveScript);
        // 68 is EventSubtypeOther.AsyncNetworking -- how a GameMaker Studio game is handed
        // incoming socket data. It is on the loader's own object rather than the mods', so a mod
        // never has to know the channel is a socket at all.
        channel.QueueReplace($"gml_Object_{ChannelObjectName}_Create_0", ModChannelCreate);
        channel.QueueReplace($"gml_Object_{ChannelObjectName}_Other_68", ModChannelAsync);
        // CleanUp is a GameMaker 2 event and simply does not exist in 1.4, where the closest
        // thing a persistent object gets is Other -> Game End (subtype 3). Same GML either way;
        // only the event it hangs off changes. Closing the socket here is courtesy in any case
        // -- the host treats a dropped connection as the game exiting, which is what a killed
        // process looks like no matter which event was declared.
        channel.QueueReplace(
            eventLists > Gm1EventListCount
                ? $"gml_Object_{ChannelObjectName}_CleanUp_0"
                : $"gml_Object_{ChannelObjectName}_Other_3",
            ModChannelCleanUp);
        channel.Import();

        var channelObject = data.GameObjects.ByName(ChannelObjectName)
            ?? throw new InvalidOperationException("mod install: the channel object was not created");
        channelObject.Persistent = true;
        channelObject.Visible = false;
        channelObject.Depth = 0;
        NormalizeEvents(channelObject, eventLists);
        PlaceFirst(data, firstRoom, channelObject, ref insertAt);

        // Declared, then inserted, then hooked, then compiled: see CompileScripts for why the hooks
        // sit between, and InstallInserts for why the insertions come before them. Insertions need
        // the scripts they call to exist as assets already, which is what declaring does.
        DeclareScripts(data, mods.Scripts);
        InstallInserts(data, mods.Inserts);
        InstallHooks(data, mods.Hooks);
        CompileScripts(data, mods.Scripts);

        foreach (var modObject in mods.Objects)
        {
            // CodeImportGroup parses "gml_Object_<name>_<Event>_<subtype>", creates the object and
            // the code entry if they don't exist, and links the code to that event -- so naming the
            // entry correctly *is* the installation. Everything below is only the properties that
            // name cannot carry.
            var group = new CodeImportGroup(data);
            foreach (var e in modObject.Events)
            {
                group.QueueReplace($"gml_Object_{modObject.Name}_{e.EventName}_{e.Subtype}", e.Source);
            }
            group.Import();

            var created = data.GameObjects.ByName(modObject.Name)
                ?? throw new InvalidOperationException($"mod install: object {modObject.Name} was not created");
            created.Persistent = modObject.Persistent;
            created.Visible = modObject.Visible;
            created.Depth = modObject.Depth;
            NormalizeEvents(created, eventLists);

            // An object asset alone never runs; something has to instantiate it. One instance in the
            // first room, marked persistent, is how GameMaker games have always done a controller
            // object -- and it needs no engine change at all, since by the time the loader sees this
            // data it is indistinguishable from the game's own.
            PlaceFirst(data, firstRoom, created, ref insertAt);
        }
    }
}
