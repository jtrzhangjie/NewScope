from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

SIZE = 1024
OUT = Path(__file__).resolve().parents[1] / "src" / "NewScope" / "Assets"


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


canvas = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))

gradient = Image.new("RGBA", (SIZE, SIZE))
pixels = gradient.load()
top = (7, 17, 31)
bottom = (18, 42, 69)
for y in range(SIZE):
    for x in range(SIZE):
        t = min(1.0, (x * 0.28 + y * 0.72) / (SIZE - 1))
        c = lerp(top, bottom, t)
        pixels[x, y] = (*c, 255)

mask = Image.new("L", (SIZE, SIZE), 0)
ImageDraw.Draw(mask).rounded_rectangle((32, 32, SIZE - 33, SIZE - 33), radius=220, fill=255)
canvas.alpha_composite(Image.composite(gradient, Image.new("RGBA", (SIZE, SIZE)), mask))

glow = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
gd = ImageDraw.Draw(glow)
gd.ellipse((180, 40, 900, 620), fill=(55, 125, 170, 95))
glow = glow.filter(ImageFilter.GaussianBlur(180))
canvas.alpha_composite(Image.composite(glow, Image.new("RGBA", (SIZE, SIZE)), mask))

grid = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
grid_draw = ImageDraw.Draw(grid)
for p in (300, 512, 724):
    grid_draw.line((172, p, 852, p), fill=(130, 190, 220, 22), width=5)
    grid_draw.line((p, 172, p, 852), fill=(130, 190, 220, 16), width=5)
canvas.alpha_composite(Image.composite(grid, Image.new("RGBA", (SIZE, SIZE)), mask))

points = [
    (170, 512), (292, 512), (352, 430), (404, 606),
    (466, 292), (536, 654), (602, 468), (664, 512), (822, 512),
]

pulse_glow = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
pg = ImageDraw.Draw(pulse_glow)
pg.line(points, fill=(77, 208, 225, 160), width=110, joint="curve")
for p in (points[0], points[-1]):
    pg.ellipse((p[0] - 55, p[1] - 55, p[0] + 55, p[1] + 55), fill=(77, 208, 225, 160))
pulse_glow = pulse_glow.filter(ImageFilter.GaussianBlur(46))
canvas.alpha_composite(pulse_glow)

pulse = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
pd = ImageDraw.Draw(pulse)
pd.line(points, fill=(77, 208, 225, 255), width=58, joint="curve")
pd.ellipse((points[0][0] - 29, points[0][1] - 29, points[0][0] + 29, points[0][1] + 29), fill=(77, 208, 225, 255))
pd.ellipse((points[-1][0] - 29, points[-1][1] - 29, points[-1][0] + 29, points[-1][1] + 29), fill=(77, 208, 225, 255))
canvas.alpha_composite(Image.composite(pulse, Image.new("RGBA", (SIZE, SIZE)), mask))

dot_glow = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
ImageDraw.Draw(dot_glow).ellipse((762, 452, 882, 572), fill=(255, 202, 40, 170))
dot_glow = dot_glow.filter(ImageFilter.GaussianBlur(30))
canvas.alpha_composite(dot_glow)
ImageDraw.Draw(canvas).ellipse((790, 480, 854, 544), fill=(255, 202, 40, 255))

border = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
ImageDraw.Draw(border).rounded_rectangle((32, 32, SIZE - 33, SIZE - 33), radius=220, outline=(83, 133, 170, 150), width=10)
canvas.alpha_composite(border)

canvas.save(OUT / "newscope.png")
canvas.resize((256, 256), Image.Resampling.LANCZOS).save(
    OUT / "newscope.ico",
    format="ICO",
    sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
)
print(OUT / "newscope.png")
print(OUT / "newscope.ico")
