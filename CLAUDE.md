# MagicSim

Magic: The Gathering deck simulator (goldfish, then head-to-head). C# / .NET 8, headless CLI `sim`.

- `CONTRACT.md` is the shared spec with the MagicDecks and Cardinator sessions (deck format, card index, CLI, JSON). Change it there first, then tell them.
- Build/test: `dotnet test`. Run: `dotnet run --project src/MagicSim.Cli -- <command>`.
- Card index: `sim sync-cards` → `%LOCALAPPDATA%\MagicSim\oracle-cards.jsonl.gz` (override with `MAGICSIM_CARDS` / `--cards`).
- Decks live in `C:\Playground\MagicDecks` (read-only from here). Test fixtures are copies in `tests/MagicSim.Tests/Fixtures`.
