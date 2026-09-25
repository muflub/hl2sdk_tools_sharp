#!/usr/bin/env python3
"""Generates p4g_ambient.vmf, the leaf-ambient unit-tier fixture map.

Self-made (no shipped map source): one sealed room built from axis-aligned box
brushes, exercising every branch leaf ambient has --
  * a power-3 displacement floor (ClipRayToDispInLeaf in the tracer and in the
    sample-rejection probe, barycentric luxel coordinates),
  * a func_detail pillar (brush rejection of candidate samples),
  * a texlight panel made an emit_surface light by p4g_ambient.rad
    (AddEmitSurfaceLights and its TestLine visibility),
  * a skybox section of ceiling and a light_environment (sky ambient, sky
    faces on nodes),
  * an ordinary point light.
Material NAMES from the reference dev set are referenced; nothing is copied.

Recipe:
    python3 gen_ambient_fixture.py > p4g_ambient.vmf
    stock vbsp, vvis -fast, vrad -both -threads 1 (x64, under wine)
    with p4g_ambient.rad as the level lights file, then the pak lump is dropped
    (BspData.SetLump(PakFile, empty) + BspFile.SaveAsync). Stock log:
    "32 of 32 (100% of) surface lights went in leaf ambient cubes", LDR 160 records.
"""
import math
import sys

# --detail: the detail-prop variant (p4g_detail.vmf): a plain floor carrying a
# %detailtype material instead of the displacement, and a spot light.
DETAIL = "--detail" in sys.argv
# --props: the static-prop variant (p4g_props.vmf): plain floor, four
# prop_static (one pushed into a wall so some vertices sit in solid). Prop 0
# does not shadow itself, prop 1 ignores normals and prop 2 is lit from an
# info_lighting, so every flag static-prop lighting reads is exercised.
PROPS = "--props" in sys.argv

W, D, H = 768, 768, 384
T = 16
ids = iter(range(1, 100000))
GEN = "DEV/DEV_MEASUREGENERIC01"
WALL = "DEV/DEV_MEASUREWALL01D"
LIGHT = "DEV/DEV_MEASUREWALL01A"      # made a texlight by p4g_ambient.rad
SKY = "TOOLS/TOOLSSKYBOX"
NODRAW = "TOOLS/TOOLSNODRAW"


def axes(n):
    if n in ("top", "bottom"):
        return "[1 0 0 0] 0.25", "[0 -1 0 0] 0.25"
    if n in ("left", "right"):
        return "[0 1 0 0] 0.25", "[0 0 -1 0] 0.25"
    return "[1 0 0 0] 0.25", "[0 0 -1 0] 0.25"


def box(x0, y0, z0, x1, y1, z1, mats, disp=None):
    planes = {
        "top": f"({x0} {y1} {z1}) ({x1} {y1} {z1}) ({x1} {y0} {z1})",
        "bottom": f"({x0} {y0} {z0}) ({x1} {y0} {z0}) ({x1} {y1} {z0})",
        "left": f"({x0} {y1} {z1}) ({x0} {y0} {z1}) ({x0} {y0} {z0})",
        "right": f"({x1} {y1} {z0}) ({x1} {y0} {z0}) ({x1} {y0} {z1})",
        "back": f"({x1} {y1} {z1}) ({x0} {y1} {z1}) ({x0} {y1} {z0})",
        "front": f"({x1} {y0} {z0}) ({x0} {y0} {z0}) ({x0} {y0} {z1})",
    }
    out = [f'\tsolid\n\t{{\n\t\t"id" "{next(ids)}"']
    for name, plane in planes.items():
        u, v = axes(name)
        mat = mats.get(name, mats.get("*"))
        out.append(f'\t\tside\n\t\t{{\n\t\t\t"id" "{next(ids)}"\n\t\t\t"plane" "{plane}"\n'
                   f'\t\t\t"material" "{mat}"\n\t\t\t"uaxis" "{u}"\n\t\t\t"vaxis" "{v}"\n'
                   f'\t\t\t"rotation" "0"\n\t\t\t"lightmapscale" "16"\n\t\t\t"smoothing_groups" "0"')
        if disp and name == "top":
            out.append(disp)
        out.append("\t\t}")
    out.append("\t}")
    return "\n".join(out)


def dispinfo(x0, y0, z):
    n = 9
    def rows(fn):
        return "\n".join(f'\t\t\t\t\t"row{r}" "{fn(r)}"' for r in range(n))
    def dist(r, c):
        return 24 * math.sin(r * 0.7) * math.cos(c * 0.5) + 8
    blocks = [
        ("normals", lambda r: " ".join("0 0 1" for _ in range(n))),
        ("distances", lambda r: " ".join(f"{dist(r, c):.4g}" for c in range(n))),
        ("offsets", lambda r: " ".join("0 0 0" for _ in range(n))),
        ("offset_normals", lambda r: " ".join("0 0 1" for _ in range(n))),
        ("alphas", lambda r: " ".join("0" for _ in range(n))),
    ]
    text = [f'\t\t\tdispinfo\n\t\t\t{{\n\t\t\t\t"power" "3"\n\t\t\t\t"startposition" "[{x0} {y0} {z}]"\n'
            f'\t\t\t\t"flags" "0"\n\t\t\t\t"elevation" "0"\n\t\t\t\t"subdiv" "0"']
    for name, fn in blocks:
        text.append(f"\t\t\t\t{name}\n\t\t\t\t{{\n{rows(fn)}\n\t\t\t\t}}")
    tags = "\n".join(f'\t\t\t\t\t"row{r}" "{" ".join("0" for _ in range(16))}"' for r in range(8))
    text.append(f"\t\t\t\ttriangle_tags\n\t\t\t\t{{\n{tags}\n\t\t\t\t}}")
    text.append('\t\t\t\tallowed_verts\n\t\t\t\t{\n\t\t\t\t\t"10" "-1 -1 -1 -1 -1 -1 -1 -1 -1 -1"\n\t\t\t\t}')
    text.append("\t\t\t}")
    return "\n".join(text)


def entity(classname, origin, extra=""):
    return (f'entity\n{{\n\t"id" "{next(ids)}"\n\t"classname" "{classname}"\n'
            f'\t"origin" "{origin}"\n{extra}}}')


solids = []
x0, y0, x1, y1 = -W // 2, -D // 2, W // 2, D // 2
# floor: its top is a displacement over the inner room
solids.append(box(x0 - T, y0 - T, -T, x1 + T, y1 + T, 0,
                  {"*": NODRAW, "top": "P4G/DETAILFLOOR" if DETAIL else GEN},
                  None if (DETAIL or PROPS) else dispinfo(x0 - T, y0 - T, 0)))
# a displacement's brush does not seal the world (vbsp takes it out of the
# structural BSP), so a plain nodraw slab under it does
solids.append(box(x0 - T, y0 - T, -2 * T, x1 + T, y1 + T, -T, {"*": NODRAW}))
# walls
solids.append(box(x0 - T, y0, -2 * T, x0, y1, H, {"*": WALL}))
solids.append(box(x1, y0, -2 * T, x1 + T, y1, H, {"*": WALL}))
solids.append(box(x0 - T, y0 - T, -2 * T, x1 + T, y0, H, {"*": WALL}))
solids.append(box(x0 - T, y1, -2 * T, x1 + T, y1 + T, H, {"*": WALL}))
# ceiling: a lit half, a sky half
solids.append(box(x0 - T, y0 - T, H, 0, y1 + T, H + T, {"*": GEN}))
solids.append(box(0, y0 - T, H, x1 + T, y1 + T, H + T, {"*": SKY}))
# a texlight panel hanging under the lit half
solids.append(box(-256, -64, H - 8, -128, 64, H, {"*": LIGHT}))

world = ('world\n{\n\t"id" "1"\n\t"mapversion" "1"\n\t"classname" "worldspawn"\n'
         '\t"skyname" "sky_day01_01"\n'
         + ('\t"detailmaterial" "detail/detailsprites"\n\t"detailvbsp" "detail.vbsp"\n' if DETAIL else '')
         + "\n".join(solids) + "\n}")

detail = ('entity\n{\n\t"id" "' + str(next(ids)) + '"\n\t"classname" "func_detail"\n'
          + box(96, -160, 48, 160, -96, 320, {"*": WALL}) + "\n}")

ents = [
    entity("info_player_start", "-200 200 64", '\t"angles" "0 0 0"\n'),
    entity("light", "-100 -200 200", '\t"_light" "255 240 200 300"\n'),
    entity("light_environment", "200 0 300",
           '\t"angles" "-60 30 0"\n\t"pitch" "-60"\n\t"_light" "255 250 230 200"\n'
           '\t"_ambient" "120 140 170 60"\n'),
]
if DETAIL:
    ents.append(entity("light_spot", "250 -250 300",
                       '\t"angles" "-70 135 0"\n\t"pitch" "-70"\n\t"_light" "200 220 255 400"\n'
                       '\t"_inner_cone" "20"\n\t"_cone" "40"\n\t"_exponent" "2"\n'))
    # a styled light (style 5, "gentle pulse"): detail props get lightstyle records
    ents.append(entity("light", "-250 250 150", '\t"_light" "255 120 60 200"\n\t"style" "5"\n'))
if PROPS:
    ents.append(entity("light_spot", "250 -250 300",
                       '\t"angles" "-70 135 0"\n\t"pitch" "-70"\n\t"_light" "200 220 255 400"\n'
                       '\t"_inner_cone" "20"\n\t"_cone" "40"\n\t"_exponent" "2"\n'))
    ents.append(entity("light", "-250 250 150", '\t"_light" "255 120 60 200"\n\t"style" "5"\n'))
    for origin, angles, model, flags in [
        ("-250 -250 0", "0 30 0", "models/props_c17/FurnitureBoiler001a.mdl", '\t"disableselfshadowing" "1"\n'),
        ("200 200 40", "0 0 90", "models/props_wasteland/wheel01a.mdl", '\t"ignorenormals" "1"\n'),
        ("-60 360 0", "0 90 0", "models/props_c17/fence01a.mdl", '\t"lightingorigin" "p4g_lo"\n'),
        ("300 -100 0", "0 200 0", "models/props_junk/garbage256_composite002b.mdl", ""),
    ]:
        ents.append(entity("prop_static", origin,
                           f'\t"angles" "{angles}"\n\t"model" "{model}"\n\t"solid" "6"\n\t"skin" "0"\n{flags}'))
    ents.append(entity("info_lighting", "-60 300 64", '\t"targetname" "p4g_lo"\n'))

print('versioninfo\n{\n\t"editorversion" "400"\n\t"editorbuild" "6412"\n\t"mapversion" "1"\n'
      '\t"formatversion" "100"\n\t"prefab" "0"\n}')
print(world)
print(detail)
for e in ents:
    print(e)
