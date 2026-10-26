"""Build the bundled regular font from Noto Sans SC (SIL OFL 1.1).

Input: google/fonts ofl/notosanssc/NotoSansSC[wght].ttf
SHA256: a3041811a78c361b1de50f953c805e0244951c21c5bd412f7232ef0d899af0da
Requires fonttools; only used to regenerate the checked-in font asset.
"""
import hashlib
import sys
from pathlib import Path
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

source = Path(sys.argv[1])
assert hashlib.sha256(source.read_bytes()).hexdigest() == "a3041811a78c361b1de50f953c805e0244951c21c5bd412f7232ef0d899af0da"
font = instantiateVariableFont(TTFont(source), {"wght": 400}, inplace=True)
names = {1: "Toolbox Sans SC", 2: "Regular", 4: "Toolbox Sans SC Regular", 6: "ToolboxSansSC-Regular", 16: "Toolbox Sans SC", 17: "Regular"}
for record in font["name"].names:
    if record.nameID in names:
        record.string = names[record.nameID].encode(record.getEncoding())
destination = Path(__file__).resolve().parents[1] / "src/Toolbox.Platform/Assets/ToolboxSansSC-Regular.ttf"
destination.parent.mkdir(parents=True, exist_ok=True)
font.save(destination)
print(f"Created {destination} ({destination.stat().st_size:,} bytes)")
