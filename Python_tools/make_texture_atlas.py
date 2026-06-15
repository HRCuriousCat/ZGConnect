import json
import re
from pathlib import Path
from PIL import Image


# ============================================================
# CONFIG
# ============================================================

METADATA_FILE = "metadata.json"

OUTPUT_FOLDER = "output"
OUTPUT_ATLAS_FILE = "atlas_128.png"
OUTPUT_METADATA_FILE = "atlas_128_metadata.json"

TILE_OUTPUT_SIZE = 128

# Transparent background for missing/empty tiles
BACKGROUND_COLOR = (0, 0, 0, 0)

# If True: missing textures only print warnings.
# If False: script stops when a texture is missing.
SKIP_MISSING_TEXTURES = True


FILENAME_COORD_RE = re.compile(
    r".*?_(?P<left>\d+)_(?P<bottom>\d+)_.*?\.(png|jpg|jpeg|webp|tif|tiff)$",
    re.IGNORECASE
)


def load_metadata(metadata_path: Path) -> dict:
    if not metadata_path.exists():
        raise FileNotFoundError(f"Metadata file not found: {metadata_path}")

    with metadata_path.open("r", encoding="utf-8") as f:
        return json.load(f)


def get_tile_coords(tile: dict):
    """
    Prefer exact coordinates from metadata.json.
    Fallback to coordinates parsed from filename.
    """

    if all(k in tile for k in ("left", "bottom", "right", "top")):
        return (
            int(tile["left"]),
            int(tile["bottom"]),
            int(tile["right"]),
            int(tile["top"]),
        )

    filename = tile.get("texture_file", "")
    match = FILENAME_COORD_RE.match(filename)

    if not match:
        raise ValueError(f"Could not determine coordinates for tile: {filename}")

    left = int(match.group("left"))
    bottom = int(match.group("bottom"))

    # Default fallback: 1km tile
    tile_size_m = 1000

    return (
        left,
        bottom,
        left + tile_size_m,
        bottom + tile_size_m,
    )


def resize_tile(image_path: Path, output_size: int) -> Image.Image:
    img = Image.open(image_path).convert("RGBA")

    return img.resize(
        (output_size, output_size),
        Image.Resampling.LANCZOS
    )


def main():
    base_folder = Path.cwd()

    metadata_path = base_folder / METADATA_FILE
    output_folder = base_folder / OUTPUT_FOLDER

    output_atlas_path = output_folder / OUTPUT_ATLAS_FILE
    output_metadata_path = output_folder / OUTPUT_METADATA_FILE

    output_folder.mkdir(parents=True, exist_ok=True)

    metadata = load_metadata(metadata_path)
    tiles = metadata.get("tiles", [])

    if not tiles:
        raise RuntimeError("No tiles found in metadata.json")

    prepared_tiles = []

    for tile in tiles:
        texture_file = tile.get("texture_file")

        if not texture_file:
            continue

        image_path = base_folder / texture_file

        if not image_path.exists():
            msg = f"Missing texture: {texture_file}"

            if SKIP_MISSING_TEXTURES:
                print(f"WARNING: {msg} - skipping")
                continue
            else:
                raise FileNotFoundError(msg)

        left, bottom, right, top = get_tile_coords(tile)

        prepared_tiles.append({
            "texture_file": texture_file,
            "image_path": image_path,
            "left": left,
            "bottom": bottom,
            "right": right,
            "top": top,
        })

    if not prepared_tiles:
        raise RuntimeError("No usable texture files found.")

    min_left = min(t["left"] for t in prepared_tiles)
    max_right = max(t["right"] for t in prepared_tiles)

    min_bottom = min(t["bottom"] for t in prepared_tiles)
    max_top = max(t["top"] for t in prepared_tiles)

    tile_width_m = min(t["right"] - t["left"] for t in prepared_tiles)
    tile_height_m = min(t["top"] - t["bottom"] for t in prepared_tiles)

    cols = int(round((max_right - min_left) / tile_width_m))
    rows = int(round((max_top - min_bottom) / tile_height_m))

    atlas_width = cols * TILE_OUTPUT_SIZE
    atlas_height = rows * TILE_OUTPUT_SIZE

    print("Building texture atlas...")
    print(f"Source folder: {base_folder}")
    print(f"Output folder: {output_folder}")
    print(f"Tiles used: {len(prepared_tiles)}")
    print(f"Grid: {cols} columns x {rows} rows")
    print(f"Atlas size: {atlas_width} x {atlas_height}px")

    atlas = Image.new(
        "RGBA",
        (atlas_width, atlas_height),
        BACKGROUND_COLOR
    )

    output_tiles = []

    for index, tile in enumerate(prepared_tiles, start=1):
        resized_tile = resize_tile(tile["image_path"], TILE_OUTPUT_SIZE)

        col = int(round((tile["left"] - min_left) / tile_width_m))

        # Map north/up to image top.
        # Larger top coordinate should appear higher in image.
        row = int(round((max_top - tile["top"]) / tile_height_m))

        atlas_x = col * TILE_OUTPUT_SIZE
        atlas_y = row * TILE_OUTPUT_SIZE

        atlas.paste(resized_tile, (atlas_x, atlas_y))

        output_tiles.append({
            "texture_file": tile["texture_file"],

            "left": tile["left"],
            "bottom": tile["bottom"],
            "right": tile["right"],
            "top": tile["top"],

            "atlas_col": col,
            "atlas_row": row,

            "atlas_x_px": atlas_x,
            "atlas_y_px": atlas_y,
            "atlas_width_px": TILE_OUTPUT_SIZE,
            "atlas_height_px": TILE_OUTPUT_SIZE,

            # Top-left origin UVs, useful for UI/debug tools.
            "uv_top_left_origin": {
                "u_min": atlas_x / atlas_width,
                "v_min": atlas_y / atlas_height,
                "u_max": (atlas_x + TILE_OUTPUT_SIZE) / atlas_width,
                "v_max": (atlas_y + TILE_OUTPUT_SIZE) / atlas_height,
            },

            # Bottom-left origin UVs, more useful for Unity meshes/materials.
            "uv_bottom_left_origin": {
                "u_min": atlas_x / atlas_width,
                "v_min": 1.0 - ((atlas_y + TILE_OUTPUT_SIZE) / atlas_height),
                "u_max": (atlas_x + TILE_OUTPUT_SIZE) / atlas_width,
                "v_max": 1.0 - (atlas_y / atlas_height),
            },
        })

        print(
            f"[{index}/{len(prepared_tiles)}] "
            f"{tile['texture_file']} -> col {col}, row {row}"
        )

    atlas.save(output_atlas_path)

    atlas_metadata = {
        "source_metadata": METADATA_FILE,
        "source_folder": str(base_folder),

        "tile_output_size_px": TILE_OUTPUT_SIZE,

        "atlas_file": str(output_atlas_path),
        "atlas_width_px": atlas_width,
        "atlas_height_px": atlas_height,

        "cols": cols,
        "rows": rows,

        "bounds": {
            "left": min_left,
            "bottom": min_bottom,
            "right": max_right,
            "top": max_top,
        },

        "tile_size_map_units": {
            "width": tile_width_m,
            "height": tile_height_m,
        },

        "tiles": output_tiles,
    }

    with output_metadata_path.open("w", encoding="utf-8") as f:
        json.dump(atlas_metadata, f, indent=2, ensure_ascii=False)

    print("")
    print("DONE")
    print(f"Atlas saved to: {output_atlas_path}")
    print(f"Metadata saved to: {output_metadata_path}")


if __name__ == "__main__":
    main()