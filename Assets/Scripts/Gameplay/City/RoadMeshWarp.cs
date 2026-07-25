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
        public static Mesh Warp(in Tile tile, Spline spline, Mesh reuse, Deck deck = default(Deck),
                                Tile[] variants = null, int[] pick = null)
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
                    for (int i = 0; i < src.tris.Length; i++)
                        outTris[baseT + i] = src.tris[i] + baseV;

                baseV += src.verts.Length;
                baseT += src.tris.Length;
            }

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
            if (outTris != null) mesh.SetTriangles(outTris, 0, false);   // bounds faites juste apres
            mesh.RecalculateBounds();
            return mesh;
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
                         Mathf.Max(3.2f, lip));
        }

        const int WallSegments = 8;   // facettes par paroi du U -- 8 suffit a ne plus se voir

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
        // levre gauche a la levre droite : le fond est la corde entre les deux bas de paroi, un
        // seul quad, il est plat.
        private static Vector2[] ChannelSection(float halfWidth, float depth, float wall,
                                                float lip, int seg)
        {
            var pts = new List<Vector2>(seg * 2 + 4);
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
            return pts.ToArray();
        }

        // Balayage d'une section transversale le long des reperes. Les sommets sont PARTAGES
        // entre stations et entre points de section : RecalculateNormals lisse alors la courbe
        // du U au lieu de la facetter, ce qui est tout l'interet d'avoir mis 8 facettes dedans.
        private static Mesh Sweep(Mesh reuse, Frame[] st, Vector2[] sec, float len,
                                  bool capStart, bool capEnd, float apron)
        {
            int ns = st.Length, np = sec.Length;
            if (ns < 2 || np < 2) return null;

            // Les murs de fond ont leurs PROPRES sommets et ne reprennent pas ceux de la station
            // du bout : partages, RecalculateNormals moyennerait la normale du mur avec celle du
            // canal et biseauterait la bouche.
            // np pour le mur de fond + 4 pour son TABLIER, la levre horizontale qui prolonge la
            // bouche vers l'exterieur. Ce tablier n'est pas cosmetique : la grille du sol ne
            // garde une cellule que si ses QUATRE coins sont hors emprise, donc son bord recule
            // jusqu'a 2,8 m au-dela du plan de bouche pendant que le canal, lui, s'y arrete net.
            // L'anneau entre les deux etait un trou beant. Meme parade que sur les cotes, ou
            // c'est la levre des parois qui recoit le recouvrement.
            int capV = (capStart ? np + 4 : 0) + (capEnd ? np + 4 : 0);
            int capT = ((capStart ? 1 : 0) + (capEnd ? 1 : 0)) * ((np - 2) + 2) * 3;

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
            if (capStart) baseV = Cap(v, uv, tris, ref t, baseV, st[0], sec, s, apron, false);
            if (capEnd) Cap(v, uv, tris, ref t, baseV, st[ns - 1], sec, s, apron, true);

            return Commit(reuse, v, uv, tris);
        }

        // Mur de fond + son tablier. La section fermee par sa corde haute est convexe (courbe en
        // U sous une droite), donc un simple eventail suffit a la remplir -- pas besoin d'une
        // triangulation de polygone.
        private static int Cap(Vector3[] v, Vector2[] uv, int[] tris, ref int t, int baseV,
                               in Frame f, Vector2[] sec, float[] s, float apron, bool forward)
        {
            int np = sec.Length;
            for (int i = 0; i < np; i++)
            {
                v[baseV + i] = f.pos + f.right * sec[i].x + f.up * sec[i].y;
                uv[baseV + i] = new Vector2(s[i], sec[i].y) * 0.1f;
            }
            // right = cross(up, fwd) donne cross(right, up) = fwd : parcourue de gauche a droite,
            // la section rend un eventail tourne vers +fwd. Le mur du DEPART regarde en sens
            // inverse, on inverse donc son ordre.
            for (int i = 1; i + 1 < np; i++)
            {
                if (forward) { tris[t++] = baseV; tris[t++] = baseV + i; tris[t++] = baseV + i + 1; }
                else { tris[t++] = baseV; tris[t++] = baseV + i + 1; tris[t++] = baseV + i; }
            }

            // Tablier : rectangle plat au niveau de la levre, pose EN DEHORS de la bouche. Son
            // bord interieur epouse le haut du mur de fond, donc l'ensemble reste etanche.
            int a = baseV + np;
            Vector3 outward = f.fwd * (forward ? apron : -apron);
            float lo = sec[0].x, hi = sec[np - 1].x, y = sec[0].y;
            Vector3 inL = f.pos + f.right * lo + f.up * y;
            Vector3 inR = f.pos + f.right * hi + f.up * y;
            v[a + 0] = inL; v[a + 1] = inL + outward;
            v[a + 2] = inR; v[a + 3] = inR + outward;
            for (int i = 0; i < 4; i++)
                uv[a + i] = new Vector2(v[a + i].x, v[a + i].z) * 0.1f;

            // Meme regle de bobinage que le ruban principal : (interieur, exterieur, interieur+1)
            // regarde vers le haut quand on avance vers l'exterieur. Au depart, l'exterieur est
            // a -fwd, donc l'ordre s'inverse.
            if (forward)
            {
                tris[t++] = a + 0; tris[t++] = a + 1; tris[t++] = a + 2;
                tris[t++] = a + 2; tris[t++] = a + 1; tris[t++] = a + 3;
            }
            else
            {
                tris[t++] = a + 0; tris[t++] = a + 2; tris[t++] = a + 1;
                tris[t++] = a + 1; tris[t++] = a + 2; tris[t++] = a + 3;
            }
            return a + 4;
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
            int fine = Mathf.Clamp(Mathf.CeilToInt(len / 0.25f), 8, 4096);
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

                // Les joues, elles, SAUTENT les contremarches : deux points de profil au meme
                // endroit en plan y donnent un quad d'aire nulle. Invisible au rendu, mais c'est
                // la moitie des triangles de la joue qui partaient dans le MeshCollider, et
                // PhysX n'a rien a faire de triangles degeneres.
                float dxz = new Vector2(pos[p + 1].x - pos[p].x, pos[p + 1].z - pos[p].z).magnitude;
                if (dxz < 1e-4f) continue;

                // joue droite -> regarde vers +right
                tris.Add(a + 1); tris.Add(b + 1); tris.Add(a + 3);
                tris.Add(a + 3); tris.Add(b + 1); tris.Add(b + 3);
                // joue gauche -> regarde vers -right
                tris.Add(a + 0); tris.Add(a + 2); tris.Add(b + 0);
                tris.Add(b + 0); tris.Add(a + 2); tris.Add(b + 2);
            }

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
