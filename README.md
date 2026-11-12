# gms-lib

Reads, patches and writes GameMaker Studio game files (`data.win`, bytecode
versions 14 to 16): installs mod resources into a game, detours its code, and
decompiles it. `GmsLib.Wasm` exports this to JavaScript as an AOT-compiled .NET
wasm runtime.

Depends on [UndertaleModLib](https://github.com/UnderminersTeam/UndertaleModTool)
and its [Underanalyzer](https://github.com/UnderminersTeam/Underanalyzer), through
the `UndertaleModTool` submodule. It points at forks
([UndertaleModTool](https://github.com/QuentinJanuel/UndertaleModTool),
[Underanalyzer](https://github.com/QuentinJanuel/Underanalyzer), branch
`downstream` in both) carrying a few small changes on top of upstream.

## Build and test

Clone with `--recurse-submodules` (or run `git submodule update --init --recursive`).
Needs the .NET 10 SDK and, for the wasm runtime, the `wasm-tools` workload
(`dotnet workload install wasm-tools`).

```sh
dotnet test GmsLib.Tests/GmsLib.Tests.csproj -p:NuGetAudit=false
npm run build   # AOT publish of GmsLib.Wasm; the runtime lands in dist/_framework
```

GPL-3.0-only. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
