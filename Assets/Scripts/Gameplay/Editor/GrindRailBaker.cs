using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEditor.SceneManagement;

namespace Gameplay.EditorTools
{
    // BAKE des rails grindables : scanne tous les meshes de la scene, extrait les ARETES
    // grindables par geometrie exacte (angle diedre entre faces adjacentes), les chaine en
    // polylignes, et ecrit le tout dans un GrindRailNetwork. Une seule passe editeur -> rien a
    // calculer au runtime. Menu : Tools/Grind/Bake Rails.
    //
    // Une arete est grindable si : partagee par 2 faces formant un angle marque (pas un plat),
    // ~horizontale, et c'est un REBORD DU HAUT (rien ne remonte au-dessus d'elle = on peut s'y
    // poser). Les coins concaves (mur/sol), les aretes verticales et les micro-aretes sont rejetes.
    public static class GrindRailBaker
    {
        // --- filtres (tuning au bake, stable, pas par-frame) ---
        const float Weld = 0.01f;          // fusion des sommets a la meme position (m)
        const float MinLen = 0.4f;         // arete plus courte -> ignoree
        const float HorizMax = 0.72f;      // |dir.y| au-dela -> trop pentu (ignore). 0.72 = jusqu'a ~45deg : rampes + rambardes d'escalier grindables (un rail penche reste un rail)
        const float DihedralMin = 22f;     // angle mini entre faces (deg) : sous ca = surface plate
        const float TopTol = 0.05f;        // un sommet voisin au-dessus de l'arete de + que ca -> pas un rebord du haut
        const float ChainAngle = 35f;      // continuite pour chainer 2 aretes en une polyligne (deg)
        const float SimplifyAngle = 6f;    // fusionne les sommets quasi-alignes d'une polyligne (deg)

        [MenuItem("Tools/Grind/Bake Rails")]
        public static void Bake()
        {
            var edgeMap = new Dictionary<long, Edge>();
            var weldIds = new Dictionary<Vector3Int, int>();
            int nextId = 0;

            int meshCount = 0;
            foreach (var mf in Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
            {
                var mesh = mf.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
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
                meshCount++;
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

            var paths = Chain(segments);

            // ecrit dans le reseau (cree le GO si absent)
            var net = Object.FindFirstObjectByType<GrindRailNetwork>();
            if (net == null)
                net = new GameObject("GrindRails").AddComponent<GrindRailNetwork>();
            Undo.RecordObject(net, "Bake Grind Rails");
            net.paths = paths;
            net.Build();
            EditorUtility.SetDirty(net);
            EditorSceneManager.MarkSceneDirty(net.gameObject.scene);

            Debug.Log($"[GrindRailBaker] {meshCount} meshes scannes -> {segments.Count} aretes -> {paths.Count} rails.");
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
        private static List<GrindRailNetwork.Path> Chain(List<(Vector3 a, Vector3 b)> segs)
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
            return result;
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
