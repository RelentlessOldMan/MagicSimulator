# MagicSim

Magic: The Gathering deck simulator (goldfish, then head-to-head). C# / .NET 8, headless CLI `sim`.

- `CONTRACT.md` is the shared spec with the MagicDecks and Cardinator sessions (deck format, card index, CLI, JSON). Change it there first, then tell them.
- Build/test: `dotnet test`. Run: `dotnet run --project src/MagicSim.Cli -- <command>`.
- Card index: `sim sync-cards` → `%LOCALAPPDATA%\MagicSim\oracle-cards.jsonl.gz` (override with `MAGICSIM_CARDS` / `--cards`).
- Decks live in `C:\Playground\MagicDecks` (read-only from here). Test fixtures are copies in `tests/MagicSim.Tests/Fixtures`.
- Forge card scripts: `sim sync-forge` → sparse clone + `forge-cards.jsonl.gz` in `%LOCALAPPDATA%\MagicSim\forge` (override `MAGICSIM_FORGE` / `--forge`). GPL-3.0: never copy Forge files into this repo; tests use hand-written scripts in Forge's format.
- Card support: `CardCompiler` (`Engine/CardDef.cs`) compiles a Forge script; anything it doesn't know is reported as missing and the card plays as a blank. Effects live in `Engine/Effects*.cs` (each lists the params it understands), triggers/keywords/conditions in `Engine/Registries.cs`, statics in `Engine/Statics.cs`.
- `sim cards [--top N]` ranks what to implement next across the whole card pool, and separately across the 5000 most-played cards (EDHREC rank). Target is ANY deck, never a particular one.
- Rules tests use hand-written cards in Forge's format: `tests/MagicSim.Tests/EngineTests.cs` and `MechanicsTests.cs` (helpers in `TestGames.cs`). Add a test for every new mechanic.
