# MagicSim contract

This is the shared agreement between **MagicSimulator** (this repo), **MagicDecks** and **Cardinator**. It covers the deck file format, the card index, and the `sim` command line and its JSON output. Code against this file. Change it here first, then tell the other sessions.

Status: **draft v0.5** (2026-10-11).
- v0.2 added review notes from MagicDecks and Cardinator.
- v0.3 added per-game output (`--per-game`) and stated the in-game play goal.
- v0.4 added per-game fields MagicDecks asked for, plus `sim replay`, and marked Cardinator's §1 support as planned.
- v0.5: card behavior comes from Forge scripts (§6), and Cardinator 1.6.32 has shipped (§5).
- Built: §1 deck format, §2 card index, and the `sync-cards`, `check`, `sync-forge` and `cards` commands.
- Spec only, not built yet: `run`, `compare`, `cards` and profiles.

---

## 1. Deck format (`.txt`)

The MagicDecks format.

### Writers must emit exactly this

```
1 Rograkh, Son of Rohgahh *CMDR*
1 Silas Renn, Seeker Adept *CMDR*

1 Demonic Consultation
2 Island
...
```

- **Encoding:** UTF-8 with no BOM, LF line ends, and a newline at the end of the file.
- **Commanders first.** Each one is written `1 Name *CMDR*`: one space before the tag, which is all caps with an asterisk on each side. They go in Moxfield's commander order, and partners get one line each.
- **Exactly one blank line,** then the rest of the deck as `N Name`.
- **Merge duplicates** into one line, e.g. `7 Island`.
- **Card names:** the plain English oracle name. Two-sided and split cards use Scryfall's full name, e.g. `Expansion // Explosion`. No set codes, no collector numbers.
- **Order after the blank line has no meaning.** Readers must not depend on it.
- **Only commanders and mainboard.** No sideboard, maybeboard or signature spells.
- **Companion:** a companion sits outside the 100, so it goes in a comment as the last line: `# companion: Name`. If the source also lists it in the mainboard, it stays there too and counts toward the 100.
- **A deck with no commander** (e.g. a 60-card constructed list): no tagged lines and no blank line, just the `N Name` block.

### Readers must also accept

- CRLF line ends and a UTF-8 BOM. Windows editors and git autocrlf produce them.
- `Nx Name`, e.g. `1x Sol Ring`.
- A trailing set code, with or without a collector number: `(SET) 123`, `(SET)` or `[SET]`.
- Moxfield markers such as `*F*` and `*E*`.
- Comment lines starting with `#` or `//`, and extra blank lines.
- The same card on several lines. The counts are added together.
- Names in any letter case, and with or without accents. `Lim-Dul's Vault` matches `Lim-Dûl's Vault`.
- A single face name standing for a two-sided or split card.
- Untagged lists, such as the `-moxfield.txt` files or 60-card decks. These parse, but give a "no commander" warning.

### Moxfield paste files (`<Deck>-moxfield.txt`)

The same list with no `*CMDR*` tag and no blank lines. These are only for pasting into Moxfield. Tools should not use them as input.

## 2. Card index

The simulator owns this file. Other tools may read it but should not write it.

- **Location:** `--cards <path>` if given, else `%MAGICSIM_CARDS%`, else `%LOCALAPPDATA%\MagicSim\oracle-cards.jsonl.gz`.
- **Refresh:** `sim sync-cards` rebuilds it from Scryfall's `oracle_cards` bulk download.
- **Contents:** gzip-compressed JSONL, one card per line, with snake_case keys so Python can read it with `json.loads`. Junk layouts are skipped: art series, tokens, emblems, planes, schemes and so on.

```json
{"name":"Thassa's Oracle","layout":"normal","mana_cost":"{U}{U}","cmc":2,
 "type_line":"Creature — Merfolk Wizard","oracle_text":"…","colors":["U"],
 "color_identity":["U"],"produced_mana":[],"keywords":[],"power":"1","toughness":"3",
 "loyalty":null,"commander_legality":"legal","faces":null}
```

**Cards with faces:**
- `faces` is a list of `{name, mana_cost, type_line, oracle_text, power, toughness}`.
- When Scryfall gives a cost or P/T only on the faces, the top-level `mana_cost`, `power` and `toughness` copy the front face's values.

**Name lookup:**
- Remove accents (Unicode NFD, then drop the accent marks), trim, and lowercase. This matches MagicDecks' `norm()`.
- Each face name is also a key that points to the whole card.
- If two cards share a name, a card that isn't `not_legal` in Commander wins over one that is.

## 3. CLI: `sim`

A headless console program. In this repo, run it with `dotnet run --project src/MagicSim.Cli -- <args>`. Once published, it's `sim.exe`.

### Exit codes

| Code | Meaning |
|-----:|---------|
| 0 | OK. The JSON was written. |
| 1 | Run error: missing file, network failure, missing card index, and so on. The message is on stderr. |
| 2 | Usage error: bad or missing arguments. |

**Where output goes:** JSON goes to stdout, or to the file given with `--json <path>`. Progress and diagnostics go to stderr. Every JSON document carries a `schema` field such as `"magicsim.run/1"`, and the number is bumped on breaking changes.

### Built

```
sim sync-cards [--out path]
sim check <deck.txt> [--cards path] [--json out.json]
sim sync-forge [--dir path]
sim cards [--deck x.txt] [--forge dir] [--cards path] [--json out.json]
```

`check` returns:

```json
{"schema":"magicsim.check/1","deck":"For-The-Lulz","ok":true,"card_count":100,
 "commanders":["Rograkh, Son of Rohgahh","Silas Renn, Seeker Adept"],
 "unknown_names":[],"warnings":[]}
```

`ok` means all of these hold: exactly 100 cards, at least one commander, and every name resolves. Bans and color identity are still checked by MagicDecks' `validate_deck.py`.

### Planned (spec, not yet built)

```
sim run <deck.txt> [--profile p.yaml] [--games N] [--seed S]
        [--mull A|B|C|all] [--seat play|draw|both] [--max-turn T] [--json out.json]
        [--per-game games.jsonl]
sim compare <base.txt> --swap "Out=>In" [--swap ...] [same options as run]
sim compare <a.txt> <b.txt> [<c.txt> ...]           [same options as run]
sim replay <deck.txt> --game I [--seed S] [--mull A|B|C] [--seat play|draw] [--profile p.yaml]
```

`replay` prints a readable turn-by-turn log of one game: the mulligans, each spell and ability, each trigger, what's on the stack and when it resolves, the mana pool, and how the game ended. The same (deck, seed, `--game`, mulligan, seat) gives the same game as row `game` in the `--per-game` output, so any interesting row can be replayed. This is for checking by hand that the engine plays correctly. Its text output isn't a stable format.

**Defaults:** `--games 10000`, `--seed 1`, `--mull all`, `--seat both`, `--max-turn 10`.

**Determinism:**
- The same deck, seed, options and simulator version always give the same JSON.
- Game *i* uses a shuffle that depends only on (seed, *i*). So in `compare`, the base deck and each variant play the same shuffles, and a difference in results comes from the card change, not from luck.

**Play or draw:**
- `play` means no draw on turn 1. `draw` means a draw on turn 1.
- `both` reports each, plus a `weighted` row: 1/3 play, 2/3 draw, which is your share of being first at a 3-player table.

**The goldfish table:**
- 2 opponents, 40 life each, 99-card libraries, no interaction.
- You start at 40 life.

**Mulligans:**
- London mulligan, and the first mulligan is free.
- `--mull` picks the keep strategy:
  - **A (T1-or-bust):** keep only a hand that has a turn-1 win, or go down to a floor of 5. "Has a turn-1 win" means the search finds one using only the hand and public information. It never sees the library order.
  - **B (sensible):** keep with at least 2 usable mana plus a win piece or a tutor.
  - **C (keep 7):** always keep, as a baseline.

**Who decides what's "better":** the simulator only plays games and reports what happened. The caller, such as MagicDecks' tuning loop, picks the goal (fastest win, most consistent by turn N, and so on) and the swaps. The simulator only judges a game in two ways:
- **How it plays:** it plays each game to win as fast as possible. This is the only play goal for now.
- **What counts as a win:** this follows the rules of Magic (opponents at 0 life, Thassa's Oracle, milling out, and so on). It isn't a goal anyone sets.

**Mulligans:** the London mulligan above is the only mulligan rule. There's no house-rule handling.

**No cheating:** the play search only knows what a real player would know. Draws and tutors are treated as random until they happen.

**Unsupported cards:**
- A card without coded behavior is treated as blank: it can be drawn or discarded, but never cast or played.
- It is listed in `unsupported_cards`, so results are a lower bound.

#### `run` output (`magicsim.run/1`)

```json
{
  "schema": "magicsim.run/1",
  "sim_version": "0.1.0",
  "deck": {"name": "For-The-Lulz", "path": "…", "card_count": 100,
           "commanders": ["…"], "unsupported_cards": ["…"]},
  "settings": {"games": 10000, "seed": 1, "max_turn": 10},
  "results": [
    {
      "mulligan": "A", "seat": "play", "games": 10000,
      "win_by_turn":        {"1": 0.312, "2": 0.201, "3": 0.090},
      "cumulative_by_turn": {"1": 0.312, "2": 0.513, "3": 0.603},
      "no_win": 0.105,
      "mean_turn": 1.9, "mean_turn_ci95": [1.88, 1.92], "median_turn": 2,
      "mean_turn_capped": 2.8, "mean_turn_capped_ci95": [2.76, 2.84],
      "hand_size_kept": {"7": 0.41, "6": 0.30, "5": 0.29},
      "win_lines": {"consult_oracle": 0.55, "breach": 0.15, "doomsday": 0.10, "storm": 0.20}
    }
  ],
  "cards": [
    {"name": "Demonic Consultation", "drawn": 0.21, "cast": 0.17, "in_win": 0.15}
  ]
}
```

**Field meanings:**
- **Win rates:** `win_by_turn` and `no_win` are fractions of all games. The win values plus `no_win` add up to 1.
- **`mean_turn` / `median_turn`:** counted over winning games only.
- **`mean_turn_capped`:** counted over all games, with each game that wasn't won counted as `max_turn + 1`. Use this to rank variants: a deck that wins less often but faster can't look better than it is.
- **`win_lines`:** fractions of the wins. The keys come from the deck profile.
- **`results`:** one row per (mulligan, seat) pair, plus `seat: "weighted"` rows when `--seat both`.
- **`cards`:** per-game fractions, across all games in the run:
  - `drawn`: the card was seen in hand.
  - `cast`: it was cast or played.
  - `in_win`: it was cast in a game that was won. Planned later: `in_win_line`, meaning the card was part of the actual winning line.

#### Per-game output (`magicsim.game/1`)

`--per-game <path>` writes one JSON object per game, as JSONL, alongside the summary. Callers can compute any measure they want from it. It works with `run` and `compare`.

```json
{"schema":"magicsim.game/1","variant":"base","game":17,"mulligan":"A","seat":"play",
 "mulligans":1,"kept":6,"hand_kept":["Dark Ritual","…"],"bottomed":["Island"],
 "won":true,"win_turn":2,"win_line":"consult_oracle","fail_reason":null,
 "life_end":{"you":36,"opponents":[40,40]},"turns_played":2,
 "mana_by_turn":[5,9],"life_paid":4,"storm_max":6,
 "notes":["Tainted Pact stopped: duplicate Island"],
 "seen":["Dark Ritual","Demonic Consultation","…"],
 "cast":["Dark Ritual","Demonic Consultation","Thassa's Oracle"],
 "win_cards":["Demonic Consultation","Thassa's Oracle"]}
```

**Fields:**
- **`game`:** the shuffle index. The same `game` number in different variants means the same shuffle, so rows can be paired across variants.
- **`variant`:** `base` for a run or the compare base, otherwise the variant's label (e.g. `Mox Opal=>Gemstone Mine`).
- **When a game isn't won:** `won` is false, `win_turn`, `win_line` and `win_cards` are null, and `turns_played` equals `max_turn`.
- **`seen`:** every card that was in hand at any point, including the opening hand. It's a set, so each card appears once.
- **`cast`:** every card that was cast or played, in order.
- **`win_cards`:** the cards in the winning line. These are what the planned `in_win_line` stat counts.
- **Hands:** `mulligans` is how many were taken, the first free one included. `hand_kept` is the hand that was kept, after bottoming, and `bottomed` is the cards put on the bottom.
- **`fail_reason`:** for a game that wasn't won, the engine's best guess at why. It's null when the game was won. Values:
  - `no_mana`: too little mana to cast the payoff pieces.
  - `no_win_piece`: no win piece was reachable, whether in hand or by tutor.
  - `pieces_but_short_mana`: the pieces were reachable, but there wasn't enough mana that turn.
  - `unsupported_dead_cards`: unsupported cards took up the hand.
  - `other`: none of the above.
  Since it's a guess, use it to point you in a direction, not as proof.
- **`mana_by_turn`:** the most mana available in each turn played, counting rituals and fast mana.
- **`life_paid`:** life you spent on purpose, e.g. on Ad Nauseam, Necropotence, Doomsday or painlands. **`storm_max`:** the most spells cast in one turn.
- **`notes`:** rules events worth checking, e.g. "Tainted Pact stopped: duplicate Island". It's free text: show it to people, don't parse it.
- **Row order:** one row per (variant, mulligan, seat, game). Rows are sorted in that order, so files can be compared byte for byte.
- **Size:** with the defaults, `run` writes 10,000 games × 3 mulligan settings × 2 seats = 60,000 rows. Use `--mull` and `--seat` to cut that down.

#### `compare` output (`magicsim.compare/1`)

```json
{
  "schema": "magicsim.compare/1",
  "base": { "…": "same shape as a run document" },
  "variants": [
    {
      "label": "Mox Opal=>Gemstone Mine",
      "swaps": [{"out": "Mox Opal", "in": "Gemstone Mine"}],
      "unreliable": false,
      "unsupported_added": [],
      "results": [
        {"mulligan": "A", "seat": "play",
         "t1_rate": 0.318, "delta_t1": 0.006, "delta_t1_ci95": [-0.002, 0.014],
         "p_value": 0.14,
         "delta_cumulative_by_turn": {"1": 0.006, "2": 0.004, "3": -0.001},
         "delta_mean_turn": -0.02, "delta_mean_turn_capped": -0.05,
         "delta_mean_turn_capped_ci95": [-0.09, -0.01]}
      ]
    }
  ]
}
```

- **The test:** each base game is paired with the variant game that used the same shuffle. `p_value` comes from McNemar's test on turn-1 wins, a test suited to paired results.
- **`unreliable: true`** means the variant adds a card the simulator can't play. Don't optimize toward it.
- **Rows:** one per (mulligan, seat) pair, including the `seat: "weighted"` rows. `delta_cumulative_by_turn` covers turns 1 through `max_turn`.
- **Swap rules:** a `--swap` is a usage error (exit 2) if its `Out` card isn't in the deck or its `In` card isn't in the card index.
- **Not checked by compare:** bans and color identity. Callers check those first (MagicDecks uses `validate_deck.py`).

#### `cards` output (`magicsim.cards/1`), built

```json
{"schema": "magicsim.cards/1",
 "pool": {"description": "Commander-legal cards with a Forge script", "cards": 32161,
          "scripted": 369, "hand_coded": 0, "supported_share": 0.0115,
          "next_to_implement": [{"requirement": "Api:Pump", "cards_needing": 4262, "cards_unlocked_alone": 458}]},
 "deck": {"name": "For-The-Lulz",
          "cards": [{"name": "Dark Ritual", "tier": "unsupported", "missing": ["Api:Mana"]}]}}
```

**Fields:**
- **`pool`:** covers every Commander-legal (or banned) card that has a Forge script.
- **`next_to_implement`:** the requirements that unblock the most cards. It's ranked first by how many cards that requirement *alone* is blocking, then by how many cards need it at all.
- **`tier`:** one of `scripted`, `hand_coded` or `unsupported`.
- **`missing`:** the requirement keys the engine doesn't support yet. `NoForgeScript` means Forge has no script for the card.
- **`deck`:** appears only when `--deck` is given.

## 4. Deck profiles (planned)

A deck profile lives at `profiles/<Deck>.yaml` in this repo and holds settings for one deck. MagicDecks keeps only decklists and strategy write-ups.

A profile is **optional**. Any deck runs without one, using what the engine can work out on its own. A profile only sharpens the results.

```yaml
deck: For-The-Lulz.txt                # relative to the profile's folder, or to --decks <root> / %MAGICDECKS_DIR%
consultation_name: "Lightning Bolt"   # a card NOT in the deck, for Consultation/Pact
win_lines: [consult_oracle, doomsday, breach, storm]
key_cards: [Thassa's Oracle, Demonic Consultation, Tainted Pact, Doomsday, Underworld Breach]
tutors: [Demonic Tutor, Vampiric Tutor, Mystical Tutor]   # optional; inferred from rules text if left out
```

- **No absolute paths.** `deck:` is resolved against the profile's folder, then `--decks`, then `%MAGICDECKS_DIR%`.
- **Mulligan policy B** uses `key_cards` and `tutors` to decide what counts as "a win piece or a tutor". Without a profile, the engine infers both: tutors from "search your library" text, and win pieces from the cards that appear in its winning lines.

## 5. Moxfield import

The simulator doesn't fetch from Moxfield itself. It shells out to Cardinator:

```
Cardinator.exe --moxfield <link | id> out.txt format=magicdecks
```

- **Exit codes:** 0 written, 1 the fetch failed, 2 usage error.
- **Overwrites:** it replaces an existing `.txt` without asking. It only refuses to overwrite a Cardinator set file. Don't point it at a hand-edited deck file.
- `format=magicdecks` writes the §1 format. It shipped in Cardinator 1.6.32.
- **The other direction also works (1.6.32):** Cardinator's list import accepts §1 files, treats the tagged cards as commanders, and ignores `#` comments.

## 6. Card support

The simulator is **generic**: any deck from anyone. Card coverage is measured against the whole card pool, not against particular decks. The rules engine handles turns, mana, the stack, combat, mulligans and the search, the same way for every deck.

**Where card behavior comes from:** [Forge](https://github.com/Card-Forge/forge)'s card scripts. Forge is an open-source Magic engine that describes nearly every card in a structured script, e.g. `A:SP$ Mana | Produced$ B | Amount$ 3` for Dark Ritual.
- **Download:** `sim sync-forge` fetches the scripts with a sparse git clone into `%LOCALAPPDATA%\MagicSim\forge`. It needs `git` on the PATH.
- **License:** Forge is GPL-3.0, so its scripts are never copied into this repo or bundled with `sim`. They're downloaded to each machine, the same way the Scryfall data is.
- **Our own engine:** the simulator implements Forge's effect types (`Mana`, `Draw`, `ChangeZone`, `Dig`, `DealDamage`, …) in C#. It doesn't use Forge's Java code.

**Each card is in one of three tiers:**
1. **Scripted:** the card's Forge script uses only effect types the engine implements.
2. **Hand-coded:** the engine has its own code for the card, for behavior that lives in Forge's Java code rather than its script.
3. **Unsupported:** treated as blank and reported, as §3 describes.

`sim cards` reports each card's tier and the share of the whole card pool that's supported.

**How correctness is checked:**
- **Exact math:** shuffling, mulligans and draws are tested against exact probabilities.
- **Per-card scenario tests:** each card and effect type is tested from a set-up board.
- **Forge comparison:** a local, unpublished tool sets up the same board in Forge's engine and compares the results.
- **Stats sanity check:** for fair decks, turn-to-win numbers are compared with Forge's AI.
- **Replay review:** `sim replay` logs, read by people.
- **Disputes:** settled by the Comprehensive Rules and card rulings, not by Forge.

## 7. Later (not planned yet)

- **Live mode:** you enter your actual hand, draws and board, and the simulator recommends the best play, using the same search as `run`.
- **Head-to-head:** two decks played against each other.
