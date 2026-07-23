# Corner building builder — Blender bpy, à exécuter via Blender MCP (execute_blender_code).
#
# Immeuble d'angle chanfreiné (pan coupé en angle de rue portant l'entrée).
# Méthode & pièges : docs/city-pipeline.md + docs/city-session-lessons.md (§7-8).
# Palette/style : mémoire city-color-palette. Réutilise la palette warm canonique.
#
# NOUVEAUTÉ vs city_builder.py (shop) :
#   - prism(footprint,z0,z1,mat) : extrude un POLYGONE plan en un mesh propre
#     (bmesh + recalc normals). Le corps chanfreiné = footprint pentagonal
#     (rectangle avec 1 coin coupé à 45°). Bandeaux/corniche/plinthe/parapet =
#     mêmes prisms à scale_fp() près. PAS de boolean.
#   - obox(px,py,pz,ang,sx,sy,sz,mat) : boîte ORIENTÉE en Z. ang=atan2(ny,nx) de
#     la normale de face → local X=normale(profondeur), Y=tangente(largeur),
#     Z=hauteur. Permet de poser fenêtres/détails sur N'IMPORTE quelle face, y
#     compris le chanfrein à 45°, sans trigo manuel.
#   - window(base,nrm,u,z,w,h) : fenêtre générique par (normale, point-origine de
#     face, offset tangent u). Cadre + verre + croix EN RELIEF + appui.
#
# PIÈGES CLÉS (voir lessons §7-8) :
#   - Cadres trop larges vs espacement / trop de colonnes sur face étroite →
#     fenêtres qui se chevauchent (et foreshorten en "collées" à 3/4). Garder
#     frame_width < espacement ET viser gap >= 0.4. Face étroite = moins de colonnes.
#   - Meneaux coplanaires au verre = z-fight. Le meneau doit RESSORTIR du verre
#     (offset meneau > offset verre + ~0.05). Vaut pour tout détail plaqué.
#   - Vérifier par vue ORTHO droite-face (annule foreshorten) + vue 3/4.

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
    for e in ('BLENDER_EEVEE_NEXT','BLENDER_EEVEE'):  # 5.x = EEVEE_NEXT
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

def prism(fp,z0,z1,m,name="prism"):
    """Extrude un polygone plan fp=[(x,y),...] de z0 à z1 en un mesh flat propre."""
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); vb=[bm.verts.new((x,y,z0)) for (x,y) in fp]; vt=[bm.verts.new((x,y,z1)) for (x,y) in fp]
    bm.faces.new(vb); bm.faces.new(vt); n=len(fp)
    for i in range(n):
        j=(i+1)%n; bm.faces.new((vb[i],vb[j],vt[j],vt[i]))
    bmesh.ops.recalc_face_normals(bm,faces=bm.faces); bm.to_mesh(me); bm.free()
    ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth=False
    CUR.append(ob); return ob

def scale_fp(fp,s): return [(x*s,y*s) for (x,y) in fp]

def obox(px,py,pz,ang,sx,sy,sz,m,name="p"):
    """Boîte orientée en Z. ang=atan2(ny,nx) d'une normale de face -> local X=normale."""
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_cube(bm,size=1); bm.to_mesh(me); bm.free()
    ob.scale=(sx,sy,sz); ob.location=(px,py,pz); ob.rotation_euler=(0,0,ang); ob.data.materials.append(_M(m))
    for p in me.polygons: p.use_smooth=False
    CUR.append(ob); return ob

def cyl(cx,cy,cz,r,h,m,name="c",verts=8,axis='Z'):
    me=bpy.data.meshes.new(name); ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    bm=bmesh.new(); bmesh.ops.create_cone(bm,cap_ends=True,segments=verts,radius1=r,radius2=r,depth=h); bm.to_mesh(me); bm.free()
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

WALL="City_Wall_Beige"; STONE="City_Roof_Grey"; FRAME="City_Frame_White"; GLASS="City_Window_Glass"
METAL="City_Metal_Dk"; DOOR="City_Door_Wood"; SIGN="City_Sign"; TRIM="City_Trim_Red"; TERRA="City_Terracotta"
LEAF="City_Leaf"; LEAF2="City_Leaf_Dk"; FLOWER="City_Flower"

def window(base,nrm,u,z,w,h,sill=True):
    """Fenêtre sur une face quelconque. base=point-origine face, nrm=normale unit, u=offset tangent."""
    nx,ny=nrm; ang=math.atan2(ny,nx); t=(-ny,nx); sx=base[0]+t[0]*u; sy=base[1]+t[1]*u
    obox(sx+nx*0.02,sy+ny*0.02,z,ang,0.10,w+0.16,h+0.16,FRAME,"wframe")
    gx=sx+nx*0.09; gy=sy+ny*0.09
    obox(gx,gy,z,ang,0.10,w,h,GLASS,"glass")
    # croix/meneaux EN RELIEF (offset 0.07 -> face ~0.19 devant verre ~0.14 : PAS de z-fight, cf lessons §8)
    obox(gx+nx*0.07,gy+ny*0.07,z,ang,0.06,0.05,h,FRAME,"mv"); obox(gx+nx*0.07,gy+ny*0.07,z,ang,0.06,w,0.05,FRAME,"mh")
    if sill: obox(sx+nx*0.05,sy+ny*0.05,z-h/2-0.10,ang,0.26,w+0.22,0.10,STONE,"sill")

def railing(base,nrm,u,zb,w,h=0.52):
    """Garde-corps à BARREAUX (préf user) : rail haut coral + rail bas + barreaux + 2 poteaux."""
    nx,ny=nrm; ang=math.atan2(ny,nx); t=(-ny,nx); off=0.24; W=w+0.22
    cx=base[0]+t[0]*u+nx*off; cy=base[1]+t[1]*u+ny*off
    obox(cx,cy,zb+h,ang,0.06,W+0.05,0.05,TRIM,"toprail"); obox(cx,cy,zb+0.05,ang,0.06,W+0.05,0.05,METAL,"botrail")
    nb=max(4,int(w/0.17))
    for i in range(nb+1):
        uu=-W/2+i*(W/nb); px=base[0]+t[0]*(u+uu)+nx*off; py=base[1]+t[1]*(u+uu)+ny*off
        obox(px,py,zb+h/2,ang,0.05,0.03,h,METAL,"bar")
    for uu in (-W/2,W/2):
        px=base[0]+t[0]*(u+uu)+nx*off; py=base[1]+t[1]*(u+uu)+ny*off
        obox(px,py,zb+h/2,ang,0.07,0.07,h,METAL,"post")

def balcony_veg(base,nrm,u,zb,w):
    nx,ny=nrm; ang=math.atan2(ny,nx); t=(-ny,nx); off=0.24
    cx=base[0]+t[0]*u+nx*off; cy=base[1]+t[1]*u+ny*off
    obox(cx,cy,zb+0.16,ang,0.20,w,0.18,TERRA,"planter")
    for uu in (-w*0.30,0,w*0.30):
        px=base[0]+t[0]*(u+uu)+nx*off; py=base[1]+t[1]*(u+uu)+ny*off
        organic(px,py,zb+0.34,0.20,LEAF if random.random()>.4 else LEAF2,"bveg")
    organic(cx+nx*0.06,cy+ny*0.06,zb+0.20,0.16,LEAF,"drape",amp=0.5)  # retombante drape sur le rail

def build_corner(tag="A", ox=0.0, wall=(0.95,0.82,0.66), trim=(0.96,0.47,0.40),
                 sign=(0.28,0.72,0.68), nfloors=5, dens_alt=(0,2,4), seed=7):
    """Immeuble d'angle chanfreiné. base+veg dans CUR ; renvoie (base_obj, veg_obj).
    dens_alt = indices d'étages portant des jardinières (verdure). ox = offset X (rangée d'assets)."""
    global CUR, WALL, TRIM, SIGN
    CUR=[]; random.seed(seed)
    # matériaux per-tag pour les 3 couleurs variables (sinon écrase les canoniques
    # partagés -> toutes les variantes prennent la couleur de la dernière).
    WALL=mat(f"Corner_{tag}_wall",wall); TRIM=mat(f"Corner_{tag}_trim",trim); SIGN=mat(f"Corner_{tag}_sign",sign)
    def O(fp): return [(x+ox,y) for (x,y) in fp]  # applique offset X
    FP=[(-3,-2.5),(1.2,-2.5),(3,-0.7),(3,2.5),(-3,2.5)]
    ground_H=2.6; fh=1.7; body_H=ground_H+fh*nfloors; ROOF=body_H+0.35
    fbot=[ground_H+fh*i for i in range(nfloors)]
    prism(O(FP),0,body_H,WALL,"body"); prism(O(scale_fp(FP,1.02)),0,0.5,STONE,"plinth")
    for z in fbot: prism(O(scale_fp(FP,1.015)),z-0.06,z+0.06,FRAME,"band")
    prism(O(scale_fp(FP,1.04)),body_H-0.30,body_H,FRAME,"cornice")
    prism(O(scale_fp(FP,0.99)),body_H,ROOF,STONE,"roof"); prism(O(scale_fp(FP,1.0)),ROOF,ROOF+0.40,STONE,"parapet")
    UW=0.80; GW=0.80; CW=1.05  # fenêtres étroites -> pilastres larges (lessons §7)
    # (base_x, normale, point-origine face, offsets tangents). Face droite étroite -> 2 colonnes.
    FACES=[("front",(0.0,-1.0),(-0.9+ox,-2.5),[-1.4,0.0,1.4]),
           ("right",(1.0,0.0),(3.0+ox,0.9),[-0.8,0.8]),
           ("cham",(0.70711,-0.70711),(2.1+ox,-1.6),[0.0])]
    for name,nrm,base,us in FACES:
        for b in fbot:
            zc=b+0.78; ww=UW if name!="cham" else CW
            for u in us:
                window(base,nrm,u,zc,ww,1.1); railing(base,nrm,u,b+0.18,ww)
    for name,nrm,base,us in FACES:
        if name=="cham":
            nx,ny=nrm; ang=math.atan2(ny,nx); t=(-ny,nx)
            obox(base[0]+nx*0.05,base[1]+ny*0.05,1.15,ang,0.16,1.0,2.1,DOOR,"door")
            obox(base[0]+nx*0.14,base[1]+ny*0.14,1.15,ang,0.05,0.06,2.0,METAL,"dhandle")
            obox(base[0]+nx*0.35,base[1]+ny*0.35,2.45,ang,0.55,1.7,0.16,TRIM,"awn")
            obox(base[0]+nx*0.30,base[1]+ny*0.30,2.20,ang,0.10,1.7,0.30,SIGN,"sign")
            obox(base[0]+nx*0.28,base[1]+ny*0.28,0.12,ang,0.5,1.4,0.22,STONE,"step")
            for s in (-0.62,0.62):
                lx=base[0]+t[0]*s+nx*0.16; ly=base[1]+t[1]*s+ny*0.16
                obox(lx,ly,2.05,ang,0.14,0.12,0.16,METAL,"lampbkt"); cyl(lx+nx*0.10,ly+ny*0.10,2.0,0.07,0.16,SIGN,"lamp",8)
        else:
            for u in us: window(base,nrm,u,1.55,GW,1.7,sill=False)
    cyl(-2.93+ox,-2.55,body_H/2,0.07,body_H-0.4,METAL,"dp1",6); cyl(3.02+ox,-0.75,body_H/2,0.07,body_H-0.4,METAL,"dp2",6)
    # toit habité
    obox(-1.4+ox,1.2,ROOF+0.7,0,1.3,1.3,1.4,WALL,"edicule"); obox(-1.4+ox,1.2,ROOF+1.45,0,1.4,1.4,0.1,STONE,"edcap")
    obox(-1.4+ox,0.55,ROOF+0.7,0,0.05,0.6,1.0,DOOR,"eddoor")
    obox(0.7+ox,-0.4,ROOF+0.35,0,0.7,0.7,0.7,METAL,"tank"); obox(0.7+ox,-0.4,ROOF+0.75,0,0.8,0.8,0.08,STONE,"tankcap")
    obox(1.6+ox,1.4,ROOF+0.25,0,0.5,0.5,0.5,STONE,"vent")
    cyl(2.0+ox,1.6,ROOF+1.0,0.03,2.0,METAL,"antenna",5); obox(2.0+ox,1.6,ROOF+1.9,0,0.45,0.03,0.3,METAL,"anttop")
    cyl(-0.4+ox,1.9,ROOF+0.45,0.35,0.06,FRAME,"dish",12,'X'); cyl(-0.4+ox,1.75,ROOF+0.45,0.04,0.35,METAL,"disharm",6)
    base_parts=list(CUR)
    # ---- végétation (objet séparé pour LOD) ----
    for name,nrm,base,us in FACES:
        if name=="cham": continue
        for fi,b in enumerate(fbot):
            if fi not in dens_alt: continue
            for u in us: balcony_veg(base,nrm,u,b+0.18,UW)
    for gx in [-1.9,-1.0,0.2,1.0,1.9]:
        organic(gx+ox,-1.9,ROOF+0.55,random.uniform(0.30,0.44),LEAF if random.random()>.35 else LEAF2,"roofbush",amp=0.46)
    cyl(0.4+ox,1.0,ROOF+0.3,0.14,0.6,DOOR,"trunk",6); organic(0.4+ox,1.0,ROOF+1.0,0.6,LEAF,"tree",amp=0.44)
    cnx,cny=0.70711,-0.70711; ct=(-cny,cnx); cb=(2.1+ox,-1.6)
    for s in (-0.95,0.95):
        px=cb[0]+ct[0]*s+cnx*0.35; py=cb[1]+ct[1]*s+cny*0.35
        cyl(px,py,0.28,0.18,0.5,TERRA,"pot",8); organic(px,py,0.62,0.30,LEAF,"potbush",amp=0.44); organic(px+0.05,py+0.05,0.72,0.12,FLOWER,"bloom",sub=1,amp=0.45)
    for i in range(9):
        z=0.9+i*1.05
        if z>body_H-0.4: break
        organic(-2.98+ox+0.12*math.sin(i*1.2),-2.52,z,0.22,LEAF2 if i%2 else LEAF,"ivy",amp=0.48)
    veg_parts=CUR[len(base_parts):]
    base_obj=join_parts(base_parts,f"Building_Corner_{tag}"); veg_obj=join_parts(veg_parts,f"Building_Corner_{tag}_Veg")
    return base_obj, veg_obj

# export FBX identique à city_builder.py::export_fbx (pivot au sol, -Z/Y, apply_unit_scale).
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

# 5 variantes validées (famille chaude) : tag, ox, wall, trim, sign, nfloors, dens_alt, seed
CORNER_SPECS=[
 ("A",-16,(0.95,0.82,0.66),(0.96,0.47,0.40),(0.28,0.72,0.68),5,(0,2,4),7),
 ("B", -8,(0.96,0.74,0.60),(0.30,0.66,0.62),(0.97,0.80,0.38),6,(0,2,4),11),
 ("C",  0,(0.96,0.87,0.60),(0.96,0.47,0.40),(0.96,0.55,0.45),4,(1,3),3),
 ("D",  8,(0.93,0.72,0.68),(0.90,0.45,0.40),(0.28,0.72,0.68),5,(0,1,2,3,4),5),
 ("E", 16,(0.90,0.80,0.62),(0.28,0.72,0.68),(0.97,0.60,0.66),6,(0,2,4),9),
]

if __name__ == "__main__":
    clean(); palette(); lighting()
    for s in CORNER_SPECS: build_corner(*s)
    aim_cam((21,-24,11),(0.2,0,6.2))
