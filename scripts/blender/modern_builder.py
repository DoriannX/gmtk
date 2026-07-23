# Modern glass/concrete tower builder — Blender bpy, via Blender MCP (execute_blender_code).
#
# Style : tour HAUTE verre/beton (curtain wall grille + dalles cantilever + penthouse
# retrait) = silhouette moderne DISTINCTE des batiments chauds trapus (Shop/Apartment/
# Corner) et du townhouse a pignon. Palette gardee CHAUDE/JOYEUSE (regle ville) :
# beton creme chaud, verre bleu ciel, spandrels coral/turquoise/jaune, terrasse
# vegetalisee. Voir docs/city-session-lessons.md + memoire city-color-palette.
#
# Reutilise les helpers city_builder (box/cyl/organic/finish/palette/lighting).
# organic() -> liste VEG SEPAREE : le feuillage est un objet enfant `_Veg` distinct
# (shader vent Unity), JAMAIS fusionne dans la base. build_modern renvoie (base, veg).
#
# PIEGES traites (lessons) :
#  - RDC "porte verre sur grande plaque de verre" = illisible -> storefront GRILLE
#    metal (rails + meneaux) qui decoupe le verre en travees + porte double vitree
#    CERCLEE metal et SAILLANTE (plan avance) -> la porte ressort et se lit.
#  - jamais 2 faces coplanaires : chaque couche (verre fixe / cadre / vantail /
#    poignee) a un offset Y distinct.
#  - materiaux VARIANTES en per-tag (MD_<tag>_*) pour ne pas ecraser les canoniques
#    partages (cf. Corner/Townhouse). Verre/blanc/metal/terracotta/feuilles restent
#    canoniques (partages entre variantes).
#
# Blender ici = 5.1, moteur BLENDER_EEVEE (EEVEE_NEXT indispo sur cette install).

import bpy, bmesh, random, math
from mathutils import Vector, noise

# ------------------------------------------------------------ scene reset / palette
def clean(keep=("Camera", "CitySun", "Light")):
    for ob in list(bpy.data.objects):
        if ob.name not in keep:
            bpy.data.objects.remove(ob, do_unlink=True)
    for m in list(bpy.data.meshes):
        if m.users == 0:
            bpy.data.meshes.remove(m)

def mat(name, rgb, rough=0.7):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes.get("Principled BSDF")
    b.inputs["Base Color"].default_value = (*rgb, 1)
    b.inputs["Roughness"].default_value = rough
    return name

def palette():
    mat("City_Frame_White", (0.97, 0.93, 0.85)); mat("City_Window_Glass", (0.58, 0.80, 0.90))
    mat("City_Roof_Grey",  (0.72, 0.66, 0.60)); mat("City_Metal_Dk",    (0.53, 0.51, 0.53))
    mat("City_Terracotta", (0.84, 0.48, 0.32)); mat("City_Leaf",        (0.48, 0.73, 0.34))
    mat("City_Leaf_Dk",    (0.34, 0.57, 0.28)); mat("City_Flower",      (0.97, 0.52, 0.58))
    mat("City_Wall_Beige", (0.95, 0.82, 0.66)); mat("City_Trim_Red",    (0.96, 0.47, 0.40))
    mat("City_Door_Wood",  (0.83, 0.57, 0.34)); mat("City_Sign",        (0.28, 0.72, 0.68))
    mat("City_Concrete",   (0.82, 0.77, 0.69))

def lighting():
    sun = bpy.data.objects.get("CitySun")
    if not sun:
        ld = bpy.data.lights.new("CitySun", 'SUN'); sun = bpy.data.objects.new("CitySun", ld)
        bpy.context.collection.objects.link(sun)
    sun.data.energy = 2.8; sun.data.color = (1.0, 0.95, 0.86)
    sun.rotation_euler = (math.radians(55), math.radians(15), math.radians(40))
    w = bpy.context.scene.world.node_tree.nodes["Background"]
    w.inputs[0].default_value = (0.90, 0.87, 0.80, 1); w.inputs[1].default_value = 0.30
    sc = bpy.context.scene; sc.render.engine = 'BLENDER_EEVEE'
    sc.render.resolution_x = 820; sc.render.resolution_y = 1040
    try:
        sc.view_settings.view_transform = 'Standard'; sc.view_settings.look = 'None'; sc.view_settings.exposure = -0.2
    except Exception:
        pass

# ------------------------------------------------------------ geo helpers
CUR = []; VEG = []; OX = 0.0; _idx = [0]
def _M(n): return bpy.data.materials[n]

def box(cx, cy, cz, sx, sy, sz, m, name="p"):
    me = bpy.data.meshes.new(name); ob = bpy.data.objects.new(name, me); bpy.context.collection.objects.link(ob)
    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1); bm.to_mesh(me); bm.free()
    ob.scale = (sx, sy, sz); ob.location = (cx + OX, cy, cz); ob.data.materials.append(_M(m)); CUR.append(ob); return ob

def cyl(cx, cy, cz, r, h, m, name="c", verts=8, axis='Z'):
    me = bpy.data.meshes.new(name); ob = bpy.data.objects.new(name, me); bpy.context.collection.objects.link(ob)
    bm = bmesh.new(); bmesh.ops.create_cone(bm, cap_ends=True, segments=verts, radius1=r, radius2=r, depth=h)
    bm.to_mesh(me); bm.free(); ob.location = (cx + OX, cy, cz)
    if axis == 'X': ob.rotation_euler = (0, 1.5708, 0)
    elif axis == 'Y': ob.rotation_euler = (1.5708, 0, 0)
    ob.data.materials.append(_M(m)); CUR.append(ob); return ob

def organic(cx, cy, cz, s, m, name="veg", sub=2, amp=0.42):
    """Touffe vegetale facettee (icosphere + bruit 2 octaves + bevel, flat, PAS de subsurf)."""
    _idx[0] += 1
    o1 = Vector((_idx[0]*4.1, _idx[0]*2.7, _idx[0]*3.3)); o2 = Vector((_idx[0]*7.3, _idx[0]*5.1, _idx[0]*6.2))
    me = bpy.data.meshes.new(name); ob = bpy.data.objects.new(name, me); bpy.context.collection.objects.link(ob)
    bm = bmesh.new(); bmesh.ops.create_icosphere(bm, subdivisions=sub, radius=1.0)
    for v in bm.verts:
        n = noise.noise(v.co*1.7 + o1)*0.7 + noise.noise(v.co*3.6 + o2)*0.3
        v.co += v.normal*(amp*n) + Vector((random.uniform(-.05, .05),)*3)
    bmesh.ops.bevel(bm, geom=bm.edges[:], offset=0.03, segments=1, affect='EDGES'); bm.to_mesh(me); bm.free()
    ob.scale = (s*random.uniform(.85, 1.2), s*random.uniform(.85, 1.2), s*random.uniform(.7, 1.0))
    ob.location = (cx + OX, cy, cz)
    ob.rotation_euler = (random.uniform(-.25, .25), random.uniform(-.25, .25), random.uniform(0, 3.14))
    ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth = False
    VEG.append(ob); return ob

def finish(name, parts):
    if not parts: return None
    bpy.ops.object.select_all(action='DESELECT')
    for p in parts: p.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]; bpy.ops.object.join()
    b = bpy.context.active_object; b.name = name
    for poly in b.data.polygons: poly.use_smooth = False
    bpy.ops.object.select_all(action='DESELECT'); return b

# canoniques partages (ne pas recolorer par variante)
GLASS, FRAME, METAL = "City_Window_Glass", "City_Frame_White", "City_Metal_Dk"
WOOD, TURQ, TERRA = "City_Door_Wood", "City_Sign", "City_Terracotta"
LEAF, LEAF2, FLOWER = "City_Leaf", "City_Leaf_Dk", "City_Flower"

# ------------------------------------------------------------ MODERN tower builder
def build_modern(tag="A", ox=0.0, conc=(0.82, 0.77, 0.69),
                 spand=None, NF=6, dens=1.0):
    """Tour verre/beton parametree. conc=teinte beton (per-tag). spand=liste de RGB
    des panneaux spandrel (per-tag). NF=nb etages tour (R+NF ~). dens=densite verdure.
    Renvoie (base, veg) : base rigide + feuillage `_Veg` separe."""
    global CUR, VEG, OX
    CUR = []; VEG = []; OX = ox; random.seed(hash(tag) % 1000)
    CONC = mat(f"MD_{tag}_conc", conc)                       # beton per-tag
    if spand is None:
        spand = [(0.96, 0.47, 0.40), (0.28, 0.72, 0.68), conc]
    SPAND = [mat(f"MD_{tag}_sp{i}", c) for i, c in enumerate(spand)]
    W, D = 4.6, 3.6; HW, HD = W/2, D/2
    POD_H = 2.4; Wp, Dp = 5.2, 4.2; HDp = Dp/2
    FH = 1.5; z_tow = POD_H; z_main_top = z_tow + NF*FH
    Wph, Dph = 3.4, 2.4; PH_H = 1.5; z_ph_top = z_main_top + PH_H
    # etages a detailler (clampes a NF) : brise-soleil + balcons plantes
    louv_floors = sorted({1, max(1, NF-3)})
    balc_floors = sorted({2, max(2, NF-2)})

    def fp(axis, u, z, w, h, out, thick, m, name="fp"):    # panneau oriente par face
        if axis == 'F':   box(u, -HD-out, z, w, thick, h, m, name)
        elif axis == 'B': box(u,  HD+out, z, w, thick, h, m, name)
        elif axis == 'R': box(HW+out, u, z, thick, w, h, m, name)
        elif axis == 'L': box(-HW-out, u, z, thick, w, h, m, name)

    def face(axis, span):                                   # curtain wall : verre + spandrel + meneaux
        nb = max(2, round(span/1.3)); bw = span/nb
        for f in range(NF):
            fb = z_tow + f*FH; sp_h = FH*0.40; sp_z = fb + sp_h/2 + 0.06
            gl_h = FH*0.50; gl_z = fb + sp_h + 0.06 + gl_h/2
            for b in range(nb):
                u = -span/2 + bw*(b+0.5)
                fp(axis, u, sp_z, bw-0.04, sp_h, 0.03, 0.05, SPAND[(f+b) % len(SPAND)], "spandrel")
                fp(axis, u, gl_z, bw-0.04, gl_h, 0.09, 0.04, GLASS, "glass")
            for b in range(nb+1):
                u = -span/2 + bw*b; fp(axis, u, fb+FH/2, 0.07, FH, 0.15, 0.05, FRAME, "mv")

    # ---- shell : noyau + podium ----
    box(0, 0, z_tow + (NF*FH)/2, W, D, NF*FH, CONC, "core")
    box(0, 0, POD_H/2, Wp, Dp, POD_H, CONC, "podium")
    box(0, 0, POD_H-0.08, Wp+0.2, Dp+0.2, 0.20, FRAME, "pod_cap")

    # ---- RDC storefront : grille metal + travees vitrees + PORTE VERRE encadree saillante ----
    span = 4.6; nb = 5; bw = span/nb; base_top = 0.20; head_z = 2.08
    yF = -HDp-0.10; yG = -HDp-0.08
    box(0, yF, 0.10, span+0.14, 0.07, 0.20, METAL, "sf_base")
    box(0, yF, head_z+0.05, span+0.14, 0.07, 0.12, METAL, "sf_head")
    for b in range(nb+1):
        u = -span/2 + bw*b; box(u, yF, (base_top+head_z)/2, 0.08, 0.07, head_z-base_top, METAL, "sf_mull")
    for b in (0, 1, 3, 4):
        u = -span/2 + bw*(b+0.5)
        box(u, yG, (base_top+head_z)/2, bw-0.12, 0.05, head_z-base_top, GLASS, "sf_glass")
    # porte double verre : cadre saillant (montants + imposte) + 2 vantaux cercles metal + poignees
    uc = 0.0; dw = bw-0.04; dh = 1.9; yJ = -HDp-0.06; yLf = -HDp-0.10; yLg = -HDp-0.16
    for s in (-1, 1): box(uc + s*(dw/2+0.02), yJ, dh/2+0.1, 0.10, 0.10, dh+0.2, METAL, "d_jamb")
    box(uc, yJ, dh+0.12, dw+0.22, 0.10, 0.12, METAL, "d_head")
    box(uc, yG, (dh+0.18+head_z)/2, dw-0.04, 0.05, max(0.15, head_z-dh-0.18), GLASS, "d_transom")
    for s in (-1, 1):
        cx = uc + s*(dw/4); lw = dw/2-0.08; lh = dh-0.14
        box(cx, yLf, dh/2+0.06, lw+0.09, 0.05, lh+0.09, METAL, "leaf_frame")     # cercle metal
        box(cx, yLg, dh/2+0.06, lw, 0.05, lh, GLASS, "leaf_glass")               # vantail verre saillant
        box(cx, yLf-0.02, 1.02, lw+0.02, 0.04, 0.09, METAL, "leaf_midrail")
    box(uc-0.055, yLg-0.05, 1.0, 0.045, 0.06, 0.55, METAL, "handle_l")
    box(uc+0.055, yLg-0.05, 1.0, 0.045, 0.06, 0.55, METAL, "handle_r")
    box(uc, -HDp-0.24, 0.12, 1.3, 0.55, 0.12, CONC, "entry_step")
    # casquette entree
    box(0, -HDp-0.55, POD_H-0.02, 3.0, 1.2, 0.14, CONC, "canopy")
    box(0, -HDp-1.12, POD_H-0.02, 3.0, 0.06, 0.16, FRAME, "canopy_edge")

    # ---- facades vitrees 4 faces (aucune face nue) + dalles cantilever + piliers ----
    face('F', W); face('B', W); face('L', D); face('R', D)
    for f in range(NF+1):
        z = z_tow + f*FH
        box(0, 0, z, W+0.34, D+0.34, 0.16, CONC, "slab"); box(0, 0, z, W+0.40, D+0.40, 0.05, FRAME, "slab_edge")
    for sx in (-1, 1):
        for sy in (-1, 1): box(sx*(HW+0.02), sy*(HD+0.02), z_tow + (NF*FH)/2, 0.34, 0.34, NF*FH, CONC, "pier")

    # ---- penthouse en retrait -> cree une terrasse au niveau z_main_top ----
    box(0, 0.4, z_main_top + PH_H/2, Wph, Dph, PH_H, CONC, "ph_core")
    box(0, 0.4-Dph/2-0.05, z_main_top + PH_H*0.55, Wph-0.4, 0.10, PH_H*0.6, GLASS, "ph_glass")
    for mx in (-1.0, 0, 1.0): box(mx, 0.4-Dph/2-0.11, z_main_top + PH_H*0.55, 0.06, 0.06, PH_H*0.6, FRAME, "ph_mv")
    box(0, 0, z_ph_top, Wph+0.2, Dph+0.2, 0.14, CONC, "ph_roof")

    # ---- garde-corps verre autour de la terrasse ----
    RAILH = 0.55; rz = z_main_top + 0.16 + RAILH/2
    box(0, -HD-0.02, rz, W+0.2, 0.05, RAILH, GLASS, "rail_F")
    box(-HW-0.02, 0, rz, 0.05, D+0.2, RAILH, GLASS, "rail_L"); box(HW+0.02, 0, rz, 0.05, D+0.2, RAILH, GLASS, "rail_R")
    for u in [x*0.7 for x in range(-3, 4)]: box(u, -HD-0.02, rz+RAILH/2, 0.05, 0.07, 0.05, METAL, "rail_post")
    box(0, -HD-0.02, rz+RAILH/2, W+0.2, 0.06, 0.05, METAL, "rail_top")

    # ---- toit penthouse : edicule + antenne + condenseur clim + gaine ----
    rt = z_ph_top + 0.07
    box(1.0, 0.9, rt+0.35, 0.9, 0.8, 0.7, METAL, "mech"); box(1.0, 0.9, rt+0.72, 1.0, 0.9, 0.06, FRAME, "mech_cap")
    cyl(-1.0, 0.6, rt+0.8, 0.03, 1.6, METAL, "antenna", 6); box(-1.0, 0.6, rt+1.55, 0.4, 0.03, 0.28, METAL, "ant_top")
    box(-0.2, 1.0, rt+0.25, 0.7, 0.6, 0.5, METAL, "ac2")
    for k in range(3): box(-0.2, 1.0, rt+0.12+k*0.15, 0.72, 0.62, 0.04, FRAME, "ac2slat")
    cyl(0.5, 0.2, rt+0.35, 0.08, 0.7, METAL, "duct", 8)

    # ---- enseigne + downlights casquette ----
    box(0, -HDp-1.18, POD_H+0.12, 1.7, 0.05, 0.34, TURQ, "logo"); box(0, -HDp-1.21, POD_H+0.12, 1.15, 0.03, 0.20, FRAME, "logo_txt")
    for lx in (-1.1, -0.37, 0.37, 1.1): box(lx, -HDp-0.55, POD_H-0.11, 0.10, 0.10, 0.04, FRAME, "dlight")

    # ---- brise-soleil horizontaux (accent moderne, face avant) ----
    for f in louv_floors:
        fb = z_tow + f*FH
        for s in range(3): box(0, -HD-0.24, fb+0.55+s*0.32, W-0.2, 0.22, 0.04, METAL, "louver")
        for ex in (-HW+0.18, HW-0.18): box(ex, -HD-0.24, fb+0.9, 0.05, 0.24, 1.05, METAL, "louver_end")

    # ---- balcons plantes (face avant) : jardiniere sur dalle + retombant drapant ----
    for f in balc_floors:
        bz = z_tow + f*FH
        for bx in (-1.3, 0.0, 1.3):
            box(bx, -HD-0.30, bz+0.16, 0.9, 0.34, 0.20, TERRA, "balc_planter")
            for dx in (-0.3, 0.0, 0.3):
                organic(bx+dx, -HD-0.36, bz+0.36, 0.24*dens**0.3, LEAF if random.random() > .4 else LEAF2, "balc_bush", amp=0.46)
            organic(bx, -HD-0.40, bz+0.04, 0.20, LEAF2, "balc_trail", amp=0.5)         # drape sur la dalle
            organic(bx+0.25, -HD-0.40, bz+0.42, 0.10, FLOWER, "balc_bloom", sub=1, amp=0.45)

    # ---- terrasse toit : jardin cotes L/R + pergola + grimpante ----
    for j in range(5):
        yv = -1.3 + j*0.66
        for sx in (-1, 1):
            box(sx*(HW-0.35), yv, z_main_top+0.28, 0.5, 0.5, 0.20, TERRA, "tpl_side")
            organic(sx*(HW-0.35), yv, z_main_top+0.55, 0.28*dens**0.3, LEAF if j % 2 else LEAF2, "tbush_side", amp=0.46)
    pf = z_main_top + 0.16
    for px in (-1.7, 1.7):
        for py in (-1.55, -0.75): box(px, py, pf+0.55, 0.08, 0.08, 1.1, METAL, "perg_post")
    for py in (-1.55, -0.75): box(0, py, pf+1.12, 3.5, 0.08, 0.08, METAL, "perg_beam")
    for px in [x*0.7 for x in (-2, -1, 0, 1, 2)]: box(px, -1.15, pf+1.16, 0.06, 0.9, 0.05, METAL, "perg_slat")
    organic(-1.7, -1.55, pf+0.9, 0.22, LEAF, "perg_vine", amp=0.5)
    # lierre sur pilier avant (variantes luxuriantes)
    for i in range(int(4*dens)):
        z = z_tow + 0.5 + i*0.8
        if z > z_main_top - 0.4: break
        organic(-(HW+0.02), -HD-0.02, z, 0.20, LEAF2 if i % 2 else LEAF, "pier_ivy", amp=0.48)

    base = finish(f"Modern_{tag}", CUR); veg = finish(f"Modern_{tag}_Veg", VEG)
    return base, veg

# 5 variantes (famille moderne, palette chaude) : tag, ox, beton, spandrels, NF, dens
MODERN_SPECS = [
    ("A", -15, (0.82, 0.77, 0.69), [(0.96, 0.47, 0.40), (0.28, 0.72, 0.68), (0.82, 0.77, 0.69)], 6, 1.0),
    ("B",  -7, (0.86, 0.80, 0.72), [(0.28, 0.72, 0.68), (0.96, 0.47, 0.40)],                     5, 0.8),
    ("C",   1, (0.90, 0.83, 0.70), [(0.96, 0.55, 0.45), (0.97, 0.60, 0.66), (0.90, 0.83, 0.70)], 7, 1.2),
    ("D",   9, (0.80, 0.78, 0.74), [(0.28, 0.72, 0.68), (0.55, 0.70, 0.45)],                     8, 1.0),
    ("E",  17, (0.88, 0.79, 0.66), [(0.96, 0.47, 0.40), (0.97, 0.80, 0.38), (0.28, 0.72, 0.68)], 6, 1.4),
]

# ------------------------------------------------------------ export FBX
def export_fbx(obj, outdir):
    import os
    os.makedirs(outdir, exist_ok=True)
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True); bpy.context.view_layer.objects.active = obj
    bpy.ops.object.origin_set(type='ORIGIN_GEOMETRY', center='BOUNDS'); obj.location = (0, 0, 0)
    zmin = min((obj.matrix_world @ v.co).z for v in obj.data.vertices)
    obj.location = (0, 0, -zmin)  # pivot au sol -> pose sur le sol dans Unity
    bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)
    fp = os.path.join(outdir, obj.name + ".fbx")
    bpy.ops.export_scene.fbx(filepath=fp, use_selection=True, object_types={'MESH'},
        apply_unit_scale=True, apply_scale_options='FBX_SCALE_ALL',
        mesh_smooth_type='FACE', bake_space_transform=True, axis_forward='-Z', axis_up='Y')
    return fp

def export_pair(base, veg, outdir):
    """Exporte base + `_Veg` ENSEMBLE dans un seul FBX (2 meshes enfants sous un root)."""
    import os
    os.makedirs(outdir, exist_ok=True)
    objs = [o for o in (base, veg) if o]
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs: o.select_set(True)
    bpy.context.view_layer.objects.active = base
    # pivot au sol, centre en XY sur la base
    bpy.ops.object.origin_set(type='ORIGIN_GEOMETRY', center='BOUNDS')  # origine base
    dx, dy = base.location.x, base.location.y
    zmin = min(min((o.matrix_world @ v.co).z for v in o.data.vertices) for o in objs)
    for o in objs: o.location = (o.location.x - dx, o.location.y - dy, o.location.z - zmin)
    bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)
    fp = os.path.join(outdir, base.name + ".fbx")
    bpy.ops.export_scene.fbx(filepath=fp, use_selection=True, object_types={'MESH'},
        apply_unit_scale=True, apply_scale_options='FBX_SCALE_ALL',
        mesh_smooth_type='FACE', bake_space_transform=True, axis_forward='-Z', axis_up='Y')
    return fp

# ------------------------------------------------------------ camera / demos
def setup_cam(target, loc, lens=42):
    cam = bpy.data.objects.get("Camera")
    if not cam:
        cd = bpy.data.cameras.new("Camera"); cam = bpy.data.objects.new("Camera", cd); bpy.context.collection.objects.link(cam)
    cam.location = Vector(loc); d = cam.location - Vector(target)
    cam.rotation_euler = d.to_track_quat('Z', 'Y').to_euler(); cam.data.lens = lens
    bpy.context.scene.camera = cam

def demo_spread():
    """Construit les 5 variantes espacees en X (pour render overview)."""
    clean(); palette(); lighting()
    objs = []
    for s in MODERN_SPECS:
        objs.append(build_modern(*s))
    return objs

def build_one(spec):
    """Construit une variante seule a l'origine (ox=0) pour export FBX."""
    clean(); palette(); lighting()
    tag = spec[0]
    return build_modern(tag, 0.0, spec[2], spec[3], spec[4], spec[5])
