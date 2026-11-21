# fnexport

CLI exporter for Fortnite emotes (`.ueanim`, `.uemodel`, `.wav`) and character skins (`.glb`) for CoD4 Coop.

## Usage

```sh
dotnet run -- list [filter]                  # Search emotes
dotnet run -- export <name/id> [--install]   # Export emote (+ optional install via addemote.py)
dotnet run -- install <name/id>              # Export and install emote

dotnet run -- skins [filter]                 # Search character skins
dotnet run -- skin <name/id>                 # Export skin character parts as .glb
dotnet run -- skin-install <name/id>         # Export and install skin via addskin.py
```

Note: Skin mesh export requires the character's skeletal mesh LODs to be present in your local Fortnite `Paks` directory (enable "Pre-download Streamed Assets" in the Epic Games Launcher for streamed cosmetics).
