import io
import pathlib
import sys
import tarfile
import urllib.request
import zipfile

# Source2ZE/MultiAddonManager repackaged as a panel plugin (csgo layout) with
# its cfg already pointing at the addons the Servers modes play sounds from.
# The game servers run on Steam Runtime 3 (sniper).
HERE = pathlib.Path(__file__).parent
version = sys.argv[1] if len(sys.argv) > 1 else "1.6.2"
url = (
    "https://github.com/Source2ZE/MultiAddonManager/releases/download/"
    f"v{version}/MultiAddonManager-v{version}-steamrt3.tar.gz"
)

with urllib.request.urlopen(url) as response:
    upstream = tarfile.open(fileobj=io.BytesIO(response.read()), mode="r:gz")

dist = HERE.parent.parent / "dist"
dist.mkdir(exist_ok=True)
out = dist / f"multiaddonmanager-{version}.zip"
out.unlink(missing_ok=True)

with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as archive:
    for member in upstream.getmembers():
        if not member.isfile() or member.name.endswith("multiaddonmanager.cfg"):
            continue
        archive.writestr(member.name, upstream.extractfile(member).read())
    archive.writestr(
        "cfg/multiaddonmanager/multiaddonmanager.cfg",
        (HERE / "multiaddonmanager.cfg").read_bytes(),
    )

print(out.name, [entry.filename for entry in zipfile.ZipFile(out).infolist()])
