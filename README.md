# MagicSimulator 🎲

**Goldfish a Magic: The Gathering Commander deck thousands of times to see how fast it really wins, then test card swaps against the same shuffles.**

> Early days: the deck reader, card index and deck check work today. The game engine (`run`, `compare`, `cards`) is being built next, starting with a turn-1 combo deck.

## Why

It started as a one-off question: *how often does my turn-1 combo deck actually win on turn 1, and which card swaps make that more likely?* That's hard to eyeball and easy to simulate. The goal now:

- **Goldfish:** play a deck alone, over and over, and report the win rate by turn, the average and median turn to win, and which win line got there.
- **Compare:** swap cards in and out and run each version on the *same* shuffles, so differences come from the cards, not luck. A paired significance test says whether a difference is real.
- **Head-to-head (later):** pit two decks against each other.
- **Honest numbers:** the play logic never peeks at the library order. A card whose rules aren't coded yet is treated as blank and listed in the report, so results are a lower bound.

It's a headless command-line tool built to be driven by scripts. A deck-tuning loop can shell out to it, try swaps, and keep the ones that help.

## Usage

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet run --project src/MagicSim.Cli -- sync-cards                # download Scryfall card data (~2.6 MB index)
dotnet run --project src/MagicSim.Cli -- check MyDeck.txt          # resolve every name, check the 100-card count
```

Decks are plain text: commanders first, tagged `*CMDR*`, then a blank line, then the rest of the deck:

```
1 Rograkh, Son of Rohgahh *CMDR*
1 Silas Renn, Seeker Adept *CMDR*

1 Demonic Consultation
1 Thassa's Oracle
2 Island
```

Set codes, `1x` counts, comments, and lists copied from Moxfield are accepted too.

**Planned commands:**

```powershell
sim run MyDeck.txt --games 10000 --seed 1 --json out.json
sim compare MyDeck.txt --swap "Mox Opal=>Gemstone Mine"
sim cards --deck MyDeck.txt
```

[CONTRACT.md](CONTRACT.md) has the full spec: deck format, card index, commands, exit codes and JSON output. It's shared with two sibling tools:
- **MagicDecks**: the decklists themselves.
- **[Cardinator](https://github.com/RelentlessOldMan/Cardinator)**: deck building and Moxfield import.

## How it works

- **Card data** comes from Scryfall's oracle bulk download. It's stored as a compact gzip JSONL index in `%LOCALAPPDATA%\MagicSim`; override the path with `MAGICSIM_CARDS` or `--cards`.
- **Determinism:** game *i* depends only on (seed, *i*), so runs are reproducible and comparisons are paired.
- **Mulligans:** London mulligan with a free first mulligan, using one of three keep strategies: keep only hands that can win turn 1 (down to 5 cards), a sensible keep, or keep any 7.
- **Play or draw:** each is reported, plus a blend weighted for a 3-player table.

## Layout

```
src/MagicSim.Core    deck reader/writer, card index, Scryfall sync (engine to come)
src/MagicSim.Cli     the `sim` command-line tool
tests/               xUnit tests and fixture decks
CONTRACT.md          shared format + CLI spec
```

## Notes

MagicSimulator is unofficial Fan Content permitted under the Fan Content Policy. It is not approved or endorsed by Wizards of the Coast. Card data comes from the [Scryfall API](https://scryfall.com/docs/api), used per their guidelines.

## Contributing

This is a personal tool, published as-is — **issues and pull requests aren't accepted** (PRs auto-close). Fork it and make it your own. 🎲

## License

MIT — see [LICENSE](LICENSE). © 2026 RelentlessOldMan.
