using System.Collections.Generic;
using UnityEngine;

namespace Gameplay.City
{
    // Construit la ville depuis la CityGrid (meme GameObject) avec les VRAIS assets
    // (Assets/Models/City : tileset route + batiments).
    //   Road    -> tuile choisie par bitmask des 4 voisins (droite/virage/T/croix),
    //              scalee NON-UNIFORME pour remplir exactement la cellule -> le reseau
    //              se connecte bord a bord (pas de trou).
    //   Block   -> les cellules Block contigues forment un PATE (flood-fill) ; UN
    //              batiment par pate, centre, scale pour remplir le pate -> blocs nets.
    //   Special -> laisse vide (lot/plaza pour hand-author l'enigme).
    // Regenerable : "Build City" detruit l'ancien conteneur et refait tout.
    [RequireComponent(typeof(CityGridAuthoring))]
    public class CityBuilder : MonoBehaviour
    {
        [Header("Dossiers assets")]
        [SerializeField] private string streetDir = "Assets/Models/City/Street/";

        [Header("Offsets d'orientation tuiles (deg, tune au besoin)")]
        [SerializeField] private float straightOffset = 0f;
        // Bases lues (yaw0) : Corner connecte S|W, T connecte N|S|W (manque E) -> +180 recale.
        [SerializeField] private float cornerOffset = 180f;
        [SerializeField] private float tOffset = 180f;

        [Header("Batiments")]
        [SerializeField] private float buildingFill = 0.9f; // part du pate occupee
        [SerializeField] private float buildingFront = 0f;  // offset yaw si la façade regarde mal

        [Header("Sol")]
        [SerializeField] private Color groundColor = new Color(0.45f, 0.55f, 0.35f);

        [Header("Rendu toon")]
        [Tooltip("Remplace les materiaux des assets par GMTK/ToonLit (halftone/ombres BD).")]
        [SerializeField] private bool toonShading = true;

        private Shader toonShader;
        private readonly Dictionary<Material, Material> toonCache = new Dictionary<Material, Material>();

        [Header("Vie & rampes")]
        [SerializeField] private bool placeRamps = true;
        [SerializeField] private int rampEvery = 5;        // 1 rampe toutes les N cellules droites
        [SerializeField] private int trafficCount = 30;    // voitures PNJ
        [SerializeField] private int pedestrianCount = 60; // pietons
        [SerializeField] private int pigeonCount = 14;     // oiseaux

        private const string RampPath = "Assets/Prefabs/Ramp.prefab";
        private const string TrafficPath = "Assets/Prefabs/TrafficCar.prefab";
        private const string PedPath = "Assets/Prefabs/Pedestrian.prefab";
        private const string PigeonPath = "Assets/Prefabs/Pigeon.prefab";

        [Header("Organicite")]
        [Tooltip("Familles selon la zone (centre = tours, peripherie = maisons basses).")]
        [SerializeField] private bool zoning = true;
        [SerializeField, Range(0f, 0.5f)] private float emptyLotChance = 0.05f; // lots vides (parcs)
        [SerializeField, Range(0f, 0.5f)] private float footprintJitter = 0.25f; // ± largeur
        [SerializeField, Range(0f, 0.4f)] private float setbackJitter = 0.18f;   // decalage dans la cellule
        [SerializeField, Range(0f, 15f)] private float rotationJitter = 5f;      // deg
        [SerializeField, Range(0f, 0.5f)] private float heightJitter = 0.3f;     // (inutilise)
        [Tooltip("Hauteur d'un etage (unites). Cale les proportions : porte ~= perso (1.3).")]
        [SerializeField] private float floorUnit = 3.0f;

        private static readonly string[] Families =
        {
            "Building_Apartment_", "Shop_", "Building_Townhouse_", "Modern_"
        };
        // Zones par distance normalisee au centre.
        private static readonly string[] ZoneDowntown = { "Modern_", "Building_Apartment_" };
        private static readonly string[] ZoneMidtown = { "Building_Apartment_", "Shop_", "Modern_" };
        private static readonly string[] ZoneSuburb = { "Building_Townhouse_", "Shop_", "Building_Apartment_" };
        private static readonly string[] Variants = { "A", "B", "C", "D", "E" };
        private const string ModelDir = "Assets/Models/City/";
        private const string ContainerName = "Generated";

        // Directions : bit N=1(+Z) E=2(+X) S=4(-Z) W=8(-X). Voisin sur la grille (dx,dyCell).
        private static readonly int[] DX = { 0, 1, 0, -1 };
        private static readonly int[] DYcell = { -1, 0, 1, 0 }; // row-1 = +Z (haut ASCII)

        [ContextMenu("Build City")]
        public void Build()
        {
            CityGrid grid = GetComponent<CityGridAuthoring>().Grid;
            if (grid == null) { Debug.LogWarning("CityBuilder: pas de grille."); return; }

            Transform old = transform.Find(ContainerName);
            if (old != null) DestroyImmediate(old.gameObject);
            Transform root = new GameObject(ContainerName).transform;
            root.SetParent(transform, false);

            toonShader = Shader.Find("GMTK/ToonLit");
            toonCache.Clear();

            float cs = grid.CellSize;
            int w = grid.Width, h = grid.Height;

            // Sol/verdure sous toute l'emprise.
            Material groundMat = new Material(toonShading && toonShader != null
                ? toonShader : Shader.Find("Universal Render Pipeline/Lit"));
            groundMat.SetColor("_BaseColor", groundColor);
            groundMat.SetFloat("_Smoothness", 0.1f);
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.SetParent(root, false);
            ground.transform.localScale = new Vector3(w * cs + cs, 0.4f, h * cs + cs);
            ground.transform.localPosition = new Vector3(0f, -0.22f, 0f);
            ground.GetComponent<MeshRenderer>().sharedMaterial = groundMat;

            // 1) Routes.
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (grid.At(x, y) == CellType.Road)
                        PlaceRoad(root, grid, x, y, cs);

            // 2) Batiments : UN PAR CELLULE Block, scale ~= cellule (echelle humaine,
            //    portes coherentes vs persos). Faces vers la rue voisine.
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (grid.At(x, y) == CellType.Block)
                        PlaceBuilding(root, grid, x, y, cs);

            // 3) Vie : rampes sur segments droits + trafic + pietons.
            SpawnLife(root, grid, cs);
        }

        // --- Routes -------------------------------------------------------------

        private bool IsRoad(CityGrid g, int x, int y) => g.At(x, y) == CellType.Road;

        private void PlaceRoad(Transform root, CityGrid g, int x, int y, float cs)
        {
            int m = 0;
            for (int d = 0; d < 4; d++)
                if (IsRoad(g, x + DX[d], y + DYcell[d])) m |= (1 << d);
            int bits = 0; for (int i = 0; i < 4; i++) if ((m & (1 << i)) != 0) bits++;

            string tile; float yaw;
            if (bits >= 4) { tile = "Road_X_A"; yaw = 0f; }
            else if (bits == 3)
            {
                tile = "Road_T_A"; // base suppose N|E|S (=7, manque W) a yaw0
                if (m == 7) yaw = 0f; else if (m == 14) yaw = 90f;
                else if (m == 13) yaw = 180f; else yaw = 270f;
                yaw += tOffset;
            }
            else if (bits == 2 && (m == 5 || m == 10))
            {
                tile = "Road_Straight_A";
                yaw = (m == 5 ? 0f : 90f) + straightOffset;
            }
            else if (bits == 2)
            {
                tile = "Road_Corner_A"; // base suppose N|E (=3) a yaw0
                if (m == 3) yaw = 0f; else if (m == 6) yaw = 90f;
                else if (m == 12) yaw = 180f; else yaw = 270f;
                yaw += cornerOffset;
            }
            else
            {
                tile = "Road_Straight_A";
                bool ns = (m & 1) != 0 || (m & 4) != 0;
                yaw = (ns ? 0f : 90f) + straightOffset;
            }

            Vector3 c = g.CellToWorld(x, y);
            GameObject go = LoadTile(streetDir + tile + ".fbx", cs, cs); // remplit la cellule
            if (go == null) return;
            go.transform.SetParent(root, false);
            go.transform.localPosition = new Vector3(c.x, 0f, c.z);
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            go.name = tile;
        }

        // --- Batiments (un par cellule) ----------------------------------------

        // Pseudo-aleatoire deterministe [0,1) depuis un hash + un sel.
        private static float Rand(uint hash, int salt)
        {
            uint v = (hash ^ ((uint)salt * 2654435761u)) * 40503u + 12345u;
            v ^= v >> 13; v *= 1274126177u; v ^= v >> 16;
            return (v % 100000u) / 100000f;
        }

        // Placement ORGANIQUE : au lieu d'1 batiment centre par cellule, on aligne des
        // RANGEES de facade (largeurs variees, petits trous) le long de chaque rue qui
        // borde la cellule. Coins laisses ouverts (ruelles) + cellules interieures = cour.
        private void PlaceBuilding(Transform root, CityGrid g, int x, int y, float cs)
        {
            bool rN = IsRoad(g, x, y - 1), rS = IsRoad(g, x, y + 1);
            bool rE = IsRoad(g, x + 1, y), rW = IsRoad(g, x - 1, y);
            if (!(rN || rS || rE || rW)) return; // cellule interieure -> cour (vide, arbres phase 2)

            // Zone (famille) selon distance normalisee au centre.
            string[] fams = Families;
            if (zoning)
            {
                float cx = (g.Width - 1) * 0.5f, cy = (g.Height - 1) * 0.5f;
                float maxD = Mathf.Sqrt(cx * cx + cy * cy);
                float d = maxD > 0.001f ? Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / maxD : 0f;
                fams = d < 0.38f ? ZoneDowntown : d < 0.72f ? ZoneMidtown : ZoneSuburb;
            }

            Vector3 c = g.CellToWorld(x, y);
            if (rN) PlaceRow(root, g, x, y, cs, c, new Vector3(0f, 0f, 1f), 0f, fams);
            if (rE) PlaceRow(root, g, x, y, cs, c, new Vector3(1f, 0f, 0f), 90f, fams);
            if (rS) PlaceRow(root, g, x, y, cs, c, new Vector3(0f, 0f, -1f), 180f, fams);
            if (rW) PlaceRow(root, g, x, y, cs, c, new Vector3(-1f, 0f, 0f), 270f, fams);
        }

        // Une rangee de batiments jointifs le long du bord de la cellule face a la rue.
        // Chaque batiment est scale par HAUTEUR (nb d'etages x floorUnit, cale sur le
        // pieton) -> portes/proportions humaines coherentes. La rangee avance selon
        // l'emprise REELLE resultante (largeurs variees = organique).
        private void PlaceRow(Transform root, CityGrid g, int x, int y, float cs,
            Vector3 c, Vector3 D, float yaw, string[] fams)
        {
            Vector3 perp = new Vector3(D.z, 0f, -D.x);   // axe le long du bord
            Vector3 edge = c + D * (cs * 0.5f);          // frontiere cellule/rue
            const float sidewalk = 0.4f;
            float half = cs * 0.48f;                     // ~96% du bord (dense, coins juste ouverts)
            float cursor = -half;
            int idx = 0;
            while (cursor < half && idx < 10)
            {
                uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663)
                       ^ ((uint)(int)yaw * 2654435761u) ^ ((uint)idx * 40503u + 17u);
                idx++;
                if (Rand(h, 2) < emptyLotChance) { cursor += cs * 0.28f; continue; } // trou

                string family = fams[h % (uint)fams.Length];
                string variant = Variants[(h / 7u) % (uint)Variants.Length];
                float footprint;
                GameObject go = SpawnScaledBuilding(root, family, variant, h, out footprint);
                if (go == null) break;
                if (cursor + footprint > half) { DestroyImmediate(go); break; } // depasse le bord

                float along = cursor + footprint * 0.5f;
                cursor += footprint + cs * (0.005f + Rand(h, 1) * 0.03f); // avance + gap serre
                float ry = (Rand(h, 3) * 2f - 1f) * rotationJitter;
                Vector3 pos = edge - D * (footprint * 0.5f + sidewalk) + perp * along;
                go.transform.localPosition = new Vector3(pos.x, go.transform.localPosition.y, pos.z);
                go.transform.localRotation = Quaternion.Euler(0f, yaw + buildingFront + ry, 0f);
            }
        }

        // Nb d'etages par famille (les tours Modern hautes, townhouses basses).
        private int FloorsFor(string fam, uint h)
        {
            int lo, hi;
            if (fam.Contains("Modern")) { lo = 8; hi = 15; }
            else if (fam.Contains("Apartment")) { lo = 4; hi = 7; }
            else if (fam.Contains("Townhouse")) { lo = 3; hi = 4; }
            else { lo = 2; hi = 3; } // Shop
            return lo + Mathf.FloorToInt(Rand(h, 8) * (hi - lo + 1));
        }

        // Instancie + scale par HAUTEUR cible, base a y=0, collider. Renvoie l'emprise
        // resultante (pour le packing). Position XZ/rotation posees par l'appelant.
        private GameObject SpawnScaledBuilding(Transform root, string family, string variant,
            uint h, out float footprint)
        {
            footprint = 0f;
            GameObject go = Load(ModelDir + family + variant + ".fbx");
            if (go == null) return null;

            Bounds nb = Fit(go);
            float targetH = FloorsFor(family, h) * floorUnit;
            float scale = nb.size.y > 0.001f ? targetH / nb.size.y : 1f;
            go.transform.localScale = Vector3.one * scale;
            go.transform.SetParent(root, false);

            Bounds sb = Fit(go); // apres scale
            footprint = Mathf.Max(sb.size.x, sb.size.z);
            float yBase = -(sb.center.y - sb.size.y * 0.5f);
            go.transform.localPosition = new Vector3(0f, yBase, 0f);

            BoxCollider box = go.AddComponent<BoxCollider>();
            box.center = go.transform.InverseTransformPoint(sb.center);
            box.size = sb.size / Mathf.Max(0.0001f, scale);

            go.name = family + variant;
            return go;
        }

        // --- Vie (rampes, trafic, pietons) -------------------------------------

        private int RoadMask(CityGrid g, int x, int y)
        {
            int m = 0;
            for (int d = 0; d < 4; d++) if (IsRoad(g, x + DX[d], y + DYcell[d])) m |= (1 << d);
            return m;
        }

        private void SpawnLife(Transform root, CityGrid g, float cs)
        {
            int w = g.Width, h = g.Height;
            List<Vector2Int> roads = new List<Vector2Int>();
            List<Vector2Int> straights = new List<Vector2Int>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (g.At(x, y) == CellType.Road)
                    {
                        roads.Add(new Vector2Int(x, y));
                        int m = RoadMask(g, x, y);
                        if (m == 5 || m == 10) straights.Add(new Vector2Int(x, y));
                    }

            // Rampes alignees sur la rue (montee = axe de la route).
            if (placeRamps && rampEvery > 0)
                for (int i = 0; i < straights.Count; i++)
                {
                    if (i % rampEvery != 0) continue;
                    Vector2Int p = straights[i];
                    float yaw = RoadMask(g, p.x, p.y) == 5 ? 0f : 90f; // 5=N-S, 10=E-O
                    GameObject r = InstancePrefab(RampPath);
                    if (r == null) continue;
                    Vector3 c = g.CellToWorld(p.x, p.y);
                    r.transform.SetParent(root, false);
                    r.transform.localPosition = new Vector3(c.x, 0f, c.z);
                    r.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                    r.name = "Ramp";
                }

            SpawnOnRoads(root, g, roads, TrafficPath, trafficCount, 0.5f, true, cs);   // voitures
            SpawnOnRoads(root, g, roads, PedPath, pedestrianCount, 0f, false, cs);     // pietons (trottoir)

            // Pigeons : eparpilles sur toute l'emprise (ils volent/sautillent seuls).
            SpawnScatter(root, g, PigeonPath, pigeonCount, 0.2f);
        }

        // Eparpille count instances sur toutes les cellules (pas seulement routes).
        private void SpawnScatter(Transform root, CityGrid g, string path, int count, float y)
        {
            if (count <= 0) return;
            int total = g.Width * g.Height;
            int step = Mathf.Max(1, total / count);
            int placed = 0;
            for (int idx = 0; idx < total && placed < count; idx += step)
            {
                int x = idx % g.Width, cy = idx / g.Width;
                GameObject go = InstancePrefab(path);
                if (go == null) return;
                Vector3 c = g.CellToWorld(x, cy);
                go.transform.SetParent(root, false);
                go.transform.localPosition = new Vector3(c.x, y, c.z);
                go.name = "Pigeon_" + placed;
                placed++;
            }
        }

        // Repartit `count` instances sur les cellules route (pas regulier, deterministe).
        private void SpawnOnRoads(Transform root, CityGrid g, List<Vector2Int> roads,
            string path, int count, float y, bool car, float cs)
        {
            if (count <= 0 || roads.Count == 0) return;
            int step = Mathf.Max(1, roads.Count / count);
            int placed = 0;
            for (int i = 0; i < roads.Count && placed < count; i += step)
            {
                Vector2Int p = roads[i];
                GameObject go = InstancePrefab(path);
                if (go == null) return;
                Vector3 c = g.CellToWorld(p.x, p.y);
                uint hh = (uint)(p.x * 73856093) ^ (uint)(p.y * 19349663) ^ (uint)(placed * 83492791);
                float ox = 0f, oz = 0f;
                if (!car) // pieton pousse vers un bord (trottoir)
                {
                    ox = ((hh & 1) == 0 ? -1f : 1f) * cs * 0.42f;
                    oz = (((hh >> 1) & 1) == 0 ? -1f : 1f) * cs * 0.42f;
                }
                go.transform.SetParent(root, false);
                go.transform.localPosition = new Vector3(c.x + ox, y, c.z + oz);
                // Voitures : yaw aleatoire. Pietons : NE PAS toucher la rotation ->
                // Pedestrian.cs capture la rotation d'import (baseTilt) au Start ; la
                // forcer les fait marcher de travers.
                if (car) go.transform.localRotation = Quaternion.Euler(0f, (hh % 4) * 90f, 0f);
                go.name = (car ? "TrafficCar_" : "Pedestrian_") + placed;
                placed++;
            }
        }

        private GameObject InstancePrefab(string path)
        {
#if UNITY_EDITOR
            GameObject src = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (src == null) { Debug.LogWarning("CityBuilder: prefab introuvable " + path); return null; }
            return UnityEditor.PrefabUtility.InstantiatePrefab(src) as GameObject;
#else
            return null;
#endif
        }

        // --- Helpers d'instanciation -------------------------------------------

        // Route : scale NON-UNIFORME pour remplir exactement footX x footZ (tuiles jointives).
        private GameObject LoadTile(string path, float footX, float footZ)
        {
            GameObject g = Load(path);
            if (g == null) return null;
            Bounds b = Fit(g);
            float sx = b.size.x > 0.0001f ? footX / b.size.x : 1f;
            float sz = b.size.z > 0.0001f ? footZ / b.size.z : 1f;
            g.transform.localScale = new Vector3(sx, Mathf.Min(sx, sz), sz);
            return g;
        }

        // Batiment : scale UNIFORME (garde les proportions) pour tenir dans foot, base a y=0.
        private GameObject LoadBuilding(string path, float foot)
        {
            GameObject g = Load(path);
            if (g == null) return null;
            Bounds b = Fit(g);
            float m = Mathf.Max(b.size.x, b.size.z);
            float s = m > 0.0001f ? foot / m : 1f;
            g.transform.localScale = Vector3.one * s;
            b = Fit(g); // recalc apres scale
            g.transform.position += new Vector3(0f, -(b.center.y - b.size.y * 0.5f), 0f);
            return g;
        }

        private GameObject Load(string path)
        {
#if UNITY_EDITOR
            GameObject src = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
#else
            GameObject src = null; // outil d'auteur : generation en editeur
#endif
            if (src == null) { Debug.LogWarning("CityBuilder: introuvable " + path); return null; }
            GameObject g = Instantiate(src);
            if (toonShading && toonShader != null) Toonify(g);
            return g;
        }

        // Remplace chaque materiau par un equivalent GMTK/ToonLit (couleur + texture
        // preservees), mutualise via cache -> ombres BD / halftone sur toute la ville.
        private void Toonify(GameObject g)
        {
            foreach (Renderer r in g.GetComponentsInChildren<Renderer>())
            {
                Material[] ms = r.sharedMaterials;
                for (int i = 0; i < ms.Length; i++) ms[i] = ToonVariant(ms[i]);
                r.sharedMaterials = ms;
            }
        }

        private Material ToonVariant(Material src)
        {
            if (src == null) return null;
            if (toonCache.TryGetValue(src, out Material cached)) return cached;
            Material t = new Material(toonShader);
            Color col = src.HasProperty("_BaseColor") ? src.GetColor("_BaseColor")
                      : src.HasProperty("_Color") ? src.GetColor("_Color") : Color.white;
            t.SetColor("_BaseColor", col);
            Texture tex = src.HasProperty("_BaseMap") ? src.GetTexture("_BaseMap")
                        : src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") : null;
            if (tex != null) t.SetTexture("_BaseMap", tex);
            toonCache[src] = t;
            return t;
        }

        // Emprise du mesh PRINCIPAL, en ignorant la vegetation (_Veg) qui deborde et
        // fausserait le scale. Fallback sur tout si aucun mesh non-veg.
        private static Bounds Fit(GameObject g)
        {
            Renderer[] rs = g.GetComponentsInChildren<Renderer>();
            bool has = false; Bounds b = new Bounds(g.transform.position, Vector3.zero);
            foreach (Renderer r in rs)
            {
                if (r.gameObject.name.Contains("Veg")) continue;
                if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
            }
            if (!has) foreach (Renderer r in rs) { if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds); }
            return b;
        }
    }
}
