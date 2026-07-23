# Street kit builder — Blender bpy, via Blender MCP (execute_blender_code).
#
# Kit rue MODULAIRE, module carre 8x8 (unites = metres Unity), chaussee 2 voies.
# Un builder GENERIQUE par jeu de directions {N,S,E,W} genere droite/virage/T/X ;
# + flags crossing (passage pieton) et verge (bande enherbee + arbres alignes).
# Toutes les tiles partagent le meme gabarit -> elles SNAPPENT sur une grille de 8.
#
# Gabarit (demi-module M=4) : chaussee = carre central [-CA,CA]^2 + bras par dir ;
# bordure (curb) a x/y = +/-C=2.60 (top 0.18) ; trottoir (sidewalk) a partir de SW=2.70
# (top 0.15, dalles claires + joints) ; surface route a z=0 ; PIVOT export = centre
# tile au sol (z=0=surface route) -> pose direct sur le plan Unity, snap a 8.
#
# Style/palette : memoire city-color-palette + galerie docs/city-refs. Verdure SOL =
# feuilles mortes plates (forme ovale pointue) + brins fins dans les jointures
# (PAS de blobs ronds au sol : refuse par l'utilisateur). Blobs organic() = OK pour
# canopees d'arbres et touffes de gazon.
#
# PIEGES (voir docs/city-session-lessons.md) : nettoyer orphelins ; jamais 2 faces
# coplanaires (marquage/joints/details a z+0.011 mini au-dessus de la surface) ;
# feuillage = objet enfant separe *_Veg (shader vent Unity) ; flat shading.

import bpy, bmesh, random, math
from mathutils import Vector, noise

# ---------------------------------------------------------------- scene / palette
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

def palette():
    # canoniques partages (cf city_builder.palette)
    mat("City_Frame_White",(0.97,0.93,0.85)); mat("City_Roof_Grey",(0.70,0.63,0.56))
    mat("City_Metal_Dk",(0.50,0.48,0.49)); mat("City_Leaf",(0.48,0.73,0.34))
    mat("City_Leaf_Dk",(0.34,0.57,0.28)); mat("City_Terracotta",(0.84,0.48,0.32))
    # rue (chauds, pas de gris froid)
    mat("City_Asphalt",(0.40,0.37,0.33),0.92)   # bitume chaud sombre
    mat("City_Line",(0.95,0.90,0.78),0.55)       # marquage blanc chaud
    mat("City_Sidewalk",(0.82,0.75,0.66),0.8)    # dalle trottoir claire
    mat("City_Tar",(0.34,0.31,0.28),0.85)        # joints de dilatation / fissures
    mat("City_Metal_Dk2",(0.44,0.42,0.43))       # fonte egout
    # feuilles mortes chaudes (automne)
    mat("City_Leaf_Gold",(0.90,0.72,0.30)); mat("City_Leaf_Rust",(0.82,0.44,0.26))
    mat("City_Leaf_Amber",(0.88,0.58,0.24))
    # bande enherbee
    mat("City_Grass",(0.46,0.62,0.31),0.85); mat("City_Trunk",(0.52,0.37,0.24))

def lighting():
    sun=bpy.data.objects.get("CitySun")
    if not sun:
        ld=bpy.data.lights.new("CitySun",'SUN'); sun=bpy.data.objects.new("CitySun",ld)
        bpy.context.collection.objects.link(sun)
    sun.data.energy=2.8; sun.data.color=(1.0,0.95,0.86)
    sun.rotation_euler=(math.radians(55),math.radians(15),math.radians(40))
    w=bpy.context.scene.world.node_tree.nodes["Background"]
    w.inputs[0].default_value=(0.90,0.87,0.80,1); w.inputs[1].default_value=0.30
    sc=bpy.context.scene; sc.render.engine='BLENDER_EEVEE'
    sc.render.resolution_x=1000; sc.render.resolution_y=1000
    try:
        sc.view_settings.view_transform='Standard'; sc.view_settings.look='None'; sc.view_settings.exposure=-0.2
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

def cyl(cx,cy,cz,r,h,m,name="c",verts=8,axis='Z',rot=None):
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_cone(bm,cap_ends=True,segments=verts,radius1=r,radius2=r,depth=h); bm.to_mesh(me); bm.free()
    ob.location=(cx+OX,cy,cz)
    if rot is not None: ob.rotation_euler=rot
    elif axis=='X': ob.rotation_euler=(0,1.5708,0)
    elif axis=='Y': ob.rotation_euler=(1.5708,0,0)
    ob.data.materials.append(_M(m)); CUR.append(ob); return ob

def organic(cx,cy,cz,s,m,name="veg",sub=2,amp=0.42):
    """Touffe/canopee organique (icosphere bruit 2 octaves). -> VEG. JAMAIS au sol comme
    mauvaise herbe (refuse) : reserve aux canopees d'arbres + touffes de gazon."""
    _idx[0]+=1; o1=Vector((_idx[0]*4.1,_idx[0]*2.7,_idx[0]*3.3)); o2=Vector((_idx[0]*7.3,_idx[0]*5.1,_idx[0]*6.2))
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_icosphere(bm,subdivisions=sub,radius=1.0)
    for v in bm.verts:
        n=noise.noise(v.co*1.7+o1)*0.7+noise.noise(v.co*3.6+o2)*0.3
        v.co+=v.normal*(amp*n)+Vector((random.uniform(-.05,.05),)*3)
    bmesh.ops.bevel(bm,geom=bm.edges[:],offset=0.03,segments=1,affect='EDGES'); bm.to_mesh(me); bm.free()
    ob.scale=(s*random.uniform(.85,1.2),s*random.uniform(.85,1.2),s*random.uniform(.7,1.0))
    ob.location=(cx+OX,cy,cz); ob.rotation_euler=(random.uniform(-.25,.25),random.uniform(-.25,.25),random.uniform(0,3.14))
    ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth=False
    VEG.append(ob); return ob

def finish(name,parts):
    if not parts: return None
    bpy.ops.object.select_all(action='DESELECT')
    for p in parts: p.select_set(True)
    bpy.context.view_layer.objects.active=parts[0]; bpy.ops.object.join()
    b=bpy.context.active_object; b.name=name
    for poly in b.data.polygons: poly.use_smooth=False
    bpy.ops.object.select_all(action='DESELECT'); return b

# ---------------------------------------------------------------- sol : verdure
def weed_tuft(cx,cy,base_z,scale=1.0):
    """Brins fins verts pour les jointures (spiky, PAS un blob)."""
    for _ in range(random.randint(4,7)):
        h=scale*random.uniform(0.10,0.22)
        box(cx+random.uniform(-.03,.03),cy+random.uniform(-.03,.03),base_z+h/2,0.018,0.018,h,
            "City_Leaf" if random.random()>.4 else "City_Leaf_Dk","blade",
            rot=(random.uniform(-.30,.30),random.uniform(-.30,.30),random.uniform(0,3.14)),lst=VEG)

def fallen_leaf(cx,cy,base_z):
    """Feuille morte : polygone ovale pointu (2 bouts) extrude fin -> forme de feuille."""
    col=random.choice(["City_Leaf_Gold","City_Leaf_Rust","City_Leaf_Amber","City_Leaf","City_Terracotta"])
    me=bpy.data.meshes.new("leaf"); ob=bpy.data.objects.new("leaf",me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); pts=[(0,-0.55),(0.24,-0.14),(0.30,0.16),(0,0.55),(-0.30,0.16),(-0.24,-0.14)]
    vs=[bm.verts.new((x,y,0)) for x,y in pts]; f=bm.faces.new(vs)
    r=bmesh.ops.extrude_face_region(bm,geom=[f]); ev=[e for e in r['geom'] if isinstance(e,bmesh.types.BMVert)]
    bmesh.ops.translate(bm,verts=ev,vec=(0,0,-0.02)); bmesh.ops.recalc_face_normals(bm,faces=bm.faces); bm.to_mesh(me); bm.free()
    L=random.uniform(0.16,0.26); ob.scale=(L,L,1.0); ob.location=(cx+OX,cy,base_z+0.02)
    ob.rotation_euler=(random.uniform(-.12,.12),random.uniform(-.12,.12),random.uniform(0,3.14)); ob.data.materials.append(_M(col))
    for p in me.polygons: p.use_smooth=False
    VEG.append(ob); return ob

# ---------------------------------------------------------------- details fonte
def drain_grate(cx,cy):
    METAL,TAR="City_Metal_Dk","City_Tar"; W,L=0.40,0.58
    box(cx,cy,0.008,W,L,0.03,TAR,"gr_void")
    for i in range(5): box(cx-0.15+i*0.075,cy,0.024,0.030,L-0.05,0.022,METAL,"gr_bar")
    box(cx-W/2,cy,0.022,0.045,L,0.03,METAL,"gr_fr"); box(cx+W/2,cy,0.022,0.045,L,0.03,METAL,"gr_fr")
    box(cx,cy-L/2,0.022,W+0.09,0.045,0.03,METAL,"gr_fr"); box(cx,cy+L/2,0.022,W+0.09,0.045,0.03,METAL,"gr_fr")

def manhole(cx,cy):
    F,TAR="City_Metal_Dk2","City_Tar"
    cyl(cx,cy,0.012,0.47,0.024,F,"mh_rim",20); cyl(cx,cy,0.028,0.42,0.030,F,"mh_cover",20); cyl(cx,cy,0.048,0.14,0.020,F,"mh_hub",16)
    for k in range(8):
        a=k*math.pi/4.0; box(cx+math.cos(a)*0.30,cy+math.sin(a)*0.30,0.050,0.24,0.035,0.014,F,"mh_rib",rot=(0,0,a))
    for k in range(16):
        a=k*math.pi/8.0; box(cx+math.cos(a)*0.35,cy+math.sin(a)*0.35,0.046,0.05,0.05,0.012,F,"mh_stud")
    for dy in (-0.12,0.12): box(cx,cy+dy,0.050,0.05,0.10,0.02,TAR,"mh_hole")

def tree(cx,cy,base_z=0.15):
    cyl(cx,cy,base_z+0.35,0.09,0.7,"City_Trunk","trunk",8)  # tronc rigide -> CUR
    for (dx,dy,dz,s) in [(0,0,1.35,0.62),(-0.28,0.10,1.15,0.44),(0.26,-0.12,1.18,0.46),(0.05,0.24,1.5,0.40)]:
        organic(cx+dx,cy+dy,base_z+dz,s,"City_Leaf" if random.random()>.4 else "City_Leaf_Dk","canopy",amp=0.5)

# ---------------------------------------------------------------- module & regions
M=4.0; CA=2.65; C=2.60; SW=2.70; CI=2.50
ARM ={'N':(-CA,CA,CA,M),'S':(-CA,CA,-M,-CA),'E':(CA,M,-CA,CA),'W':(-M,-CA,-CA,CA)}
FILL={'N':(-SW,SW,SW,M),'S':(-SW,SW,-M,-SW),'E':(SW,M,-SW,SW),'W':(-M,-SW,-SW,SW)}
CORN=[(SW,M,SW,M),(-M,-SW,SW,M),(SW,M,-M,-SW),(-M,-SW,-M,-SW)]
DRAIN={'N':(2.32,3.4),'S':(-2.32,-3.4),'E':(3.4,-2.32),'W':(-3.4,2.32)}

def asph_rect(x0,x1,y0,y1): box((x0+x1)/2,(y0+y1)/2,-0.075,x1-x0,y1-y0,0.15,"City_Asphalt","asph")
def paved_rect(x0,x1,y0,y1,rects):
    rects.append((x0,x1,y0,y1)); box((x0+x1)/2,(y0+y1)/2,0.070,x1-x0,y1-y0,0.14,"City_Roof_Grey","sw_base")
    W=x1-x0; L=y1-y0; nx=max(1,round(W/0.95)); ny=max(1,round(L/0.95)); tw=W/nx; tl=L/ny
    for i in range(nx):
        for j in range(ny): box(x0+tw*(i+0.5),y0+tl*(j+0.5),0.145,tw-0.07,tl-0.07,0.05,"City_Sidewalk","slab")
def grass_rect(x0,x1,y0,y1,rects):
    rects.append((x0,x1,y0,y1)); box((x0+x1)/2,(y0+y1)/2,0.075,x1-x0,y1-y0,0.15,"City_Grass","grass")
    for _ in range(int((x1-x0)*(y1-y0)*0.7)):
        organic(random.uniform(x0+.2,x1-.2),random.uniform(y0+.2,y1-.2),0.18,random.uniform(0.12,0.22),
                "City_Leaf" if random.random()>.4 else "City_Leaf_Dk","tuft",amp=0.5)
def curb_seg(cx,cy,sx,sy,curbs):
    curbs.append((cx,cy,sx,sy)); box(cx,cy,0.0,sx,sy,0.36,"City_Frame_White","curb")

# ---------------------------------------------------------------- tile generique
def build_tile(tag,ox,dirs,crossing=False,verge=False):
    """dirs = sous-ensemble de {'N','S','E','W'} (bras de chaussee presents).
    {N,S}=droite, {N,E}=virage, {N,S,E}=T, {N,S,E,W}=X. crossing=passage pieton
    (droite N,S). verge=bande enherbee (cotes E/W en gazon + arbres alignes)."""
    global CUR,VEG,OX
    CUR=[]; VEG=[]; OX=ox; random.seed(hash(tag)%1000)
    sw=[]; curbs=[]
    asph_rect(-CA,CA,-CA,CA)
    for d in dirs: asph_rect(*ARM[d])
    side_fn=grass_rect if verge else paved_rect
    for r in CORN: side_fn(*r,sw)
    for d in "NSEW":
        if d not in dirs: side_fn(*FILL[d],sw)
    # bordures : flancs de bras (dirs presents) + fermeture des cotes absents + 4 coins
    for d in dirs:
        if d in "NS":
            y0,y1=(SW,M) if d=='N' else (-M,-SW)
            curb_seg(-C,(y0+y1)/2,0.20,y1-y0,curbs); curb_seg(C,(y0+y1)/2,0.20,y1-y0,curbs)
        else:
            x0,x1=(SW,M) if d=='E' else (-M,-SW)
            curb_seg((x0+x1)/2,-C,x1-x0,0.20,curbs); curb_seg((x0+x1)/2,C,x1-x0,0.20,curbs)
    for d in "NSEW":
        if d not in dirs:
            if d in "NS": curb_seg(0,(C if d=='N' else -C),2*CI,0.20,curbs)
            else: curb_seg((C if d=='E' else -C),0,0.20,2*CI,curbs)
    for sx in (-C,C):
        for sy in (-C,C): curb_seg(sx,sy,0.20,0.20,curbs)
    # marquage
    if crossing:
        x=-2.4
        while x<=2.4: box(x,0,0.013,0.30,1.5,0.02,"City_Line","zebra"); x+=0.62
        for s in (1,-1): box(0,s*1.05,0.013,2.2,0.14,0.02,"City_Line","stopc")
    else:
        for d in dirs:
            if d in "NS":
                for t in (2.9,3.6): box(0,(t if d=='N' else -t),0.012,0.14,0.5,0.02,"City_Line","dash")
            else:
                for t in (2.9,3.6): box((t if d=='E' else -t),0,0.012,0.5,0.14,0.02,"City_Line","dash")
        if dirs=={'N','S'}:
            for t in (-1.5,-0.6,0.6,1.5): box(0,t,0.012,0.14,0.5,0.02,"City_Line","dash")
    if len(dirs)>=3:  # lignes d'arret aux bouches
        for d in dirs:
            if d in "NS": box(0,(CA-0.2 if d=='N' else -(CA-0.2)),0.013,2.2,0.16,0.02,"City_Line","stop")
            else: box((CA-0.2 if d=='E' else -(CA-0.2)),0,0.013,0.16,2.2,0.02,"City_Line","stop")
    for d in dirs: drain_grate(*DRAIN[d])
    manhole(0,0) if len(dirs)>=3 else manhole(-0.8,1.1)
    if verge:  # arbres alignes sur bandes laterales (E/W absents pour une droite)
        for d in "EW":
            if d not in dirs:
                sx=3.35 if d=='E' else -3.35
                for ty in (-2.2,1.0): tree(sx,ty)
    # mauvaises herbes dans les jointures (cote route de chaque bordure)
    for (cx,cy,sx,sy) in curbs:
        n=int(max(sx,sy)/1.1)
        for _ in range(n):
            if sx<sy: px=cx-0.18*(1 if cx>0 else -1); py=cy+random.uniform(-sy/2,sy/2)
            else: px=cx+random.uniform(-sx/2,sx/2); py=cy-0.18*(1 if cy>0 else -1)
            if random.random()<0.6: weed_tuft(px,py,0.02,random.uniform(0.6,1.0))
    # feuilles mortes eparpillees (z=trottoir si sur trottoir, sinon route)
    def on_sw(x,y):
        return any(x0<x<x1 and y0<y<y1 for (x0,x1,y0,y1) in sw)
    for _ in range(24):
        x=random.uniform(-3.8,3.8); y=random.uniform(-3.8,3.8)
        fallen_leaf(x,y,0.15 if on_sw(x,y) else 0.0)
    base=finish(f"{tag}",CUR); veg=finish(f"{tag}_Veg",VEG)
    return base,veg

# ---------------------------------------------------------------- kit valide (A)
KIT=[
    ("Road_Straight_A", {'N','S'},          False, False),
    ("Road_Corner_A",   {'N','E'},          False, False),
    ("Road_T_A",        {'N','S','E'},      False, False),
    ("Road_X_A",        {'N','S','E','W'},  False, False),
    ("Road_Crosswalk_A",{'N','S'},          True,  False),
    ("Road_Verge_A",    {'N','S'},          False, True),
]

# ---------------------------------------------------------------- export
def export_fbx(pair, ox, outdir):
    """Recentre la tile a l'origine (pivot commun = centre tile au sol z=0) puis exporte
    base + _Veg = 2 meshes dans un FBX. -Z fwd / Y up (comme city_builder)."""
    import os
    os.makedirs(outdir, exist_ok=True)
    bpy.context.scene.cursor.location=(0,0,0)
    objs=[o for o in pair if o]
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        o.location=(o.location.x-ox,o.location.y,o.location.z); o.select_set(True)
    bpy.context.view_layer.objects.active=objs[0]
    bpy.ops.object.origin_set(type='ORIGIN_CURSOR')
    fp=os.path.join(outdir, objs[0].name+".fbx")
    bpy.ops.export_scene.fbx(filepath=fp, use_selection=True, object_types={'MESH'},
        apply_unit_scale=True, apply_scale_options='FBX_SCALE_ALL',
        mesh_smooth_type='FACE', bake_space_transform=True, axis_forward='-Z', axis_up='Y')
    return fp
# Cote Unity : import_model_file(source_path=<fbx>, output_folder="Assets/Models/City/Street", name=...)

if __name__ == "__main__":
    clean(); palette(); lighting()
    SP=9.0; xs=[(-2.5+i)*SP for i in range(len(KIT))]
    for (tag,dirs,cr,vg),ox in zip(KIT,xs):
        build_tile(tag,ox,dirs,crossing=cr,verge=vg)
