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
            // Sous-mesh d'origine de chaque TRIANGLE (tris.Length / 3 entrees) et nom du
            // materiau de chaque sous-mesh. Le kit SM_* separe bitume / trottoir / bordure /
            // marquage neon en slots nommes : sans ces deux tableaux le warp les fond en un
            // seul materiau et toute la distinction est perdue.
            public int[] triSub;
            public string[] subNames;
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
        public static Tile Prepare(Mesh mesh, Matrix4x4 toRoot, bool lengthOnZ, bool subdivide = true,
                                   string[] subNames = null)
        {
            var t = new Tile();
            if (mesh == null || !mesh.isReadable || mesh.vertexCount == 0) return t;

            var src = mesh.vertices;
            var srcN = mesh.normals;
            var srcT = mesh.tangents;
            var srcUV = mesh.uv;
            var tris = (int[])mesh.triangles.Clone();
            int n = src.Length;

            // `mesh.triangles` concatene les sous-meshes DANS L'ORDRE : les compteurs d'index
            // suffisent donc a etiqueter chaque triangle sans relire les sous-meshes un par un.
            var triSub = new int[tris.Length / 3];
            for (int s = 0, at = 0; s < mesh.subMeshCount; s++)
            {
                int count = (int)mesh.GetIndexCount(s) / 3;
                for (int i = 0; i < count && at < triSub.Length; i++, at++) triSub[at] = s;
            }

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

            if (subdivide) Subdivide(ref verts, ref norms, ref tans, ref uvs, ref tris, ref triSub, hasN, hasT, hasUV);

            t.verts = verts;
            t.normals = hasN ? norms : null;
            t.tangents = hasT ? tans : null;
            t.uv = hasUV ? uvs : null;
            t.tris = tris;
            t.triSub = triSub;
            t.subNames = subNames;
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
                                      ref Vector2[] uvs, ref int[] tris, ref int[] triSub,
                                      bool hasN, bool hasT, bool hasUV)
        {
            var v = new List<Vector3>(verts);
            var n = new List<Vector3>(norms);
            var g = new List<Vector4>(tans);
            var u = new List<Vector2>(uvs);
            var f = new List<int>(tris);
            // Les enfants d'un triangle tranche heritent de son sous-mesh : la decoupe change la
            // resolution, jamais l'appartenance a un materiau.
            var fs = new List<int>(triSub);

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
            var nextS = new List<int>();
            for (int s = 1; s < slabs && f.Count / 3 < MaxTris; s++)
            {
                float plane = zmin + s * (zmax - zmin) / slabs;
                next.Clear();
                nextS.Clear();

                for (int i = 0; i < f.Count; i += 3)
                {
                    int sub = i / 3 < fs.Count ? fs[i / 3] : 0;
                    int a = f[i], b = f[i + 1], c = f[i + 2];
                    bool pa = v[a].z >= plane, pb = v[b].z >= plane, pc = v[c].z >= plane;
                    if (pa == pb && pb == pc) { next.Add(a); next.Add(b); next.Add(c); nextS.Add(sub); continue; }

                    // A = le sommet seul de son cote ; B et C suivent dans l'ordre cyclique,
                    // ce qui garde le sens de parcours dans les trois enfants.
                    int A, B, C;
                    if (pa != pb && pa != pc) { A = a; B = b; C = c; }
                    else if (pb != pa && pb != pc) { A = b; B = c; C = a; }
                    else { A = c; B = a; C = b; }

                    float dA = v[A].z - plane, dB = v[B].z - plane, dC = v[C].z - plane;
                    int P = cut(A, B, dA / (dA - dB));
                    int Q = cut(A, C, dA / (dA - dC));
                    Emit(next, nextS, v, A, P, Q, sub);
                    Emit(next, nextS, v, P, B, C, sub);
                    Emit(next, nextS, v, P, C, Q, sub);
                }

                f.Clear();
                f.AddRange(next);
                fs.Clear();
                fs.AddRange(nextS);
            }

            verts = v.ToArray();
            norms = n.ToArray();
            tans = g.ToArray();
            uvs = u.ToArray();
            tris = f.ToArray();
            triSub = fs.ToArray();
        }

        // Un sommet pile sur le plan donne un enfant d'aire nulle : on ne l'emet pas. Le seuil
        // ne mord que sur du vraiment degenere (aire ~5e-7 m2), pas sur un triangle fin.
        private static void Emit(List<int> dst, List<int> dstSub, List<Vector3> v, int a, int b, int c, int sub)
        {
            if (Vector3.Cross(v[b] - v[a], v[c] - v[a]).sqrMagnitude < 1e-12f) return;
            dst.Add(a); dst.Add(b); dst.Add(c);
            dstSub.Add(sub);
        }

        // Repere orthonorme a une station : position + base (fwd/right/up).
        private struct Frame
        {
            public Vector3 pos, fwd, right, up;
        }

        // Habillage d'OUVRAGE, en plus de la tuile. Tout est en metres, dans le repere de la
        // tuile (chaussee a ~0). Zero partout = comportement d'avant, une rue au sol.
        //
        // Sans ca un pont est une FEUILLE DE PAPIER : la tuile du kit n'a pas d'epaisseur, et le
        // ruban du dessous la ferme sans rien lui donner de visible depuis le cote.
        public struct Deck
        {
            public float fascia;       // retombee laterale sous le tablier -> ca se lit comme une poutre
            public float parapet;      // hauteur du garde-corps au-dessus du trottoir (0 = aucun)
            public float parapetBase;  // y du trottoir, d'ou part le garde-corps
        }

        // Warpe la tuile le long de la spline. Retourne null si la spline est trop courte ou
        // la tuile invalide. `reuse` evite de reallouer un Mesh a chaque frame de drag.
        // Nombre de repetitions de tuile sur une longueur donnee. Expose parce que l'appelant doit
        // pouvoir preparer un choix de variante PAR repetition avant d'appeler Warp.
        public static int Repeats(float splineLength, float tileLength)
            => Mathf.Max(1, Mathf.RoundToInt(splineLength / Mathf.Max(0.01f, tileLength)));

        // `variants` + `pick` : une tuile differente par repetition (passage pieton, egouts).
        // `pick[rep]` indexe `variants` ; 0 ou hors bornes = la tuile de base. Les variantes du kit
        // ont EXACTEMENT la meme emprise que la tuile droite (13 x 8), c'est ce qui permet de les
        // substituer sans toucher au calcul de repetition.
        // `slots` : table GLOBALE des materiaux du segment, dans l'ordre des sous-meshes de
        // sortie. Elle ne peut pas etre l'ordre des slots d'UNE tuile : la tuile droite, la
        // variante pieton et la variante egouts partagent leurs trois premiers slots mais
        // different sur le quatrieme (neon / passage pieton / plaque). Fondre par INDEX
        // peindrait le passage pieton en neon selon la repetition ; on mappe donc par NOM.
        // slots == null -> ancien comportement, un seul sous-mesh.
        public static Mesh Warp(in Tile tile, Spline spline, Mesh reuse, Deck deck = default(Deck),
                                Tile[] variants = null, int[] pick = null, string[] slots = null)
        {
            if (!tile.valid) return null;
            float len = spline.GetLength();
            if (len < MinLength) return null;

            // Nombre de repetitions + pas exact : la tuile est etiree/compressee sur Z pour
            // tomber pile sur la longueur de la spline (facteur borne a ~[0.67, 1.33]).
            int repeats = Repeats(len, tile.length);
            float step = len / repeats;

            // --- LUT de reperes, echantillonnee a distance d'arc EGALE ---
            int k = Mathf.Clamp(Mathf.CeilToInt(len / StationEvery), StationsMin, StationsMax);
            Frame[] stations = Stations(spline, len, k);

            // --- warp ---
            // Les tuiles du kit sont des COQUES OUVERTES : sur SM_Tile_droit, 8 triangles
            // regardent vers le bas sur 254. En ville plate personne ne s'en apercoit ; des
            // qu'un segment est leve, on voit le marquage au sol PAR EN DESSOUS. On ferme donc
            // le tablier par un ruban plat au niveau du bas de la tuile, normales vers le bas.
            // Ajoute partout et pas seulement sur les ouvrages : au sol il finit sous le
            // trottoir, invisible, et ca evite une branche et un cas particulier a maintenir.
            // Les repetitions n'ont plus forcement la meme tuile : les compteurs se cumulent au
            // lieu de se multiplier.
            int vn = 0, tn = 0;
            for (int rep = 0; rep < repeats; rep++)
            {
                Tile src = Pick(tile, variants, pick, rep);
                vn += src.verts.Length;
                tn += src.tris.Length;
            }

            int soffitV = 2 * (k + 1);
            int soffitT = k * 6;
            // Flancs : 4 bandes (bord droit et gauche, chacune vue de dehors ET de dedans). Les
            // deux faces sont emises separement parce qu'elles n'ont pas la meme normale -- un
            // garde-corps sans face interieure disparait des qu'on roule sur le pont.
            bool hasSides = deck.fascia > 0.001f || deck.parapet > 0.001f;
            int sideV = hasSides ? 8 * (k + 1) : 0;
            int sideT = hasSides ? k * 24 : 0;
            int total = vn + soffitV + sideV;
            int triCount = tn + soffitT + sideT;

            int subCount = slots != null && slots.Length > 0 ? slots.Length : 1;
            // Le dessous et les flancs sont de la chaussee vue par en bas : ils suivent le
            // bitume. A defaut de slot nomme, le premier fait l'affaire.
            int groundSlot = SlotIndex(slots, "M_bitume");

            // Pendant un drag, `repeats` ne change en general pas d'une frame a l'autre : les
            // triangles et les UV sont alors identiques et n'ont pas besoin d'etre re-uploades.
            uint reuseIdx = 0;
            if (reuse != null && reuse.subMeshCount == subCount)
                for (int s = 0; s < subCount; s++) reuseIdx += reuse.GetIndexCount(s);
            bool sameTopology = reuse != null
                                && reuse.vertexCount == total
                                && reuse.subMeshCount == subCount
                                && reuseIdx == (uint)triCount;

            var outV = new Vector3[total];
            var outN = tile.normals != null ? new Vector3[total] : null;
            var outT = tile.tangents != null ? new Vector4[total] : null;
            var outUV = !sameTopology && tile.uv != null ? new Vector2[total] : null;
            var outTris = sameTopology ? null : new int[triCount];
            var outSub = outTris != null && subCount > 1 ? new int[triCount / 3] : null;

            int baseV = 0, baseT = 0;
            for (int rep = 0; rep < repeats; rep++)
            {
                Tile src = Pick(tile, variants, pick, rep);
                float dBase = rep * step;
                for (int i = 0; i < src.verts.Length; i++)
                {
                    Vector3 v = src.verts[i];
                    float d = dBase + (v.z / src.length) * step;

                    float f = Mathf.Clamp(d / len, 0f, 1f) * k;
                    int s0 = Mathf.Clamp(Mathf.FloorToInt(f), 0, k - 1);
                    float u = f - s0;
                    Frame fr = Lerp(stations[s0], stations[s0 + 1], u);

                    int o = baseV + i;
                    outV[o] = fr.pos + fr.right * v.x + fr.up * v.y;
                    // Normales transformees par un repere ORTHONORME -> restent unitaires.
                    // Surtout pas de RecalculateNormals() : ca ecraserait le split hard/soft
                    // edges de l'asset.
                    if (outN != null && src.normals != null)
                    {
                        Vector3 nv = src.normals[i];
                        outN[o] = fr.right * nv.x + fr.up * nv.y + fr.fwd * nv.z;
                    }
                    if (outT != null && src.tangents != null)
                    {
                        Vector4 tv = src.tangents[i];
                        Vector3 tw = fr.right * tv.x + fr.up * tv.y + fr.fwd * tv.z;
                        outT[o] = new Vector4(tw.x, tw.y, tw.z, tv.w);
                    }
                    if (outUV != null && src.uv != null) outUV[o] = src.uv[i];   // inchange -> chaque repeat retile
                }

                if (outTris != null)
                {
                    for (int i = 0; i < src.tris.Length; i++)
                        outTris[baseT + i] = src.tris[i] + baseV;

                    if (outSub != null)
                    {
                        int[] map = SlotMap(src.subNames, slots);
                        for (int t = 0; t < src.tris.Length / 3; t++)
                        {
                            int local = src.triSub != null && t < src.triSub.Length ? src.triSub[t] : 0;
                            outSub[baseT / 3 + t] = map != null && local < map.Length ? map[local] : groundSlot;
                        }
                    }
                }

                baseV += src.verts.Length;
                baseT += src.tris.Length;
            }

            // Tout ce qui suit les repetitions (ruban du dessous, flancs) est de la geometrie
            // fabriquee ici, pas de l'asset : elle n'a pas de slot d'origine et prend le bitume.
            if (outSub != null)
                for (int t = tn / 3; t < outSub.Length; t++) outSub[t] = groundSlot;

            // --- ruban du dessous ---
            // Une station = deux sommets (gauche/droite) a la largeur de la tuile. Le winding
            // (L0,R0,L1) puis (R0,R1,L1) donne cross(b-a, c-a) = -up, donc des faces qui
            // regardent bien vers le sol : vu d'en dessous elles sont pleines, vu d'en haut
            // elles sont cullees et ne peuvent pas masquer la chaussee.
            int sBase = vn;
            float hw = tile.width * 0.5f;
            for (int s = 0; s <= k; s++)
            {
                Frame fr = stations[s];
                // 1 cm SOUS le point le plus bas : la tuile garde quelques triangles bas
                // isoles, exactement a cette hauteur -> coplanaires avec le ruban, ils
                // mouchettent le dessous en z-fighting.
                // Le dessous descend avec la retombee : sinon le ruban resterait colle sous la
                // chaussee et les flancs pendraient dans le vide.
                Vector3 b = fr.pos + fr.up * (tile.bottom - deck.fascia - SoffitBias);
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
                int tBase = tn;
                for (int s = 0; s < k; s++)
                {
                    int l0 = sBase + s * 2, r0 = l0 + 1, l1 = l0 + 2, r1 = l0 + 3;
                    int o = tBase + s * 6;
                    outTris[o] = l0; outTris[o + 1] = r0; outTris[o + 2] = l1;
                    outTris[o + 3] = r0; outTris[o + 4] = r1; outTris[o + 5] = l1;
                }
            }

            // --- flancs : une seule bande verticale par bord ---
            // Elle court du bas de la retombee jusqu'au haut du garde-corps. Les deux d'un coup
            // parce que c'est la MEME arete du tablier qui les porte : les separer laisserait une
            // fente a hauteur de trottoir des que la spline tourne.
            if (hasSides)
            {
                int fBase = sBase + soffitV;
                int fTri = tn + soffitT;
                float yLo = tile.bottom - deck.fascia;
                float yHi = deck.parapet > 0.001f ? deck.parapetBase + deck.parapet : tile.bottom;

                for (int band = 0; band < 4; band++)
                {
                    float side = band < 2 ? 1f : -1f;        // +right = bord droit
                    bool outward = (band & 1) == 0;
                    int vb = fBase + band * 2 * (k + 1);

                    for (int s = 0; s <= k; s++)
                    {
                        Frame fr = stations[s];
                        Vector3 e = fr.pos + fr.right * (hw * side);
                        int o = vb + s * 2;
                        outV[o] = e + fr.up * yLo;
                        outV[o + 1] = e + fr.up * yHi;
                        if (outN != null)
                        {
                            Vector3 nv = fr.right * (side * (outward ? 1f : -1f));
                            outN[o] = nv; outN[o + 1] = nv;
                        }
                        if (outT != null)
                        {
                            var tw = new Vector4(fr.fwd.x, fr.fwd.y, fr.fwd.z, -1f);
                            outT[o] = tw; outT[o + 1] = tw;
                        }
                        if (outUV != null) { outUV[o] = tile.bottomUV; outUV[o + 1] = tile.bottomUV; }
                    }

                    if (outTris == null) continue;
                    // (bas0, haut0, bas1) puis (haut0, haut1, bas1) donne une normale +right ;
                    // le bord gauche et les faces interieures inversent l'ordre.
                    bool reverse = (side > 0f) != outward;
                    int tb = fTri + band * k * 6;
                    for (int s = 0; s < k; s++)
                    {
                        int a = vb + s * 2, b2 = a + 1, c = a + 2, d = a + 3;
                        int o = tb + s * 6;
                        if (reverse)
                        {
                            outTris[o] = a; outTris[o + 1] = c; outTris[o + 2] = b2;
                            outTris[o + 3] = b2; outTris[o + 4] = c; outTris[o + 5] = d;
                        }
                        else
                        {
                            outTris[o] = a; outTris[o + 1] = b2; outTris[o + 2] = c;
                            outTris[o + 3] = b2; outTris[o + 4] = d; outTris[o + 5] = c;
                        }
                    }
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
            if (outTris != null)
            {
                if (outSub == null) mesh.SetTriangles(outTris, 0, false);   // bounds faites juste apres
                else
                {
                    // Un sous-mesh VIDE est legal et volontaire : les slots sont les memes pour
                    // tous les segments, donc l'ordre des materiaux du renderer est stable meme
                    // quand un segment n'a ni passage pieton ni plaque d'egout.
                    var buckets = new List<int>[subCount];
                    for (int i = 0; i < subCount; i++) buckets[i] = new List<int>();
                    for (int t = 0; t < outSub.Length; t++)
                    {
                        var b = buckets[Mathf.Clamp(outSub[t], 0, subCount - 1)];
                        b.Add(outTris[t * 3]); b.Add(outTris[t * 3 + 1]); b.Add(outTris[t * 3 + 2]);
                    }
                    mesh.subMeshCount = subCount;
                    for (int i = 0; i < subCount; i++) mesh.SetTriangles(buckets[i], i, false);
                }
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        // Position d'un slot dans la table globale. -1 / table vide -> 0 : mieux vaut peindre au
        // bitume qu'exploser sur un kit qui ne nommerait pas ses materiaux.
        private static int SlotIndex(string[] slots, string name)
        {
            if (slots == null) return 0;
            for (int i = 0; i < slots.Length; i++) if (slots[i] == name) return i;
            return 0;
        }

        // Sous-mesh local -> slot global, par NOM. Null si la tuile n'a pas de noms : l'appelant
        // retombe alors sur le bitume.
        private static int[] SlotMap(string[] subNames, string[] slots)
        {
            if (subNames == null || slots == null) return null;
            var map = new int[subNames.Length];
            for (int i = 0; i < subNames.Length; i++) map[i] = SlotIndex(slots, subNames[i]);
            return map;
        }

        // Tuile d'une repetition : la variante demandee si elle est valide, la tuile de base
        // sinon. Un repli et pas une erreur -- une variante absente du kit ou un mesh illisible
        // doit donner une route normale, pas un trou.
        private static Tile Pick(in Tile fallback, Tile[] variants, int[] pick, int rep)
        {
            if (variants == null || pick == null || rep < 0 || rep >= pick.Length) return fallback;
            int v = pick[rep];
            if (v <= 0 || v >= variants.Length || !variants[v].valid) return fallback;
            return variants[v];
        }

        // LUT de reperes le long de la spline, a distance d'arc EGALE, base propagee par
        // TRANSPORT PARALLELE. Partagee par les trois natures de trace : c'est le seul endroit
        // du fichier qui sait lire une spline, et il n'y a aucune raison qu'un escalier et une
        // route ne soient pas d'accord sur ou est la gauche.
        private static Frame[] Stations(Spline spline, float len, int k)
        {
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

                // On porte le up precedent en retirant sa composante sur la nouvelle tangente
                // -> pas de vrille entre knots, pas de flip a 180 deg pres de la verticale.
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
            return stations;
        }

        // ---------------------------------------------------------------- traces non routiers
        //
        // Escalier et egout ne sont PAS des tuiles warpees : le kit n'a d'asset pour ni l'un ni
        // l'autre, et en commander un serait payer un aller-retour Blender pour deux extrusions.
        // Les deux sont donc balayes directement depuis la spline. L'auteur fait le MEME geste
        // que pour une route -- poser des points, tirer un segment -- et c'est la nature du
        // segment qui decide de la geometrie produite.

        // EGOUT : canal balaye le long de la spline, qui en porte le FOND.
        //
        // Le profil est en U et pas en V ni en boite parce que le canal doit se RIDER. Un quart
        // d'ellipse part TANGENT AU FOND (on y arrive a plat, sans arete a taper) et finit
        // VERTICAL a la levre : c'est la transition d'un half-pipe, pas une tranchee.
        //
        // La levre plate qui prolonge le haut n'est pas decorative. C'est elle qui recoit le
        // recouvrement de la grille du sol -- exactement le role du trottoir sur une tuile de
        // route (voir CityGroundBuilder.Overlap) -- et elle evite d'avoir a decouper la grille
        // sur le profil exact du canal.
        // `capStart` / `capEnd` : mur de fond a la bouche. Pose seulement la ou AUCUN autre egout
        // ne continue -- sinon on planterait un mur au milieu du canal a chaque noeud. Sans lui,
        // le U est un tube ouvert et on voit au travers depuis le bout.
        public static Mesh Channel(Spline spline, Mesh reuse, float halfWidth, float depth,
                                   float wall, float lip, bool capStart, bool capEnd)
        {
            float len = spline.GetLength();
            if (len < MinLength) return null;

            int k = Mathf.Clamp(Mathf.CeilToInt(len / StationEvery), StationsMin, StationsMax);
            lip = Mathf.Max(0f, lip);
            var sec = ChannelSection(Mathf.Max(0.5f, halfWidth), Mathf.Max(0.1f, depth),
                                     Mathf.Max(0.1f, wall), lip, WallSegments);
            // Le tablier de bouche vaut au moins la diagonale d'une cellule de sol (2 m -> 2,83),
            // sinon le recul de la grille le deborde et le trou revient.
            return Sweep(reuse, UprightStations(spline, len, k), sec, len, capStart, capEnd,
                         Mathf.Max(3.2f, lip), 1);
        }

        const int WallSegments = 8;   // facettes par paroi du U -- 8 suffit a ne plus se voir
        // RETOMBEE sous le bord exterieur de la levre. Sans elle le canal est une FEUILLE : la
        // levre regarde vers le haut, donc on la traverse par en dessous, et le sol s'arrete plus
        // tot qu'elle (sa grille ne garde une cellule que si ses quatre coins sont hors emprise --
        // mesure sur seg14 : levre de 9 a 12,5 m de l'axe, sol a partir de 10,75 seulement, et
        // 30 cm plus bas). Il restait donc un anneau ouvert sur tout le pourtour, par lequel un
        // regard rasant depuis le canal sortait droit sur le ciel.
        //
        // 2 m suffisent : la retombee part du bord exterieur de la levre, la ou le sol la recouvre
        // deja -- elle est enterree, on ne la voit jamais, elle ne fait que boucher.
        const float ChannelSkirt = 2f;

        // Reperes references sur la VERTICALE DU MONDE, et non transportes le long de la courbe.
        //
        // Le transport parallele depend du CHEMIN parcouru, et c'est disqualifiant des que deux
        // troncons se rejoignent : mesure sur la ville, le repere du premier arrivait au noeud
        // ROULE DE 1,65 deg apres 52 stations de descente en courbe, quand celui du second en
        // repartait droit. 38 cm d'ecart sur les levres, soit la fente visible a la jonction.
        //
        // Ici le repere ne depend QUE de la tangente. Deux troncons qui partagent un noeud y ont
        // des tangentes colineaires (voir RoadNetwork.Arm, bissectrice traversante), donc des
        // reperes identiques -- le raccord est seamless par construction et non par chance. Un
        // egout ne se devers pas : on ne perd rien au change.
        private static Frame[] UprightStations(Spline spline, float len, int k)
        {
            var st = new Frame[k + 1];
            for (int s = 0; s <= k; s++)
            {
                float tt = spline.ConvertIndexUnit(len * s / k, PathIndexUnit.Distance,
                                                   PathIndexUnit.Normalized);
                spline.Evaluate(tt, out float3 p, out float3 tan, out _);

                Vector3 fwd = (Vector3)tan;
                if (fwd.sqrMagnitude < 1e-10f) fwd = s > 0 ? st[s - 1].fwd : Vector3.forward;
                fwd.Normalize();

                Vector3 up = Vector3.up - fwd * Vector3.Dot(Vector3.up, fwd);
                if (up.sqrMagnitude < 1e-6f) up = Vector3.Cross(fwd, Vector3.right);   // quasi vertical
                up.Normalize();

                st[s] = new Frame
                {
                    pos = (Vector3)p,
                    fwd = fwd,
                    up = up,
                    right = Vector3.Cross(up, fwd)
                };
            }
            return st;
        }

        // Section transversale du canal, en (lateral, hauteur), fond a y = 0. Parcourue de la
        // retombee gauche a la retombee droite : le fond est la corde entre les deux bas de
        // paroi, un seul quad, il est plat.
        //
        // Les deux points de RETOMBEE (premier et dernier) sont a part : ils n'ajoutent aucune
        // aire a la section -- ils la prolongent vers le bas, a la verticale -- donc les murs de
        // bouche les IGNORENT (voir le parametre `trim` de Sweep). Les inclure ferait passer
        // l'eventail de Cap sous le fond du canal et retournerait la moitie de ses triangles.
        private static Vector2[] ChannelSection(float halfWidth, float depth, float wall,
                                                float lip, int seg)
        {
            var pts = new List<Vector2>(seg * 2 + 6);
            pts.Add(new Vector2(-(halfWidth + wall + lip), depth - ChannelSkirt));
            pts.Add(new Vector2(-(halfWidth + wall + lip), depth));
            for (int i = seg; i >= 0; i--)
            {
                float a = i / (float)seg * Mathf.PI * 0.5f;
                pts.Add(new Vector2(-(halfWidth + wall * Mathf.Sin(a)), depth * (1f - Mathf.Cos(a))));
            }
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 0.5f;
                pts.Add(new Vector2(halfWidth + wall * Mathf.Sin(a), depth * (1f - Mathf.Cos(a))));
            }
            pts.Add(new Vector2(halfWidth + wall + lip, depth));
            pts.Add(new Vector2(halfWidth + wall + lip, depth - ChannelSkirt));
            return pts.ToArray();
        }

        // Balayage d'une section transversale le long des reperes. Les sommets sont PARTAGES
        // entre stations et entre points de section : RecalculateNormals lisse alors la courbe
        // du U au lieu de la facetter, ce qui est tout l'interet d'avoir mis 8 facettes dedans.
        // `trim` : nombre de points a ignorer a CHAQUE bout de la section quand on ferme une
        // bouche. Le ruban balaye, lui, toute la section.
        private static Mesh Sweep(Mesh reuse, Frame[] st, Vector2[] sec, float len,
                                  bool capStart, bool capEnd, float apron, int trim)
        {
            int ns = st.Length, np = sec.Length;
            if (ns < 2 || np < 2) return null;
            int lo = Mathf.Clamp(trim, 0, np / 2 - 1), hi = np - 1 - lo;
            int cw = hi - lo + 1;

            // Les murs de fond ont leurs PROPRES sommets et ne reprennent pas ceux de la station
            // du bout : partages, RecalculateNormals moyennerait la normale du mur avec celle du
            // canal et biseauterait la bouche.
            // np pour le mur de fond + 4 pour son TABLIER, la levre horizontale qui prolonge la
            // bouche vers l'exterieur. Ce tablier n'est pas cosmetique : la grille du sol ne
            // garde une cellule que si ses QUATRE coins sont hors emprise, donc son bord recule
            // jusqu'a 2,8 m au-dela du plan de bouche pendant que le canal, lui, s'y arrete net.
            // L'anneau entre les deux etait un trou beant. Meme parade que sur les cotes, ou
            // c'est la levre des parois qui recoit le recouvrement.
            // cw sommets de section + 4 pour le tablier + 4 pour sa retombee.
            int capV = (capStart ? cw + 8 : 0) + (capEnd ? cw + 8 : 0);
            // Majorant : (cw - 2) triangles de mur, 2 de tablier, 6 de retombee. Les bandes
            // d'aire nulle sont sautees, donc le compte reel est plus petit -- le tableau est
            // retaille a la fin plutot que de laisser trainer des triangles degeneres.
            int capT = ((capStart ? 1 : 0) + (capEnd ? 1 : 0)) * (cw + 6) * 3;

            var v = new Vector3[ns * np + capV];
            var uv = new Vector2[ns * np + capV];
            var tris = new int[(ns - 1) * (np - 1) * 6 + capT];

            // Abscisse transversale = longueur d'arc de la SECTION. Prendre le simple x
            // comprimerait la texture sur les parois, qui sont pourtant ce qu'on regarde.
            var s = new float[np];
            for (int i = 1; i < np; i++) s[i] = s[i - 1] + Vector2.Distance(sec[i - 1], sec[i]);

            for (int a = 0; a < ns; a++)
            {
                Frame f = st[a];
                float d = len * a / (ns - 1);
                for (int i = 0; i < np; i++)
                {
                    v[a * np + i] = f.pos + f.right * sec[i].x + f.up * sec[i].y;
                    uv[a * np + i] = new Vector2(s[i], d) * 0.1f;
                }
            }

            int t = 0;
            for (int a = 0; a + 1 < ns; a++)
                for (int i = 0; i + 1 < np; i++)
                {
                    int p = a * np + i, q = (a + 1) * np + i;
                    // right = cross(up, fwd), donc cross(q-p, (p+1)-p) = cross(fwd, right) = up :
                    // cet ordre-la regarde vers le HAUT du canal, soit vers qui roule dedans.
                    tris[t++] = p; tris[t++] = q; tris[t++] = p + 1;
                    tris[t++] = p + 1; tris[t++] = q; tris[t++] = q + 1;
                }

            int baseV = ns * np;
            if (capStart) baseV = Cap(v, uv, tris, ref t, baseV, st[0], sec, s, apron, false, lo, hi);
            if (capEnd) Cap(v, uv, tris, ref t, baseV, st[ns - 1], sec, s, apron, true, lo, hi);
            if (t < tris.Length) System.Array.Resize(ref tris, t);

            return Commit(reuse, v, uv, tris);
        }

        // Mur de fond + son tablier. La section fermee par sa corde haute est convexe (courbe en
        // U sous une droite), donc un simple eventail suffit a la remplir -- pas besoin d'une
        // triangulation de polygone.
        private static int Cap(Vector3[] v, Vector2[] uv, int[] tris, ref int t, int baseV,
                               in Frame f, Vector2[] sec, float[] s, float apron, bool forward,
                               int from, int to)
        {
            int np = to - from + 1;
            for (int i = 0; i < np; i++)
            {
                Vector2 c = sec[from + i];
                v[baseV + i] = f.pos + f.right * c.x + f.up * c.y;
                uv[baseV + i] = new Vector2(s[from + i], c.y) * 0.1f;
            }
            // Remplissage en BANDES entre points MIROIR, pas en eventail depuis un coin.
            //
            // La section est symetrique : le point i a gauche et le point np-1-i a droite sont a
            // la meme hauteur. Les relier donne des trapezes larges qui suivent le profil du U.
            // L'eventail, lui, faisait converger dix-huit fuseaux sur la levre gauche, et le
            // rendu cartoon souligne chaque arete : la bouche se lisait en ZIGZAG, avec des
            // triangles d'autant plus effiles qu'on s'eloignait de l'apex.
            //
            // Parcourue l0 -> l1 -> r1 -> r0, la bande tourne dans le sens direct du repere
            // (right, up), donc sa normale vaut cross(right, up) = +fwd.
            //
            // Le mur regarde VERS LE CANAL, donc a l'oppose de `forward` -- d'ou le !forward.
            // C'est le seul cote qu'on puisse voir : l'autre est enterre dans le terrain. Oriente
            // vers le terrain (ce qu'il faisait), le mur est invisible depuis la tranchee et on
            // regarde droit au travers -- mesure sur les quatre bouches de la ville, la normale
            // moyenne pointait a -1.00 vers l'interieur, soit exactement a l'envers.
            for (int i = 0; i + 1 < np - 2 - i; i++)
            {
                // Bande d'aire nulle : les deux points de levre sont a plat sous la corde haute,
                // les quatre coins sont alors alignes. PhysX n'a rien a faire d'un triangle
                // degenere, et le compte de triangles a ete majore pour permettre ce saut.
                if (Mathf.Abs(sec[from + i].y - sec[from + i + 1].y) < 1e-4f) continue;

                int l0 = baseV + i, l1 = baseV + i + 1;
                int r1 = baseV + np - 2 - i, r0 = baseV + np - 1 - i;
                Emit(tris, ref t, !forward, l0, l1, r1);
                Emit(tris, ref t, !forward, l0, r1, r0);
            }

            // Tablier : rectangle plat au niveau de la levre, pose EN DEHORS de la bouche. Son
            // bord interieur epouse le haut du mur de fond, donc l'ensemble reste etanche.
            //
            // Il porte sa propre RETOMBEE sur ses trois bords libres, pour la meme raison que la
            // levre du canal : le sol autour arrive 7 cm plus bas (mesure a la bouche du noeud
            // 22), et une dalle a bord vif laisse donc une fente sur tout son pourtour -- le
            // petit trou qu'on voit en s'accroupissant a cote de la bouche.
            int a = baseV + np;
            Vector3 outward = f.fwd * (forward ? apron : -apron);
            Vector3 drop = Vector3.down * ChannelSkirt;
            float lo = sec[from].x, hi = sec[to].x, y = sec[from].y;
            Vector3 inL = f.pos + f.right * lo + f.up * y;
            Vector3 inR = f.pos + f.right * hi + f.up * y;
            v[a + 0] = inL; v[a + 1] = inL + outward;
            v[a + 2] = inR; v[a + 3] = inR + outward;
            for (int i = 0; i < 4; i++) v[a + 4 + i] = v[a + i] + drop;
            for (int i = 0; i < 8; i++)
                uv[a + i] = new Vector2(v[a + i].x, v[a + i].z) * 0.1f;

            // Dessus, puis les trois cotes de la retombee : gauche, exterieur, droit.
            Emit(tris, ref t, forward, a + 0, a + 1, a + 2);
            Emit(tris, ref t, forward, a + 2, a + 1, a + 3);
            Emit(tris, ref t, forward, a + 0, a + 4, a + 1);
            Emit(tris, ref t, forward, a + 1, a + 4, a + 5);
            Emit(tris, ref t, forward, a + 1, a + 5, a + 3);
            Emit(tris, ref t, forward, a + 3, a + 5, a + 7);
            Emit(tris, ref t, forward, a + 2, a + 3, a + 6);
            Emit(tris, ref t, forward, a + 3, a + 7, a + 6);
            return a + 8;
        }

        // Un triangle, dans l'ordre donne pour le mur d'ARRIVEE et inverse pour celui du depart :
        // les deux bouchons sont geometriquement identiques et se regardent dos a dos.
        private static void Emit(int[] tris, ref int t, bool forward, int a, int b, int c)
        {
            tris[t++] = a;
            tris[t++] = forward ? b : c;
            tris[t++] = forward ? c : b;
        }

        // ESCALIER : volee a marches EGALES entre les deux bouts de la spline.
        //
        // La hauteur des marches sort du DENIVELE ENTRE LES EXTREMITES, pas de la spline : celle
        // -ci suit le relief (voir RoadNetwork.SegmentSpline) et une volee qui epouserait les
        // bosses du terrain sous elle n'est plus un escalier. Le trace EN PLAN, lui, vient bien
        // de la spline -- un escalier a le droit de tourner.
        //
        // Les joues descendent jusqu'a un plancher commun sous le bout le plus bas : sans elles
        // on verrait le terrain entre deux contremarches des que le sol ne colle pas au profil.
        public static Mesh Stairs(Spline spline, Mesh reuse, float halfWidth, float riser,
                                  float skirt)
        {
            float len = spline.GetLength();
            if (len < MinLength) return null;

            riser = Mathf.Max(0.05f, riser);
            spline.Evaluate(0f, out float3 e0, out _, out _);
            float y0 = ((Vector3)e0).y;

            // Le profil SUIT LA SPLINE, quantifiee par pas de contremarche.
            //
            // C'est le point critique du fichier. Le sol se raccorde a la spline
            // (CityGroundBuilder vise probe.point.y), donc toute volee qui se donnerait son
            // propre profil de hauteur ressort du terrain. Une simple droite entre les deux
            // bouts, c'est ce qu'il y avait avant, se decalait de la spline de 0,096 x denivele
            // -- soit 2,4 m au milieu d'un escalier de 25 m, tres au-dela des joues.
            //
            // Bande morte a 0,75 contremarche avant de changer de niveau : sans elle, une volee
            // presque plate dont la spline ondule autour d'une frontiere de quantification
            // monterait et descendrait d'une marche en permanence.
            // Pas d'echantillonnage nettement plus court que le giron le plus serre qu'on
            // fabrique : le profil ne peut changer de niveau qu'aux abscisses testees, donc un
            // pas plus long que le giron fusionnerait des marches deux a deux. A 0,18 m de
            // contremarche sur la volee la plus raide de la ville (pente 0,64), le giron tombe a
            // 0,28 m -- l'ancien pas de 0,25 passait tout juste, celui-ci garde de la marge.
            int fine = Mathf.Clamp(Mathf.CeilToInt(len / 0.1f), 8, 8192);
            var pd = new List<float>();
            var py = new List<float>();
            float level = Quant(SampleY(spline, 0f, len), y0, riser);
            pd.Add(0f); py.Add(level);
            for (int i = 1; i <= fine; i++)
            {
                float d = len * i / fine;
                float raw = SampleY(spline, d, len);
                if (Mathf.Abs(raw - level) <= riser * 0.75f) continue;
                float q = Quant(raw, y0, riser);
                if (Mathf.Abs(q - level) < 1e-4f) continue;
                pd.Add(d); py.Add(level);   // fin de la marche
                pd.Add(d); py.Add(q);       // haut de la contremarche
                level = q;
            }
            pd.Add(len); py.Add(level);

            int np = pd.Count;
            var pos = new Vector3[np];
            var rgt = new Vector3[np];
            for (int p = 0; p < np; p++)
            {
                Frame f = FlatFrame(spline, pd[p], len);
                pos[p] = new Vector3(f.pos.x, py[p], f.pos.z);
                rgt[p] = f.right;
            }

            float hw = Mathf.Max(0.5f, halfWidth);
            float drop = Mathf.Max(0.1f, skirt);

            // 4 sommets par point de profil : dessus gauche/droit, puis pied de joue gauche/droit.
            var v = new Vector3[np * 4];
            var uv = new Vector2[np * 4];
            float run = 0f;
            for (int p = 0; p < np; p++)
            {
                if (p > 0) run += Vector3.Distance(pos[p - 1], pos[p]);
                Vector3 l = pos[p] - rgt[p] * hw, r = pos[p] + rgt[p] * hw;
                v[p * 4 + 0] = l;
                v[p * 4 + 1] = r;
                // Le bas des joues SUIT le profil au lieu de tomber sur un plancher commun : sur
                // une volee de 25 m de denivele, un plancher unique donnait une joue de 27 m de
                // haut au sommet de l'escalier -- un mur, pas un escalier.
                v[p * 4 + 2] = new Vector3(l.x, l.y - drop, l.z);
                v[p * 4 + 3] = new Vector3(r.x, r.y - drop, r.z);
                uv[p * 4 + 0] = new Vector2(0f, run) * 0.1f;
                uv[p * 4 + 1] = new Vector2(hw * 2f, run) * 0.1f;
                uv[p * 4 + 2] = new Vector2(0f, run) * 0.1f;
                uv[p * 4 + 3] = new Vector2(hw * 2f, run) * 0.1f;
            }

            var tris = new List<int>((np - 1) * 18);
            for (int p = 0; p + 1 < np; p++)
            {
                int a = p * 4, b = (p + 1) * 4;
                // dessus (marches ET contremarches : meme ruban, la contremarche est juste un
                // pas d'abscisse nul et un saut de hauteur)
                tris.Add(a + 0); tris.Add(b + 0); tris.Add(a + 1);
                tris.Add(a + 1); tris.Add(b + 0); tris.Add(b + 1);

                // DESSOUS. Sans lui la volee est une coque ouverte : vue d'en dessous -- depuis un
                // egout qui passe sous elle, depuis le bas d'un talus -- on traverse les marches
                // et on voit le ciel au travers. Il suit le meme profil en escalier que les pieds
                // de joue, donc les quads de contremarche existent aussi ici et il n'y a rien a
                // sauter : c'est ce qui rend le volume etanche.
                tris.Add(a + 2); tris.Add(a + 3); tris.Add(b + 2);
                tris.Add(a + 3); tris.Add(b + 3); tris.Add(b + 2);

                // Les joues, elles, SAUTENT les contremarches : deux points de profil au meme
                // endroit en plan y donnent un quad d'aire nulle. Invisible au rendu, mais c'est
                // la moitie des triangles de la joue qui partaient dans le MeshCollider, et
                // PhysX n'a rien a faire de triangles degeneres. Le volume ne fuit pas pour
                // autant : les deux tranches de joue voisines se CHEVAUCHENT en hauteur des que
                // la descente de joue depasse une contremarche, ce qui est le cas par contrat.
                float dxz = new Vector2(pos[p + 1].x - pos[p].x, pos[p + 1].z - pos[p].z).magnitude;
                if (dxz < 1e-4f) continue;

                // joue droite -> regarde vers +right
                tris.Add(a + 1); tris.Add(b + 1); tris.Add(a + 3);
                tris.Add(a + 3); tris.Add(b + 1); tris.Add(b + 3);
                // joue gauche -> regarde vers -right
                tris.Add(a + 0); tris.Add(a + 2); tris.Add(b + 0);
                tris.Add(b + 0); tris.Add(a + 2); tris.Add(b + 2);
            }

            // Bouchons de bout. Les deux extremites d'une volee sont en general enterrees, mais
            // "en general" ne suffit pas : le haut d'un escalier qui debouche sur un pont, ou le
            // bas d'un escalier qui plonge dans un egout, laisse la tranche a l'air libre.
            // 0/1/2/3 = dessus gauche, dessus droit, pied gauche, pied droit.
            int e = (np - 1) * 4;
            tris.Add(3); tris.Add(2); tris.Add(1);            // depart -> regarde vers -fwd
            tris.Add(1); tris.Add(2); tris.Add(0);
            tris.Add(e + 2); tris.Add(e + 3); tris.Add(e + 0);   // arrivee -> regarde vers +fwd
            tris.Add(e + 0); tris.Add(e + 3); tris.Add(e + 1);

            int[] tri = tris.ToArray();
            Unweld(ref v, ref uv, ref tri);
            return Commit(reuse, v, uv, tri);
        }

        // Dedouble les sommets, un jeu par triangle -> RecalculateNormals sort des normales PAR
        // FACE. Un escalier a besoin de ca : partager les sommets entre une marche et sa
        // contremarche arrondit le nez de marche, et entre la marche et la joue biseaute l'arete
        // -- mesure faite, la normale de joue sortait a 0.96 vers le haut au lieu d'etre
        // laterale. L'egout veut exactement l'inverse et garde donc ses sommets soudes.
        private static void Unweld(ref Vector3[] v, ref Vector2[] uv, ref int[] tris)
        {
            var nv = new Vector3[tris.Length];
            var nu = new Vector2[tris.Length];
            var nt = new int[tris.Length];
            for (int i = 0; i < tris.Length; i++)
            {
                nv[i] = v[tris[i]];
                nu[i] = uv[tris[i]];
                nt[i] = i;
            }
            v = nv; uv = nu; tris = nt;
        }

        private static float SampleY(Spline spline, float d, float len)
        {
            float tt = spline.ConvertIndexUnit(Mathf.Clamp(d, 0f, len), PathIndexUnit.Distance,
                                               PathIndexUnit.Normalized);
            spline.Evaluate(tt, out float3 p, out _, out _);
            return ((Vector3)p).y;
        }

        // Hauteur ramenee sur la grille des contremarches, calee sur le depart de la volee.
        private static float Quant(float y, float y0, float riser)
            => y0 + Mathf.Round((y - y0) / riser) * riser;

        // Repere HORIZONTAL a une abscisse donnee. Un escalier ne se devers pas : de la spline
        // on ne retient que le trace en plan.
        private static Frame FlatFrame(Spline spline, float d, float len)
        {
            float tt = spline.ConvertIndexUnit(Mathf.Clamp(d, 0f, len), PathIndexUnit.Distance,
                                               PathIndexUnit.Normalized);
            spline.Evaluate(tt, out float3 p, out float3 tan, out _);
            Vector3 fwd = (Vector3)tan;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-8f) fwd = Vector3.forward;
            fwd.Normalize();
            return new Frame
            {
                pos = (Vector3)p,
                fwd = fwd,
                up = Vector3.up,
                right = Vector3.Cross(Vector3.up, fwd)
            };
        }

        // Normales RECALCULEES et pas posees a la main : ces deux geometries-la sont courbes
        // (le U) ou en marches (l'escalier), et dans les deux cas la normale juste est celle du
        // maillage produit, pas une valeur qu'on pourrait deviner en amont.
        private static Mesh Commit(Mesh reuse, Vector3[] v, Vector2[] uv, int[] tris)
        {
            Mesh mesh = reuse != null ? reuse : new Mesh();
            mesh.Clear();
            mesh.indexFormat = v.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = v;
            mesh.uv = uv;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
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
