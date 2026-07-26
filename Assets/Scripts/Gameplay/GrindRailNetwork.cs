using System.Collections.Generic;
using UnityEngine;

namespace Gameplay
{
    // Reseau de rails grindables. Stocke des polylignes -> a l'execution, ZERO detection : on
    // attache la moto au chemin le plus proche et on la fait glisser dessus par arc-length. C'est
    // ca qui rend le grind robuste (chemin deterministe connu d'avance, contrairement a une
    // detection par-frame qui saute).
    //
    // Trois sources, toutes DECLAREES et non devinees : les rails poses a la main (GrindRail), les
    // rambardes d'ouvrage (RoadNetwork, qui connait exactement ses splines) et les props qui
    // portent un GrindRailSource (rails figes sur le prefab). Il a existe un baker qui extrayait
    // les aretes de tous les meshes de la scene : il rendait grindable un tas de choses qui
    // n'avaient rien demande (marquages au sol, joints de dallage) et demandait un rebake apres
    // chaque edition.
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
        private bool dirty = true;

        // Polylignes REELLEMENT indexees : `paths` recollees bout a bout. Liste separee et non
        // une reecriture de `paths`, pour deux raisons : `paths` est de la donnee d'auteur
        // serialisee, la reecrire depuis Build la salirait a chaque entree en play mode ; et le
        // recollage doit repartir des morceaux d'origine a chaque fois qu'un emetteur s'ajoute,
        // pas d'un resultat deja recolle.
        private List<Path> active;

        // Reseau unique de la scene, cree au besoin. Cache statique parce que les emetteurs
        // s'enregistrent tous pendant le meme Awake : un FindAnyObjectByType chacun rendait
        // l'enregistrement quadratique des qu'une rue portait quelques centaines de rambardes.
        // La comparaison a null passe par la surcharge Unity -> un objet detruit (changement de
        // scene, domain reload) est vu comme null et l'instance est recreee.
        private static GrindRailNetwork instance;

        public static GrindRailNetwork Instance
        {
            get
            {
                if (instance == null) instance = FindAnyObjectByType<GrindRailNetwork>();
                if (instance == null) instance = new GameObject("GrindRails").AddComponent<GrindRailNetwork>();
                return instance;
            }
        }

        private void Awake()
        {
            instance = this;
            dirty = true;
        }

        // (Re)construit l'index a partir des polylignes. Appele au bake (editeur), et sinon
        // PARESSEUSEMENT a la premiere requete : tous les emetteurs se declarent pendant le
        // meme Awake, reconstruire a chaque ajout coutait un index complet par rail.
        public void Build()
        {
            dirty = false;

            // Recollage AVANT indexation. C'est ce qui rattrape le fait que les rails arrivent
            // en morceaux : une file de rambardes, chacune apportant son bout de 1.67 m, ressort
            // d'ici en une seule ligne continue -- sinon la moto decrocherait a chaque element.
            // Le cout porte sur les EXTREMITES des rails, pas sur des triangles.
            active = GrindRailExtractor.JoinAdjacent(paths);

            segs = new List<Seg>();
            grid = new Dictionary<Vector2Int, List<int>>();
            pathLen = new float[active.Count];
            for (int p = 0; p < active.Count; p++)
            {
                var pts = active[p].points;
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

        // Ajoute une polyligne a chaud (GrindRail, GrindRailSource). L'index n'est PAS rebati
        // ici : il l'est a la premiere requete, une fois tout le monde enregistre.
        public void AddPath(Vector3[] points)
        {
            if (points == null || points.Length < 2) return;
            paths.Add(new Path { points = points });
            dirty = true;
        }

        public void AddPaths(List<Path> more)
        {
            if (more == null) return;
            foreach (var p in more)
                if (p?.points != null && p.points.Length >= 2) { paths.Add(p); dirty = true; }
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
            EnsureIndex();
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

        // L'index est reconstruit paresseusement, donc TOUT lecteur de pathLen doit passer par
        // ici : sans ca, une requete arrivant apres un AddPath et avant le prochain Build lisait
        // un pathLen plus court que paths et sortait des bornes.
        private void EnsureIndex()
        {
            if (segs == null || dirty) Build();
        }

        // Position + tangente a l'arc-length s sur un chemin. false si s hors [0,longueur] (bout de rail).
        public bool Sample(int path, float s, out Vector3 point, out Vector3 tangent)
        {
            point = Vector3.zero; tangent = Vector3.forward;
            EnsureIndex();
            if (path < 0 || path >= active.Count) return false;
            if (s < 0f || s > pathLen[path]) return false;
            var pts = active[path].points;
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

        public float Length(int path)
        {
            EnsureIndex();
            return (path >= 0 && path < pathLen.Length) ? pathLen[path] : 0f;
        }

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
            // En play mode on dessine les rails RECOLLES : c'est la seule facon de voir d'un coup
            // d'oeil si une file de rambardes forme bien une ligne unique ou une suite de bouts.
            var shown = active ?? paths;
            if (shown == null) return;
            foreach (var p in shown)
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
