using System.Collections.Generic;
using UnityEngine;

namespace Gameplay
{
    // Deux services sans rapport de parente, reunis parce qu'ils parlent tous deux de la
    // fabrication d'une polyligne de rail. Lire ce qui suit avant de rappeler Extract.
    //
    // 1. JoinAdjacent -- RECOLLAGE de rails qui se touchent. Appele a chaque Build du reseau,
    //    quelle que soit la provenance des rails. C'est lui qui fait qu'une file de rambardes,
    //    chacune apportant son bout de 1.67 m, devient une ligne continue au lieu de faire
    //    decrocher la moto a chaque element.
    //
    // 2. Extract -- EXTRACTION des aretes grindables d'un lot de meshes, par geometrie exacte
    //    (angle diedre entre faces adjacentes). ATTENTION : cette voie a ete retiree du chemin
    //    automatique. Lachee sur une scene entiere elle rend grindable tout ce qui n'a rien
    //    demande -- marquages au sol, joints de dallage -- et il en restait 399 parasites apres
    //    trois etages de filtres. Le baker plein-scene qui l'appelait a ete supprime pour ca.
    //
    //    Elle survit comme OUTIL D'EDITEUR ponctuel, derriere le bouton "Figer les rails" d'un
    //    GrindRailSource : un objet a la fois, sur demande, avec le resultat visible au gizmo et
    //    corrigeable avant d'etre serialise. A cette echelle l'heuristique est verifiable, et le
    //    cout aussi (une rambarde ~50 triangles est gratuite, la scene Game 63 k -> 2841 ms).
    //    Ne la rebranche pas sur un balayage automatique.
    //
    // Une arete est grindable si : partagee par 2 faces formant un angle marque (pas un plat),
    // ~horizontale, et c'est un REBORD DU HAUT (rien ne remonte au-dessus d'elle = on peut s'y
    // poser). Les coins concaves (mur/sol), les aretes verticales et les micro-aretes sont
    // rejetes.
    public static class GrindRailExtractor
    {
        // --- filtres (stables, pas par-frame) ---
        const float Weld = 0.01f;          // fusion des sommets a la meme position (m)
        const float MinLen = 0.4f;         // arete plus courte -> ignoree
        const float HorizMax = 0.72f;      // |dir.y| au-dela -> trop pentu (ignore). 0.72 = jusqu'a ~45deg : rampes + rambardes d'escalier grindables (un rail penche reste un rail)
        const float DihedralMin = 22f;     // angle mini entre faces (deg) : sous ca = surface plate
        const float TopTol = 0.05f;        // un sommet voisin au-dessus de l'arete de + que ca -> pas un rebord du haut
        const float MinStep = 0.06f;       // marche mini sous l'arete : bordure 0.27, peinture 0.02
        const float ChainAngle = 35f;      // continuite pour chainer 2 aretes en une polyligne (deg)
        const float SimplifyAngle = 6f;    // fusionne les sommets quasi-alignes d'une polyligne (deg)

        // Le trottoir du kit est DALLE : un joint transversal de 2 cm tous les 3.9 m. La
        // bordure est donc geometriquement interrompue a chaque dalle, la soudure de 1 cm ne
        // franchit pas le joint, et une rue de 70 m ressort en 18 rails de 3.9 m. Le grind
        // decroche a chaque dalle -- c'est le vrai defaut, il existait avant la decoupe des
        // tuiles et n'a rien a voir avec elle.
        //
        // On recolle donc deux rails dont les extremites se touchent a JoinGap pres, a trois
        // conditions d'alignement : les deux directions entre elles, et chacune avec le trou.
        // C'est cette derniere qui empeche de coudre deux bordures PARALLELES separees de
        // quelques centimetres -- leur trou est perpendiculaire a leur direction.
        const float JoinGap = 0.35f;       // trou max comble (m). Un joint de dalle fait 0.02.

        // Rails extraits du lot de meshes fourni, en coordonnees MONDE.
        //
        // `minRail` est la longueur mini d'un rail UNE FOIS CHAINE, et le filtre qui compte : la
        // deformation de route decoupe la bordure tous les ~1 m, donc un trottoir arrive ici en
        // aretes aussi courtes qu'un pointille de ligne blanche. Ce qui les separe, c'est qu'une
        // bordure CHAINE sur des dizaines de metres et qu'un pointille reste seul a 0.8 m. Sur un
        // objet isole il n'y a pas de bruit a trier et on peut descendre a la taille d'une
        // rambarde (1.67 m) ; c'est a l'echelle d'une scene qu'aucun reglage ne suffisait.
        //
        // `skipped` compte les meshes ignores faute d'etre lisibles : c'est le piege silencieux de
        // cet algo (un FBX importe sans isReadable ne produit AUCUN rail et rien ne le signale),
        // l'appelant doit pouvoir le dire a l'utilisateur.
        public static List<GrindRailNetwork.Path> Extract(IEnumerable<MeshFilter> meshes,
                                                          float minRail,
                                                          out int scanned, out int skipped)
        {
            var edgeMap = new Dictionary<long, Edge>();
            var weldIds = new Dictionary<Vector3Int, int>();
            int nextId = 0;
            scanned = 0;
            skipped = 0;

            foreach (var mf in meshes)
            {
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                if (!mesh.isReadable) { skipped++; continue; }

                var tf = mf.transform;
                var verts = mesh.vertices;
                var tris = mesh.triangles;
                // pre-transforme en monde
                var w = new Vector3[verts.Length];
                for (int i = 0; i < verts.Length; i++) w[i] = tf.TransformPoint(verts[i]);

                for (int t = 0; t < tris.Length; t += 3)
                {
                    Vector3 p0 = w[tris[t]], p1 = w[tris[t + 1]], p2 = w[tris[t + 2]];
                    Vector3 n = Vector3.Cross(p1 - p0, p2 - p0);
                    if (n.sqrMagnitude < 1e-10f) continue; // triangle degenere
                    n.Normalize();
                    int id0 = WeldId(p0, weldIds, ref nextId);
                    int id1 = WeldId(p1, weldIds, ref nextId);
                    int id2 = WeldId(p2, weldIds, ref nextId);
                    AddEdge(edgeMap, id0, id1, p0, p1, p2, n);
                    AddEdge(edgeMap, id1, id2, p1, p2, p0, n);
                    AddEdge(edgeMap, id2, id0, p2, p0, p1, n);
                }
                scanned++;
            }

            // filtre -> segments grindables
            var segments = new List<(Vector3 a, Vector3 b)>();
            foreach (var e in edgeMap.Values)
            {
                Vector3 dir = e.b - e.a;
                float len = dir.magnitude;
                if (len < MinLen) continue;
                dir /= len;
                if (Mathf.Abs(dir.y) > HorizMax) continue;                 // pas trop pentu

                // REBORD DU HAUT : aucun sommet voisin ne remonte au-dessus du rail. On compare a la
                // hauteur du rail SOUS ce sommet (interpolee) et non au min global -> correct meme pour
                // un rail PENCHE (rambarde), sinon un cote passait et l'autre etait rejete selon la
                // triangulation.
                Vector3 ab = e.b - e.a;
                float abLen2 = ab.sqrMagnitude;
                float maxRel = float.NegativeInfinity, minRel = float.PositiveInfinity;
                foreach (var w in e.wings)
                {
                    float t = abLen2 < 1e-8f ? 0f : Mathf.Clamp01(Vector3.Dot(w - e.a, ab) / abLen2);
                    float rel = w.y - (e.a.y + (e.b.y - e.a.y) * t); // hauteur voisin RELATIVE au rail
                    maxRel = Mathf.Max(maxRel, rel); minRel = Mathf.Min(minRel, rel);
                }
                if (maxRel > TopTol) continue;

                // Il faut une vraie MARCHE sous l'arete. Sans ca, le marquage peint est un
                // rebord du haut parfaitement valide : il depasse de 2 cm, rien au-dessus,
                // angle vif sur les bords. Une bordure de trottoir plonge de 27 cm, un
                // pointille de 2 -- le seuil passe franchement entre les deux. C'est ce qui
                // evite de grinder sur la peinture au milieu d'un carrefour.
                if (minRel > -MinStep) continue;

                // VRAIE ARETE : soit un pli marque entre 2 faces (on prend le MEILLEUR couple, sinon
                // un couple coplanaire malchanceux masquait l'arete), soit une arete de BORD (1 seule
                // face) dont la face PLONGE sous l'arete (rampe/plan a une face -> le haut est grindable).
                bool crease = false;
                int m = e.normals.Count;
                if (m >= 2)
                {
                    for (int i = 0; i < m && !crease; i++)
                        for (int j = i + 1; j < m && !crease; j++)
                            if (Vector3.Angle(e.normals[i], e.normals[j]) >= DihedralMin) crease = true;
                }
                else
                {
                    crease = minRel < -TopTol; // bord ouvert (rampe/plan 1 face) : la face plonge -> rebord haut
                }
                if (!crease) continue;
                segments.Add((e.a, e.b));
            }

            return Chain(segments, minRail);
        }

        private static int WeldId(Vector3 p, Dictionary<Vector3Int, int> map, ref int next)
        {
            var key = new Vector3Int(
                Mathf.RoundToInt(p.x / Weld),
                Mathf.RoundToInt(p.y / Weld),
                Mathf.RoundToInt(p.z / Weld));
            if (!map.TryGetValue(key, out int id)) { id = next++; map[key] = id; }
            return id;
        }

        private class Edge
        {
            public Vector3 a, b;                                          // positions monde de l'arete
            public readonly List<Vector3> normals = new List<Vector3>();  // normale de CHAQUE face adjacente
            public readonly List<Vector3> wings = new List<Vector3>();    // 3e sommet de chaque face adjacente
        }

        private static void AddEdge(Dictionary<long, Edge> map, int i, int j,
            Vector3 pi, Vector3 pj, Vector3 wing, Vector3 n)
        {
            long key = i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i;
            if (!map.TryGetValue(key, out var e)) { e = new Edge { a = pi, b = pj }; map[key] = e; }
            e.normals.Add(n); e.wings.Add(wing);
        }

        // Chaine les segments contigus + ~colineaires en polylignes, puis simplifie.
        private static List<GrindRailNetwork.Path> Chain(List<(Vector3 a, Vector3 b)> segs, float minRail)
        {
            var result = new List<GrindRailNetwork.Path>();
            int n = segs.Count;
            var used = new bool[n];
            // adjacence par endpoint welde
            var byPoint = new Dictionary<Vector3Int, List<int>>();
            Vector3Int K(Vector3 p) => new Vector3Int(
                Mathf.RoundToInt(p.x / Weld), Mathf.RoundToInt(p.y / Weld), Mathf.RoundToInt(p.z / Weld));
            void Reg(Vector3 p, int idx)
            {
                var k = K(p);
                if (!byPoint.TryGetValue(k, out var l)) { l = new List<int>(); byPoint[k] = l; }
                l.Add(idx);
            }
            for (int i = 0; i < n; i++) { Reg(segs[i].a, i); Reg(segs[i].b, i); }

            for (int i = 0; i < n; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                var pts = new LinkedList<Vector3>();
                pts.AddLast(segs[i].a); pts.AddLast(segs[i].b);
                ExtendEnd(pts, true, segs, used, byPoint, K);   // etend cote fin
                ExtendEnd(pts, false, segs, used, byPoint, K);  // etend cote debut
                var arr = Simplify(new List<Vector3>(pts));
                if (arr.Count >= 2) result.Add(new GrindRailNetwork.Path { points = arr.ToArray() });
            }

            // Le filtre de longueur passe APRES le recollage : un morceau de 2 m isole est du
            // bruit, mais le meme morceau recolle a ses voisins fait une bordure de 70 m.
            var joined = new List<GrindRailNetwork.Path>();
            foreach (var p in Join(result))
                if (Length(p.points) >= minRail) joined.Add(p);
            return joined;
        }

        // Recolle les rails dont les extremites se touchent. Public parce que le recollage sert
        // DEUX FOIS, et la seconde est la plus importante : les rails d'une rue arrivent au
        // reseau prop par prop (chaque prefab apporte le sien, extrait a vide), donc c'est au
        // niveau du RESEAU qu'une file de rambardes redevient une ligne continue. Sans cette
        // passe, la moto decrocherait tous les 1.67 m.
        public static List<GrindRailNetwork.Path> JoinAdjacent(List<GrindRailNetwork.Path> rails)
        {
            // Le recollage lit points[0], points[len-1] et leurs voisins immediats : une
            // polyligne vide ou a un seul point le ferait sortir des bornes. En interne l'entree
            // vient de Chain et respecte ca par construction, mais ici elle vient de l'exterieur.
            var clean = new List<GrindRailNetwork.Path>(rails.Count);
            foreach (var p in rails)
                if (p?.points != null && p.points.Length >= 2) clean.Add(p);
            return clean.Count == 0 ? clean : Join(clean);
        }

        private static List<GrindRailNetwork.Path> Join(List<GrindRailNetwork.Path> rails)
        {
            int n = rails.Count;
            var used = new bool[n];
            var byCell = new Dictionary<Vector3Int, List<int>>();
            Vector3Int C(Vector3 p) => new Vector3Int(
                Mathf.FloorToInt(p.x / JoinGap), Mathf.FloorToInt(p.y / JoinGap), Mathf.FloorToInt(p.z / JoinGap));
            void Reg(Vector3 p, int idx)
            {
                var k = C(p);
                if (!byCell.TryGetValue(k, out var l)) { l = new List<int>(); byCell[k] = l; }
                l.Add(idx);
            }
            for (int i = 0; i < n; i++)
            {
                Reg(rails[i].points[0], i);
                Reg(rails[i].points[rails[i].points.Length - 1], i);
            }

            var result = new List<GrindRailNetwork.Path>();
            for (int i = 0; i < n; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                var pts = new List<Vector3>(rails[i].points);
                ExtendRail(pts, rails, used, byCell, C);
                pts.Reverse();
                ExtendRail(pts, rails, used, byCell, C);
                result.Add(new GrindRailNetwork.Path { points = pts.ToArray() });
            }
            return result;
        }

        // Prolonge `pts` par son extremite de fin, tant qu'un rail libre s'y raccorde.
        private static void ExtendRail(List<Vector3> pts, List<GrindRailNetwork.Path> rails,
            bool[] used, Dictionary<Vector3Int, List<int>> byCell,
            System.Func<Vector3, Vector3Int> C)
        {
            while (true)
            {
                Vector3 end = pts[pts.Count - 1];
                Vector3 dir = (end - pts[pts.Count - 2]).normalized;
                var cell = C(end);
                int best = -1; bool bestReversed = false; float bestScore = ChainAngle;

                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!byCell.TryGetValue(cell + new Vector3Int(dx, dy, dz), out var list)) continue;
                    foreach (int k in list)
                    {
                        if (used[k]) continue;
                        var q = rails[k].points;
                        for (int side = 0; side < 2; side++)
                        {
                            Vector3 head = side == 0 ? q[0] : q[q.Length - 1];
                            Vector3 nextPt = side == 0 ? q[1] : q[q.Length - 2];
                            Vector3 gap = head - end;
                            if (gap.magnitude > JoinGap) continue;

                            Vector3 outDir = (head - nextPt).normalized;   // direction du rail candidat
                            float a = Vector3.Angle(dir, -outDir);
                            if (a >= bestScore) continue;
                            // le trou lui-meme doit suivre la direction : sinon on coud deux
                            // bordures paralleles cote a cote.
                            if (gap.sqrMagnitude > 1e-6f && Vector3.Angle(dir, gap.normalized) > ChainAngle) continue;

                            bestScore = a; best = k; bestReversed = side != 0;
                        }
                    }
                }

                if (best < 0) return;
                used[best] = true;
                var add = new List<Vector3>(rails[best].points);
                if (bestReversed) add.Reverse();
                pts.AddRange(add);
            }
        }

        private static float Length(IReadOnlyList<Vector3> pts)
        {
            float s = 0f;
            for (int i = 0; i < pts.Count - 1; i++) s += Vector3.Distance(pts[i], pts[i + 1]);
            return s;
        }

        // Etend la polyligne depuis une extremite en accrochant un segment contigu + ~aligne.
        private static void ExtendEnd(LinkedList<Vector3> pts, bool fromLast,
            List<(Vector3 a, Vector3 b)> segs, bool[] used,
            Dictionary<Vector3Int, List<int>> byPoint, System.Func<Vector3, Vector3Int> K)
        {
            while (true)
            {
                Vector3 tip = fromLast ? pts.Last.Value : pts.First.Value;
                Vector3 prev = fromLast ? pts.Last.Previous.Value : pts.First.Next.Value;
                Vector3 curDir = (tip - prev).normalized;
                if (!byPoint.TryGetValue(K(tip), out var cand)) return;
                int pick = -1; Vector3 nextPt = Vector3.zero; float bestAngle = ChainAngle;
                Vector3Int kt = K(tip);
                foreach (int si in cand)
                {
                    if (used[si]) continue;
                    // quelle extremite du segment coincide avec le tip ?
                    Vector3 other;
                    if (K(segs[si].a) == kt) other = segs[si].b;
                    else if (K(segs[si].b) == kt) other = segs[si].a;
                    else continue;
                    float ang = Vector3.Angle(curDir, (other - tip).normalized);
                    if (ang < bestAngle) { bestAngle = ang; pick = si; nextPt = other; }
                }
                if (pick < 0) return;
                used[pick] = true;
                if (fromLast) pts.AddLast(nextPt); else pts.AddFirst(nextPt);
            }
        }

        // Vire les sommets quasi-alignes (reduit le nombre de points).
        private static List<Vector3> Simplify(List<Vector3> pts)
        {
            if (pts.Count <= 2) return pts;
            var outp = new List<Vector3> { pts[0] };
            for (int i = 1; i < pts.Count - 1; i++)
            {
                Vector3 d0 = (pts[i] - outp[outp.Count - 1]).normalized;
                Vector3 d1 = (pts[i + 1] - pts[i]).normalized;
                if (Vector3.Angle(d0, d1) > SimplifyAngle) outp.Add(pts[i]);
            }
            outp.Add(pts[pts.Count - 1]);
            return outp;
        }
    }
}
