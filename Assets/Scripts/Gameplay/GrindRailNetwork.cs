using System.Collections.Generic;
using UnityEngine;

namespace Gameplay
{
    // Reseau de rails grindables BAKE (voir GrindRailBaker). Stocke des polylignes (chemins)
    // en dur -> a l'execution, ZERO detection : on attache la moto au chemin le plus proche et
    // on la fait glisser dessus par arc-length. C'est ca qui rend le grind robuste (chemin
    // deterministe connu d'avance, contrairement a une detection par-frame qui saute).
    //
    // Query spatiale via grille XZ (cheap meme avec des milliers de segments en ville).
    public class GrindRailNetwork : MonoBehaviour
    {
        [System.Serializable]
        public class Path { public Vector3[] points; }

        [SerializeField] public List<Path> paths = new List<Path>();
        [SerializeField] private float cellSize = 4f; // taille de cellule de la grille de lookup

        // --- index runtime ---
        private struct Seg { public Vector3 a, b; public int path; public float sStart; public float len; }
        private List<Seg> segs;
        private float[] pathLen;
        private Dictionary<Vector2Int, List<int>> grid;

        private void Awake() => Build();

        // (Re)construit l'index a partir des polylignes. Appele au bake (editeur) et au Awake.
        public void Build()
        {
            segs = new List<Seg>();
            grid = new Dictionary<Vector2Int, List<int>>();
            pathLen = new float[paths.Count];
            for (int p = 0; p < paths.Count; p++)
            {
                var pts = paths[p].points;
                if (pts == null || pts.Length < 2) continue;
                float s = 0f;
                for (int i = 0; i < pts.Length - 1; i++)
                {
                    float len = Vector3.Distance(pts[i], pts[i + 1]);
                    if (len < 1e-4f) continue;
                    int idx = segs.Count;
                    segs.Add(new Seg { a = pts[i], b = pts[i + 1], path = p, sStart = s, len = len });
                    RasterizeToGrid(pts[i], pts[i + 1], idx);
                    s += len;
                }
                pathLen[p] = s;
            }
        }

        // Ajoute une polyligne a chaud (utilise par GrindRail, les rails poses a la main).
        public void AddPath(Vector3[] points)
        {
            if (points == null || points.Length < 2) return;
            paths.Add(new Path { points = points });
            Build();
        }

        private void RasterizeToGrid(Vector3 a, Vector3 b, int segIdx)
        {
            float d = Vector3.Distance(a, b);
            int steps = Mathf.Max(1, Mathf.CeilToInt(d / cellSize));
            for (int i = 0; i <= steps; i++)
            {
                Vector3 pt = Vector3.Lerp(a, b, i / (float)steps);
                var c = Cell(pt);
                if (!grid.TryGetValue(c, out var list)) { list = new List<int>(); grid[c] = list; }
                if (!list.Contains(segIdx)) list.Add(segIdx);
            }
        }

        private Vector2Int Cell(Vector3 p) =>
            new Vector2Int(Mathf.FloorToInt(p.x / cellSize), Mathf.FloorToInt(p.z / cellSize));

        // Point le plus proche du reseau dans maxDist. Renvoie le chemin, l'arc-length s, le point
        // projete et la tangente (direction du segment). Cherche seulement les segments des cellules
        // autour de pos -> O(1) amorti.
        public bool QueryNearest(Vector3 pos, float maxDist, out int path, out float s, out Vector3 point, out Vector3 tangent)
        {
            path = -1; s = 0f; point = pos; tangent = Vector3.forward;
            if (segs == null) Build();
            float best = maxDist * maxDist;
            bool found = false;
            int r = Mathf.CeilToInt(maxDist / cellSize);
            Vector2Int c0 = Cell(pos);
            var seen = new HashSet<int>();
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    if (!grid.TryGetValue(new Vector2Int(c0.x + dx, c0.y + dz), out var list)) continue;
                    foreach (int si in list)
                    {
                        if (!seen.Add(si)) continue;
                        var seg = segs[si];
                        Vector3 pp = ClosestOnSeg(pos, seg.a, seg.b, out float t);
                        float d2 = (pp - pos).sqrMagnitude;
                        if (d2 < best)
                        {
                            best = d2; found = true;
                            path = seg.path; point = pp; tangent = (seg.b - seg.a).normalized;
                            s = seg.sStart + t * seg.len;
                        }
                    }
                }
            return found;
        }

        // Position + tangente a l'arc-length s sur un chemin. false si s hors [0,longueur] (bout de rail).
        public bool Sample(int path, float s, out Vector3 point, out Vector3 tangent)
        {
            point = Vector3.zero; tangent = Vector3.forward;
            if (path < 0 || path >= paths.Count) return false;
            if (s < 0f || s > pathLen[path]) return false;
            var pts = paths[path].points;
            float acc = 0f;
            for (int i = 0; i < pts.Length - 1; i++)
            {
                float len = Vector3.Distance(pts[i], pts[i + 1]);
                if (len < 1e-4f) continue;
                if (s <= acc + len)
                {
                    float t = (s - acc) / len;
                    point = Vector3.Lerp(pts[i], pts[i + 1], t);
                    tangent = (pts[i + 1] - pts[i]).normalized;
                    return true;
                }
                acc += len;
            }
            return false;
        }

        public float Length(int path) => (path >= 0 && path < pathLen.Length) ? pathLen[path] : 0f;

        private static Vector3 ClosestOnSeg(Vector3 p, Vector3 a, Vector3 b, out float t)
        {
            Vector3 ab = b - a;
            float d = ab.sqrMagnitude;
            t = d < 1e-6f ? 0f : Mathf.Clamp01(Vector3.Dot(p - a, ab) / d);
            return a + ab * t;
        }

        // Gizmos : dessine tous les rails bakes. Cyan = ligne, points aux sommets, boules aux bouts.
        private void OnDrawGizmos()
        {
            if (paths == null) return;
            foreach (var p in paths)
            {
                if (p?.points == null || p.points.Length < 2) continue;
                Gizmos.color = Color.cyan;
                for (int i = 0; i < p.points.Length - 1; i++)
                    Gizmos.DrawLine(p.points[i], p.points[i + 1]);
                Gizmos.color = new Color(1f, 0.5f, 0f);
                Gizmos.DrawSphere(p.points[0], 0.12f);
                Gizmos.DrawSphere(p.points[p.points.Length - 1], 0.12f);
            }
        }
    }
}
