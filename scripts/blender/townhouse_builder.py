# Townhouse builder — Blender bpy, à exécuter via Blender MCP (execute_blender_code).
#
# Maison de ville ETROITE à toit à DEUX PENTES (pignon) — silhouette signature qui
# diversifie le kit (apparts/corner ont un toit PLAT). Méthode & pièges :
# docs/city-pipeline.md + docs/city-session-lessons.md (§9-10 : toit = dalles
# SOLIDES fermées, jamais 2 faces coplanaires). Palette/style : mémoire
# city-color-palette. Réutilise la palette warm canonique.
#
# NOUVEAUTES vs corner_builder.py :
#   - Toit 2 pentes : chaque pan = slope_slab() = dalle SOLIDE fermée (dessus +
#     dessous décalé -T + 4 côtés dont les 2 rives) -> épaisseur visible ET rives
#     bouchées. PAS de prisme triangulaire plein (fait un gros triangle plat moche,
#     refusé user). Pignons = gable_solid() = triangle extrudé en épaisseur.
#   - Rangs de tuiles : tile_courses() = fines marches proéminentes (offset le long
#     de la normale de pente, NON coplanaires) en terracotta un poil + foncée.
#   - Cheminée : plantée en TRAVERSANT le toit (bas sous la surface de pente) ->
#     jamais de flottement (retour user).
#
# PIEGES (lessons) :
#   - Jamais 2 faces coplanaires superposées = z-fight. Détail plaqué -> RESSORT.
#   - Mesh custom : vérifier 0 arête ouverte (sum(len(e.link_faces)!=2)==0).
#   - Fenêtre dernier étage doit dégager la corniche (haut fenêtre < EAV).

import bpy, bmesh, math, random
from mathutils import Vector, noise

def clean(keep=("Camera",)):
    for ob in list(bpy.data.objects):
        if ob.name not in keep: bpy.data.objects.remove(ob, do_unlink=True)
    for m in list(bpy.data.meshes):
        if m.users==0: bpy.data.meshes.remove(m)

def mat(name,rgb,rough=0.7):
    m=bpy.data.materials.get(name) or bpy.data.materials.new(name); m.use_nodes=True
    b=m.node_tree.nodes.get("Principled BSDF"); b.inputs["Base Color"].default_value=(*rgb,1); b.inputs["Roughness"].default_value=rough; return name

def palette():
    mat("City_Frame_White",(0.97,0.93,0.85)); mat("City_Window_Glass",(0.58,0.80,0.90))
    mat("City_Roof_Grey",(0.72,0.66,0.60)); mat("City_Metal_Dk",(0.53,0.51,0.53))
    mat("City_Terracotta",(0.84,0.48,0.32)); mat("City_Leaf",(0.48,0.73,0.34))
    mat("City_Leaf_Dk",(0.34,0.57,0.28)); mat("City_Flower",(0.97,0.52,0.58))
    mat("City_Wall_Beige",(0.95,0.82,0.66)); mat("City_Trim_Red",(0.96,0.47,0.40))
    mat("City_Door_Wood",(0.83,0.57,0.34)); mat("City_Sign",(0.28,0.72,0.68))

def lighting():
    sun=bpy.data.objects.get("CitySun")
    if not sun:
        ld=bpy.data.lights.new("CitySun",'SUN'); sun=bpy.data.objects.new("CitySun",ld); bpy.context.collection.objects.link(sun)
    sun.data.energy=2.8; sun.data.color=(1.0,0.95,0.86); sun.rotation_euler=(math.radians(55),math.radians(15),math.radians(40))
    w=bpy.context.scene.world.node_tree.nodes["Background"]; w.inputs[0].default_value=(0.90,0.87,0.80,1); w.inputs[1].default_value=0.30
    sc=bpy.context.scene
    for e in ('BLENDER_EEVEE_NEXT','BLENDER_EEVEE'):
        try: sc.render.engine=e; break
        except: pass
    sc.render.resolution_x=1000; sc.render.resolution_y=1000
    try: sc.view_settings.view_transform='Standard'; sc.view_settings.look='None'; sc.view_settings.exposure=-0.2
    except: pass

def aim_cam(loc,tgt,lens=55):
    cam=bpy.data.objects.get("Camera")
    if not cam:
        cd=bpy.data.cameras.new("Camera"); cam=bpy.data.objects.new("Camera",cd); bpy.context.collection.objects.link(cam)
    cam.data.type='PERSP'; cam.location=loc; cam.rotation_euler=(Vector(tgt)-Vector(loc)).to_track_quat('-Z','Y').to_euler(); cam.data.lens=lens; bpy.context.scene.camera=cam

CUR=[]
def _M(n): return bpy.data.materials[n]

def box(cx,cy,cz,sx,sy,sz,m,name="b",rot=(0,0,0)):
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_cube(bm,size=1); bm.to_mesh(me); bm.free()
    ob.scale=(sx,sy,sz); ob.location=(cx,cy,cz); ob.rotation_euler=rot; ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth=False
    CUR.append(ob); return ob

def solid(verts,faces,m,name):
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); vv=[bm.verts.new(v) for v in verts]
    for f in faces: bm.faces.new([vv[i] for i in f])
    bmesh.ops.recalc_face_normals(bm,faces=bm.faces); bm.to_mesh(me); bm.free()
    ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth=False
    CUR.append(ob); return ob

def cyl(cx,cy,cz,r,h,m,name="c",v=6,axis='Z'):
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_cone(bm,cap_ends=True,segments=v,radius1=r,radius2=r,depth=h); bm.to_mesh(me); bm.free()
    ob.location=(cx,cy,cz)
    if axis=='X': ob.rotation_euler=(0,1.5708,0)
    elif axis=='Y': ob.rotation_euler=(1.5708,0,0)
    ob.data.materials.append(_M(m)); CUR.append(ob); return ob

_idx=[0]
def organic(cx,cy,cz,s,m,name="veg",sub=2,amp=0.42):
    """Touffe facettée = icosphère sub2 + bruit 2 octaves + bevel + flat. JAMAIS de subsurf."""
    _idx[0]+=1; o1=Vector((_idx[0]*4.1,_idx[0]*2.7,_idx[0]*3.3)); o2=Vector((_idx[0]*7.3,_idx[0]*5.1,_idx[0]*6.2))
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_icosphere(bm,subdivisions=sub,radius=1.0)
    for v in bm.verts:
        n=noise.noise(v.co*1.7+o1)*0.7+noise.noise(v.co*3.6+o2)*0.3
        v.co+=v.normal*(amp*n)+Vector((random.uniform(-.05,.05),)*3)
    bmesh.ops.bevel(bm,geom=bm.edges[:],offset=0.03,segments=1,affect='EDGES')
    bm.to_mesh(me); bm.free()
    ob.scale=(s*random.uniform(.85,1.2),s*random.uniform(.85,1.2),s*random.uniform(.7,1.0))
    ob.location=(cx,cy,cz); ob.rotation_euler=(random.uniform(-.25,.25),random.uniform(-.25,.25),random.uniform(0,3.14))
    ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth=False
    CUR.append(ob); return ob

def join_parts(parts,name):
    bpy.ops.object.select_all(action='DESELECT')
    for p in parts: p.select_set(True)
    bpy.context.view_layer.objects.active=parts[0]; bpy.ops.object.join()
    b=bpy.context.active_object; b.name=name
    for poly in b.data.polygons: poly.use_smooth=False
    bpy.ops.object.select_all(action='DESELECT'); return b

# matériaux canoniques partagés
STONE="City_Roof_Grey"; FRAME="City_Frame_White"; GLASS="City_Window_Glass"
METAL="City_Metal_Dk"; DOOR="City_Door_Wood"; SIGN="City_Sign"; TERRA="City_Terracotta"
LEAF="City_Leaf"; LEAF2="City_Leaf_Dk"; FLOWER="City_Flower"

def build_townhouse(tag="A", ox=0.0, wall=(0.95,0.82,0.66), trim=(0.96,0.47,0.40),
                    tile=(0.84,0.48,0.32), nup=2, dens=1.0, seed=5):
    """Maison de ville étroite à toit pignon. base+veg dans CUR ; renvoie (base,veg).
    nup = nb d'étages au-dessus du RDC (1 -> R+1, 2 -> R+2). ox = offset X (rangée).
    Matériaux per-tag pour murs/volets/tuiles (sinon écrase les canoniques partagés)."""
    global CUR
    CUR=[]; random.seed(seed)
    WALL=mat(f"TH_{tag}_wall",wall); TRIM=mat(f"TH_{tag}_trim",trim)
    TILE=mat(f"TH_{tag}_tile",tile); TILE2=mat(f"TH_{tag}_tile2",tuple(c*0.88 for c in tile))

    W=3.4; D=4.6; HW=W/2; HD=D/2; GH=2.4; FH=1.6
    EAV=GH+FH*nup; RIDGE=EAV+1.55; OV=0.28; OVG=0.22
    SL=HW+OV; DZ=RIDGE-EAV
    fbot=[GH+FH*i for i in range(nup)]  # bas de chaque étage upper

    # ---- CORPS ----
    box(0,0,EAV/2,W,D,EAV,WALL,"body")
    box(0,0,0.28,W+0.06,D+0.06,0.56,STONE,"plinth")
    for z in fbot: box(0,0,z,W+0.04,D+0.04,0.10,FRAME,"band")
    box(0,0,EAV,W+0.10,D+0.10,0.14,FRAME,"cornice")

    # ---- PIGNONS solides (triangle extrudé, plâtre) ----
    def gable_solid(yout,yin,name):
        v=[(-HW,yout,EAV),(HW,yout,EAV),(0,yout,RIDGE),(-HW,yin,EAV),(HW,yin,EAV),(0,yin,RIDGE)]
        f=[(0,1,2),(5,4,3),(0,3,4,1),(1,4,5,2),(2,5,3,0)]
        return solid(v,f,WALL,name)
    gable_solid(-HD,-HD+0.16,"gable_front"); gable_solid(HD,HD-0.16,"gable_back")

    # ---- TOIT : 2 pans = dalles SOLIDES fermées ----
    y0=-(HD+OVG); y1=(HD+OVG); T=0.16
    def slope_slab(eave_x,name):
        top=[(eave_x,y0,EAV),(0.0,y0,RIDGE),(0.0,y1,RIDGE),(eave_x,y1,EAV)]
        bot=[(x,y,z-T) for (x,y,z) in top]; v=top+bot
        f=[(0,1,2,3),(7,6,5,4),(0,3,7,4),(1,5,6,2),(0,4,5,1),(3,2,6,7)]
        return solid(v,f,TILE,name)
    slope_slab(-SL,"slopeL"); slope_slab(SL,"slopeR")
    box(0,0,RIDGE+0.02,0.16,D+2*OVG,0.12,STONE,"ridgecap")
    box(-SL,0,EAV-0.12,0.07,D+2*OVG,0.18,STONE,"fasciaL")
    box(SL,0,EAV-0.12,0.07,D+2*OVG,0.18,STONE,"fasciaR")
    # rangs de tuiles (marches proéminentes, non coplanaires)
    ang=math.atan2(DZ,SL)
    for s in (-1,1):
        nx=s*math.sin(ang); nz=math.cos(ang)
        for f in (0.16,0.30,0.44,0.58,0.72,0.86):
            x=s*SL*f; z=RIDGE-DZ*f
            box(x+nx*0.05,0.0,z+nz*0.05,0.09,D+2*OVG-0.02,0.05,TILE2,f"tile_{s}",rot=(0,s*ang,0))
    # cheminée plantée (traverse le toit)
    chx,chy=0.7,0.5; surf=RIDGE-DZ*(chx/SL)
    ch_bot=surf-0.7; ch_top=RIDGE+0.62
    box(chx,chy,(ch_bot+ch_top)/2,0.5,0.5,ch_top-ch_bot,STONE,"chimney")
    box(chx,chy,ch_top+0.06,0.62,0.62,0.14,TERRA,"chimcap")

    # ---- FENÊTRES ----
    def win_y(cx,z,w,h,ys,shutters=False,sill=True):
        yf=ys*HD; o=ys
        box(cx,yf+o*0.02,z,w+0.16,0.10,h+0.16,FRAME,"wf")
        box(cx,yf+o*0.05,z,w,0.10,h,GLASS,"gl")
        box(cx,yf+o*0.10,z,0.05,0.06,h,FRAME,"mv"); box(cx,yf+o*0.10,z,w,0.06,0.05,FRAME,"mh")
        if sill: box(cx,yf+o*0.06,z-h/2-0.08,w+0.24,0.24,0.10,STONE,"sill")
        if shutters:
            for sx in (-(w/2+0.13),(w/2+0.13)): box(cx+sx,yf+o*0.04,z,0.22,0.06,h+0.06,TRIM,"shut")
    def win_x(cy,z,w,h,xs,sill=True):
        xf=xs*HW; o=xs
        box(xf+o*0.02,cy,z,0.10,w+0.16,h+0.16,FRAME,"wf")
        box(xf+o*0.05,cy,z,0.10,w,h,GLASS,"gl")
        box(xf+o*0.10,cy,z,0.06,0.05,h,FRAME,"mv"); box(xf+o*0.10,cy,z,0.06,w,0.05,FRAME,"mh")
        if sill: box(xf+o*0.06,cy,z-h/2-0.08,0.24,w+0.24,0.10,STONE,"sill")

    # FRONT (-Y) : RDC porte + fenêtre ; étages upper = 2 fenêtres à volets ; lucarne pignon
    box(-0.60,-HD-0.02,1.05,1.0,0.12,2.1,DOOR,"door")
    box(-0.60,-HD-0.10,1.05,0.05,0.05,0.20,METAL,"dhandle")
    box(-0.60,-HD-0.05,2.18,1.24,0.16,0.14,FRAME,"dlintel")
    box(-0.60,-HD-0.30,0.12,1.3,0.5,0.22,STONE,"step")
    box(-0.60,-HD-0.28,2.32,1.35,0.42,0.12,TRIM,"doorhood"); box(-0.60,-HD-0.30,2.26,1.20,0.36,0.06,STONE,"hoodtop")
    box(-1.18,-HD-0.06,2.05,0.10,0.12,0.14,METAL,"lampbkt"); cyl(-1.18,-HD-0.16,2.0,0.06,0.16,SIGN,"lamp",8,'Y')
    box(-0.60,-HD-0.12,1.75,0.16,0.04,0.20,SIGN,"numplate")
    win_y(0.78,1.35,0.8,1.25,-1,sill=True)
    for b in fbot:
        for cx in (-0.78,0.78): win_y(cx,b+0.95,0.8,1.25,-1,shutters=True)
    win_y(0.0,EAV+0.55,0.7,0.7,-1,sill=False)  # lucarne pignon
    # BACK (+Y) : fenêtres sobres
    for z in [1.35]+[b+0.95 for b in fbot]:
        for cx in (-0.78,0.78): win_y(cx,z,0.8,1.25,1,sill=False)
    # SIDES (±X) : fenêtres tous niveaux
    for xs in (-1,1):
        for z in [1.35]+[b+0.95 for b in fbot]:
            for cy in (-1.15,1.15): win_x(cy,z,0.8,1.25,xs,sill=(z<2))
    # descente EP arrière-droite
    cyl(HW+0.06,HD-0.12,EAV/2,0.06,EAV-0.3,METAL,"downpipe",6)
    base_parts=list(CUR)

    # ---- VÉGÉTATION (objet séparé _Veg pour vent/LOD) ----
    def planter_front(cx,ztop):
        box(cx,-HD-0.16,ztop,0.9,0.22,0.16,TERRA,"planterbox")
        for uu in (-0.28,0.0,0.28):
            if random.random()<dens: organic(cx+uu,-HD-0.18,ztop+0.16,0.17,LEAF if random.random()>.4 else LEAF2,"tuft")
        organic(cx-0.15,-HD-0.20,ztop+0.10,0.10,FLOWER,"bloom",sub=1,amp=0.5)
        organic(cx+0.20,-HD-0.22,ztop+0.02,0.13,LEAF,"drape",amp=0.5)
    for b in fbot:
        for cx in (-0.78,0.78): planter_front(cx,b+0.95-0.08-1.25/2+0.03)  # sur l'appui
    box(0.78,-HD-0.16,0.72,0.9,0.22,0.16,TERRA,"planterbox")
    for uu in (-0.26,0.10): organic(0.78+uu,-HD-0.18,0.90,0.16,LEAF,"tuft")
    # lierre grimpant angle avant-gauche
    for i in range(int(7*dens)+2):
        z=0.7+i*0.7
        if z>EAV-0.4: break
        organic(-HW+0.10*math.sin(i*1.1),-HD-0.05,z,0.22,LEAF2 if i%2 else LEAF,"ivy",amp=0.48)
    # pots entrée
    cyl(0.15,-HD-0.32,0.24,0.16,0.44,TERRA,"pot",8)
    organic(0.15,-HD-0.32,0.58,0.28,LEAF,"potbush",amp=0.44); organic(0.20,-HD-0.36,0.66,0.11,FLOWER,"potbloom",sub=1,amp=0.45)
    cyl(-1.25,-HD-0.30,0.20,0.14,0.36,TERRA,"pot",8); organic(-1.25,-HD-0.32,0.48,0.24,LEAF2,"potbush",amp=0.44)
    veg_parts=CUR[len(base_parts):]

    base=join_parts(base_parts,f"Building_Townhouse_{tag}")
    veg=join_parts(veg_parts,f"Building_Townhouse_{tag}_Veg")
    for o in (base,veg): o.location.x+=ox  # décale la rangée
    return base, veg

def export_fbx(obj, outdir):
    import os
    os.makedirs(outdir, exist_ok=True)
    bpy.ops.object.select_all(action='DESELECT'); obj.select_set(True); bpy.context.view_layer.objects.active=obj
    bpy.ops.object.origin_set(type='ORIGIN_GEOMETRY', center='BOUNDS'); obj.location=(0,0,0)
    zmin=min((obj.matrix_world@v.co).z for v in obj.data.vertices); obj.location=(0,0,-zmin)
    bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)
    fp=os.path.join(outdir, obj.name+".fbx")
    bpy.ops.export_scene.fbx(filepath=fp, use_selection=True, object_types={'MESH'},
        apply_unit_scale=True, apply_scale_options='FBX_SCALE_ALL', mesh_smooth_type='FACE',
        bake_space_transform=True, axis_forward='-Z', axis_up='Y')
    return fp

# 5 variantes (famille chaude) : tag, ox, wall, trim, tile, nup, dens, seed
TOWNHOUSE_SPECS=[
 ("A",-16,(0.95,0.82,0.66),(0.96,0.47,0.40),(0.84,0.48,0.32),2,1.0,5),
 ("B", -8,(0.96,0.74,0.60),(0.28,0.72,0.68),(0.78,0.44,0.30),2,0.7,11),
 ("C",  0,(0.96,0.87,0.60),(0.96,0.47,0.40),(0.84,0.48,0.32),1,1.0,3),
 ("D",  8,(0.93,0.72,0.68),(0.83,0.57,0.34),(0.80,0.40,0.34),2,1.0,7),
 ("E", 16,(0.90,0.80,0.62),(0.28,0.72,0.68),(0.82,0.45,0.30),1,0.6,9),
]

if __name__ == "__main__":
    clean(); palette(); lighting()
    for s in TOWNHOUSE_SPECS: build_townhouse(*s)
    aim_cam((21,-24,10),(0.2,0,4.6))
