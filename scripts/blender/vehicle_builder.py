# Vehicle builder — Blender bpy, via Blender MCP (execute_blender_code).
#
# Véhicules cartoon low-poly (kit ville) : voiture citadine, berline, camionnette,
# bus, vélo, moto. Style chunky arrondi (bbox biseautée), palette CHAUDE, facetté.
# Échelle calée sur le kit rue (voie ~2.5). Orientation : front = -Y.
#
# COULEUR = slot unique `Vehicle_Body` → recolor par SWAP MATÉRIAU Unity (comme les
# props), PAS de FBX bakés. 10 matériaux dans Assets/Materials/Vehicles/.
#
# LEÇONS CLÉS (docs/city-session-lessons.md §23-25) :
#  - Cabine voiture = **bandeau vitré VERTICAL + toit + montants A/B/C** (pas de
#    pare-brise incliné bricolé : la 1re version avait une rotation de vitre à
#    l'envers + greenhouse boxy + verre géant qui dépassait). Vertical = zéro bug de
#    rotation, lit clean.
#  - **Vérifier le SENS de chaque rotation** (pare-brise à l'envers signalé) et que
#    rien ne flotte : pare-chocs doivent CHEVAUCHER la coque (gap = flotte), rétro
#    rattaché par un bras au montant A.
#  - **Moto** : silhouette = topline continue réservoir→selle→coque + fourche
#    INCLINÉE + gros pneus jantés + phare rond + moteur à ailettes + échappement.
#    Une moto trop sparse "ne ressemble pas à une moto" (retour user).
#  - **Vélo** : roues = TORE ajouré + rayons (pas disque plein) ; pédales/manivelles
#    à **180°** (une avant-basse, une arrière-haute), jamais au même niveau.

import bpy, bmesh, math, os
from mathutils import Vector

def clean(keep=("Camera","CitySun","Light")):
    for ob in list(bpy.data.objects):
        if ob.name not in keep: bpy.data.objects.remove(ob, do_unlink=True)
    for m in list(bpy.data.meshes):
        if m.users==0: bpy.data.meshes.remove(m)
def mat(name,rgb,rough=0.6):
    m=bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes=True; b=m.node_tree.nodes.get("Principled BSDF"); b.inputs["Base Color"].default_value=(*rgb,1); b.inputs["Roughness"].default_value=rough; return name
def mat_emit(name,rgb,strength=2.5):
    m=bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes=True; b=m.node_tree.nodes.get("Principled BSDF"); b.inputs["Base Color"].default_value=(*rgb,1)
    try: b.inputs["Emission Color"].default_value=(*rgb,1); b.inputs["Emission Strength"].default_value=strength
    except Exception: b.inputs["Emission"].default_value=(*rgb,1); b.inputs["Emission Strength"].default_value=strength
    return name
def glass_mat():
    m=bpy.data.materials.get("City_Window_Glass") or bpy.data.materials.new("City_Window_Glass")
    m.use_nodes=True; b=m.node_tree.nodes.get("Principled BSDF"); b.inputs["Base Color"].default_value=(0.30,0.44,0.54,1); b.inputs["Alpha"].default_value=0.72; m.blend_method='BLEND'; return "City_Window_Glass"
def palette():
    mat("Vehicle_Body",(0.90,0.42,0.38),0.45)   # SLOT recolorable (swap Unity)
    mat("Car_Tire",(0.16,0.16,0.18),0.7); mat("Car_Hub",(0.82,0.82,0.80),0.4); mat("Car_Chrome",(0.86,0.86,0.83),0.35)
    mat("Car_Trim",(0.24,0.24,0.26),0.5); mat("Car_Plate",(0.95,0.93,0.82))
    mat_emit("Car_Head",(1.0,0.93,0.72),2.5); mat_emit("Car_Tail",(0.92,0.22,0.20),2.2); glass_mat()
def lighting():
    sun=bpy.data.objects.get("CitySun")
    if not sun:
        ld=bpy.data.lights.new("CitySun",'SUN'); sun=bpy.data.objects.new("CitySun",ld); bpy.context.collection.objects.link(sun)
    sun.data.energy=2.8; sun.data.color=(1.0,0.95,0.86); sun.rotation_euler=(math.radians(55),math.radians(15),math.radians(40))
    w=bpy.context.scene.world.node_tree.nodes["Background"]; w.inputs[0].default_value=(0.90,0.87,0.80,1); w.inputs[1].default_value=0.30
    bpy.context.scene.render.engine='BLENDER_EEVEE'
    try:
        vs=bpy.context.scene.view_settings; vs.view_transform='Standard'; vs.look='None'; vs.exposure=-0.2
    except Exception: pass

CUR=[]; OX=0.0
def _M(n): return bpy.data.materials[n]
def box(cx,cy,cz,sx,sy,sz,m,name="p",rot=(0,0,0)):
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_cube(bm,size=1); bm.to_mesh(me); bm.free()
    ob.scale=(sx,sy,sz); ob.location=(cx+OX,cy,cz); ob.rotation_euler=rot; ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth=False
    CUR.append(ob); return ob
def bbox(cx,cy,cz,sx,sy,sz,m,name="p",bev=0.14,seg=2,rot=(0,0,0)):
    """Boîte biseautée = rondeur cartoon (scale AVANT bevel pour biseau uniforme)."""
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_cube(bm,size=1); bmesh.ops.scale(bm,vec=(sx,sy,sz),verts=bm.verts)
    bmesh.ops.bevel(bm,geom=bm.edges[:]+bm.verts[:],offset=bev,segments=seg,affect='EDGES',clamp_overlap=True)
    bm.to_mesh(me); bm.free(); ob.location=(cx+OX,cy,cz); ob.rotation_euler=rot; ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth=False
    CUR.append(ob); return ob
def cylob(cx,cy,cz,r,h,m,name="c",verts=12,rot=(0,0,0),bev=0.0):
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_cone(bm,cap_ends=True,segments=verts,radius1=r,radius2=r,depth=h)
    if bev: bmesh.ops.bevel(bm,geom=bm.edges[:],offset=bev,segments=1,affect='EDGES')
    bm.to_mesh(me); bm.free(); ob.location=(cx+OX,cy,cz); ob.rotation_euler=rot; ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth=False
    CUR.append(ob); return ob
def cyl_between(p0,p1,r,m,name="t",verts=8):
    p0=Vector(p0); p1=Vector(p1); vec=p1-p0; L=vec.length; mid=(p0+p1)/2
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_cone(bm,cap_ends=True,segments=verts,radius1=r,radius2=r,depth=L); bm.to_mesh(me); bm.free()
    ob.location=(mid.x+OX,mid.y,mid.z); ob.rotation_euler=vec.to_track_quat('Z','Y').to_euler(); ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth=False
    CUR.append(ob); return ob
def wheel(cx,cy,r,w):
    """Roue auto : pneu (axe X) + moyeu."""
    cylob(cx,cy,r,r,w,"Car_Tire","tire",16,rot=(0,1.5708,0),bev=0.04)
    cylob(cx+(w*0.30 if cx>0 else -w*0.30),cy,r,r*0.5,w*0.7,"Car_Hub","hub",12,rot=(0,1.5708,0))
def ring_wheel(cy,r):
    """Roue vélo : tore ajouré (axe X) + moyeu + 6 rayons."""
    bpy.ops.mesh.primitive_torus_add(major_radius=r,minor_radius=0.035,major_segments=20,minor_segments=6,location=(OX,cy,r))
    ob=bpy.context.active_object; ob.rotation_euler=(0,1.5708,0); ob.data.materials.append(_M("Car_Tire"))
    for p in ob.data.polygons: p.use_smooth=False
    CUR.append(ob)
    cylob(0,cy,r,0.05,0.05,"Car_Hub","hub",8,rot=(0,1.5708,0))
    for k in range(6):
        a=k*math.pi/3; cyl_between((0,cy,r),(0,cy+math.cos(a)*(r-0.04),r+math.sin(a)*(r-0.04)),0.006,"Car_Hub","spoke",4)
def finish(name):
    bpy.ops.object.select_all(action='DESELECT')
    for p in CUR: p.select_set(True)
    bpy.context.view_layer.objects.active=CUR[0]; bpy.ops.object.join()
    b=bpy.context.active_object; b.name=name
    for poly in b.data.polygons: poly.use_smooth=False
    bpy.ops.object.select_all(action='DESELECT'); return b
def greenhouse(BODY,cyc,zc,halfL,pillars_y):
    """Cabine = bandeau vitré vertical + toit + montants (pillars_y = positions A/B/C)."""
    box(0,cyc,zc,1.18,halfL*2,0.33,"City_Window_Glass","glass")
    bbox(0,cyc,zc+0.22,1.16,halfL*2-0.05,0.16,BODY,"roof",bev=0.10,seg=2)
    for x in (-0.585,0.585):
        for y in pillars_y: box(x,cyc+y,zc,0.05,0.07,0.35,BODY,"pillar")

B="Vehicle_Body"; CH="Car_Chrome"; TR="Car_Trim"

def build_car(ox):
    global CUR,OX; CUR=[]; OX=ox
    bbox(0,0,0.56,1.5,3.3,0.55,B,"body",bev=0.16,seg=2); greenhouse(B,0.10,1.00,0.70,(-0.70,0.02,0.70))
    for (x,y) in [(-0.74,-1.02),(0.74,-1.02),(-0.74,1.02),(0.74,1.02)]: wheel(x,y,0.33,0.30)
    bbox(0,-1.66,0.46,1.46,0.20,0.24,CH,"bF",bev=0.07,seg=1); bbox(0,1.66,0.46,1.46,0.20,0.24,CH,"bR",bev=0.07,seg=1)
    for x in (-0.48,0.48): box(x,-1.63,0.66,0.26,0.08,0.15,"Car_Head","h")
    for x in (-0.48,0.48): box(x,1.63,0.68,0.26,0.08,0.14,"Car_Tail","t")
    box(0,-1.64,0.52,0.6,0.05,0.14,TR,"g"); box(0,-1.67,0.40,0.34,0.03,0.10,"Car_Plate","pF"); box(0,1.69,0.40,0.34,0.03,0.10,"Car_Plate","pR")
    for x in (-0.755,0.755): box(x,0.0,0.60,0.015,1.7,0.02,TR,"dl")
    for sx in (-1,1): cyl_between((sx*0.60,-0.60,0.92),(sx*0.73,-0.52,0.90),0.02,TR,"marm")  # bras retro
    for sx in (-1,1): box(sx*0.76,-0.50,0.90,0.12,0.09,0.08,B,"mirror")
    return finish("Vehicle_Car_A")
def build_sedan(ox):
    global CUR,OX; CUR=[]; OX=ox
    bbox(0,0,0.55,1.5,4.1,0.54,B,"body",bev=0.16,seg=2); greenhouse(B,0.05,0.99,0.95,(-0.95,-0.05,0.85))
    for (x,y) in [(-0.74,-1.35),(0.74,-1.35),(-0.74,1.35),(0.74,1.35)]: wheel(x,y,0.33,0.30)
    bbox(0,-2.06,0.46,1.46,0.20,0.24,CH,"bF",bev=0.07,seg=1); bbox(0,2.06,0.46,1.46,0.20,0.24,CH,"bR",bev=0.07,seg=1)
    for x in (-0.48,0.48): box(x,-2.03,0.62,0.28,0.08,0.14,"Car_Head","h")
    for x in (-0.5,0.5): box(x,2.03,0.64,0.30,0.08,0.13,"Car_Tail","t")
    box(0,-2.04,0.50,0.7,0.05,0.13,TR,"g"); box(0,-2.07,0.38,0.34,0.03,0.10,"Car_Plate","pF"); box(0,2.09,0.38,0.34,0.03,0.10,"Car_Plate","pR")
    for x in (-0.755,0.755): box(x,0.0,0.58,0.015,2.2,0.02,TR,"dl")
    for sx in (-1,1): cyl_between((sx*0.60,-0.85,0.90),(sx*0.73,-0.77,0.88),0.02,TR,"marm")
    for sx in (-1,1): box(sx*0.76,-0.75,0.88,0.12,0.09,0.08,B,"mirror")
    return finish("Vehicle_Sedan_A")
def build_van(ox):
    global CUR,OX; CUR=[]; OX=ox; GL="City_Window_Glass"
    bbox(0,0.1,1.12,1.55,3.7,1.25,B,"body",bev=0.16,seg=2)   # front -1.75 / rear 1.95
    box(0,-1.80,1.30,1.36,0.05,0.55,GL,"wind")
    for x in (-0.72,0.72): box(x,-1.35,1.30,0.05,0.70,0.45,GL,"sidewin")
    box(0.78,0.7,1.05,0.02,1.3,0.9,TR,"door")
    for (x,y) in [(-0.75,-1.15),(0.75,-1.15),(-0.75,1.25),(0.75,1.25)]: wheel(x,y,0.36,0.32)
    bbox(0,-1.72,0.5,1.5,0.22,0.26,CH,"bF",bev=0.07,seg=1); bbox(0,1.90,0.5,1.5,0.22,0.26,CH,"bR",bev=0.07,seg=1)  # pare-chocs qui chevauchent la coque
    for x in (-0.5,0.5): box(x,-1.78,0.78,0.28,0.10,0.16,"Car_Head","h")
    for x in (-0.5,0.5): box(x,1.92,1.0,0.24,0.08,0.5,"Car_Tail","t")
    box(0,-1.80,0.55,0.7,0.06,0.16,TR,"g"); box(0,-1.82,0.40,0.34,0.04,0.10,"Car_Plate","pF")
    return finish("Vehicle_Van_A")
def build_bus(ox):
    global CUR,OX; CUR=[]; OX=ox; GL="City_Window_Glass"
    bbox(0,0,1.15,1.72,6.4,1.35,B,"body",bev=0.18,seg=2)
    for x in (-0.865,0.865): box(x,0.2,1.42,0.03,5.4,0.44,GL,"sidewin")
    box(0,-3.22,1.42,1.5,0.05,0.5,GL,"wind"); box(0,3.22,1.40,1.5,0.05,0.42,GL,"rearwin")
    for x in (-0.87,0.87):
        for i in range(9): box(x,-2.4+i*0.62,1.42,0.05,0.06,0.46,B,"pillar")
    box(-0.88,-2.2,0.95,0.03,0.9,0.9,TR,"door"); box(0,0.2,0.72,1.74,5.6,0.10,TR,"stripe")
    for (x,y) in [(-0.8,-2.2),(0.8,-2.2),(-0.8,2.2),(0.8,2.2)]: wheel(x,y,0.44,0.34)
    bbox(0,-3.32,0.55,1.66,0.16,0.30,CH,"bF",bev=0.07,seg=1); bbox(0,3.32,0.55,1.66,0.16,0.30,CH,"bR",bev=0.07,seg=1)
    for x in (-0.55,0.55): box(x,-3.28,0.80,0.28,0.08,0.18,"Car_Head","h")
    for x in (-0.6,0.6): box(x,3.28,0.95,0.22,0.06,0.4,"Car_Tail","t")
    return finish("Vehicle_Bus_A")
def build_bike(ox):
    global CUR,OX; CUR=[]; OX=ox; FR=B; r=0.36; yf=-0.60; yr=0.60
    ring_wheel(yf,r); ring_wheel(yr,r)
    bb=(0,0.0,0.42); st=(0,0.12,0.88); hd=(0,-0.42,0.84)
    cyl_between(bb,(0,yr,r),0.028,FR,"cs"); cyl_between(st,(0,yr,r),0.028,FR,"ss")
    cyl_between(bb,st,0.033,FR,"stube"); cyl_between(bb,hd,0.033,FR,"dtube"); cyl_between(st,hd,0.033,FR,"ttube")
    cyl_between(hd,(0,yf,r),0.028,FR,"fork")
    cyl_between(hd,(0,-0.47,1.04),0.024,TR,"stem"); box(0,-0.49,1.06,0.44,0.05,0.05,TR,"bar")
    box(0,0.15,0.98,0.13,0.28,0.05,TR,"saddle"); cyl_between(st,(0,0.14,0.94),0.02,TR,"spost")
    cylob(0,0.0,0.42,0.028,0.30,TR,"crankaxle",6,rot=(0,1.5708,0))
    # pédales OPPOSÉES (manivelles à 180°) : droite avant-bas, gauche arrière-haut
    cyl_between(bb,(0.085,-0.15,0.30),0.02,TR,"crankR"); box(0.12,-0.16,0.28,0.11,0.15,0.03,TR,"pedalR")
    cyl_between(bb,(-0.085,0.15,0.55),0.02,TR,"crankL"); box(-0.12,0.16,0.57,0.11,0.15,0.03,TR,"pedalL")
    return finish("Vehicle_Bike_A")
def build_moto(ox):
    global CUR,OX; CUR=[]; OX=ox
    def mw(cy,r=0.40):
        cylob(0,cy,r,r,0.20,"Car_Tire","tire",20,rot=(0,1.5708,0),bev=0.05)
        cylob(0,cy,r,r*0.6,0.22,"Car_Hub","rim",16,rot=(0,1.5708,0)); cylob(0,cy,r,r*0.18,0.24,TR,"hub",8,rot=(0,1.5708,0))
    rw=(0,0.70,0.40); fw=(0,-0.86,0.40); mw(0.70); mw(-0.86)
    bbox(0,-0.22,0.82,0.32,0.56,0.28,B,"tank",bev=0.13,seg=2,rot=(math.radians(-6),0,0))   # topline
    box(0,0.26,0.78,0.26,0.55,0.09,TR,"seat"); bbox(0,0.60,0.86,0.28,0.32,0.22,B,"tail",bev=0.10,seg=1)
    box(0,0.66,0.98,0.26,0.10,0.05,"Car_Tail","tailL")
    box(0,-0.05,0.50,0.34,0.5,0.42,TR,"engine")
    for i in range(4): box(0,-0.05,0.36+i*0.07,0.38,0.46,0.02,CH,"fin")   # ailettes
    box(0,-0.28,0.55,0.30,0.14,0.30,CH,"cyl")
    hs=(0,-0.52,0.86); cyl_between(hs,(0,0.30,0.60),0.045,B,"backbone")
    for sx in (-1,1): cyl_between((sx*0.06,hs[1],hs[2]),(sx*0.06,fw[1],fw[2]),0.032,TR,"fork")   # fourche inclinée
    cyl_between((0,0.28,0.56),rw,0.035,TR,"swingarm")
    cyl_between(hs,(0,-0.44,1.08),0.03,TR,"riser"); box(0,-0.46,1.10,0.50,0.05,0.05,TR,"bar")
    for sx in (-1,1): cylob(sx*0.22,-0.46,1.10,0.03,0.12,B,"grip",6,rot=(0,1.5708,0))
    cylob(0,-0.64,0.86,0.13,0.05,CH,"headrim",14,rot=(1.5708,0,0)); cylob(0,-0.665,0.86,0.10,0.05,"Car_Head","headlens",14,rot=(1.5708,0,0))
    box(0,-0.86,0.74,0.22,0.34,0.05,B,"ffend",rot=(math.radians(12),0,0)); box(0,0.70,0.72,0.24,0.40,0.05,B,"rfend")
    cyl_between((0.14,-0.18,0.44),(0.17,0.45,0.34),0.045,CH,"pipe"); cylob(0.18,0.62,0.34,0.07,0.34,CH,"muffler",10,rot=(1.5708,0,0))
    return finish("Vehicle_Moto_A")

KIT=[build_car,build_sedan,build_van,build_bus,build_bike,build_moto]

# 10 gammes recolor (slot Vehicle_Body) -> matériaux URP Assets/Materials/Vehicles/. RGB lin :
COLORWAYS={"Coral":(0.90,0.42,0.38),"Teal":(0.28,0.60,0.58),"Yellow":(0.95,0.78,0.40),
    "Red":(0.86,0.32,0.28),"Sky":(0.46,0.68,0.82),"Orange":(0.93,0.52,0.24),
    "Mint":(0.55,0.80,0.62),"Cream":(0.94,0.90,0.78),"Lavender":(0.66,0.58,0.82),"Plum":(0.62,0.34,0.42)}

def export_veh(ob,outdir):
    """1 mesh/véhicule, pivot = base au sol centré XY. -Z fwd / Y up."""
    os.makedirs(outdir,exist_ok=True); bpy.context.scene.cursor.location=(0,0,0)
    bpy.ops.object.select_all(action='DESELECT'); ob.select_set(True); bpy.context.view_layer.objects.active=ob
    pts=[(ob.matrix_world@v.co) for v in ob.data.vertices]
    zmin=min(p.z for p in pts); cx=sum(p.x for p in pts)/len(pts); cy=sum(p.y for p in pts)/len(pts)
    ob.location=(ob.location.x-cx,ob.location.y-cy,ob.location.z-zmin)
    bpy.ops.object.origin_set(type='ORIGIN_CURSOR')
    fp=os.path.join(outdir,ob.name+".fbx")
    bpy.ops.export_scene.fbx(filepath=fp,use_selection=True,object_types={'MESH'},apply_unit_scale=True,apply_scale_options='FBX_SCALE_ALL',mesh_smooth_type='FACE',bake_space_transform=True,axis_forward='-Z',axis_up='Y')
    return fp
# Unity : import_model_file(source_path=<fbx>, output_folder="Assets/Models/City/Vehicles", name=...)

if __name__ == "__main__":
    clean(); palette(); lighting()
    for i,fn in enumerate(KIT):
        fn(i*8.0)
