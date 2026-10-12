"""Regenerate small, synthetic viewer fixtures. Pillow is needed only to regenerate."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1] / 'tests/fixtures/images'
root.mkdir(parents=True, exist_ok=True)
image = Image.new('RGB', (320, 200), '#EAF0EC')
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((24, 24, 296, 176), radius=20, fill='#137F70')
draw.ellipse((52, 52, 132, 132), fill='#D4FAE5')
draw.polygon([(146, 146), (211, 60), (272, 146)], fill='#8CC6AF')
for name in ('01-sample.png', '02-sample.jpg', '03-sample.bmp', '05-sample.tiff', '06-sample.webp'):
    image.save(root / name)
frames = [Image.new('RGB', (320, 200), color) for color in ('#137F70', '#577DBB')]
for index, frame in enumerate(frames):
    canvas = ImageDraw.Draw(frame)
    x = 36 + index * 152
    canvas.rounded_rectangle((x, 52, x + 96, 148), radius=22, fill='#E4FFF2')
frames[0].save(root / '04-animation.gif', save_all=True, append_images=frames[1:], duration=180, loop=0)
