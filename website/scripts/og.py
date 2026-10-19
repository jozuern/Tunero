# Renders public/og.png (1200 x 630), the preview image for links to tunero.dev. Needs Windows (Segoe UI Variable) and
# Pillow: python website/scripts/og.py. Uses the brand tile color, the app icon and a real screenshot.
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

site = Path(__file__).resolve().parent.parent
repo = site.parent
W, H = 1200, 630
tile, ink, ink2 = (14, 23, 38), (238, 241, 245), (174, 183, 196)

img = Image.new('RGB', (W, H), tile)
draw = ImageDraw.Draw(img)

def font(size, weight, opsz):
    f = ImageFont.truetype('C:/Windows/Fonts/SegUIVar.ttf', size)
    f.set_variation_by_axes([weight, opsz])
    return f

icon = Image.open(repo / 'docs/brand/tunero-icon-256.png').convert('RGBA').resize((64, 64), Image.LANCZOS)
img.paste(icon, (72, 72), icon)
draw.text((152, 82), 'Tunero', font=font(40, 600, 36), fill=ink)
draw.text((72, 200), 'PC optimizer', font=font(58, 600, 36), fill=ink)
draw.text((72, 268), 'for Windows 11.', font=font(58, 600, 36), fill=ink)
draw.text((72, 360), 'Every finding explained with sources. Every setting can be undone.', font=font(26, 400, 12), fill=ink2)

# The app window, cut off at the bottom right like a window that continues off the card.
shot = Image.open(site / 'src/assets/screens/site-overview-dark-en.png').convert('RGB')
shot = shot.resize((900, int(shot.height * 900 / shot.width)), Image.LANCZOS)
mask = Image.new('L', shot.size, 0)
ImageDraw.Draw(mask).rounded_rectangle((0, 0, shot.width, shot.height), radius=10, fill=255)
img.paste(shot, (560, 440), mask)
draw.rounded_rectangle((560, 440, 560 + shot.width, 440 + shot.height), radius=10, outline=(44, 58, 82), width=1)

img.save(site / 'public/og.png', optimize=True)
print('wrote', site / 'public/og.png')
