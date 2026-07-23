# Pigeon builder — Blender bpy via MCP. Drop-in pour Assets/Scripts/Gameplay/Pigeon.cs.
#
# Parts attendues par le script (FindDeep par nom) : "Head" (picore, pivot au COU),
# "WingL"/"WingR" (pivot EPAULE, battent autour de Z). Style cute chunky LISSE
# (smooth + Subsurf 1) comme les pietons. Front = -Y, Z-up. Palette : gris pigeon
# + ventre clair + collerette teal + bec/pattes orange.
#
# Convention aile (repliquee du prefab primitif) : pivot a l'epaule, aile le long
# de X (laterale). En Blender front -Y, objet aile SANS rotation -> apres export
# (axis -Z fwd / Y up) l'axe Z local Unity = forward -> le DOLocalRotate Z du
# script fait battre l'aile haut/bas. Origine objet = epaule (verts vers +/-X).

import bpy, bmesh, math
from mathutils import Vector

def mat(name,rgb,rough=0.8):
    m=bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes=True; b=m.node_tree.nodes.get("Principled BSDF")
    b.inputs["Base Color"].default_value=(*rgb,1); b.inputs["Roughness"].default_value=rough
    return name

def palette():
    mat("Pigeon_Grey",(0.47,0.50,0.56)); mat("Pigeon_Belly",(0.60,0.62,0.66))
    mat("Pigeon_Dark",(0.31,0.33,0.40)); mat("Pigeon_Neck",(0.36,0.62,0.58))
    mat("Pigeon_Beak",(0.90,0.52,0.30)); mat("Pigeon_Foot",(0.88,0.46,0.34))
    mat("Pigeon_Eye",(0.05,0.05,0.06),0.4); mat("Pigeon_EyeR",(0.92,0.55,0.20),0.4)

def lighting():
    L=bpy.data.objects.get("Light")
    if L and L.type=='LIGHT':
        L.data.type='SUN'; L.data.energy=3.0; L.data.color=(1.0,0.96,0.88)
        L.rotation_euler=(math.radians(55),math.radians(12),math.radians(40))
    sc=bpy.context.scene; sc.render.engine='BLENDER_EEVEE'
    try: sc.view_settings.view_transform='Standard'
    except Exception: pass
    if sc.world is None: sc.world=bpy.data.worlds.new("World")
    sc.world.use_nodes=True
    w=sc.world.node_tree.nodes.get("Background")
    if w: w.inputs[0].default_value=(0.90,0.87,0.80,1); w.inputs[1].default_value=0.55

# ---------- helpers (echelle bakee, smooth+subsurf, keep-transform) ----------
def _finish(bm,name,parent,matname,loc=(0,0,0),rot=(0,0,0),sub=1,solid=0.0,smooth=True):
    me=bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    for p in me.polygons: p.use_smooth=smooth
    if solid:
        sm=ob.modifiers.new("sol",'SOLIDIFY'); sm.thickness=solid; sm.offset=0
    if sub:
        s=ob.modifiers.new("sub",'SUBSURF'); s.levels=sub; s.render_levels=sub
    ob.data.materials.append(bpy.data.materials[matname])
    ob.location=loc; ob.rotation_euler=rot
    if parent:
        ob.parent=parent; bpy.context.view_layer.update()
        ob.matrix_parent_inverse=parent.matrix_world.inverted()
    return ob

def uvbm(cx,cy,cz,sx,sy,sz,u=18,v=12):
    bm=bmesh.new(); bmesh.ops.create_uvsphere(bm,u_segments=u,v_segments=v,radius=0.5)
    for vv in bm.verts:
        vv.co.x=vv.co.x*sx+cx; vv.co.y=vv.co.y*sy+cy; vv.co.z=vv.co.z*sz+cz
    return bm

def cone_bm(cx,cy,cz,r1,r2,h,verts=12,rot=(0,0,0)):
    bm=bmesh.new(); bmesh.ops.create_cone(bm,cap_ends=True,segments=verts,radius1=r1,radius2=r2,depth=h)
    R=None
    if any(rot):
        import mathutils
        R=mathutils.Euler(rot,'XYZ').to_matrix()
    for vv in bm.verts:
        if R: vv.co=R@vv.co
        vv.co+=Vector((cx,cy,cz))
    return bm

# ---------- pigeon ----------
def build_pigeon():
    palette()
    root=bpy.data.objects.new("Pigeon",None); bpy.context.collection.objects.link(root)
    # CORPS : UN oeuf plombe, poitrine bombee a l'avant (-Y), queue vers +Y.
    body=uvbm(0,0.05,0.22, 0.32,0.52,0.36, u=22,v=15)
    bodyob=_finish(body,"Body",root,"Pigeon_Grey")
    bodyob.data.materials.append(bpy.data.materials["Pigeon_Belly"])
    for p in bodyob.data.polygons:                      # ventre clair = bas-avant
        c=p.center; p.material_index = 1 if (c.z<0.18 and c.y<0.12) else 0
    # POITRINE-PONT : gros bombe avant qui relie corps -> cou -> tete (pas de gap)
    _finish(uvbm(0,-0.17,0.27, 0.28,0.28,0.32, u=18,v=12),"Chest",root,"Pigeon_Grey")

    # TETE : ronde, chevauche la poitrine (raccordee), pivot = cou a l'origine
    neck=(0,-0.20,0.34)
    hb=uvbm(neck[0],neck[1]-0.09,neck[2]+0.07, 0.22,0.22,0.22, u=16,v=12)
    head=_finish(hb,"Head",root,"Pigeon_Grey",loc=(0,0,0))
    # collerette iridescente teal (patch cou/poitrine) a la jonction
    _finish(uvbm(0,-0.18,0.32, 0.22,0.14,0.18, u=12,v=9),"Neck",head,"Pigeon_Neck")
    # bec orange fin vers l'avant (-Y)
    _finish(cone_bm(0,-0.44,0.42, 0.05,0.006,0.13, verts=10, rot=(math.radians(-90),0,0)),
            "Beak",head,"Pigeon_Beak")
    # yeux SUR la surface de la tete (centre (0,-0.29,0.41) r~0.11), embed leger
    for sgn,nm in ((-1,"EyeL"),(1,"EyeR")):
        _finish(uvbm(sgn*0.082,-0.35,0.45, 0.05,0.05,0.05, u=8,v=6),nm+"r",head,"Pigeon_EyeR",sub=0)
        _finish(uvbm(sgn*0.090,-0.365,0.45, 0.030,0.032,0.032, u=8,v=6),nm,head,"Pigeon_Eye",sub=0)

    # AILES REPLIEES : goutte plate PLAQUEE sur le flanc (racine embed dans le
    # corps), s'etend vers la queue. Origine objet = EPAULE. Flanc du corps a
    # x~0.29 -> aile a x~0.24-0.28 (colle, racine un peu dedans).
    for sgn,nm in ((-1,"WingL"),(1,"WingR")):
        sh=(sgn*0.24,-0.06,0.28)                         # epaule sur le flanc
        bm=bmesh.new(); bmesh.ops.create_uvsphere(bm,u_segments=14,v_segments=10,radius=0.5)
        for vv in bm.verts:
            x=vv.co.x; y=vv.co.y; z=vv.co.z
            yy=(y+0.5)                                   # 0 avant(epaule) -> 1 arriere(pointe)
            taper=max(0.12,1.0-0.55*yy)
            # aile contre le flanc : fine en X, longue en Y, galbee en Z ; le BOUT
            # se rentre vers le centre (tuck) pour ne pas faire une palette plate
            vv.co.x = x*0.07 - sgn*0.03 - sgn*0.09*yy    # racine embed + pointe qui rentre
            vv.co.y = y*0.40 + 0.20                      # vers l'arriere
            vv.co.z = z*0.20*taper - 0.03*yy             # galbe, descend vers la pointe
        w=_finish(bm,nm,root,"Pigeon_Grey",loc=sh,solid=0.015)
        w.data.materials.append(bpy.data.materials["Pigeon_Dark"])
        for p in w.data.polygons:                        # bord de fuite + pointe foncés (barre d'aile)
            c=p.center; p.material_index = 1 if c.y>0.20 else 0

    # QUEUE : eventail plus plein, releve ~20deg, chevauche le corps (pas de gap)
    tail=uvbm(0,0.40,0.30, 0.20,0.34,0.08, u=16,v=9)
    _finish(tail,"Tail",root,"Pigeon_Dark",rot=(math.radians(-20),0,0))

    # PATTES orange courtes + doigts
    for sgn,nm in ((-1,"FootL"),(1,"FootR")):
        _finish(cone_bm(sgn*0.07,0.0,0.04, 0.018,0.028,0.09),nm,root,"Pigeon_Foot",sub=1)
        _finish(uvbm(sgn*0.07,-0.04,-0.005, 0.09,0.11,0.02, u=8,v=5),nm+"toe",root,"Pigeon_Foot",sub=1)
    return root

# ================================================================ OISEAU LOINTAIN
# Oiseau vu de LOIN dans les airs : silhouette de vol simple (ailes deployees,
# balayees + diedre, queue fourchue). Low-poly (pas de detail utile a distance).
# Front -Y. Parts WingL/WingR (pivot epaule) + Head pour battre (drop-in Pigeon.cs
# style ou script d'ambiance). Dos sombre + ventre clair (lu par-dessous).
def skybird_palette():
    mat("Sky_Top",(0.40,0.44,0.52)); mat("Sky_Belly",(0.72,0.74,0.78))
    mat("Sky_Tip",(0.26,0.28,0.34)); mat("Sky_Beak",(0.88,0.52,0.30))

def build_skybird():
    skybird_palette()
    root=bpy.data.objects.new("SkyBird",None); bpy.context.collection.objects.link(root)
    # CORPS fuselé (le long de Y)
    body=uvbm(0,0.0,0.0, 0.13,0.44,0.13, u=14,v=9)
    bo=_finish(body,"Body",root,"Sky_Top")
    bo.data.materials.append(bpy.data.materials["Sky_Belly"])
    for p in bo.data.polygons: p.material_index = 1 if p.center.z<0.0 else 0   # ventre clair
    # TETE + bec
    head=_finish(uvbm(0,-0.26,0.02, 0.13,0.13,0.13, u=12,v=8),"Head",root,"Sky_Top")
    _finish(cone_bm(0,-0.36,0.02, 0.028,0.004,0.10, verts=8, rot=(math.radians(-90),0,0)),
            "Beak",head,"Sky_Beak")
    # AILES DEPLOYEES : lame effilee, balayee vers l'arriere + diedre vers le haut
    for sgn,nm in ((-1,"WingL"),(1,"WingR")):
        sh=(sgn*0.06,-0.02,0.04)
        bm=bmesh.new(); bmesh.ops.create_uvsphere(bm,u_segments=16,v_segments=8,radius=0.5)
        for vv in bm.verts:
            x=vv.co.x*0.54; y=vv.co.y*0.16; z=vv.co.z*0.028
            outer=abs(x)/0.54                       # 0 racine -> 1 pointe
            taper=max(0.10,1.0-0.72*outer)
            vv.co.x = x + sgn*0.06                   # part de l'epaule vers l'exterieur
            vv.co.y = y*taper + 0.30*outer           # balaye vers l'arriere (+Y)
            vv.co.z = z + 0.16*outer                 # diedre : monte vers la pointe
        w=_finish(bm,nm,root,"Sky_Top",loc=sh,solid=0.01)
        w.data.materials.append(bpy.data.materials["Sky_Tip"])
        for p in w.data.polygons:                    # bout d'aile foncé
            if abs(p.center.x)>0.30: p.material_index=1
    # QUEUE FOURCHUE (2 pointes)
    for sgn in (-1,1):
        t=bmesh.new(); bmesh.ops.create_uvsphere(t,u_segments=10,v_segments=6,radius=0.5)
        for vv in t.verts:
            x=vv.co.x*0.05; y=vv.co.y*0.20; z=vv.co.z*0.02
            outer=(vv.co.y+0.5)                      # vers l'arriere
            vv.co.x = x + sgn*0.06*outer             # les 2 pointes s'ecartent (fourche)
            vv.co.y = y + 0.28
            vv.co.z = z
        _finish(t,"TailL" if sgn<0 else "TailR",root,"Sky_Tip")
    return root

def export_skybird(outdir):
    import os
    root=bpy.data.objects["SkyBird"]
    for o in bpy.data.objects: o.select_set(False)
    stack=[root]; sel=[]
    while stack:
        o=stack.pop(); sel.append(o); o.select_set(True); stack.extend(list(o.children))
    bpy.context.view_layer.objects.active=root
    fp=os.path.join(outdir,"SkyBird.fbx").replace("\\","/")
    with bpy.context.temp_override(selected_objects=sel, active_object=root, object=root,
                                   selected_editable_objects=sel):
        bpy.ops.export_scene.fbx(filepath=fp, use_selection=True,
            object_types={'MESH','EMPTY'}, use_mesh_modifiers=True, mesh_smooth_type='FACE',
            add_leaf_bones=False, bake_anim=False, apply_scale_options='FBX_SCALE_NONE',
            use_custom_props=False, axis_forward='-Z', axis_up='Y', bake_space_transform=False)
    return fp

def export_pigeon(outdir):
    import os
    root=bpy.data.objects["Pigeon"]
    for o in bpy.data.objects: o.select_set(False)
    stack=[root]; sel=[]
    while stack:
        o=stack.pop(); sel.append(o); o.select_set(True); stack.extend(list(o.children))
    bpy.context.view_layer.objects.active=root
    fp=os.path.join(outdir,"Pigeon.fbx").replace("\\","/")
    with bpy.context.temp_override(selected_objects=sel, active_object=root, object=root,
                                   selected_editable_objects=sel):
        bpy.ops.export_scene.fbx(filepath=fp, use_selection=True,
            object_types={'MESH','EMPTY'}, use_mesh_modifiers=True, mesh_smooth_type='FACE',
            add_leaf_bones=False, bake_anim=False, apply_scale_options='FBX_SCALE_NONE',
            use_custom_props=False, axis_forward='-Z', axis_up='Y', bake_space_transform=False)
    return fp
