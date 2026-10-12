"""Regenerate the Toolbox icon; requires Pillow, only for asset editing."""
from pathlib import Path
from PIL import Image, ImageDraw

image = Image.new('RGBA', (256, 256))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((4, 4, 252, 252), radius=62, fill='#127C72')
draw.rounded_rectangle((94, 63, 162, 111), radius=15, outline='#E4FFF2', width=10)
draw.rounded_rectangle((48, 91, 208, 194), radius=22, fill='#127C72', outline='#E4FFF2', width=10)
draw.line((55, 131, 201, 131), fill='#E4FFF2', width=9)
draw.rounded_rectangle((115, 117, 141, 150), radius=6, fill='#E4FFF2')
destination = Path(__file__).resolve().parents[1] / 'src/Toolbox.App/Assets/Toolbox.ico'
destination.parent.mkdir(parents=True, exist_ok=True)
image.save(destination, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
