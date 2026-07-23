# City asset builder — Blender bpy, à exécuter via Blender MCP (execute_blender_code).
#
# Méthode & pièges : docs/city-pipeline.md. Palette/style : mémoire city-color-palette
# + docs/city-assets.md. NE PAS terminer un run sans `result = {...}` (dict) si lancé
# via execute_blender_code.
#
# Réutiliser tel quel : helpers box()/cyl()/organic() + palette warm + build_shop()
# paramétré. Pour un NOUVEL asset (immeuble R+4, prop, véhicule...), garder les mêmes
# helpers + palette, écrire une nouvelle fonction build_<asset>() sur le même modèle.
#
# PIÈGES (voir doc) : nettoyer les orphelins en début de run ; pas de boolean (verre
# en relief, pas de trou) ; face avant = -Y ; select_all(DESELECT) avant chaque join ;
# flat shading (use_smooth=False) ; feuillage subdiv2 pèse ~10k tris (LOD plus tard).

import bpy, bmesh, random, math
from mathutils import Vector, noise

# ---------------------------------------------------------------- scene reset
def clean(keep=("Camera", "CitySun", "Light")):
    for ob in list(bpy.data.objects):
        if ob.name not in keep:
            bpy.data.objects.remove(ob, do_unlink=True)
    for m in list(bpy.data.meshes):
        if m.users == 0:
            bpy.data.meshes.remove(m)

# ---------------------------------------------------------------- warm palette
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

def lighting():
    sun = bpy.data.objects.get("CitySun")
    if not sun:
        ld = bpy.data.lights.new("CitySun", 'SUN'); sun = bpy.data.objects.new("CitySun", ld)
        bpy.context.collection.objects.link(sun)
    sun.data.energy = 2.8; sun.data.color = (1.0, 0.95, 0.86)
    sun.rotation_euler = (math.radians(55), math.radians(15), math.radians(40))
    w = bpy.context.scene.world.node_tree.nodes["Background"]
    w.inputs[0].default_value = (0.90, 0.87, 0.80, 1); w.inputs[1].default_value = 0.30
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_EEVEE'; sc.render.resolution_x = 800; sc.render.resolution_y = 800
    try:
        sc.view_settings.view_transform = 'Standard'; sc.view_settings.look = 'None'
        sc.view_settings.exposure = -0.2
    except Exception:
        pass

# ---------------------------------------------------------------- geo helpers
# (module-level CUR/OX so box/cyl/organic append to the current asset, offset in X)
# CUR = parts rigides (base). VEG = feuillage (organic) -> objet enfant séparé,
# JAMAIS fusionné dans la base : Unity a besoin du feuillage comme MeshRenderer
# distinct pour lui appliquer un shader de vent (oscillation). Voir lessons.
CUR = []; VEG = []; OX = 0.0; _idx = [0]
def _M(n): return bpy.data.materials[n]

def box(cx, cy, cz, sx, sy, sz, m, name="p"):
    me = bpy.data.meshes.new(name); ob = bpy.data.objects.new(name, me)
    bpy.context.collection.objects.link(ob)
    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1); bm.to_mesh(me); bm.free()
    ob.scale = (sx, sy, sz); ob.location = (cx + OX, cy, cz); ob.data.materials.append(_M(m))
    CUR.append(ob); return ob

def cyl(cx, cy, cz, r, h, m, name="c", verts=8, axis='Z'):
    me = bpy.data.meshes.new(name); ob = bpy.data.objects.new(name, me)
    bpy.context.collection.objects.link(ob)
    bm = bmesh.new(); bmesh.ops.create_cone(bm, cap_ends=True, segments=verts, radius1=r, radius2=r, depth=h)
    bm.to_mesh(me); bm.free(); ob.location = (cx + OX, cy, cz)
    if axis == 'X': ob.rotation_euler = (0, 1.5708, 0)
    elif axis == 'Y': ob.rotation_euler = (1.5708, 0, 0)
    ob.data.materials.append(_M(m)); CUR.append(ob); return ob

def organic(cx, cy, cz, s, m, name="veg", sub=2, amp=0.42):
    """Touffe végétale organique : icosphère déplacée au bruit cohérent 2 octaves."""
    _idx[0] += 1
    o1 = Vector((_idx[0]*4.1, _idx[0]*2.7, _idx[0]*3.3))
    o2 = Vector((_idx[0]*7.3, _idx[0]*5.1, _idx[0]*6.2))
    me = bpy.data.meshes.new(name); ob = bpy.data.objects.new(name, me)
    bpy.context.collection.objects.link(ob)
    bm = bmesh.new(); bmesh.ops.create_icosphere(bm, subdivisions=sub, radius=1.0)
    for v in bm.verts:
        n = noise.noise(v.co*1.7 + o1)*0.7 + noise.noise(v.co*3.6 + o2)*0.3
        v.co += v.normal*(amp*n) + Vector((random.uniform(-.05, .05),)*3)
    bmesh.ops.bevel(bm, geom=bm.edges[:], offset=0.03, segments=1, affect='EDGES')
    bm.to_mesh(me); bm.free()
    ob.scale = (s*random.uniform(.85, 1.2), s*random.uniform(.85, 1.2), s*random.uniform(.7, 1.0))
    ob.location = (cx + OX, cy, cz)
    ob.rotation_euler = (random.uniform(-.25, .25), random.uniform(-.25, .25), random.uniform(0, 3.14))
    ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth = False
    VEG.append(ob); return ob  # feuillage -> liste veg séparée (objet enfant, shader vent)

def finish(name, parts=None):
    """Joint `parts` (défaut CUR) en un mesh flat-shaded nommé `name`."""
    parts = CUR if parts is None else parts
    if not parts: return None
    bpy.ops.object.select_all(action='DESELECT')
    for p in parts: p.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]; bpy.ops.object.join()
    b = bpy.context.active_object; b.name = name
    for poly in b.data.polygons: poly.use_smooth = False
    bpy.ops.object.select_all(action='DESELECT')
    return b

# ---------------------------------------------------------------- shop builder
def build_shop(tag, ox, wall, awn, fri, door, sign, n_rows, dens=1.0, has_awn=True):
    global CUR, VEG, OX
    CUR = []; VEG = []; OX = ox; random.seed(hash(tag) % 1000)
    WALL = mat(f"{tag}_wall", wall); AWN = mat(f"{tag}_awn", awn); FRI = mat(f"{tag}_fri", fri)
    DOOR = mat(f"{tag}_door", door); SIGN = mat(f"{tag}_sign", sign)
    FRAME, GLASS, STONE, METAL = "City_Frame_White", "City_Window_Glass", "City_Roof_Grey", "City_Metal_Dk"
    TERRA, LEAF, LEAF2, FLOWER = "City_Terracotta", "City_Leaf", "City_Leaf_Dk", "City_Flower"
    fy = -1.5
    row_zs = [3.4 + i*1.8 for i in range(n_rows)]
    frieze_z = row_zs[-1] + 1.22; top_cornice_z = frieze_z + 0.40; roof_z = frieze_z + 0.73
    body_H = roof_z - 0.15; body_cz = body_H/2
    box(0, 0, body_cz, 4, 3, body_H, WALL, "body")
    for z in row_zs:
        for x in (-1.0, 1.0):
            box(x, fy+0.01, z, 1.45, 0.10, 1.55, FRAME, "frame")
            box(x, fy, z, 1.2, 0.12, 1.3, GLASS, "win")
            box(x, fy-0.05, z, 0.05, 0.08, 1.3, FRAME, "mv"); box(x, fy-0.05, z, 1.2, 0.08, 0.05, FRAME, "mh")
            zb = z - 0.72
            box(x, fy-0.17, zb+0.02, 1.3, 0.30, 0.16, TERRA, "planter")
            for dx in (-0.42, -0.14, 0.14, 0.42):
                organic(x+dx, fy-0.27, zb+0.14, 0.25, LEAF if random.random() > .4 else LEAF2, "box_leaf")
            for _ in range(2):
                organic(x+random.uniform(-.35, .35), fy-0.32, zb+0.20, 0.10, FLOWER, "bloom", sub=1, amp=0.45)
        box(0, fy-0.03, z-0.68, 4.05, 0.14, 0.10, FRAME, "cornice")
    box(0, fy-0.04, frieze_z, 3.6, 0.10, 0.44, FRI, "frieze")
    box(0, fy-0.10, frieze_z, 3.5, 0.03, 0.34, FRAME, "frieze_inset")
    box(0, fy-0.05, top_cornice_z, 4.05, 0.18, 0.14, FRAME, "top_cornice")
    cyl(0, fy-0.22, frieze_z, 0.17, 0.06, FRAME, "clk_face", 16, 'Y')
    cyl(0, fy-0.17, frieze_z, 0.20, 0.05, METAL, "clk_rim", 16, 'Y')
    box(0, fy-0.27, frieze_z+0.08, 0.02, 0.02, 0.12, METAL, "clk_m")
    box(0.05, fy-0.27, frieze_z, 0.09, 0.02, 0.02, METAL, "clk_h")
    box(-0.5, fy+0.01, 1.2, 2.2, 0.10, 2.0, FRAME, "shopframe")
    box(-0.5, fy, 1.2, 2.0, 0.12, 1.8, GLASS, "shopwin")
    box(1.3, fy, 1.05, 0.7, 0.14, 2.1, DOOR, "door")
    box(1.55, fy-0.09, 1.05, 0.06, 0.05, 0.35, METAL, "handle")
    if has_awn:
        box(-0.5, fy-0.25, 2.3, 2.2, 0.5, 0.18, AWN, "awning")
        box(-0.5, fy-0.49, 2.24, 2.2, 0.04, 0.30, FRAME, "awn_edge")
    box(0, 0, 0.3, 4.15, 3.15, 0.6, STONE, "plinth")
    box(0.05, -1.70, 0.50, 3.3, 0.30, 0.20, STONE, "s1")
    box(0.05, -1.88, 0.30, 3.3, 0.66, 0.20, STONE, "s2")
    box(0.05, -2.10, 0.10, 3.3, 1.10, 0.20, STONE, "s3")
    box(2.08, 0.7, body_cz+0.7, 0.20, 0.85, 0.62, STONE, "ac")
    for k in range(3): box(2.19, 0.7, body_cz+0.5+k*0.16, 0.02, 0.72, 0.05, METAL, "slat")
    box(2.05, 0.7, body_cz+0.33, 0.14, 0.55, 0.06, METAL, "ac_br")
    cyl(2.0, 1.05, body_cz-0.2, 0.03, 0.9, METAL, "acpipe", 6)
    cyl(1.9, 1.45, body_cz+0.1, 0.06, body_H-0.2, METAL, "downpipe", 6)
    box(1.85, fy-0.55, 2.05, 0.05, 0.55, 0.45, SIGN, "sign")
    cyl(1.85, fy-0.30, 2.25, 0.03, 0.55, METAL, "sarm", 6, 'Y')
    box(1.85, fy-0.30, 2.27, 0.05, 0.55, 0.05, METAL, "sbar")
    box(0, 0, roof_z, 4.2, 3.2, 0.35, STONE, "roof")
    rt = roof_z + 0.175
    for _ in range(int(9*dens)):
        organic(random.uniform(-1.7, 1.7), random.uniform(-1.3, 1.3), rt+0.3,
                random.uniform(0.30, 0.46), LEAF if random.random() > .35 else LEAF2, "roofbush", amp=0.46)
    box(-1.4, -0.9, rt+0.45, 0.4, 0.4, 0.7, STONE, "chimney")
    box(-1.4, -0.9, rt+0.83, 0.5, 0.5, 0.08, METAL, "chimcap")
    box(0.9, 0.6, rt+0.3, 0.5, 0.5, 0.25, METAL, "vent")
    cyl(1.3, 1.0, rt+0.75, 0.02, 1.4, METAL, "antenna", 5)
    box(1.3, 1.0, rt+1.35, 0.35, 0.02, 0.25, METAL, "anttop")
    for i in range(int((body_H+2)/0.55)):
        z = 0.9 + i*0.55
        if z > frieze_z - 0.2: break
        off = 0.14*math.sin(i*1.2)
        organic(-1.92+off, fy-0.06, z, 0.21+random.uniform(-.03, .05), LEAF2 if i % 2 else LEAF, "ivy", amp=0.48)
    for px in (-1.95, 1.95):
        cyl(px, -1.9, 0.18, 0.16, 0.36, TERRA, "pot", 8)
        organic(px, -1.9, 0.47, 0.28, LEAF, "potbush", amp=0.44)
        organic(px+0.08, -1.9, 0.55, 0.11, FLOWER, "potbloom", sub=1, amp=0.45)
    base = finish(f"Shop_{tag}", CUR); veg = finish(f"Shop_{tag}_Veg", VEG)
    return base, veg  # base rigide + feuillage enfant séparé (None si pas de veg)

# 5 variantes validées (famille chaude) : tag, ox, wall, awn, frieze, door, sign, n_rows, dens, awn
SHOP_SPECS = [
    ("A", -13, (0.95, 0.82, 0.66), (0.96, 0.47, 0.40), (0.28, 0.72, 0.68), (0.83, 0.57, 0.34), (0.28, 0.72, 0.68), 2, 1.0, True),
    ("B", -6.5, (0.96, 0.74, 0.60), (0.30, 0.66, 0.62), (0.97, 0.80, 0.38), (0.28, 0.55, 0.52), (0.30, 0.66, 0.62), 3, 1.2, True),
    ("C", 0, (0.86, 0.86, 0.70), (0.96, 0.47, 0.40), (0.96, 0.55, 0.45), (0.83, 0.57, 0.34), (0.96, 0.55, 0.45), 2, 0.8, True),
    ("D", 6.5, (0.96, 0.87, 0.60), (0.28, 0.72, 0.68), (0.97, 0.60, 0.66), (0.90, 0.45, 0.40), (0.97, 0.60, 0.66), 1, 1.0, False),
    ("E", 13, (0.93, 0.72, 0.68), (0.55, 0.70, 0.45), (0.28, 0.72, 0.68), (0.83, 0.57, 0.34), (0.55, 0.70, 0.45), 3, 1.4, True),
]

# ---------------------------------------------------------------- export
def export_fbx(obj, outdir):
    import os
    os.makedirs(outdir, exist_ok=True)
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True); bpy.context.view_layer.objects.active = obj
    bpy.ops.object.origin_set(type='ORIGIN_GEOMETRY', center='BOUNDS'); obj.location = (0, 0, 0)
    zmin = min((obj.matrix_world @ v.co).z for v in obj.data.vertices)
    obj.location = (0, 0, -zmin)  # pivot at base -> sits on ground in Unity
    bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)
    fp = os.path.join(outdir, obj.name + ".fbx")
    bpy.ops.export_scene.fbx(filepath=fp, use_selection=True, object_types={'MESH'},
        apply_unit_scale=True, apply_scale_options='FBX_SCALE_ALL',
        mesh_smooth_type='FACE', bake_space_transform=True, axis_forward='-Z', axis_up='Y')
    return fp
# Puis côté Unity : import_model_file(source_path=<fbx>, output_folder="Assets/Models/City", name=...)

if __name__ == "__main__":
    clean(); palette(); lighting()
    for s in SHOP_SPECS:
        build_shop(*s)
