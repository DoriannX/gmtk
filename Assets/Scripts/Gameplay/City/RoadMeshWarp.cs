using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;

namespace Gameplay.City
{
    // DEFORMATION d'une tuile de route le long d'une spline : on prend le mesh de la tuile
    // droite (13 x 8 m), on le repete N fois bout a bout et on WARPE chaque sommet dans le
    // repere local de la spline -> la route se COURBE au lieu d'etre facettee tous les 8 m.
    //
    // Trois points sensibles, tous traites ici :
    //  - le PIVOT des FBX n'est pas sur la geo (les tuiles sont a Y +2.14 m) -> Prepare()
    //    recentre depuis mesh.bounds, jamais depuis le pivot, et colle la surface roulable
    //    (bounds.max.y) sur y = 0 pour que tuiles et croisements soient d'affleurement ;
    //  - l'abscisse curviligne : le t d'une spline n'est PAS proportionnel a la distance,
    //    d'ou ConvertIndexUnit(Distance -> Normalized), sinon les tuiles se tassent aux
    //    extremites des virages ;
    //  - le repere : propage par TRANSPORT PARALLELE plutot que par le up de la spline ->
    //    pas de vrille entre knots, pas de flip a 180 deg quand la tangente passe pres de
    //    la verticale (rampes).
    // Pur C#, zero dependance UnityEditor -> marche aussi en build.
    public static class RoadMeshWarp
    {
        const float StationEvery = 1.0f;   // espacement cible des reperes le long de la spline (m)
        const int StationsMin = 8;
        const int StationsMax = 1024;
        const float MinLength = 0.05f;     // spline plus courte -> segment ignore
        const float SoffitBias = 0.01f;    // recul du ruban du dessous, anti z-fighting

        // Tuile source lue une fois puis recentree : x = largeur (centree), y <= 0 (epaisseur
        // SOUS la surface), z = longueur dans [0, length].
        public struct Tile
        {
            public Vector3[] verts;
            public Vector3[] normals;
            public Vector4[] tangents;
            public Vector2[] uv;
            public int[] tris;
            public float length;   // taille sur Z apres recentrage
            public float width;    // taille sur X
            public float bottom;   // y du point le plus bas (negatif) -> hauteur du dessous
            public Vector2 bottomUV; // UV de ce point : le ruban du dessous reprend son texel
            public bool valid;
        }

        const float FlatCos = 0.999f;      // au-dela d'un poil d'inclinaison, c'est un chanfrein
        const float AreaShare = 0.05f;     // part de l'emprise au sol qu'une vraie surface occupe
        const float HeightStep = 0.01f;    // regroupement des hauteurs (1 cm)

        // Hauteur de la CHAUSSEE d'un asset : la surface HORIZONTALE la plus BASSE occupant une
        // part notable de l'emprise au sol. Mesure sur le kit SM_* : la chaussee est bombee sur
        // 4 cm en 3 bandes de ~10 % de l'emprise chacune, le trottoir fait 60 %, le marquage
        // axial 0.5 %, et des CHANFREINS inclines descendent sous la chaussee. D'ou les deux
        // filtres : horizontal (ecarte les chanfreins) + aire (ecarte les marquages).
        // Se caler sur bounds.max.y a la place enterre la route, et c'est incoherent d'un asset
        // a l'autre : max.y = haut de bordure sur les tuiles, ilot central sur le rond-point.
        public static bool RoadwayHeight(Mesh mesh, Matrix4x4 toRoot, out float y)
        {
            y = 0f;
            if (mesh == null || !mesh.isReadable) return false;
            var src = mesh.vertices;
            var verts = new Vector3[src.Length];
            for (int i = 0; i < src.Length; i++) verts[i] = toRoot.MultiplyPoint3x4(src[i]);
            return RoadwayHeight(verts, mesh.triangles, out y);
        }

        private static bool RoadwayHeight(Vector3[] verts, int[] tris, out float y)
        {
            y = 0f;
            if (verts.Length == 0) return false;

            Vector3 lo = verts[0], hi = verts[0];
            for (int i = 1; i < verts.Length; i++) { lo = Vector3.Min(lo, verts[i]); hi = Vector3.Max(hi, verts[i]); }
            float minArea = (hi.x - lo.x) * (hi.z - lo.z) * AreaShare;
            if (minArea <= 0f) return false;

            // aire cumulee des faces horizontales, par tranche de hauteur
            var area = new Dictionary<int, float>();
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 a = verts[tris[i]], b = verts[tris[i + 1]], c = verts[tris[i + 2]];
                Vector3 cr = Vector3.Cross(b - a, c - a);
                float len = cr.magnitude;
                if (len < 1e-9f || cr.y / len < FlatCos) continue;
                int k = Mathf.RoundToInt((a.y + b.y + c.y) / 3f / HeightStep);
                area[k] = (area.TryGetValue(k, out float acc) ? acc : 0f) + len * 0.5f;
            }

            int best = int.MaxValue;
            foreach (var kv in area)
                if (kv.Value >= minArea && kv.Key < best) best = kv.Key;
            if (best == int.MaxValue) return false;

            y = best * HeightStep;
            return true;
        }

        // Lit le mesh, le recentre, et met l'axe long sur Z. Retourne valid=false si le mesh
        // n'est pas lisible (isReadable) ou vide -> l'appelant log et abandonne le segment.
        public static Tile Prepare(Mesh mesh, Matrix4x4 toRoot, bool lengthOnZ, bool subdivide = true)
        {
            var t = new Tile();
            if (mesh == null || !mesh.isReadable || mesh.vertexCount == 0) return t;

            var src = mesh.vertices;
            var srcN = mesh.normals;
            var srcT = mesh.tangents;
            var srcUV = mesh.uv;
            var tris = (int[])mesh.triangles.Clone();
            int n = src.Length;

            bool hasN = srcN != null && srcN.Length == n;
            bool hasT = srcT != null && srcT.Length == n;
            bool hasUV = srcUV != null && srcUV.Length == n;

            var verts = new Vector3[n];
            var norms = new Vector3[n];
            var tans = new Vector4[n];
            var uvs = new Vector2[n];

            // Matrice du MeshFilter vers la racine du prefab (identite dans le cas courant,
            // mais un FBX peut avoir un enfant decale/scale).
            Matrix4x4 mn = toRoot.inverse.transpose;   // normales : inverse transposee

            for (int i = 0; i < n; i++)
            {
                verts[i] = toRoot.MultiplyPoint3x4(src[i]);
                if (hasN) norms[i] = mn.MultiplyVector(srcN[i]).normalized;
                if (hasT)
                {
                    Vector3 tv = toRoot.MultiplyVector(srcT[i]).normalized;
                    tans[i] = new Vector4(tv.x, tv.y, tv.z, srcT[i].w);
                }
                if (hasUV) uvs[i] = srcUV[i];
            }

            // Echange X/Z si l'axe long est sur X. C'est une REFLEXION (det = -1) -> il faut
            // inverser le winding des triangles et la handedness des tangentes, sinon la
            // route apparait retournee (faces vers l'interieur).
            if (!lengthOnZ)
            {
                for (int i = 0; i < n; i++)
                {
                    verts[i] = new Vector3(verts[i].z, verts[i].y, verts[i].x);
                    norms[i] = new Vector3(norms[i].z, norms[i].y, norms[i].x);
                    tans[i] = new Vector4(tans[i].z, tans[i].y, tans[i].x, -tans[i].w);
                }
                for (int i = 0; i < tris.Length; i += 3)
                    (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
            }

            // Recentrage (jamais depuis le pivot, qui est decale de la geo dans ces FBX) :
            // X centre, CHAUSSEE sur y = 0, Z ramene dans [0, length].
            Vector3 lo = verts[0], hi = verts[0];
            for (int i = 1; i < n; i++) { lo = Vector3.Min(lo, verts[i]); hi = Vector3.Max(hi, verts[i]); }
            float roadY = RoadwayHeight(verts, tris, out float rh) ? rh : hi.y;
            Vector3 off = new Vector3(-(lo.x + hi.x) * 0.5f, -roadY, -lo.z);
            for (int i = 0; i < n; i++) verts[i] += off;

            if (subdivide) Subdivide(ref verts, ref norms, ref tans, ref uvs, ref tris, hasN, hasT, hasUV);

            t.verts = verts;
            t.normals = hasN ? norms : null;
            t.tangents = hasT ? tans : null;
            t.uv = hasUV ? uvs : null;
            t.tris = tris;
            t.length = hi.z - lo.z;
            t.width = hi.x - lo.x;
            t.bottom = lo.y - roadY;                 // meme recentrage que les sommets
            int iLow = 0;
            for (int i = 1; i < n; i++) if (verts[i].y < verts[iLow].y) iLow = i;
            t.bottomUV = hasUV ? uvs[iLow] : Vector2.zero;
            t.valid = t.length > 1e-3f;
            return t;
        }

        // DECOUPE de la tuile en tranches le long de son axe long.
        //
        // Sans ca, une route qui monte est une suite de PLANCHES PLATES. La chaussee et les
        // trottoirs de SM_Tile_droit sont des bandes pleine longueur : 26 triangles couvrent les
        // 8 m d'un seul tenant. Le warp ne deplace que des sommets ; s'il n'y en a aucun au
        // milieu, la surface reste la CORDE de la courbe entre les deux bouts -- mesure sur une
        // rampe a 3 m : 37 cm de creux au milieu de chaque repetition, avec une cassure d'angle
        // a chaque couture. Parfaitement invisible a plat, injouable des que ca monte.
        //
        // On TRANCHE donc la tuile par des plans perpendiculaires a son axe, espaces de MaxSpan.
        //
        // Trancher, et pas couper l'arete la plus longue de chaque triangle : deux triangles
        // voisins doivent couper leur arete COMMUNE au meme endroit. Sinon on cree des
        // T-junctions -- un sommet plante au milieu de l'arete du voisin -- et l'arete n'est
        // plus partagee par exactement deux faces. Invisible au rendu, mais le bake de grind
        // repose precisement sur ce partage : la premiere version bisectrice cassait la
        // bordure d'une rue en 164 morceaux de 4 m, et le grind decrochait a chaque morceau.
        // Un plan coupe tous les triangles qu'il traverse au meme endroit : conforme d'office.
        //
        // Les sommets crees sur une arete partagee sont dupliques mais CONFONDUS : memes
        // positions, donc pas de fissure apres deformation, et la soudure du baker les fusionne.
        // Le meme defaut existait a l'HORIZONTALE : dans un virage, seuls les pointillés
        // suivaient la courbe, la chaussee restait une corde. Ca se voyait beaucoup moins parce
        // qu'on ne roule pas dessus verticalement.
        //
        // Orienter des tuiles rigides le long de la spline ne remplace PAS la decoupe : ca
        // deplace l'erreur du milieu de la tuile vers ses joints. Sur la rampe de test, la pente
        // de la spline passe de 0.242 a 0.312 puis 0.186 -> deux tuiles rigides de 8 m se
        // raccorderaient avec 4 a 7 deg de cassure d'un coup. Une courbe ne rentre pas dans des
        // morceaux plats de 8 m, quelle que soit leur orientation. D'ou la resolution.
        // Le cout est evite la ou il ne sert a rien : RoadNetwork prepare DEUX tuiles et
        // n'utilise la decoupee que sur les segments dont la spline s'ecarte d'une droite.
        const float MaxSpan = 1.0f;        // portee max d'un triangle sur l'axe long (m)
        const int MaxTris = 20000;         // garde-fou : sortie si la decoupe s'emballe

        private static void Subdivide(ref Vector3[] verts, ref Vector3[] norms, ref Vector4[] tans,
                                      ref Vector2[] uvs, ref int[] tris,
                                      bool hasN, bool hasT, bool hasUV)
        {
            var v = new List<Vector3>(verts);
            var n = new List<Vector3>(norms);
            var g = new List<Vector4>(tans);
            var u = new List<Vector2>(uvs);
            var f = new List<int>(tris);

            // Sommet sur l'arete p->q, au parametre t. Les deux triangles qui partagent cette
            // arete appellent avec les memes p, q et le meme t -> memes valeurs des deux cotes.
            System.Func<int, int, float, int> cut = (p, q, t) =>
            {
                v.Add(Vector3.LerpUnclamped(v[p], v[q], t));
                n.Add(hasN ? Vector3.Slerp(n[p], n[q], t) : Vector3.zero);
                g.Add(hasT ? Vector4.LerpUnclamped(g[p], g[q], t) : Vector4.zero);
                u.Add(hasUV ? Vector2.LerpUnclamped(u[p], u[q], t) : Vector2.zero);
                return v.Count - 1;
            };

            float zmin = float.MaxValue, zmax = float.MinValue;
            foreach (var p in v) { if (p.z < zmin) zmin = p.z; if (p.z > zmax) zmax = p.z; }
            int slabs = Mathf.Clamp(Mathf.CeilToInt((zmax - zmin) / MaxSpan), 1, 512);

            var next = new List<int>();
            for (int s = 1; s < slabs && f.Count / 3 < MaxTris; s++)
            {
                float plane = zmin + s * (zmax - zmin) / slabs;
                next.Clear();

                for (int i = 0; i < f.Count; i += 3)
                {
                    int a = f[i], b = f[i + 1], c = f[i + 2];
                    bool pa = v[a].z >= plane, pb = v[b].z >= plane, pc = v[c].z >= plane;
                    if (pa == pb && pb == pc) { next.Add(a); next.Add(b); next.Add(c); continue; }

                    // A = le sommet seul de son cote ; B et C suivent dans l'ordre cyclique,
                    // ce qui garde le sens de parcours dans les trois enfants.
                    int A, B, C;
                    if (pa != pb && pa != pc) { A = a; B = b; C = c; }
                    else if (pb != pa && pb != pc) { A = b; B = c; C = a; }
                    else { A = c; B = a; C = b; }

                    float dA = v[A].z - plane, dB = v[B].z - plane, dC = v[C].z - plane;
                    int P = cut(A, B, dA / (dA - dB));
                    int Q = cut(A, C, dA / (dA - dC));
                    Emit(next, v, A, P, Q);
                    Emit(next, v, P, B, C);
                    Emit(next, v, P, C, Q);
                }

                f.Clear();
                f.AddRange(next);
            }

            verts = v.ToArray();
            norms = n.ToArray();
            tans = g.ToArray();
            uvs = u.ToArray();
            tris = f.ToArray();
        }

        // Un sommet pile sur le plan donne un enfant d'aire nulle : on ne l'emet pas. Le seuil
        // ne mord que sur du vraiment degenere (aire ~5e-7 m2), pas sur un triangle fin.
        private static void Emit(List<int> dst, List<Vector3> v, int a, int b, int c)
        {
            if (Vector3.Cross(v[b] - v[a], v[c] - v[a]).sqrMagnitude < 1e-12f) return;
            dst.Add(a); dst.Add(b); dst.Add(c);
        }

        // Repere orthonorme a une station : position + base (fwd/right/up).
        private struct Frame
        {
            public Vector3 pos, fwd, right, up;
        }

        // Warpe la tuile le long de la spline. Retourne null si la spline est trop courte ou
        // la tuile invalide. `reuse` evite de reallouer un Mesh a chaque frame de drag.
        public static Mesh Warp(in Tile tile, Spline spline, Mesh reuse)
        {
            if (!tile.valid) return null;
            float len = spline.GetLength();
            if (len < MinLength) return null;

            // Nombre de repetitions + pas exact : la tuile est etiree/compressee sur Z pour
            // tomber pile sur la longueur de la spline (facteur borne a ~[0.67, 1.33]).
            int repeats = Mathf.Max(1, Mathf.RoundToInt(len / tile.length));
            float step = len / repeats;

            // --- LUT de reperes, echantillonnee a distance d'arc EGALE ---
            int k = Mathf.Clamp(Mathf.CeilToInt(len / StationEvery), StationsMin, StationsMax);
            var stations = new Frame[k + 1];
            Vector3 prevUp = Vector3.up;
            for (int s = 0; s <= k; s++)
            {
                float d = len * s / k;
                float tt = spline.ConvertIndexUnit(d, PathIndexUnit.Distance, PathIndexUnit.Normalized);
                spline.Evaluate(tt, out float3 p, out float3 tan, out _);   // le up renvoye est IGNORE

                Vector3 fwd = ((Vector3)tan);
                if (fwd.sqrMagnitude < 1e-10f) fwd = s > 0 ? stations[s - 1].fwd : Vector3.forward;
                fwd.Normalize();

                // Transport parallele : on porte le up precedent en retirant sa composante
                // sur la nouvelle tangente -> pas de vrille, pas de flip.
                Vector3 up = prevUp - fwd * Vector3.Dot(prevUp, fwd);
                if (up.sqrMagnitude < 1e-6f) up = Vector3.Cross(fwd, Vector3.right);   // tangente quasi verticale
                if (up.sqrMagnitude < 1e-6f) up = Vector3.Cross(fwd, Vector3.forward);
                up.Normalize();

                stations[s] = new Frame
                {
                    pos = (Vector3)p,
                    fwd = fwd,
                    up = up,
                    right = Vector3.Cross(up, fwd)
                };
                prevUp = up;
            }

            // --- warp ---
            // Les tuiles du kit sont des COQUES OUVERTES : sur SM_Tile_droit, 8 triangles
            // regardent vers le bas sur 254. En ville plate personne ne s'en apercoit ; des
            // qu'un segment est leve, on voit le marquage au sol PAR EN DESSOUS. On ferme donc
            // le tablier par un ruban plat au niveau du bas de la tuile, normales vers le bas.
            // Ajoute partout et pas seulement sur les ouvrages : au sol il finit sous le
            // trottoir, invisible, et ca evite une branche et un cas particulier a maintenir.
            int vn = tile.verts.Length;
            int soffitV = 2 * (k + 1);
            int soffitT = k * 6;
            int total = vn * repeats + soffitV;
            int triCount = tile.tris.Length * repeats + soffitT;

            // Pendant un drag, `repeats` ne change en general pas d'une frame a l'autre : les
            // triangles et les UV sont alors identiques et n'ont pas besoin d'etre re-uploades.
            bool sameTopology = reuse != null
                                && reuse.vertexCount == total
                                && reuse.subMeshCount == 1
                                && reuse.GetIndexCount(0) == (uint)triCount;

            var outV = new Vector3[total];
            var outN = tile.normals != null ? new Vector3[total] : null;
            var outT = tile.tangents != null ? new Vector4[total] : null;
            var outUV = !sameTopology && tile.uv != null ? new Vector2[total] : null;
            var outTris = sameTopology ? null : new int[triCount];

            for (int rep = 0; rep < repeats; rep++)
            {
                int baseV = rep * vn;
                float dBase = rep * step;
                for (int i = 0; i < vn; i++)
                {
                    Vector3 v = tile.verts[i];
                    float d = dBase + (v.z / tile.length) * step;

                    float f = Mathf.Clamp(d / len, 0f, 1f) * k;
                    int s0 = Mathf.Clamp(Mathf.FloorToInt(f), 0, k - 1);
                    float u = f - s0;
                    Frame fr = Lerp(stations[s0], stations[s0 + 1], u);

                    int o = baseV + i;
                    outV[o] = fr.pos + fr.right * v.x + fr.up * v.y;
                    // Normales transformees par un repere ORTHONORME -> restent unitaires.
                    // Surtout pas de RecalculateNormals() : ca ecraserait le split hard/soft
                    // edges de l'asset.
                    if (outN != null)
                    {
                        Vector3 nv = tile.normals[i];
                        outN[o] = fr.right * nv.x + fr.up * nv.y + fr.fwd * nv.z;
                    }
                    if (outT != null)
                    {
                        Vector4 tv = tile.tangents[i];
                        Vector3 tw = fr.right * tv.x + fr.up * tv.y + fr.fwd * tv.z;
                        outT[o] = new Vector4(tw.x, tw.y, tw.z, tv.w);
                    }
                    if (outUV != null) outUV[o] = tile.uv[i];   // inchange -> chaque repeat retile
                }

                if (outTris == null) continue;
                int baseT = rep * tile.tris.Length;
                for (int i = 0; i < tile.tris.Length; i++)
                    outTris[baseT + i] = tile.tris[i] + baseV;
            }

            // --- ruban du dessous ---
            // Une station = deux sommets (gauche/droite) a la largeur de la tuile. Le winding
            // (L0,R0,L1) puis (R0,R1,L1) donne cross(b-a, c-a) = -up, donc des faces qui
            // regardent bien vers le sol : vu d'en dessous elles sont pleines, vu d'en haut
            // elles sont cullees et ne peuvent pas masquer la chaussee.
            int sBase = vn * repeats;
            float hw = tile.width * 0.5f;
            for (int s = 0; s <= k; s++)
            {
                Frame fr = stations[s];
                // 1 cm SOUS le point le plus bas : la tuile garde quelques triangles bas
                // isoles, exactement a cette hauteur -> coplanaires avec le ruban, ils
                // mouchettent le dessous en z-fighting.
                Vector3 b = fr.pos + fr.up * (tile.bottom - SoffitBias);
                int o = sBase + s * 2;
                outV[o] = b - fr.right * hw;
                outV[o + 1] = b + fr.right * hw;
                if (outN != null) { outN[o] = -fr.up; outN[o + 1] = -fr.up; }
                if (outT != null)
                {
                    var tw = new Vector4(fr.right.x, fr.right.y, fr.right.z, -1f);
                    outT[o] = tw; outT[o + 1] = tw;
                }
                if (outUV != null) { outUV[o] = tile.bottomUV; outUV[o + 1] = tile.bottomUV; }
            }
            if (outTris != null)
            {
                int tBase = tile.tris.Length * repeats;
                for (int s = 0; s < k; s++)
                {
                    int l0 = sBase + s * 2, r0 = l0 + 1, l1 = l0 + 2, r1 = l0 + 3;
                    int o = tBase + s * 6;
                    outTris[o] = l0; outTris[o + 1] = r0; outTris[o + 2] = l1;
                    outTris[o + 3] = r0; outTris[o + 4] = r1; outTris[o + 5] = l1;
                }
            }

            Mesh mesh = reuse != null ? reuse : new Mesh();
            if (!sameTopology)
            {
                mesh.Clear();
                mesh.indexFormat = total > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            }
            mesh.SetVertices(outV);
            if (outN != null) mesh.SetNormals(outN);
            if (outT != null) mesh.SetTangents(outT);
            if (outUV != null) mesh.SetUVs(0, outUV);
            if (outTris != null) mesh.SetTriangles(outTris, 0, false);   // bounds faites juste apres
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Frame Lerp(in Frame a, in Frame b, float u)
        {
            Vector3 fwd = Vector3.Lerp(a.fwd, b.fwd, u);
            if (fwd.sqrMagnitude < 1e-10f) fwd = a.fwd;
            fwd.Normalize();
            Vector3 up = Vector3.Lerp(a.up, b.up, u);
            up -= fwd * Vector3.Dot(up, fwd);                                   // re-orthogonalise
            if (up.sqrMagnitude < 1e-8f) up = Vector3.Cross(fwd, Vector3.right);
            up.Normalize();
            return new Frame
            {
                pos = Vector3.Lerp(a.pos, b.pos, u),
                fwd = fwd,
                up = up,
                right = Vector3.Cross(up, fwd)
            };
        }
    }
}
