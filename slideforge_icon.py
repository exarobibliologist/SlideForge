from PIL import Image, ImageDraw

def create_slideforge_icon(output_path="SlideForge.ico"):
    size = 512
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    # 1. Outer Dark Squircle Badge
    draw.rounded_rectangle([20, 20, 492, 492], radius=96, fill=(22, 24, 34, 255), outline=(94, 129, 172, 255), width=12)

    # 2. Back Tilted Slide Layer
    back_layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    b_draw = ImageDraw.Draw(back_layer)
    b_draw.rounded_rectangle([92, 108, 402, 348], radius=28, fill=(46, 52, 72, 255), outline=(108, 146, 191, 220), width=8)
    back_layer = back_layer.rotate(9, resample=Image.BICUBIC, center=(256, 256))
    img = Image.alpha_composite(img, back_layer)

    # 3. Front Viewport Frame
    draw = ImageDraw.Draw(img)
    draw.rounded_rectangle([96, 124, 424, 388], radius=28, fill=(18, 20, 28, 255), outline=(136, 192, 208, 255), width=10)

    # 4. Gold Star/Sun Circle + Cyan Play Triangle
    draw.ellipse([142, 164, 194, 216], fill=(255, 209, 102, 255))
    draw.polygon([(232, 182), (362, 256), (232, 330)], fill=(136, 192, 208, 255), outline=(240, 244, 248, 255))

    # 5. Filmstrip Indicator Dots at Bottom
    for i in range(5):
        x = 148 + i * 50
        color = (255, 209, 102, 255) if i == 2 else (136, 192, 208, 190)
        draw.ellipse([x, 420, x + 20, y := 440], fill=color)

    # Save multi-resolution .ico
    icon_sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    img.save(output_path, format="ICO", sizes=icon_sizes)
    print(f"Saved {output_path}")

if __name__ == "__main__":
    create_slideforge_icon()