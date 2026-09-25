#!/usr/bin/env python3
"""p4c: writes p4c_texlights.vmf + p4c_texlights.rad, the synthetic map that
exercises every direct-light emit type stock vrad has.

No catalogue map carries a texlight, a spotlight or a sun, so the catalogue
alone gates only emit_point. This room adds:

  emit_surface    a ceiling panel in lights/white001, lit through the .rad
  emit_spotlight  a light_spot aimed by `target`, and one by angles
  emit_point      a light_spot with both cones at 180 (becomes a point light),
                  a plain light with _fifty_percent_distance (solved falloff)
                  and one with _hardfalloff, and a style-2 light
  emit_skylight   a light_environment over a toolsskybox half-ceiling
  emit_skyambient its ambient partner, with SunSpreadAngle set

usage: make_texlight_fixture.py <outdir>
"""
import os
import sys

ids = [0]


def nid():
    ids[0] += 1
    return ids[0]


def side(plane, material, uaxis, vaxis, scale=16):
    return f"""		side
		{{
			"id" "{nid()}"
			"plane" "{plane}"
			"material" "{material}"
			"uaxis" "{uaxis}"
			"vaxis" "{vaxis}"
			"rotation" "0"
			"lightmapscale" "{scale}"
			"smoothing_groups" "0"
		}}
"""


def box(mins, maxs, material, faces=None, scale=16):
    """An axial box. `faces` maps a face name to a material override."""
    faces = faces or {}
    x0, y0, z0 = mins
    x1, y1, z1 = maxs
    m = lambda k: faces.get(k, material)
    sides = [
        side(f"({x0} {y1} {z1}) ({x1} {y1} {z1}) ({x1} {y0} {z1})", m("top"),
             "[1 0 0 0] 0.25", "[0 -1 0 0] 0.25", scale),
        side(f"({x0} {y0} {z0}) ({x1} {y0} {z0}) ({x1} {y1} {z0})", m("bottom"),
             "[1 0 0 0] 0.25", "[0 -1 0 0] 0.25", scale),
        side(f"({x1} {y1} {z1}) ({x1} {y1} {z0}) ({x1} {y0} {z0})", m("east"),
             "[0 1 0 0] 0.25", "[0 0 -1 0] 0.25", scale),
        side(f"({x0} {y0} {z1}) ({x0} {y0} {z0}) ({x0} {y1} {z0})", m("west"),
             "[0 1 0 0] 0.25", "[0 0 -1 0] 0.25", scale),
        side(f"({x1} {y1} {z1}) ({x0} {y1} {z1}) ({x0} {y1} {z0})", m("north"),
             "[1 0 0 0] 0.25", "[0 0 -1 0] 0.25", scale),
        side(f"({x1} {y0} {z0}) ({x0} {y0} {z0}) ({x0} {y0} {z1})", m("south"),
             "[1 0 0 0] 0.25", "[0 0 -1 0] 0.25", scale),
    ]
    return "\tsolid\n\t{\n" + f'\t\t"id" "{nid()}"\n' + "".join(sides) + "\t}\n"


def entity(classname, **kv):
    body = "".join(f'\t"{k}" "{v}"\n' for k, v in kv.items())
    return f'entity\n{{\n\t"id" "{nid()}"\n\t"classname" "{classname}"\n{body}}}\n'


def main():
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)

    concrete = "concrete/concretefloor001a"
    brick = "brick/brickwall001a"
    sky = "tools/toolsskybox"

    solids = [
        box((-272, -272, -16), (272, 272, 0), concrete),              # floor
        box((-272, -272, 256), (0, 272, 272), concrete),              # ceiling, west half
        box((0, -272, 256), (272, 272, 272), sky),                    # ceiling, east half: sky
        box((256, -272, 0), (272, 272, 256), brick),                  # east wall
        box((-272, -272, 0), (-256, 272, 256), brick),                # west wall
        box((-256, 256, 0), (256, 272, 256), brick),                  # north wall
        box((-256, -272, 0), (256, -256, 256), brick),                # south wall
        # the texlight panel, hanging under the concrete half of the ceiling
        box((-192, -64, 240), (-64, 64, 248), concrete,
            faces={"bottom": "lights/white001"}),
        # a pillar to cast shadows
        box((-32, -32, 0), (32, 32, 128), brick),
    ]

    world = ('world\n{\n\t"id" "1"\n\t"mapversion" "1"\n\t"classname" "worldspawn"\n'
             '\t"skyname" "sky_day01_01"\n\t"maxpropscreenwidth" "-1"\n'
             + "".join(solids) + "}\n")

    ents = [
        entity("info_player_start", origin="-128 -128 16", angles="0 0 0"),
        entity("light_environment", origin="128 0 200", angles="-60 30 0",
               pitch="-60", _light="255 240 200 400", _ambient="120 140 200 60",
               _lightHDR="-1 -1 -1 1", _ambientHDR="-1 -1 -1 1", SunSpreadAngle="5"),
        entity("light_spot", origin="-200 200 200", angles="0 0 0", pitch="0",
               target="p4c_spot_target", _light="255 200 150 600", _inner_cone="30",
               _cone="45", _exponent="2", _lightHDR="200 200 255 900", _lightscaleHDR="2"),
        entity("info_target", targetname="p4c_spot_target", origin="-200 -100 0"),
        entity("light_spot", origin="200 -200 200", angles="-90 0 0", pitch="-90",
               _light="150 255 150 500", _inner_cone="20", _cone="60", _exponent="1"),
        entity("light_spot", origin="-150 -200 64", angles="0 90 0", pitch="0",
               _light="255 255 255 100", _inner_cone="180", _cone="180"),
        entity("light", origin="100 150 64", _light="255 128 64 300",
               _fifty_percent_distance="128", _zero_percent_distance="400"),
        entity("light", origin="-100 150 64", _light="64 128 255 300",
               _fifty_percent_distance="96", _zero_percent_distance="256", _hardfalloff="1"),
        entity("light", origin="0 -150 180", _light="255 255 255 200", style="2",
               targetname="p4c_flicker", _quadratic_attn="1", _linear_attn="0.5"),
        entity("light", origin="150 150 40", _light="255 64 64"),
    ]

    vmf = ('versioninfo\n{\n\t"editorversion" "400"\n\t"editorbuild" "8075"\n'
           '\t"mapversion" "1"\n\t"formatversion" "100"\n\t"prefab" "0"\n}\n'
           'visgroups\n{\n}\nviewsettings\n{\n\t"bSnapToGrid" "1"\n\t"nGridSpacing" "16"\n}\n'
           + world + "".join(ents)
           + 'cameras\n{\n\t"activecamera" "-1"\n}\ncordon\n{\n\t"mins" "(-1024 -1024 -1024)"\n'
           '\t"maxs" "(1024 1024 1024)"\n\t"active" "0"\n}\n')

    with open(os.path.join(out, "p4c_texlights.vmf"), "w", newline="\r\n") as f:
        f.write(vmf)

    # LDR and HDR spellings both, so -both exercises the hdr:/ldr: prefixes.
    rad = ("ldr:lights/white001 255 255 255 200\n"
           "hdr:lights/white001 255 240 220 400\n")
    with open(os.path.join(out, "p4c_texlights.rad"), "w", newline="\r\n") as f:
        f.write(rad)


if __name__ == "__main__":
    main()
