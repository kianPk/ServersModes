import pathlib
import subprocess
import sys
import zipfile

ROOT = pathlib.Path(__file__).parent
# The panel takes a custom plugin's slug from the archive name.
PLUGINS = {
    "ServersDuels": "servers-duels",
    "ServersDm": "servers-dm",
    "ServersBhop": "servers-bhop",
    "Servers2x2": "servers-2x2",
}
version = sys.argv[1] if len(sys.argv) > 1 else "1.0.0"
dist = ROOT / "dist"
dist.mkdir(exist_ok=True)

for plugin, slug in PLUGINS.items():
    subprocess.run(
        ["dotnet", "build", f"src/{plugin}/{plugin}.csproj", "-c", "Release", "-nologo", "-v", "q", f"-p:Version={version}"],
        cwd=ROOT,
        check=True,
    )
    dll = ROOT / "src" / plugin / "bin" / "Release" / "net10.0" / f"{plugin}.dll"
    out = dist / f"{slug}-{version}.zip"
    out.unlink(missing_ok=True)
    # The panel unpacks the archive into game/csgo, so a plugin's csgo folder
    # (bot scripts and the like) lands next to the game's own files.
    game_files = ROOT / "src" / plugin / "csgo"
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr(f"addons/counterstrikesharp/plugins/{plugin}/{plugin}.dll", dll.read_bytes())
        for file in sorted(game_files.rglob("*")) if game_files.is_dir() else []:
            if file.is_file():
                archive.writestr(file.relative_to(game_files).as_posix(), file.read_bytes())
    print(out.name, [entry.filename for entry in zipfile.ZipFile(out).infolist()])
