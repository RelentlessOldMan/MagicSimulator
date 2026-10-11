# MagicSim

Magic: The Gathering deck simulator (goldfish, then head-to-head). C# / .NET 8, headless CLI `sim`.

- `CONTRACT.md` is the shared spec with the MagicDecks and Cardinator sessions (deck format, card index, CLI, JSON). Change it there first, then tell them.
- Build/test: `dotnet test`. Run: `dotnet run --project src/MagicSim.Cli -- <command>`.
- Card index: `sim sync-cards` → `%LOCALAPPDATA%\MagicSim\oracle-cards.jsonl.gz` (override with `MAGICSIM_CARDS` / `--cards`).
- Decks live in `C:\Playground\MagicDecks` (read-only from here). Test fixtures are copies in `tests/MagicSim.Tests/Fixtures`.
- Forge card scripts: `sim sync-forge` → sparse clone + `forge-cards.jsonl.gz` in `%LOCALAPPDATA%\MagicSim\forge` (override `MAGICSIM_FORGE` / `--forge`). GPL-3.0: never copy Forge files into this repo; tests use hand-written scripts in Forge's format.
- Engine support registry: `src/MagicSim.Core/Engine/Support.cs`. `sim cards` ranks what to implement next across the whole card pool. Target is ANY deck, never a particular one.
