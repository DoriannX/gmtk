# Street props builder — Blender bpy, via Blender MCP (execute_blender_code).
#
# Props urbains (se posent sur les trottoirs du kit rue, cf street_builder.py) :
# lampadaire, banc, poubelle, feu tricolore, abribus. Style commun : fonte teal
# (City_Iron_Green) + accents laiton (City_Brass), palette CHAUDE/joyeuse, low-poly
# facetté, végétal facetté Shop_A. Échelle calée sur le kit rue (module 8, étage
# bâtiment ~1.8 m) : lampadaire ~4 m, feu ~3.2 m, abribus ~2.5 m.
#
# Chaque prop = base rigide + objet enfant `*_Veg` SÉPARÉ (feuillage/fleurs) pour le
# shader de vent Unity (règle d'export, cf city-session-lessons). Pivot export = base
# au sol, centré XY. FBX -Z fwd / Y up.
#
# LEÇONS CLÉS (voir docs/city-session-lessons.md §16-18) :
#  - Lanterne qui "glow" = matériau émissif (Emission Color + Strength), pas juste clair.
#  - Verre d'abribus LISIBLE = transparent (Principled Alpha 0.30 + blend_method='BLEND'),
#    sinon lit comme une caisse fermée.
#  - Fleur LISIBLE = pétales (isphere aplaties en couronne) + cœur jaune, PAS un blob vert
#    (un blob organic seul lit "buisson"). Panier fleuri = bol terracotta visible + petit
#    dôme vert + retombantes + fleurs colorées variées dessus, sur un HOOK dédié dégagé.

import bpy, bmesh, random, math, os
from mathutils import Vector, noise

# ---------------------------------------------------------------- scene / mats
def clean(keep=("Camera","CitySun","Light")):
    for ob in list(bpy.data.objects):
        if ob.name not in keep: bpy.data.objects.remove(ob, do_unlink=True)
    for m in list(bpy.data.meshes):
        if m.users==0: bpy.data.meshes.remove(m)
def mat(name,rgb,rough=0.7):
    m=bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes=True; b=m.node_tree.nodes.get("Principled BSDF")
    b.inputs["Base Color"].default_value=(*rgb,1); b.inputs["Roughness"].default_value=rough
    return name
def mat_emit(name,rgb,strength=2.2):
    m=bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes=True; b=m.node_tree.nodes.get("Principled BSDF")
    b.inputs["Base Color"].default_value=(*rgb,1)
    try: b.inputs["Emission Color"].default_value=(*rgb,1); b.inputs["Emission Strength"].default_value=strength
    except Exception: b.inputs["Emission"].default_value=(*rgb,1); b.inputs["Emission Strength"].default_value=strength
    return name
def glass_mat():
    m=bpy.data.materials.get("City_Window_Glass") or bpy.data.materials.new("City_Window_Glass")
    m.use_nodes=True; b=m.node_tree.nodes.get("Principled BSDF")
    b.inputs["Base Color"].default_value=(0.62,0.82,0.92,1); b.inputs["Roughness"].default_value=0.15
    b.inputs["Alpha"].default_value=0.30; m.blend_method='BLEND'
    return "City_Window_Glass"
def palette():
    mat("City_Frame_White",(0.97,0.93,0.85)); mat("City_Metal_Dk",(0.50,0.48,0.49))
    mat("City_Leaf",(0.48,0.73,0.34)); mat("City_Leaf_Dk",(0.34,0.57,0.28))
    mat("City_Terracotta",(0.84,0.48,0.32)); mat("City_Wood",(0.74,0.52,0.32),0.6)
    mat("City_Iron_Green",(0.19,0.33,0.30),0.5); mat("City_Brass",(0.80,0.60,0.28),0.4)
    mat("City_Sign_Blue",(0.30,0.55,0.75)); mat("City_Roof_Grey",(0.70,0.63,0.56))
    mat("City_Sign_Red",(0.86,0.25,0.22)); mat("City_Fire_Red",(0.82,0.24,0.19),0.5)
    mat("City_Bag",(0.16,0.18,0.17),0.30); mat("City_Bag2",(0.20,0.24,0.22),0.30); mat("City_Can_Red",(0.80,0.30,0.26))
    mat("City_Flower",(0.97,0.52,0.58)); mat("City_Flower_Y",(0.98,0.80,0.34))
    mat("City_Flower_C",(0.97,0.55,0.42)); mat("City_Flower_W",(0.98,0.95,0.90))
    mat("City_Flower_P",(0.72,0.56,0.86)); mat("City_Flower_Ctr",(0.99,0.86,0.36))
    mat_emit("City_Lamp_Glow",(1.0,0.85,0.55),3.0)
    mat_emit("City_Red",(0.92,0.24,0.18)); mat_emit("City_Amber",(0.98,0.72,0.24)); mat_emit("City_GreenLite",(0.34,0.78,0.44))
    glass_mat()
def lighting():
    sun=bpy.data.objects.get("CitySun")
    if not sun:
        ld=bpy.data.lights.new("CitySun",'SUN'); sun=bpy.data.objects.new("CitySun",ld); bpy.context.collection.objects.link(sun)
    sun.data.energy=2.8; sun.data.color=(1.0,0.95,0.86); sun.rotation_euler=(math.radians(55),math.radians(15),math.radians(40))
    w=bpy.context.scene.world.node_tree.nodes["Background"]; w.inputs[0].default_value=(0.90,0.87,0.80,1); w.inputs[1].default_value=0.30
    sc=bpy.context.scene; sc.render.engine='BLENDER_EEVEE'
    try: sc.view_settings.view_transform='Standard'; sc.view_settings.look='None'; sc.view_settings.exposure=-0.2
    except Exception: pass

# ---------------------------------------------------------------- geo helpers
CUR=[]; VEG=[]; OX=0.0; _idx=[0]
def _M(n): return bpy.data.materials[n]
def box(cx,cy,cz,sx,sy,sz,m,name="p",rot=(0,0,0),lst=None):
    lst=CUR if lst is None else lst
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_cube(bm,size=1); bm.to_mesh(me); bm.free()
    ob.scale=(sx,sy,sz); ob.location=(cx+OX,cy,cz); ob.rotation_euler=rot; ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth=False
    lst.append(ob); return ob
def cone(cx,cy,cz,r1,r2,h,m,name="c",verts=8,lst=None,rot=(0,0,0)):
    lst=CUR if lst is None else lst
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_cone(bm,cap_ends=True,segments=verts,radius1=r1,radius2=r2,depth=h); bm.to_mesh(me); bm.free()
    ob.location=(cx+OX,cy,cz); ob.rotation_euler=rot; ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth=False
    lst.append(ob); return ob
def cyl(cx,cy,cz,r,h,m,name="c",verts=8,rot=(0,0,0)): return cone(cx,cy,cz,r,r,h,m,name,verts,rot=rot)
def cyl_between(p0,p1,r,m,name="arm",verts=8):
    p0=Vector(p0); p1=Vector(p1); vec=p1-p0; L=vec.length; mid=(p0+p1)/2
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_cone(bm,cap_ends=True,segments=verts,radius1=r,radius2=r,depth=L); bm.to_mesh(me); bm.free()
    ob.location=(mid.x+OX,mid.y,mid.z); ob.rotation_euler=vec.to_track_quat('Z','Y').to_euler(); ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth=False
    CUR.append(ob); return ob
def organic(cx,cy,cz,s,m,name="veg",sub=2,amp=0.42,lst=None):
    lst=VEG if lst is None else lst
    _idx[0]+=1; o1=Vector((_idx[0]*4.1,_idx[0]*2.7,_idx[0]*3.3)); o2=Vector((_idx[0]*7.3,_idx[0]*5.1,_idx[0]*6.2))
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_icosphere(bm,subdivisions=sub,radius=1.0)
    for v in bm.verts:
        n=noise.noise(v.co*1.7+o1)*0.7+noise.noise(v.co*3.6+o2)*0.3
        v.co+=v.normal*(amp*n)+Vector((random.uniform(-.05,.05),)*3)
    bmesh.ops.bevel(bm,geom=bm.edges[:],offset=0.03,segments=1,affect='EDGES'); bm.to_mesh(me); bm.free()
    ob.scale=(s*random.uniform(.85,1.2),s*random.uniform(.85,1.2),s*random.uniform(.7,1.0))
    ob.location=(cx+OX,cy,cz); ob.rotation_euler=(random.uniform(-.25,.25),random.uniform(-.25,.25),random.uniform(0,3.14)); ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth=False
    lst.append(ob); return ob
def isphere(cx,cy,cz,sx,sy,sz,col,rot,sub=1,lst=None):
    lst=VEG if lst is None else lst
    me=bpy.data.meshes.new("pt"); ob=bpy.data.objects.new("pt",me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_icosphere(bm,subdivisions=sub,radius=1.0); bm.to_mesh(me); bm.free()
    ob.scale=(sx,sy,sz); ob.location=(cx+OX,cy,cz); ob.rotation_euler=rot; ob.data.materials.append(_M(col))
    for p in me.polygons: p.use_smooth=False
    lst.append(ob); return ob
def finish(name,parts):
    if not parts: return None
    bpy.ops.object.select_all(action='DESELECT')
    for p in parts: p.select_set(True)
    bpy.context.view_layer.objects.active=parts[0]; bpy.ops.object.join()
    b=bpy.context.active_object; b.name=name
    for poly in b.data.polygons: poly.use_smooth=False
    bpy.ops.object.select_all(action='DESELECT'); return b

# ---------------------------------------------------------------- flower basket
def flower(cx,cy,cz,col,s=0.10):
    """Fleur lisible = 5 pétales aplatis en couronne (relevés) + cœur jaune."""
    for k in range(5):
        pa=k*2*math.pi/5+random.uniform(-.15,.15); px=cx+math.cos(pa)*s*0.85; py=cy+math.sin(pa)*s*0.85
        isphere(px,py,cz+0.01,s*0.75,s*0.42,s*0.22,col,rot=(math.radians(28)*math.sin(pa+1.57),math.radians(28)*math.cos(pa+1.57),pa))
    isphere(cx,cy,cz+0.03,s*0.45,s*0.45,s*0.30,"City_Flower_Ctr",(0,0,0))
def basket(cx,cy,cz):
    LEAF,LEAF2="City_Leaf","City_Leaf_Dk"
    cone(cx,cy,cz,0.21,0.13,0.20,"City_Terracotta","bowl",12); cyl(cx,cy,cz+0.10,0.22,0.035,"City_Terracotta","rim",12)
    for _ in range(4):
        a=random.uniform(0,6.28); r=random.uniform(0,0.13)
        organic(cx+math.cos(a)*r,cy+math.sin(a)*r,cz+0.15,random.uniform(0.09,0.12),LEAF if random.random()>.5 else LEAF2,"mound",amp=0.5)
    for k in range(5):
        a=k*2*math.pi/5+0.3; ex=cx+math.cos(a)*0.20; ey=cy+math.sin(a)*0.20
        for j in range(2): organic(ex+random.uniform(-.03,.03),ey+random.uniform(-.03,.03),cz+0.02-j*0.11,random.uniform(0.055,0.08),LEAF2,"trail",sub=1,amp=0.55)
    cols=["City_Flower","City_Flower_Y","City_Flower_C","City_Flower_W","City_Flower_P"]
    for i in range(12):
        a=random.uniform(0,6.28); r=random.uniform(0,0.20); flower(cx+math.cos(a)*r,cy+math.sin(a)*r,cz+0.19+random.uniform(0,0.05),random.choice(cols),s=random.uniform(0.085,0.12))

# ---------------------------------------------------------------- builders
def lantern(cx,cy,ztop):
    B,GLOW="City_Brass","City_Lamp_Glow"
    cyl(cx,cy,ztop+0.02,0.15,0.06,B,"lh",10); cone(cx,cy,ztop-0.05,0.17,0.13,0.10,B,"lc",4)
    box(cx,cy,ztop-0.32,0.24,0.24,0.42,GLOW,"lg")
    for (sx,sy) in [(0.12,0.12),(-0.12,0.12),(0.12,-0.12),(-0.12,-0.12)]: box(cx+sx,cy+sy,ztop-0.32,0.03,0.03,0.46,B,"le")
    cone(cx,cy,ztop-0.58,0.15,0.05,0.10,B,"lb",4); cyl(cx,cy,ztop-0.66,0.03,0.08,B,"ld",6)
def build_lamppost(tag,ox):
    global CUR,VEG,OX; CUR=[]; VEG=[]; OX=ox; random.seed(hash(tag)%1000)
    G,B="City_Iron_Green","City_Brass"
    cone(0,0,0.09,0.32,0.28,0.18,G,"b1",10); cone(0,0,0.25,0.24,0.20,0.14,G,"b2",10)
    cone(0,0,0.55,0.15,0.10,0.5,G,"foot",10); cyl(0,0,2.05,0.075,3.0,G,"pole",8)
    cyl(0,0,1.25,0.11,0.10,B,"c1",8); cyl(0,0,3.55,0.10,0.12,B,"c2",8)
    cyl_between((0,0.0,3.55),(0,0.35,3.80),0.05,B); cyl_between((0,0.35,3.80),(0,0.72,3.88),0.05,B)
    cyl_between((0,0.72,3.88),(0,0.92,3.80),0.045,B); cyl_between((0,0.06,3.30),(0,0.55,3.78),0.035,B)
    lantern(0,0.92,3.72)
    # panier fleuri sur HOOK dédié à l'arrière (dégagé de la lanterne)
    cyl_between((0,-0.06,2.70),(0,-0.40,2.66),0.03,B); cyl_between((0,-0.40,2.64),(0,-0.40,2.48),0.015,B); basket(0,-0.40,2.30)
    return finish(f"Prop_Lamppost_{tag}",CUR),finish(f"Prop_Lamppost_{tag}_Veg",VEG)
def build_bench(tag,ox):
    global CUR,VEG,OX; CUR=[]; VEG=[]; OX=ox; random.seed(1)
    G,W="City_Iron_Green","City_Wood"
    for x in (-0.78,0.78):
        box(x,0.20,0.24,0.07,0.07,0.44,G); box(x,-0.24,0.24,0.07,0.07,0.44,G)
        box(x,-0.02,0.44,0.09,0.62,0.06,G); box(x,-0.26,0.64,0.07,0.07,0.42,G)
        box(x,0.02,0.66,0.06,0.52,0.05,G); box(x,0.26,0.55,0.06,0.06,0.24,G)
    for sy in (0.20,0.04,-0.12): box(0,sy,0.475,1.62,0.12,0.05,W)
    for sz in (0.62,0.78): box(0,-0.27,sz,1.62,0.05,0.12,W)
    return finish(f"Prop_Bench_{tag}",CUR),finish(f"Prop_Bench_{tag}_Veg",VEG)
def build_bin(tag,ox):
    global CUR,VEG,OX; CUR=[]; VEG=[]; OX=ox; random.seed(2)
    G,B,D="City_Iron_Green","City_Brass","City_Metal_Dk"
    cyl(0,0,0.04,0.27,0.08,D,"base",6); cone(0,0,0.5,0.25,0.23,0.82,G,"body",6)
    for z in (0.30,0.72): cyl(0,0,z,0.26,0.045,B,"band",6)
    cyl(0,0,0.95,0.28,0.05,B,"rim",6)
    for k in range(3):
        a=k*2*math.pi/3; cyl(math.cos(a)*0.20,math.sin(a)*0.20,1.03,0.02,0.14,B,"post",6)
    cone(0,0,1.16,0.30,0.10,0.20,G,"lid",6); cyl(0,0,1.28,0.04,0.06,B,"knob",6)
    return finish(f"Prop_Bin_{tag}",CUR),finish(f"Prop_Bin_{tag}_Veg",VEG)
def build_traffic(tag,ox):
    global CUR,VEG,OX; CUR=[]; VEG=[]; OX=ox; random.seed(3)
    D,B="City_Metal_Dk","City_Iron_Green"
    cyl(0,0,0.04,0.22,0.08,D,"base",8); cyl(0,0,1.55,0.07,3.0,D,"pole",8); box(0,0,2.95,0.30,0.20,0.86,B,"head")
    for (z,col) in [(3.18,"City_Red"),(2.92,"City_Amber"),(2.66,"City_GreenLite")]:
        cyl(0,-0.11,z,0.085,0.06,col,"lamp",12,rot=(1.5708,0,0)); box(0,-0.16,z+0.10,0.24,0.12,0.03,B,"visor",rot=(0.5,0,0))
    box(0,0,1.75,0.24,0.16,0.5,B,"pedbox")
    cyl(0,-0.09,1.87,0.06,0.05,"City_Red","pr",12,rot=(1.5708,0,0)); cyl(0,-0.09,1.66,0.06,0.05,"City_GreenLite","pg",12,rot=(1.5708,0,0))
    return finish(f"Prop_TrafficLight_{tag}",CUR),finish(f"Prop_TrafficLight_{tag}_Veg",VEG)
def build_shelter(tag,ox):
    global CUR,VEG,OX; CUR=[]; VEG=[]; OX=ox; random.seed(4)
    G,GL,W,DK="City_Iron_Green","City_Window_Glass","City_Wood","City_Metal_Dk"; D=0.55
    for (x,y) in [(-1.6,-D),(1.6,-D),(-1.6,D-0.05),(1.6,D-0.05)]: box(x,y,1.15,0.08,0.08,2.3,G,"post")
    box(0,-D-0.01,1.35,3.15,0.035,1.65,GL,"bg"); box(0,-D-0.02,0.53,3.2,0.05,0.06,G); box(0,-D-0.02,2.18,3.2,0.05,0.06,G); box(0,-D-0.02,1.35,0.05,0.05,1.65,G)
    for x in (-1.6,1.6):
        box(x,0.0,1.35,0.035,1.0,1.65,GL,"sg"); box(x,0.0,0.53,0.05,1.06,0.06,G); box(x,0.0,2.18,0.05,1.06,0.06,G)
    box(0.0,0.02,2.44,3.55,1.55,0.09,DK,"roof"); box(0.0,0.02,2.33,3.4,1.42,0.05,G,"rlip")
    for sy in (-D+0.10,-D+0.24): box(0,sy,0.50,2.9,0.12,0.05,W)
    for x in (-1.2,0,1.2): box(x,-D+0.17,0.28,0.08,0.22,0.5,G)
    box(1.95,0.35,1.2,0.06,0.06,2.4,G); box(1.95,0.35,2.25,0.5,0.05,0.62,"City_Sign_Blue"); box(1.95,0.31,2.38,0.34,0.03,0.16,"City_Frame_White")
    for _ in range(15): organic(random.uniform(-1.65,1.65),random.uniform(-D+0.1,D),2.56,random.uniform(0.16,0.26),"City_Leaf" if random.random()>.4 else "City_Leaf_Dk","rv",amp=0.5)
    for x in (-1.3,-0.5,0.5,1.3): organic(x,D+0.15,2.44,random.uniform(0.10,0.15),"City_Leaf_Dk","tr",sub=1,amp=0.55)
    return finish(f"Prop_Shelter_{tag}",CUR),finish(f"Prop_Shelter_{tag}_Veg",VEG)

# ---------------------------------------------------------------- petits props
def bag(cx,cy,s,col):
    """Sac poubelle : sphere bombee + goulot pince en haut + noeud. PAS un blob (=caillou)."""
    import random as _r
    _idx[0]+=1; o1=Vector((_idx[0]*4.1,_idx[0]*2.7,_idx[0]*3.3))
    me=bpy.data.meshes.new("bag"); ob=bpy.data.objects.new("bag",me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_icosphere(bm,subdivisions=2,radius=1.0)
    for v in bm.verts:
        n=noise.noise(v.co*2.0+o1); v.co+=v.normal*(0.18*n)
        if v.co.z>0.4: v.co.x*=0.6; v.co.y*=0.6      # goulot pince
    bmesh.ops.bevel(bm,geom=bm.edges[:],offset=0.02,segments=1,affect='EDGES'); bm.to_mesh(me); bm.free()
    ob.scale=(s,s*_r.uniform(.85,1.0),s*1.15); ob.location=(cx+OX,cy,s*1.0); ob.rotation_euler=(0,0,_r.uniform(0,3.14))
    ob.data.materials.append(_M(col))
    for p in me.polygons: p.use_smooth=False
    CUR.append(ob); cone(cx,cy,s*1.9,0.2*s,0.32*s,0.10,col,"knot",6)   # noeud
def pennant(pts_xz,cy,depth,cz0,m,name="flag"):
    """Polygone (x,z) extrude en Y = fanion/fleche pointu d'un SEUL tenant (pas de losange separe)."""
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); vs=[bm.verts.new((x,cy+depth/2,cz0+z)) for (x,z) in pts_xz]; f=bm.faces.new(vs)
    r=bmesh.ops.extrude_face_region(bm,geom=[f]); ev=[e for e in r['geom'] if isinstance(e,bmesh.types.BMVert)]
    bmesh.ops.translate(bm,verts=ev,vec=(0,-depth,0)); bmesh.ops.recalc_face_normals(bm,faces=bm.faces)
    bm.to_mesh(me); bm.free(); ob.location=(OX,0,0); ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth=False
    CUR.append(ob); return ob
def text_mesh(body,cx,cy,cz,size,m,name="txt"):
    """Texte lisible = objet FONT converti en MESH (ex: STOP). Face -Y, lettres droites."""
    cu=bpy.data.curves.new(name,'FONT'); ob=bpy.data.objects.new(name,cu)
    cu.body=body; cu.align_x='CENTER'; cu.align_y='CENTER'; cu.extrude=0.01; cu.size=size
    bpy.context.collection.objects.link(ob); ob.location=(cx+OX,cy,cz); ob.rotation_euler=(math.radians(90),0,0)
    bpy.ops.object.select_all(action='DESELECT'); ob.select_set(True); bpy.context.view_layer.objects.active=ob
    bpy.ops.object.convert(target='MESH'); ob=bpy.context.active_object; ob.name=name
    if not ob.data.materials: ob.data.materials.append(_M(m))
    else: ob.data.materials[0]=_M(m)
    for p in ob.data.polygons: p.use_smooth=False
    CUR.append(ob); return ob

def build_hydrant(tag,ox):
    global CUR,VEG,OX; CUR=[]; VEG=[]; OX=ox; random.seed(11); R,B="City_Fire_Red","City_Brass"
    cyl(0,0,0.03,0.16,0.06,R,"flange",12); cyl(0,0,0.10,0.10,0.10,R,"neck",12); cyl(0,0,0.34,0.115,0.42,R,"barrel",12)
    cone(0,0,0.58,0.13,0.10,0.08,R,"shoulder",12); cone(0,0,0.66,0.115,0.05,0.12,R,"bonnet",12); cyl(0,0,0.75,0.03,0.06,B,"topbolt",6)
    cyl(0,-0.11,0.34,0.05,0.07,B,"noz_f",8,rot=(1.5708,0,0))
    for x in (-0.11,0.11): cyl(x,0,0.44,0.045,0.06,B,"noz_s",8,rot=(0,1.5708,0))
    cyl(0,0,0.30,0.125,0.03,B,"band",12)
    return finish(f"Prop_Hydrant_{tag}",CUR),None
def build_bollard(tag,ox):
    global CUR,VEG,OX; CUR=[]; VEG=[]; OX=ox; random.seed(12); G,B="City_Iron_Green","City_Brass"
    cyl(0,0,0.04,0.14,0.08,G,"base",12); cone(0,0,0.40,0.11,0.09,0.66,G,"post",12); cyl(0,0,0.66,0.105,0.05,B,"ring",12); cone(0,0,0.76,0.09,0.03,0.14,G,"cap",12)
    isphere(0,0,0.86,0.06,0.06,0.06,"City_Brass",(0,0,0),sub=1,lst=CUR)   # boule finiale (rigide)
    return finish(f"Prop_Bollard_{tag}",CUR),None
def build_sign(tag,ox):
    global CUR,VEG,OX; CUR=[]; VEG=[]; OX=ox; random.seed(13); D="City_Metal_Dk"
    cyl(0,0,0.03,0.12,0.06,D,"base",8); cyl(0,0,1.15,0.045,2.2,D,"pole",8)
    cone(0,-0.05,2.0,0.30,0.30,0.02,"City_Frame_White","stop_bd",8,rot=(1.5708,0,0)); cone(0,-0.065,2.0,0.255,0.255,0.03,"City_Sign_Red","stop",8,rot=(1.5708,0,0))
    text_mesh("STOP",0,-0.088,2.0,0.16,"City_Frame_White","stoptxt")
    pennant([(-0.30,-0.14),(0.22,-0.14),(0.46,0.0),(0.22,0.14),(-0.30,0.14)],-0.06,0.04,1.5,"City_Sign_Blue","dir")
    box(0.02,-0.10,1.5,0.34,0.02,0.055,"City_Frame_White","dirbar")
    return finish(f"Prop_Sign_{tag}",CUR),None
def build_litter(tag,ox):
    global CUR,VEG,OX; CUR=[]; VEG=[]; OX=ox; random.seed(21)
    bag(-0.10,0.04,0.26,"City_Bag"); bag(0.16,-0.05,0.22,"City_Bag2")
    cyl(0.34,0.10,0.05,0.045,0.14,"City_Can_Red","can",10,rot=(1.4,0,0.6)); cyl(-0.30,0.16,0.06,0.05,0.10,"City_Frame_White","cup",10)
    box(-0.34,-0.10,0.02,0.12,0.09,0.015,"City_Frame_White","paper",rot=(0,0,0.5)); box(0.30,-0.18,0.02,0.09,0.06,0.02,"City_Sign_Blue","wrap",rot=(0,0,1.1)); box(0.02,0.28,0.02,0.07,0.05,0.02,"City_Can_Red","wrap2",rot=(0,0,0.3))
    return finish(f"Prop_Litter_{tag}",CUR),None
def build_planter(tag,ox):
    global CUR,VEG,OX; CUR=[]; VEG=[]; OX=ox; random.seed(15); T,G="City_Terracotta","City_Iron_Green"
    box(0,0,0.42,0.9,0.34,0.34,T,"trough"); box(0,0,0.60,0.96,0.40,0.05,T,"rim")
    for (x,y) in [(-0.4,-0.13),(0.4,-0.13),(-0.4,0.13),(0.4,0.13)]: box(x,y,0.13,0.06,0.06,0.26,G,"leg")
    for gx in [-0.35,-0.12,0.12,0.35]:
        organic(gx,random.uniform(-.06,.06),0.66,random.uniform(0.12,0.16),"City_Leaf" if random.random()>.5 else "City_Leaf_Dk","mound",amp=0.5)
    for j in range(3): organic(random.uniform(-0.4,0.4),-0.20,0.52-j*0.02,random.uniform(0.06,0.09),"City_Leaf_Dk","trail",sub=1,amp=0.55)
    for i in range(10): flower(random.uniform(-0.42,0.42),random.uniform(-0.10,0.10),0.70+random.uniform(0,0.04),random.choice(["City_Flower","City_Flower_Y","City_Flower_C","City_Flower_W","City_Flower_P"]),s=random.uniform(0.08,0.11))
    return finish(f"Prop_Planter_{tag}",CUR),finish(f"Prop_Planter_{tag}_Veg",VEG)

# 4 gammes couleur fonte (retour user) — LIVREES EN SWAP MATERIAU URP (slot City_Iron_Green),
# PAS de FBX bakes (geo identique). Cf Assets/Materials/City/City_Iron_{...}. RGB (lin 0..1) :
COLORWAYS={"Teal":(0.19,0.33,0.30),"Bordeaux":(0.44,0.19,0.20),"Navy":(0.17,0.24,0.37),"Anthracite":(0.24,0.25,0.27)}

KIT=[("Lamppost",build_lamppost),("Bench",build_bench),("Bin",build_bin),
     ("TrafficLight",build_traffic),("Shelter",build_shelter),("Hydrant",build_hydrant),
     ("Bollard",build_bollard),("Sign",build_sign),("Litter",build_litter),("Planter",build_planter)]

# ---------------------------------------------------------------- export
def export_prop(pair,outdir):
    """Base + _Veg = 2 meshes ; pivot = base au sol, centré XY. -Z fwd / Y up."""
    os.makedirs(outdir,exist_ok=True); objs=[o for o in pair if o]
    bpy.context.scene.cursor.location=(0,0,0)
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs: o.select_set(True)
    bpy.context.view_layer.objects.active=objs[0]
    pts=[(o.matrix_world@v.co) for o in objs for v in o.data.vertices]
    zmin=min(p.z for p in pts); cx=sum(p.x for p in pts)/len(pts); cy=sum(p.y for p in pts)/len(pts)
    for o in objs: o.location=(o.location.x-cx,o.location.y-cy,o.location.z-zmin)
    bpy.ops.object.origin_set(type='ORIGIN_CURSOR')
    fp=os.path.join(outdir,objs[0].name+".fbx")
    bpy.ops.export_scene.fbx(filepath=fp,use_selection=True,object_types={'MESH'},
        apply_unit_scale=True,apply_scale_options='FBX_SCALE_ALL',mesh_smooth_type='FACE',
        bake_space_transform=True,axis_forward='-Z',axis_up='Y')
    return fp
# Puis Unity : import_model_file(source_path=<fbx>, output_folder="Assets/Models/City/Props", name=...)

if __name__ == "__main__":
    clean(); palette(); lighting()
    for i,(nm,fn) in enumerate(KIT):
        fn("A", i*4.0)   # ox unique par prop (evite collisions de noms d'objets)
