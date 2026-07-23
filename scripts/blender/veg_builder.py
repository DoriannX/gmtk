# Vegetation builder — Blender bpy, via Blender MCP (execute_blender_code).
#
# Végétation dédiée placeable (kit ville) : arbres (rond/grand/petit/fleuri/conifère),
# buisson, buisson fleuri, haie, plante en pot, arbre en pot (topiaire), touffe d'herbe.
# Style facetté low-poly (icosphère bruitée, JAMAIS de subsurf), palette chaude/verte.
#
# EXPORT : feuillage = objet enfant `*_Veg` SÉPARÉ (shader de vent). Trunc/pot/dirt = base
# rigide. Assets tout-feuillage (buisson, haie) = 1 seul mesh (nom sans `_Veg`, il prend le
# shader vent en entier). Pivot = base au sol centré XY. FBX → Assets/Models/City/Vegetation/.
#
# LEÇONS (docs/city-session-lessons.md §26) :
#  - **Haie** : les touffes du dessus doivent être calées sur la LONGUEUR du bloc et
#    DESCENDUES pour FUSIONNER dedans (centre sous le dessus) → dessus bombé continu.
#    Bug vécu : touffes placées sur ±1.7 alors que le bloc faisait ±1.0 → touffes des
#    bouts flottent dans le vide (« des trucs qui flottent »).
#  - **Arbre fleuri** : blossom doit être DENSE (≈24 clumps, taille 0.16-0.26, rose+blanc)
#    sur houppier vert sombre, sinon le rose ne se lit pas.
#  - **Conifère** = cônes étagés décroissants (verts alternés) + tronc court + pointe.
#  - **Arbre** : ancrer le houppier par 4 branches (`cyl_between`) tronc→couronne (anti-flottement).

import bpy, bmesh, random, math, os
from mathutils import Vector, noise

def clean(keep=("Camera","CitySun","Light")):
    for ob in list(bpy.data.objects):
        if ob.name not in keep: bpy.data.objects.remove(ob, do_unlink=True)
    for m in list(bpy.data.meshes):
        if m.users==0: bpy.data.meshes.remove(m)
def mat(name,rgb,rough=0.7):
    m=bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes=True; b=m.node_tree.nodes.get("Principled BSDF"); b.inputs["Base Color"].default_value=(*rgb,1); b.inputs["Roughness"].default_value=rough; return name
def palette():
    mat("City_Trunk",(0.52,0.37,0.24)); mat("City_Leaf",(0.48,0.73,0.34)); mat("City_Leaf_Dk",(0.34,0.57,0.28))
    mat("City_Leaf_Lt",(0.62,0.81,0.44)); mat("City_Pine",(0.30,0.52,0.36)); mat("City_Pine_Dk",(0.22,0.42,0.30))
    mat("City_Flower",(0.97,0.60,0.72)); mat("Blossom_Pink",(0.97,0.60,0.72)); mat("Blossom_Lt",(0.99,0.82,0.88))
    mat("City_Terracotta",(0.84,0.48,0.32)); mat("City_Soil",(0.34,0.26,0.20))
def lighting():
    sun=bpy.data.objects.get("CitySun")
    if not sun:
        ld=bpy.data.lights.new("CitySun",'SUN'); sun=bpy.data.objects.new("CitySun",ld); bpy.context.collection.objects.link(sun)
    sun.data.energy=2.8; sun.data.color=(1.0,0.95,0.86); sun.rotation_euler=(math.radians(55),math.radians(15),math.radians(40))
    w=bpy.context.scene.world.node_tree.nodes["Background"]; w.inputs[0].default_value=(0.90,0.87,0.80,1); w.inputs[1].default_value=0.30
    bpy.context.scene.render.engine='BLENDER_EEVEE'

CUR=[]; VEG=[]; OX=0.0; _idx=[0]
def _M(n): return bpy.data.materials[n]
def cone(cx,cy,cz,r1,r2,h,m,name="c",verts=8,rot=(0,0,0),lst=None):
    lst=CUR if lst is None else lst
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_cone(bm,cap_ends=True,segments=verts,radius1=r1,radius2=r2,depth=h); bm.to_mesh(me); bm.free()
    ob.location=(cx+OX,cy,cz); ob.rotation_euler=rot; ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth=False
    lst.append(ob); return ob
def cyl_between(p0,p1,r,m,name="br",verts=6,lst=None):
    lst=CUR if lst is None else lst
    p0=Vector(p0); p1=Vector(p1); vec=p1-p0; L=vec.length; mid=(p0+p1)/2
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_cone(bm,cap_ends=True,segments=verts,radius1=r,radius2=r,depth=L); bm.to_mesh(me); bm.free()
    ob.location=(mid.x+OX,mid.y,mid.z); ob.rotation_euler=vec.to_track_quat('Z','Y').to_euler(); ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth=False
    lst.append(ob); return ob
def organic(cx,cy,cz,s,m,name="veg",sub=2,amp=0.42):
    """Touffe facettée (icosphère bruit 2 octaves + bevel + flat). JAMAIS de subsurf."""
    _idx[0]+=1; o1=Vector((_idx[0]*4.1,_idx[0]*2.7,_idx[0]*3.3)); o2=Vector((_idx[0]*7.3,_idx[0]*5.1,_idx[0]*6.2))
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_icosphere(bm,subdivisions=sub,radius=1.0)
    for v in bm.verts:
        n=noise.noise(v.co*1.7+o1)*0.7+noise.noise(v.co*3.6+o2)*0.3
        v.co+=v.normal*(amp*n)+Vector((random.uniform(-.05,.05),)*3)
    bmesh.ops.bevel(bm,geom=bm.edges[:],offset=0.03,segments=1,affect='EDGES'); bm.to_mesh(me); bm.free()
    ob.scale=(s*random.uniform(.88,1.12),s*random.uniform(.88,1.12),s*random.uniform(.8,1.0))
    ob.location=(cx+OX,cy,cz); ob.rotation_euler=(random.uniform(-.22,.22),random.uniform(-.22,.22),random.uniform(0,3.14)); ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth=False
    VEG.append(ob); return ob
def blades(cx,cy,base_z,n,scale,cols):
    for _ in range(n):
        h=scale*random.uniform(0.6,1.1); bx=cx+random.uniform(-scale*0.5,scale*0.5); by=cy+random.uniform(-scale*0.5,scale*0.5)
        me=bpy.data.meshes.new("bl"); ob=bpy.data.objects.new("bl",me); bpy.context.collection.objects.link(ob)
        bm=bmesh.new(); bmesh.ops.create_cone(bm,cap_ends=True,segments=3,radius1=0.02,radius2=0.005,depth=h); bm.to_mesh(me); bm.free()
        ob.location=(bx+OX,by,base_z+h/2); ob.rotation_euler=(random.uniform(-.3,.3),random.uniform(-.3,.3),random.uniform(0,3.14)); ob.data.materials.append(_M(random.choice(cols)))
        for p in me.polygons: p.use_smooth=False
        VEG.append(ob)
def finish(name,parts):
    if not parts: return None
    bpy.ops.object.select_all(action='DESELECT')
    for p in parts: p.select_set(True)
    bpy.context.view_layer.objects.active=parts[0]; bpy.ops.object.join()
    b=bpy.context.active_object; b.name=name
    for poly in b.data.polygons: poly.use_smooth=False
    bpy.ops.object.select_all(action='DESELECT'); return b
def RESET(ox,seed):
    global CUR,VEG,OX; CUR=[]; VEG=[]; OX=ox; random.seed(seed)

def build_tree(tag,ox,H=2.8,crownR=1.0,leaf=("City_Leaf","City_Leaf_Dk","City_Leaf_Lt"),blossom=None,dens=1.0,seed=1):
    RESET(ox,seed); T="City_Trunk"; th=H*0.5
    cone(0,0,0.06,0.22,0.15,0.12,T,"flare",8); cone(0,0,th/2+0.10,0.13,0.09,th,T,"trunk",8)
    cz=th+0.10+crownR*0.55
    for k in range(4):   # branches ancrent le houppier
        a=k*math.pi/2+0.4; cyl_between((0,0,th),(math.cos(a)*crownR*0.5,math.sin(a)*crownR*0.5,cz-0.1),0.05,T,"branch")
    organic(0,0,cz+0.1,crownR*0.9,leaf[0],"crown",amp=0.5)
    for _ in range(int(9*dens)):
        a=random.uniform(0,6.28); rr=random.uniform(0.3,1.0)*crownR
        organic(math.cos(a)*rr,math.sin(a)*rr,cz+random.uniform(-0.35,0.45),random.uniform(0.4,0.62)*crownR,random.choice(leaf),"clump",amp=0.5)
    if blossom:
        for _ in range(int(24*dens)):
            a=random.uniform(0,6.28); rr=random.uniform(0.2,1.05)*crownR
            organic(math.cos(a)*rr,math.sin(a)*rr,cz+random.uniform(-0.25,0.55),random.uniform(0.16,0.26),"Blossom_Pink" if random.random()>.35 else "Blossom_Lt","bloom",sub=1,amp=0.5)
    return finish(f"Veg_Tree_{tag}",CUR),finish(f"Veg_Tree_{tag}_Veg",VEG)
def build_conifer(tag,ox,H=3.0,seed=5):
    RESET(ox,seed); T="City_Trunk"; cone(0,0,0.20,0.14,0.10,0.4,T,"trunk",8)
    P,PD="City_Pine","City_Pine_Dk"; z=0.45; r=0.85
    for i in range(5):
        cone(0,0,z+0.001,r,r*0.35,H/5*1.25,PD if i%2 else P,"skirt",7,lst=VEG); z+=H/5*0.72; r*=0.72
    cone(0,0,z+0.05,0.02,0.10,0.18,P,"tip",6,lst=VEG)
    return finish(f"Veg_Conifer_{tag}",CUR),finish(f"Veg_Conifer_{tag}_Veg",VEG)
def build_bush(tag,ox,s=0.7,seed=7,flower=None):
    RESET(ox,seed)
    organic(0,0,s*0.7,s*0.9,"City_Leaf","bush",amp=0.5)
    for _ in range(5):
        a=random.uniform(0,6.28); rr=random.uniform(0.2,0.75)*s
        organic(math.cos(a)*rr,math.sin(a)*rr,s*0.55+random.uniform(0,0.3*s),random.uniform(0.4,0.6)*s,random.choice(["City_Leaf","City_Leaf_Dk"]),"lobe",amp=0.5)
    if flower:
        for _ in range(9):
            a=random.uniform(0,6.28); rr=random.uniform(0.3,0.9)*s
            organic(math.cos(a)*rr,math.sin(a)*rr,s*0.7+random.uniform(0,0.3*s),0.10,flower,"fl",sub=1,amp=0.5)
    return None,finish(f"Veg_Bush_{tag}",VEG)   # tout-feuillage -> 1 mesh
def build_hedge(tag,ox,Lh=2.0,seed=9):
    RESET(ox,seed)
    me=bpy.data.meshes.new("hb"); ob=bpy.data.objects.new("hb",me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_cube(bm,size=1); bmesh.ops.scale(bm,vec=(0.52,Lh,0.60),verts=bm.verts)   # bloc span y=±Lh/2
    bmesh.ops.bevel(bm,geom=bm.edges[:]+bm.verts[:],offset=0.08,segments=2,affect='EDGES',clamp_overlap=True)
    bm.to_mesh(me); bm.free(); ob.location=(OX,0,0.33); ob.data.materials.append(_M("City_Leaf_Dk"))   # dessus ~0.63
    for p in me.polygons: p.use_smooth=False
    VEG.append(ob)
    y=-Lh/2+0.22   # touffes CALEES sur le bloc + centre SOUS le dessus (fusion, pas de flottement)
    while y<=Lh/2-0.22:
        organic(random.uniform(-0.06,0.06),y,0.56,random.uniform(0.28,0.34),"City_Leaf" if random.random()>.4 else "City_Leaf_Dk","top",amp=0.4); y+=0.30
    for yy in (-Lh/2+0.5,Lh/2-0.5):
        organic(0.18,yy,0.42,0.24,"City_Leaf_Dk","side",amp=0.4); organic(-0.18,yy,0.42,0.24,"City_Leaf_Dk","side",amp=0.4)
    return None,finish(f"Veg_Hedge_{tag}",VEG)
def build_potplant(tag,ox,seed=11,tree=False):
    RESET(ox,seed); T="City_Terracotta"
    cone(0,0,0.22,0.24,0.17,0.44,T,"pot",12); cone(0,0,0.44,0.26,0.24,0.06,T,"rim",12); cone(0,0,0.47,0.22,0.22,0.03,"City_Soil","soil",12)
    if tree:   # topiaire
        cone(0,0,0.72,0.06,0.05,0.5,"City_Trunk","stem",6); organic(0,0,1.05,0.42,"City_Leaf","ball",amp=0.5)
        for _ in range(4): organic(random.uniform(-.25,.25),random.uniform(-.25,.25),1.05+random.uniform(-.1,.2),0.26,random.choice(["City_Leaf","City_Leaf_Dk"]),"lb",amp=0.5)
    else:      # arbuste fleuri
        organic(0,0,0.66,0.30,"City_Leaf","sh",amp=0.5)
        for _ in range(4): organic(random.uniform(-.22,.22),random.uniform(-.22,.22),0.62+random.uniform(0,0.25),0.20,random.choice(["City_Leaf","City_Leaf_Dk"]),"lb",amp=0.5)
        for _ in range(5): organic(random.uniform(-.24,.24),random.uniform(-.24,.24),0.72,0.07,"City_Flower","fl",sub=1,amp=0.5)
    return finish(f"Veg_Pot_{tag}",CUR),finish(f"Veg_Pot_{tag}_Veg",VEG)
def build_grass(tag,ox,seed=13):
    RESET(ox,seed); cone(0,0,0.02,0.20,0.16,0.04,"City_Soil","dirt",8)
    blades(0,0,0.03,26,0.34,["City_Leaf","City_Leaf_Dk","City_Leaf_Lt"])
    return finish(f"Veg_Grass_{tag}",CUR),finish(f"Veg_Grass_{tag}_Veg",VEG)

def export_veg(pair,outdir):
    base,veg=pair
    if base is None and veg is not None and veg.name.endswith("_Veg"):
        veg.name=veg.name[:-4]   # tout-feuillage -> nom sans _Veg (1 mesh)
    objs=[o for o in (base,veg) if o]
    os.makedirs(outdir,exist_ok=True); bpy.context.scene.cursor.location=(0,0,0)
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs: o.select_set(True)
    bpy.context.view_layer.objects.active=objs[0]
    pts=[(o.matrix_world@v.co) for o in objs for v in o.data.vertices]
    zmin=min(p.z for p in pts); cx=sum(p.x for p in pts)/len(pts); cy=sum(p.y for p in pts)/len(pts)
    for o in objs: o.location=(o.location.x-cx,o.location.y-cy,o.location.z-zmin)
    bpy.ops.object.origin_set(type='ORIGIN_CURSOR')
    fp=os.path.join(outdir,objs[0].name+".fbx")
    bpy.ops.export_scene.fbx(filepath=fp,use_selection=True,object_types={'MESH'},apply_unit_scale=True,apply_scale_options='FBX_SCALE_ALL',mesh_smooth_type='FACE',bake_space_transform=True,axis_forward='-Z',axis_up='Y')
    return fp

if __name__ == "__main__":
    clean(); palette(); lighting()
    specs=[
        build_tree("A",0,H=2.8,crownR=1.0,dens=1.0,seed=3),
        build_tree("Big",8,H=3.6,crownR=1.35,dens=1.2,seed=4),
        build_tree("Small",16,H=1.9,crownR=0.7,dens=0.9,seed=6),
        build_tree("Blossom",24,H=2.4,crownR=0.95,leaf=("City_Leaf_Dk","City_Leaf_Dk"),blossom=True,dens=1.0,seed=8),
        build_conifer("A",32,H=3.0,seed=5),
        build_bush("A",40,s=0.75,seed=7), build_bush("Flower",44,s=0.7,seed=17,flower="City_Flower"),
        build_hedge("A",48,Lh=2.0,seed=9),
        build_potplant("A",54,seed=11,tree=False), build_potplant("Tree",58,seed=12,tree=True),
        build_grass("A",62,seed=13),
    ]
    # for p in specs: export_veg(p, "C:/.../veg_fbx")
