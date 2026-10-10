<p align="center"><img src="icon.png" width="96" alt="icon"></p>

<h1 align="center">WWII Local Play Loadout Editor</h1>

<p align="center">
Use <b>every</b> weapon, camo, attachment, charm and perk in <b>Call of Duty: WWII Local Play</b> (offline LAN / split-screen),<br>
and unlock everything in the game's own menus.
</p>

<p align="center">Made by <b>Petsox</b></p>

---

In Local Play almost every weapon is locked, because the unlocks come from the online inventory, which isn't
available offline. The only way to get other guns was the Wanderlust perk, which swaps your weapon at random.
This tool fixes that. It edits your Local Play classes directly and can switch on the game's built-in
"unlock everything" mode for the menus.

> **Offline / Local Play only.** The tool reads and writes the game's memory. Never use it in online matches.

## Tested on

| | |
|---|---|
| Game | Call of Duty: WWII (PC), multiplayer executable `s2_mp64_ship.exe` |
| Game version | **1.25.0.1** (build changelist **2244937**) |
| `s2_mp64_ship.exe` | 48,417,280 bytes, SHA-256 `381C737EC7EF8141C1FF15A8BAAC044CECAA9C1CD8F329D3AC6E4A82BBF982FD` |
| OS | Windows 11 23H2 (10.0.22631), .NET Framework 4.8 |

Other game builds probably won't work. The tool locates most things by signature or by name, but a few memory
offsets are specific to this build. If an offset doesn't match, the tool reports it in its log rather than
writing anything.

## Features

- **Edit any Local Play class (1–10):** primary and secondary weapon, **6 attachments each**, camo, grip and
  charm per weapon, lethal and tactical (including extra counts) and all 9 perk/training slots.
- **Every weapon:** all guns, launchers and melee weapons, optionally including all loot variants.
- **Camos that actually load:** every multiplayer camo (patterns, Gold, Diamond, Chrome and the loot camos), filtered
  to what fits the selected weapon. Camos that would freeze the game are fixed automatically (see below).
- **Unlock everything in game menus (one button):** every weapon, melee weapon, Challenges camo, charm and reticle
  can be picked in the game's own *Soldier → Divisions* screen, and about 80 camos per weapon appear under
  *Camouflage → Special*. Normally Local Play shows only a handful there, because all the seasonal camo tabs
  are hidden offline. It also adds **Carentan (winter)** to the Local Play map list.
- **Prop Hunt in Local Play:** click *Party modes in Local Play* (under *Connect to game*; green while on) and *Game Setup → Mode* lists the game's
  hidden **Party Modes** group (Prop Hunt, Gun Game, Spy, Dogfight, Dogfight FFA) in place of Esports Modes. Only the
  host needs it; friends on the LAN join as usual.
- **Load class from game:** reads the selected class back from the game and fills every field. The class names
  from the game also appear in the class list. *Load primary / Load secondary from game* (at the top of each
  weapon column) read just that weapon, so you can copy a weapon from one class to another.
- **Export / Import:** save a class to a small `.json` file and share it with friends.
- **Dev & hidden items (experimental):** event Tesla guns, flamethrowers, the riot shield, the developers' test
  reticle, and **hidden maps** the Local Play map list never shows: `mp_house`, `mp_sandbox_01` (developer test maps)
  and `mp_scorestreak_training` (the scorestreak training map).
- **Full item list from the game:** reads `mp/statstable.csv` straight from memory, so nothing is missing.
- Mouse-wheel protection on all dropdowns, so a class can't be changed by accident.

## Download

Get `WW2_LocalPlay_Loadout_Editor.exe` from the [Releases](../../releases) page, or build it yourself (below).

> Antivirus programs often flag tools that write into another program's memory, and Windows Defender may delete
> this exe. The full source is in this repository. If that happens, add an exclusion for the tool's folder.

## Usage

1. Put `WW2_LocalPlay_Loadout_Editor.exe` anywhere you like; the game folder is convenient.
2. Start the game and wait for the main menu, then start the tool. It connects automatically (the badge at the
   top right turns green), or you can press **Connect to game** in the sidebar.
3. Press **Load full item list from game** in the sidebar once. This saves `statstable_full.csv` next to the tool
   for later runs.
4. Optional: press **Unlock everything in game menus** to use the game's own class editor, and
   **Party modes in Local Play** to get Prop Hunt (host only).
5. Go to **Local Play** in the game.
6. On the **Class editor** tab, pick a class and press **Load class from game** to see what's in it. Change any
   fields, then press **Apply to class**. Fields left on *(keep current)* are not touched. Hidden weapons, hidden
   maps and the raw console command are on the **Dev & hidden items** tab.
7. Back out of the Divisions screen in the game and go in again, then check your weapon in the Firing Range.

Every player needs the tool running on their own PC, because each PC keeps its own classes.
Keep the tool open while you play. If the game restarts, the tool reconnects and re-applies its fixes on its own.

## How it works

The tool doesn't change any files. Everything happens in the running game's memory and is gone when the game closes.

| What | How |
|---|---|
| Writing classes | Injects `setPrivateLoadout "privateMatchCustomClasses" <class> ...` into the game's console command buffer (`cmd_textArray`, found by signature, the same method the community *WW2 Loadout Editor* used for online classes). |
| Camos that freeze the game | Camos without the "free" flag in `mp/camotable.csv` make the game wait for the online inventory, which never answers offline. The tool sets that flag on every camo in memory. Only *generic* camo IDs work, so the tool never sends per-weapon IDs. |
| Unlock everything | Sets dvar `709` (the developers' unlock-all switch, checked throughout the menu scripts). In `mp/unlocktable.csv` it also sets *UnlockForLANTournament* = 1 on every item, gives post-launch weapons a challenge value (Local Play only lets those be equipped if they have one), and gives mastery/tier camos a basic challenge, so the offline menu shows them under *Challenges*. Offline the camo menu only shows the tabs marked `Offline=1` in `mp/categories_camo.csv` (*Special* and *Challenges*), so loot camos from the hidden seasonal tabs get their statstable *Operation* (column 52) set to Special. |
| Party modes | The Mode menu (`ui/s2/mp_game_setup_menu_uc.lua`) only lists the groups `Cac.GameModes.Group.Standard` and `.Esports`, although `Cac.GameModes.Data` also defines a `Party` group with `prop` (Prop Hunt) and the other party modes. The tool finds the menu functions' constants (`"Group"`, `"Standard"`, `"Esports"`, `"ipairs"`) in the Lua heap and points their `"Esports"` at the game's own `"Party"` string. Picking a mode then runs the game's `SetPartyGameType` as usual. The menu scripts reload after every match, so the tool re-applies this every 1.5 s while the button is on. |
| Hidden maps | The lobby keeps its selected map as a pointer to an interned string (party data at `exe+0x8DD8DB0`, pointer at `+0x268`, as used by the game's `SetPartyMapName`). The tool points it at the pooled string of a hidden map and moves one reference count from the old string to the new one. `set 5887 1` makes the menu list Carentan (winter). |
| Reading classes | The Local Play classes live in the encrypted "private loadouts" stats buffer. The tool reads it, recovers the per-session XOR key (only 65,536 keys are possible; the right one turns the class fields into valid item IDs), then decodes the classes using the layout from `mp/ddl/privateloadouts.ddl`. |

The menu logic was worked out by decompiling the game's UI scripts with
[CoDLuaDecompiler](https://github.com/JariKCoding/CoDLuaDecompiler).

## Class file format

```json
{
  "format": "ww2-localplay-class",
  "version": 1,
  "name": "Class 1  -  Commando",
  "extraLethal": null,
  "extraTactical": null,
  "fields": {
    "primary":     { "id": "0x1019000", "name": "Rifle  |  STG-44" },
    "primaryCamo": { "id": "0x6000F0",  "name": "Camo  |  Gold" },
    "perk1":       { "id": "0x4600072", "name": "Perk  |  Airborne Enlisted" }
  }
}
```

### Example classes

The [`loadouts`](loadouts) folder has three ready-made classes: **Commando** (PPSh-41 variant with Chrome Tiger),
**Sniper** (PTRS-41) and **Ultra Akimbo** (Blyskawica + akimbo Machine Pistol). Copy the folder next to the tool,
then use **Import class...** → pick a class slot → **Apply to class**. The examples use loot variants and up to 6
attachments, so run **Load full item list from game** and **Unlock everything in game menus** first.

Field keys: `primary`, `secondary`, `primaryCamo`, `primaryGrip`, `primaryCharm`, `primaryAttachment1`–`6`,
`secondaryCamo`, `secondaryGrip`, `secondaryCharm`, `secondaryAttachment`, `secondaryAttachment2`–`6`, `lethal`,
`tactical`, `perk1`–`9`. Only the `id` is used on import; `name` is there for humans.

## Building

You don't need Visual Studio or any SDK. Double-click **`build.bat`**. It uses the C# compiler that comes with
Windows (.NET Framework 4) and writes `WW2_LocalPlay_Loadout_Editor.exe` next to the source.
`tools/make_icon.py` regenerates the icon (needs Pillow).

## Known limitations

- Paintjobs are stored in online file slots, so they can't be used offline.
- Attachment slots 5 and 6 are more than the game's own menu allows. Test them in the Firing Range first.
- Dev & hidden items may look broken, do nothing, or freeze the game. Test in the Firing Range.
- Party modes replace the Esports Modes group in the menu while enabled (click the button again to get it back). Prop Hunt is tested
  in Local Play; Gun Game, Spy and the Dogfight modes are not, and the Dogfight maps may not load offline.
- Hidden maps: pick any normal map in the lobby first (as host), then use *Set as lobby map* and start the match.
  The lobby keeps showing the old map's name and picture. Other hidden maps aren't offered: Excavation's internal
  map (`mp_tank_graveyard_2`) fails the game's DLC content check, and the Dogfight maps aren't in the game's string
  pool.
- Zombies camos (Pack-a-Punch, Red Herring, Ice, the "amp" camos etc.) can't be used in multiplayer. Their textures
  are only loaded in Zombies mode, so they have no effect on a multiplayer weapon. The developers' test camo freezes
  the game.
- Tested on a single game build only (see above).

## Credits

- The community *WW2 Loadout Editor*, for the command-buffer signature and the `setRankedLoadout` approach.
- [JariKCoding/CoDLuaDecompiler](https://github.com/JariKCoding/CoDLuaDecompiler), for decompiling the menu scripts.
- The UnknownCheats *Ultimate CoD WW2* thread, for community research.

## Contributing

Contributions are welcome! Open an issue, or fork the repository and send a pull request. Bug reports are most
useful with the tool's log text and your game version.

## License

**Source-available. This is not an open-source license.** See [LICENSE](LICENSE). In short:

- ✅ Use the tool, build it yourself, and share **unmodified** official releases for free, with credit to Petsox.
- ✅ Modify it to **contribute back** to this repository through pull requests.
- ❌ Don't publish or distribute **modified** versions, don't remove the author credit, and don't sell it.

## Disclaimer

This is a fan-made tool for offline play. It is not affiliated with or endorsed by Activision or Sledgehammer
Games. No game files are included. Use at your own risk, and only in offline Local Play.
