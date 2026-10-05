# Generates app.ico (olive drab badge, white star, khaki crosshair). Requires Pillow: pip install pillow
import math
from PIL import Image, ImageDraw

S = 1024  # draw large, downscale for smooth edges
OLIVE, OLIVE_DARK, KHAKI, WHITE, RED = (75, 83, 32), (48, 54, 20), (195, 176, 145), (245, 242, 230), (170, 30, 30)

img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.rounded_rectangle([24, 24, S - 24, S - 24], radius=190, fill=OLIVE_DARK)
d.rounded_rectangle([56, 56, S - 56, S - 56], radius=160, fill=OLIVE)

c = S / 2
# crosshair ring + ticks
d.ellipse([c - 360, c - 360, c + 360, c + 360], outline=KHAKI, width=44)
for a in range(4):
    ang = a * math.pi / 2
    x1, y1 = c + math.cos(ang) * 300, c + math.sin(ang) * 300
    x2, y2 = c + math.cos(ang) * 440, c + math.sin(ang) * 440
    d.line([x1, y1, x2, y2], fill=KHAKI, width=44)

# five-pointed star
def star(cx, cy, r_out, r_in):
    pts = []
    for i in range(10):
        r = r_out if i % 2 == 0 else r_in
        ang = -math.pi / 2 + i * math.pi / 5
        pts.append((cx + math.cos(ang) * r, cy + math.sin(ang) * r))
    return pts
d.polygon(star(c, c + 12, 270, 108), fill=WHITE)
d.ellipse([c - 26, c - 14, c + 26, c + 38], fill=RED)

sizes = [16, 24, 32, 48, 64, 128, 256]
img.resize((256, 256), Image.LANCZOS).save("app.ico", sizes=[(s, s) for s in sizes])
img.resize((256, 256), Image.LANCZOS).save("icon.png")
print("app.ico + icon.png written")
