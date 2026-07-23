# Pedestrian builder — Blender bpy, via Blender MCP (execute_blender_code).
#
# Variantes de PIETONS pour le proto (Assets/Scripts/Gameplay/Pedestrian.cs).
# Style RAYMAN : tete-oeuf FLOTTANTE + mains FLOTTANTES, corps-oeuf, LISSE
# (smooth + Subsurf 1). Palette chaude. Front = -Y. Z-up.
#
# PRINCIPE (retour user) : NE PAS empiler des primitives. On DUPLIQUE l'anatomie
# SCULPTEE de la base (character.blend : tete/nez/oreilles/yeux/sourcils/mains/
# moustache/corps deja modeles) et on MODELE chaque feature de variante (coiffes,
# chapeaux, lunettes, barbes, robes, sacs) avec de vrais outils bmesh (extrude,
# solidify, bevel, spin, anneaux). L'anatomie garde ainsi la meme qualite que le
# 1er perso ; seules les features nouvelles sont modelees.
#
# DROP-IN Pedestrian.cs : chaque FBX garde PedRoot/PedHead/PedHandL/PedHandR et
# (si moustache) PedStacheL/PedStacheR. Export : 1 variante par FBX, noms canoniques.

import bpy, bmesh, math
from mathutils import Vector

CHAR=r"C:/Users/P0ulpy/Documents/GitHub/gmtk/Assets/ModelBlender/character.blend"
TPL_NAMES=("PedRoot","PedHead","PedNose","PedEarL","PedEarR","PedEyeL","PedEyeR",
           "PedBrowL","PedBrowR","PedHandL","PedHandR","PedThumbL","PedThumbR",
           "PedBody","PedBtn0","PedBtn1","PedBtn2","PedStacheL","PedStacheR","PedHair")

# ---------------------------------------------------------------- mats / scene
def mat(name,rgb,rough=0.85):
    m=bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes=True; b=m.node_tree.nodes.get("Principled BSDF")
    b.inputs["Base Color"].default_value=(*rgb,1); b.inputs["Roughness"].default_value=rough
    return name

def palette():
    mat("PedSkinL",(0.98,0.75,0.58)); mat("PedSkinM",(0.80,0.56,0.40)); mat("PedSkinD",(0.55,0.38,0.27))
    mat("PedNoseL",(0.90,0.62,0.48)); mat("PedNoseM",(0.70,0.46,0.33)); mat("PedNoseD",(0.47,0.31,0.22))
    mat("PedBrown",(0.32,0.18,0.09)); mat("HairBlack",(0.10,0.09,0.10))
    mat("HairAuburn",(0.52,0.24,0.13)); mat("HairBlonde",(0.86,0.68,0.36)); mat("HairGrey",(0.74,0.72,0.68))
    mat("HairBrown2",(0.50,0.34,0.20))
    mat("PedShirt",(0.32,0.58,0.3)); mat("PedPants",(0.22,0.24,0.32))
    mat("T_Red",(0.80,0.28,0.26)); mat("T_Yellow",(0.95,0.78,0.30)); mat("T_Teal",(0.28,0.60,0.58))
    mat("T_Orange",(0.90,0.52,0.26)); mat("T_Coral",(0.93,0.46,0.42)); mat("T_Sky",(0.52,0.72,0.86))
    mat("T_Cream",(0.94,0.90,0.80)); mat("T_Grey",(0.55,0.56,0.58)); mat("T_Plum",(0.58,0.34,0.50))
    mat("T_White",(0.95,0.94,0.92)); mat("T_Suit",(0.24,0.26,0.34)); mat("T_Brown",(0.52,0.37,0.24))
    mat("Lens",(0.60,0.80,0.90),0.15); mat("Metal",(0.30,0.30,0.33),0.4)

def lighting():
    L=bpy.data.objects.get("Light")
    if L and L.type=='LIGHT':
        L.data.type='SUN'; L.data.energy=3.0; L.data.color=(1.0,0.96,0.88)
        L.rotation_euler=(math.radians(55),math.radians(12),math.radians(40))
    sc=bpy.context.scene; sc.render.engine='BLENDER_EEVEE'
    try: sc.view_settings.view_transform='Standard'
    except Exception: pass
    if sc.world is None:
        sc.world=bpy.data.worlds.new("World")
    sc.world.use_nodes=True
    w=sc.world.node_tree.nodes.get("Background")
    if w: w.inputs[0].default_value=(0.90,0.87,0.80,1); w.inputs[1].default_value=0.55

# ---------------------------------------------------------------- template
def load_template():
    """Append la base sculptee une fois, planquee a l'ecart (y=-50)."""
    if bpy.data.objects.get("TPL_PedRoot"):
        return
    with bpy.data.libraries.load(CHAR, link=False) as (src, dst):
        dst.objects=[n for n in src.objects if n in TPL_NAMES]
    for o in dst.objects:
        if o is None: continue
        try: bpy.context.collection.objects.link(o)
        except Exception: pass
    for n in TPL_NAMES:
        o=bpy.data.objects.get(n)
        if o: o.name="TPL_"+n
    root=bpy.data.objects["TPL_PedRoot"]; root.location=(0,-50,0)

def dup_base(ox):
    """Copie manuelle de l'anatomie sculptee (bpy.ops.selection indispo en MCP).
    Renvoie (root, {canonical:obj}) avec meshes independants (recolor safe)."""
    tpl=[o for o in bpy.data.objects if o.name.startswith("TPL_Ped")]
    mapping={}
    for o in tpl:
        nb=o.copy()
        if o.data: nb.data=o.data.copy()
        bpy.context.collection.objects.link(nb); mapping[o]=nb
    for o,nb in mapping.items():                      # reparente vers les copies
        if o.parent in mapping:
            nb.parent=mapping[o.parent]
            nb.matrix_parent_inverse=o.matrix_parent_inverse.copy()
    parts={}
    for o,nb in mapping.items():
        canon=o.name.replace("TPL_","")
        parts[canon]=nb; nb.name=canon            # noms canoniques (drop-in Pedestrian.cs)
    root=parts["PedRoot"]; root.location=(ox,0,0)
    bpy.context.view_layer.update()
    return root, parts

# ---------------------------------------------------------------- geo helpers
def _finish(bm,name,parent,matname,sub=1,solid=0.0,smooth=True,keepworld=True):
    me=bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    ob=bpy.data.objects.new(name,me); bpy.context.collection.objects.link(ob)
    for p in me.polygons: p.use_smooth=smooth
    if solid:
        sm=ob.modifiers.new("sol",'SOLIDIFY'); sm.thickness=solid; sm.offset=0
    if sub:
        s=ob.modifiers.new("sub",'SUBSURF'); s.levels=sub; s.render_levels=sub
    ob.data.materials.append(bpy.data.materials[matname])
    if parent:
        ob.parent=parent; bpy.context.view_layer.update()
        if keepworld: ob.matrix_parent_inverse=parent.matrix_world.inverted()
    return ob

def uvbm(cx,cy,cz,sx,sy,sz,u=20,v=14):
    bm=bmesh.new(); bmesh.ops.create_uvsphere(bm,u_segments=u,v_segments=v,radius=0.5)
    for vv in bm.verts:
        vv.co.x=vv.co.x*sx+cx; vv.co.y=vv.co.y*sy+cy; vv.co.z=vv.co.z*sz+cz
    return bm

def torus_bm(cx,cy,cz,R,r,maj=18,mnr=8):
    # anneau dans le plan XZ (face vers -Y), pour lentilles de lunettes
    bm=bmesh.new(); loops=[]
    for i in range(maj):
        A=2*math.pi*i/maj; ca,sa=math.cos(A),math.sin(A); loop=[]
        for j in range(mnr):
            B=2*math.pi*j/mnr
            rr=R+r*math.cos(B)
            loop.append(bm.verts.new((cx+rr*ca, cy+r*math.sin(B), cz+rr*sa)))
        loops.append(loop)
    for i in range(maj):
        L0=loops[i]; L1=loops[(i+1)%maj]
        for j in range(mnr):
            k=(j+1)%mnr; bm.faces.new((L0[j],L0[k],L1[k],L1[j]))
    bmesh.ops.recalc_face_normals(bm,faces=bm.faces)
    return bm

def cyl_bm(p0,p1,r,seg=10):
    bm=bmesh.new()
    d=Vector(p1)-Vector(p0); L=d.length;
    if L<1e-5: L=1e-5
    bmesh.ops.create_cone(bm,cap_ends=True,segments=seg,radius1=r,radius2=r,depth=L)
    z=d.normalized(); up=Vector((0,0,1))
    q=up.rotation_difference(z)
    mid=(Vector(p0)+Vector(p1))/2
    for v in bm.verts: v.co=q@v.co+mid
    return bm

# ================================================================ HAIR
def model_hair(root,head,parts,style,col):
    HW=head.matrix_world.translation
    def rm(n):
        o=parts.get(n)
        if o: bpy.data.objects.remove(o,do_unlink=True); parts.pop(n,None)
    if style=="bald_side":
        parts["PedHair"].data.materials.clear(); parts["PedHair"].data.materials.append(bpy.data.materials[col]); return
    rm("PedHair")
    if style=="none":
        return
    if style=="fringe":
        # cheveux qui depassent SOUS un chapeau : anneau bas (haut coupe), visage ouvert
        bm=uvbm(HW.x,HW.y+0.02,HW.z+0.02, 0.88,0.86,0.62, u=22,v=14)
        kill=[]
        for v in bm.verts:
            lz=v.co.z-HW.z; ly=v.co.y-HW.y
            if lz>0.12: kill.append(v)                       # haut = cache par chapeau
            elif ly<-0.14 and -0.02<lz<0.12: kill.append(v)  # ouvre le front
        bmesh.ops.delete(bm,geom=list(set(kill)),context='VERTS'); bmesh.ops.recalc_face_normals(bm,faces=bm.faces)
        _finish(bm,"PedHair",head,col,sub=1,solid=0.045); return
    # casque de base
    top = 1.14 if style in ("long","bob","bun","ponytail","short") else 1.10
    bm=uvbm(HW.x,HW.y+0.05,HW.z+0.07, 0.90*0.92,0.90*0.92,0.78, u=24,v=16)
    kill=[]
    for v in bm.verts:
        lz=v.co.z-HW.z; ly=v.co.y-HW.y
        if lz < -0.05 and not (style in ("long","bob") and ly>0.02):
            kill.append(v)                                   # coupe le bas (drape garde a l'arriere si long)
        elif ly < -0.14 and lz<0.16 and lz>-0.34:
            kill.append(v)                                   # ouvre le visage
    bmesh.ops.delete(bm,geom=list(set(kill)),context='VERTS')
    bmesh.ops.recalc_face_normals(bm,faces=bm.faces)
    if style in ("long","bob","ponytail"):
        drop=0.42 if style in ("long","bob") else 0.16
        low=[e for e in bm.edges if e.is_boundary
             and (e.verts[0].co.z+e.verts[1].co.z)/2 < HW.z+0.0
             and (e.verts[0].co.y+e.verts[1].co.y)/2 > HW.y-0.10]
        ex=bmesh.ops.extrude_edge_only(bm,edges=low)
        for v in [g for g in ex['geom'] if isinstance(g,bmesh.types.BMVert)]:
            dx=v.co.x-HW.x; v.co.z-=drop; v.co.x=HW.x+dx*0.85; v.co.y+=0.02
        low2=[e for e in bm.edges if e.is_boundary and (e.verts[0].co.z+e.verts[1].co.z)/2 < HW.z-0.22]
        ex2=bmesh.ops.extrude_edge_only(bm,edges=low2)
        for v in [g for g in ex2['geom'] if isinstance(g,bmesh.types.BMVert)]:
            dx=v.co.x-HW.x; v.co.z-=0.20; v.co.x=HW.x+dx*0.72
    hair=_finish(bm,"PedHair",head,col,sub=1,solid=0.05)
    if style=="bun":
        b=uvbm(HW.x,HW.y+0.30,HW.z+0.30, 0.30,0.30,0.30); _finish(b,"PedBun",head,col,sub=1)
    if style=="ponytail":
        t=cyl_bm((HW.x,HW.y+0.34,HW.z+0.10),(HW.x,HW.y+0.42,HW.z-0.55),0.11,seg=10)
        _finish(t,"PedTail",head,col,sub=1)

# ================================================================ FACIAL HAIR
def model_facial(root,head,parts,style,col):
    HW=head.matrix_world.translation
    def rm(n):
        o=parts.get(n)
        if o: bpy.data.objects.remove(o,do_unlink=True); parts.pop(n,None)
    if style=="stache":
        for n in ("PedStacheL","PedStacheR"):
            if parts.get(n):
                parts[n].data.materials.clear(); parts[n].data.materials.append(bpy.data.materials[col])
        return
    rm("PedStacheL"); rm("PedStacheR")           # rase pour les autres
    if style in ("beard","goatee","stubble"):
        # barbe modelee : coque qui epouse la machoire (basse, resserree)
        w=0.36 if style=="beard" else (0.18 if style=="goatee" else 0.34)
        bm=uvbm(HW.x,HW.y-0.14,HW.z-0.19, w,0.34,0.30, u=20,v=14)
        kill=[]
        for v in bm.verts:
            lz=v.co.z-HW.z; ly=v.co.y-HW.y
            if lz>-0.02 or ly>-0.04: kill.append(v)          # garde bas + devant (menton/joues basses)
        bmesh.ops.delete(bm,geom=list(set(kill)),context='VERTS')
        bmesh.ops.recalc_face_normals(bm,faces=bm.faces)
        solid=0.02 if style=="stubble" else 0.05
        _finish(bm,"PedBeard",head,col,sub=1,solid=solid)

# ================================================================ GLASSES
def model_glasses(root,head,parts,col="Metal"):
    HW=head.matrix_world.translation
    for sgn,nm in ((-1,"PedGlassL"),(1,"PedGlassR")):
        rim=torus_bm(HW.x+sgn*0.115, HW.y-0.345, HW.z+0.055, 0.10,0.022, maj=16,mnr=6)
        _finish(rim,nm,head,col,sub=0)
        lens=uvbm(HW.x+sgn*0.115, HW.y-0.345, HW.z+0.055, 0.16,0.04,0.16, u=12,v=8)
        _finish(lens,nm+"Lens",head,"Lens",sub=0)
    br=cyl_bm((HW.x-0.055,HW.y-0.345,HW.z+0.075),(HW.x+0.055,HW.y-0.345,HW.z+0.075),0.016,seg=6)
    _finish(br,"PedBridge",head,col,sub=0)
    for sgn in (-1,1):                                   # branches vers les oreilles
        t=cyl_bm((HW.x+sgn*0.20,HW.y-0.33,HW.z+0.06),(HW.x+sgn*0.35,HW.y+0.02,HW.z+0.07),0.014,seg=6)
        _finish(t,"PedTemple%s"%('L'if sgn<0 else'R'),head,col,sub=0)

# ================================================================ HATS
def model_hat(root,head,parts,hat,col):
    HW=head.matrix_world.translation
    if hat=="cap":
        # dome qui EPOUSE le haut du crane, assis au front
        dome=uvbm(HW.x,HW.y+0.02,HW.z+0.07, 0.82,0.80,0.62, u=24,v=14)
        kill=[v for v in dome.verts if v.co.z<HW.z+0.07]
        bmesh.ops.delete(dome,geom=kill,context='VERTS'); bmesh.ops.recalc_face_normals(dome,faces=dome.faces)
        _finish(dome,"PedHat",head,col,sub=1,solid=0.04)
        # visiere = LEVRE courbe accrochee au bord avant du dome (3 rangs, descend)
        bm=bmesh.new()
        rows=[[(-0.27,-0.34,0.075),(0.27,-0.34,0.075)],
              [(-0.29,-0.52,0.025),(0.29,-0.52,0.025)],
              [(-0.25,-0.64,-0.02),(0.25,-0.64,-0.02)]]
        V=[[bm.verts.new((HW.x+a,HW.y+b,HW.z+c)) for (a,b,c) in row] for row in rows]
        for r in range(len(V)-1):
            bm.faces.new((V[r][0],V[r][1],V[r+1][1],V[r+1][0]))
        bmesh.ops.recalc_face_normals(bm,faces=bm.faces)
        _finish(bm,"PedVisor",head,col,sub=1,solid=0.03)
    elif hat=="beanie":
        # bonnet assis au FRONT (laisse voir yeux/lunettes) + revers roule
        dome=uvbm(HW.x,HW.y+0.02,HW.z+0.10, 0.84,0.82,0.58, u=24,v=14)
        kill=[v for v in dome.verts if v.co.z<HW.z+0.09]
        bmesh.ops.delete(dome,geom=kill,context='VERTS'); bmesh.ops.recalc_face_normals(dome,faces=dome.faces)
        _finish(dome,"PedHat",head,col,sub=1,solid=0.05)
        brim=torus_bm(HW.x,HW.y+0.02,HW.z+0.08, 0.40,0.075, maj=24,mnr=8)
        _finish(brim,"PedBrim",head,col,sub=1)
    elif hat=="beret":
        bm=uvbm(HW.x,HW.y+0.06,HW.z+0.20, 0.86,0.84,0.30, u=20,v=10)
        kill=[v for v in bm.verts if v.co.z<HW.z+0.14]
        bmesh.ops.delete(bm,geom=kill,context='VERTS'); bmesh.ops.recalc_face_normals(bm,faces=bm.faces)
        _finish(bm,"PedHat",head,col,sub=1,solid=0.04)
        nub=uvbm(HW.x,HW.y+0.06,HW.z+0.35,0.06,0.06,0.06); _finish(nub,"PedBeretTip",head,col,sub=1)
    elif hat=="sun":
        # se POSE sur le dessus de la tete (crane top ~ +0.31) : bord au sommet,
        # calotte AU-DESSUS -> ne rentre pas dans la tete
        crown=uvbm(HW.x,HW.y,HW.z+0.42, 0.58,0.58,0.40, u=20,v=12)
        kill=[v for v in crown.verts if v.co.z<HW.z+0.32]
        bmesh.ops.delete(crown,geom=kill,context='VERTS'); bmesh.ops.recalc_face_normals(crown,faces=crown.faces)
        _finish(crown,"PedHat",head,col,sub=1,solid=0.04)
        # bord large legerement incline, pose au niveau du sommet du crane
        bm=bmesh.new(); N=28; inner=[]; outer=[]
        for i in range(N):
            a=2*math.pi*i/N; ca,sa=math.cos(a),math.sin(a)
            inner.append(bm.verts.new((HW.x+0.33*ca,HW.y+0.33*sa,HW.z+0.34)))
            outer.append(bm.verts.new((HW.x+0.74*ca,HW.y+0.74*sa,HW.z+0.28)))
        for i in range(N):
            j=(i+1)%N; bm.faces.new((inner[i],inner[j],outer[j],outer[i]))
        bmesh.ops.recalc_face_normals(bm,faces=bm.faces)
        _finish(bm,"PedBrim",head,col,sub=1,solid=0.03)

# ================================================================ GARMENTS
def _skirt(root,HW,col,z0=0.30,flare=0.52,zbot=-0.06,pleat=0.05):
    bm=bmesh.new(); N=26; rings=[]
    prof=[(z0,0.30),(0.16,flare*0.82),(0.02,flare),(zbot,flare*0.96)]
    for (zz,rr) in prof:
        ring=[]
        for i in range(N):
            a=2*math.pi*i/N; r=rr*(1.0+pleat*math.sin(a*9))
            ring.append(bm.verts.new((HW.x+r*math.cos(a),HW.y+r*math.sin(a),zz)))
        rings.append(ring)
    for k in range(len(rings)-1):
        for i in range(N):
            j=(i+1)%N; bm.faces.new((rings[k][i],rings[k][j],rings[k+1][j],rings[k+1][i]))
    bmesh.ops.recalc_face_normals(bm,faces=bm.faces)
    return _finish(bm,"PedSkirt",root,col,sub=1,solid=0.04)

def model_garment(root,parts,garment,shirt,pants):
    body=parts["PedBody"]; HW=body.matrix_world.translation
    body.data.materials.clear()
    if garment in ("dress",):
        body.data.materials.append(bpy.data.materials[shirt])
        _skirt(root,HW,shirt,z0=0.30,flare=0.52)
    elif garment=="skirt":
        body.data.materials.append(bpy.data.materials[shirt])
        _skirt(root,HW,pants,z0=0.22,flare=0.44,zbot=0.02)
    else:                                   # pants : haut chemise / bas pantalon (comme la base)
        body.data.materials.append(bpy.data.materials[shirt])
        body.data.materials.append(bpy.data.materials[pants])
        for p in body.data.polygons:
            p.material_index = 0 if p.center.z>0.28 else 1

def model_apron(root,parts,col):
    HW=parts["PedBody"].matrix_world.translation
    # panneau PLAT devant + bretelles (pas un gros bulbe)
    bm=uvbm(HW.x,HW.y-0.37,HW.z+0.34, 0.38,0.05,0.52, u=14,v=10)
    kill=[v for v in bm.verts if (v.co.y-(HW.y-0.37))>0.01]
    bmesh.ops.delete(bm,geom=kill,context='VERTS'); bmesh.ops.recalc_face_normals(bm,faces=bm.faces)
    _finish(bm,"PedApron",root,col,sub=1,solid=0.02)
    for sgn in (-1,1):
        t=cyl_bm((HW.x+sgn*0.16,HW.y-0.34,HW.z+0.58),(HW.x+sgn*0.05,HW.y-0.34,HW.z+0.30),0.02,seg=6)
        _finish(t,"PedStrap%s"%('L'if sgn<0 else'R'),root,col,sub=1)

# ================================================================ ACCESSORIES
def model_acc(root,parts,acc,col):
    HW=parts["PedRoot"].matrix_world.translation
    if acc=="handbag":
        bm=bmesh.new(); bmesh.ops.create_cube(bm,size=1)
        for v in bm.verts: v.co.x*=0.30; v.co.y*=0.20; v.co.z*=0.26
        bmesh.ops.bevel(bm,geom=[e for e in bm.edges],offset=0.05,segments=2,affect='EDGES')
        for v in bm.verts: v.co+=Vector((HW.x+0.60,HW.y+0.06,HW.z+0.30))
        _finish(bm,"PedBag",root,col,sub=1)
        arc=torus_bm(HW.x+0.60,HW.y+0.06,HW.z+0.46,0.10,0.02,maj=14,mnr=5)  # anse
        # garde demi-cercle sup
        _finish(arc,"PedBagStrap",root,col,sub=1)
    elif acc=="backpack":
        bm=bmesh.new(); bmesh.ops.create_cube(bm,size=1)
        for v in bm.verts: v.co.x*=0.36; v.co.y*=0.22; v.co.z*=0.46
        bmesh.ops.bevel(bm,geom=[e for e in bm.edges],offset=0.05,segments=2,affect='EDGES')
        for v in bm.verts: v.co+=Vector((HW.x,HW.y+0.34,HW.z+0.42))
        _finish(bm,"PedPack",root,col,sub=1)
        for sgn in (-1,1):                              # bretelles sur les epaules
            t=cyl_bm((HW.x+sgn*0.20,HW.y+0.22,HW.z+0.60),(HW.x+sgn*0.20,HW.y-0.28,HW.z+0.42),0.03,seg=6)
            _finish(t,"PedPackStrap%s"%('L'if sgn<0 else'R'),root,"T_Suit",sub=1)
    elif acc=="briefcase":
        bm=bmesh.new(); bmesh.ops.create_cube(bm,size=1)
        for v in bm.verts: v.co.x*=0.06; v.co.y*=0.22; v.co.z*=0.18
        bmesh.ops.bevel(bm,geom=[e for e in bm.edges],offset=0.03,segments=2,affect='EDGES')
        for v in bm.verts: v.co+=Vector((HW.x+0.60,HW.y+0.02,HW.z+0.22))
        _finish(bm,"PedCase",root,"T_Suit",sub=1)
        h=torus_bm(HW.x+0.60,HW.y+0.02,HW.z+0.34,0.07,0.015,maj=12,mnr=5)
        _finish(h,"PedCaseH",root,"Metal",sub=1)

# ================================================================ recolor anatomy
def recolor(parts,skin,nose):
    for n in ("PedHead","PedEarL","PedEarR","PedHandL","PedHandR","PedThumbL","PedThumbR"):
        o=parts.get(n)
        if o: o.data.materials.clear(); o.data.materials.append(bpy.data.materials[skin])
    o=parts.get("PedNose")
    if o: o.data.materials.clear(); o.data.materials.append(bpy.data.materials[nose])
    for n in ("PedBrowL","PedBrowR"):
        o=parts.get(n)
        if o: o.data.materials.clear(); o.data.materials.append(bpy.data.materials["PedBrown"])

def set_brow(parts,col):
    for n in ("PedBrowL","PedBrowR"):
        o=parts.get(n)
        if o: o.data.materials.clear(); o.data.materials.append(bpy.data.materials[col])

# ================================================================ RIG leger (os)
# Squelette Root/Body/Head/HandL/HandR (+ StacheL/R si moustache). Bone-parenting
# (PAS de skinning) : chaque piece rigide accrochee a un os -> l'Animator Unity
# bouge les os (facon artiste). Head = pivot au COU (nod naturel). Les cosmetiques
# de tete (cheveux/chapeau/lunettes/barbe) restent enfants de PedHead -> suivent.
def _bonemap(name):
    if name=="PedHead": return "Head"
    if name=="PedHandL": return "HandL"
    if name=="PedHandR": return "HandR"
    return "Body"    # corps, boutons, jupe, tablier, cravate, sacs...

def rig_ped(root,parts):
    arm=bpy.data.armatures.new(root.name+"_Arm")
    ao=bpy.data.objects.new(root.name+"_Arm",arm); bpy.context.collection.objects.link(ao)
    ao.parent=root; ao.matrix_parent_inverse=root.matrix_world.inverted()  # armature = repere du root
    vl=bpy.context.view_layer; vl.objects.active=ao; ao.select_set(True); vl.update()
    with bpy.context.temp_override(active_object=ao,object=ao,selected_objects=[ao],selected_editable_objects=[ao]):
        bpy.ops.object.mode_set(mode='EDIT')
        eb=arm.edit_bones
        b={}
        b["Root"]=eb.new("Root");  b["Root"].head=(0,0,0);       b["Root"].tail=(0,0,0.12)
        b["Body"]=eb.new("Body");  b["Body"].head=(0,0,0.06);    b["Body"].tail=(0,0,0.60); b["Body"].parent=b["Root"]
        b["Head"]=eb.new("Head");  b["Head"].head=(0,0,0.64);    b["Head"].tail=(0,0,1.22); b["Head"].parent=b["Body"]
        b["HandL"]=eb.new("HandL");b["HandL"].head=(-0.52,-0.05,0.52); b["HandL"].tail=(-0.52,-0.05,0.66); b["HandL"].parent=b["Body"]
        b["HandR"]=eb.new("HandR");b["HandR"].head=(0.52,-0.05,0.52);  b["HandR"].tail=(0.52,-0.05,0.66);  b["HandR"].parent=b["Body"]
        bpy.ops.object.mode_set(mode='OBJECT')
    # bone-parent les pieces TOP-LEVEL (enfants directs du root) a leur os.
    # PedStacheL/R restent enfants de PedHead -> suivent l'os Head (pas d'os
    # dedie : le matrix_world d'un objet bone-parente est peu fiable, ca cassait
    # leur position ; le jiggle MustacheFrenzy reste code-driven sur le mesh).
    for o in list(root.children):
        if o is ao: continue
        bn=_bonemap(o.name.split('.')[0])
        mw=o.matrix_world.copy()
        o.parent=ao; o.parent_type='BONE'; o.parent_bone=bn
        vl.update(); o.matrix_world=mw
    return ao

# ================================================================ build one ped
def build_ped(tag,spec,ox=0.0):
    root,parts=dup_base(ox)
    recolor(parts, spec.get("skin","PedSkinL"), spec.get("nose","PedNoseL"))
    set_brow(parts, spec.get("haircol","PedBrown"))
    model_garment(root,parts, spec.get("garment","pants"),
                  spec.get("shirt","PedShirt"), spec.get("pants","PedPants"))
    # boutons : garde pour 'buttons', sinon supprime ; tie/zip ajoutes
    det=spec.get("torso","buttons")
    if det!="buttons":
        for n in ("PedBtn0","PedBtn1","PedBtn2"):
            o=parts.get(n)
            if o: bpy.data.objects.remove(o,do_unlink=True)
    HW=root.matrix_world.translation
    if det=="tie":
        t=cyl_bm((HW.x,HW.y-0.35,HW.z+0.58),(HW.x,HW.y-0.34,HW.z+0.30),0.05,seg=6)
        for v in t.verts: v.co.x=HW.x+(v.co.x-HW.x)*1.0
        _finish(t,"PedTie",root,spec.get("tiecol","T_Red"),sub=1)
    if spec.get("apron"): model_apron(root,parts,spec.get("aproncol","T_Cream"))
    model_acc(root,parts, spec.get("acc","none"), spec.get("acccol","T_Brown"))
    model_facial(root,parts["PedHead"],parts, spec.get("face","none"), spec.get("haircol","PedBrown"))
    hat=spec.get("hat","none")
    hair=spec.get("hair","short")
    if hat in ("cap","beanie","beret"):
        hair="none"                                     # le chapeau couvre la tete
    model_hair(root,parts["PedHead"],parts, hair, spec.get("haircol","PedBrown"))
    if spec.get("glasses"): model_glasses(root,parts["PedHead"],parts)
    if hat!="none": model_hat(root,parts["PedHead"],parts, hat, spec.get("hatcol","T_Red"))
    if spec.get("fat",1.0)!=1.0:
        b=parts.get("PedBody")
        if b: b.scale=(spec["fat"],spec["fat"],1.0)
    if spec.get("scale",1.0)!=1.0:
        root.scale=(spec["scale"],)*3
    root.name="PedRoot_%s"%tag
    if spec.get("rig",True): rig_ped(root,parts)
    return root

# ================================================================ cast
SPECS={
 "Oldman":  dict(skin="PedSkinL",nose="PedNoseL",hair="bald_side",haircol="HairGrey",face="stache",
                 shirt="PedShirt",pants="PedPants",torso="buttons"),
 "Woman":   dict(skin="PedSkinL",nose="PedNoseL",hair="long",haircol="HairAuburn",face="none",
                 shirt="T_Coral",garment="dress",torso="none",acc="handbag",acccol="T_Plum"),
 "Kid":     dict(skin="PedSkinL",nose="PedNoseL",hair="short",haircol="PedBrown",face="none",scale=0.82,
                 shirt="T_Yellow",pants="T_Sky",hat="cap",hatcol="T_Red",torso="zip",acc="backpack",acccol="T_Teal"),
 "Business":dict(skin="PedSkinM",nose="PedNoseM",hair="short",haircol="HairBlack",face="stubble",glasses=True,
                 shirt="T_White",pants="T_Suit",torso="tie",tiecol="T_Red",acc="briefcase"),
 "Hipster": dict(skin="PedSkinL",nose="PedNoseL",hair="short",haircol="HairBrown2",face="beard",glasses=True,
                 hat="beanie",hatcol="T_Orange",shirt="T_Teal",pants="PedPants",torso="buttons"),
 "Granny":  dict(skin="PedSkinL",nose="PedNoseL",hair="bun",haircol="HairGrey",face="none",glasses=True,
                 shirt="T_Plum",garment="skirt",pants="T_Cream",apron=True,aproncol="T_Cream",
                 torso="none",acc="handbag",acccol="T_Brown"),
 "Capguy":  dict(skin="PedSkinD",nose="PedNoseD",hair="short",haircol="HairBlack",face="none",
                 hat="cap",hatcol="T_Sky",shirt="T_Grey",pants="PedPants",torso="zip"),
 "Sunhat":  dict(skin="PedSkinL",nose="PedNoseL",hair="ponytail",haircol="HairBlonde",face="none",
                 hat="sun",hatcol="T_Cream",shirt="T_Coral",garment="dress",torso="none",acc="handbag",acccol="T_Yellow"),
}

import os
def _select_tree(root):
    for o in bpy.data.objects: o.select_set(False)
    stack=[root]; sel=[]
    while stack:
        o=stack.pop(); sel.append(o); o.select_set(True); stack.extend(list(o.children))
    bpy.context.view_layer.objects.active=root
    return sel

def export_one(tag,outdir):
    for o in list(bpy.data.objects):
        if o.name.startswith("TPL_") or o.type in ('CAMERA','LIGHT'): continue
        bpy.data.objects.remove(o,do_unlink=True)
    palette(); load_template()
    root=build_ped(tag,SPECS[tag],ox=0.0)
    sel=_select_tree(root)
    fp=os.path.join(outdir,"Pedestrian_%s.fbx"%tag).replace("\\","/")
    with bpy.context.temp_override(selected_objects=sel, active_object=root, object=root,
                                   selected_editable_objects=sel):
        bpy.ops.export_scene.fbx(filepath=fp, use_selection=True,
            object_types={'MESH','ARMATURE','EMPTY'}, use_mesh_modifiers=True,
            mesh_smooth_type='FACE', add_leaf_bones=False, use_armature_deform_only=False,
            bake_anim=False, apply_scale_options='FBX_SCALE_NONE', use_custom_props=False,
            axis_forward='-Z', axis_up='Y', bake_space_transform=False)
    return fp

def export_all(outdir):
    load_template()
    return [export_one(t,outdir) for t in SPECS]

def build_cast(step=2.2):
    # nettoie tout sauf cam/lumiere/template
    for o in list(bpy.data.objects):
        if o.type in ('CAMERA','LIGHT'): continue
        if o.name.startswith("TPL_"): continue
        bpy.data.objects.remove(o,do_unlink=True)
    palette(); lighting(); load_template()
    order=list(SPECS.keys()); roots=[]
    for i,tag in enumerate(order):
        roots.append(build_ped(tag,SPECS[tag],ox=i*step))
    return roots,order
