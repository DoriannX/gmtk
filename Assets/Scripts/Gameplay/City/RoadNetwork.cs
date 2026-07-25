using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

namespace Gameplay.City
{
    // RESEAU ROUTIER par splines, edite en Shift+clic dans la scene (voir RoadNetworkEditor).
    // On ne stocke QUE le graphe : une liste de noeuds (position) et de segments (a -> b).
    // Tout le reste — splines, croisements, meshes — est DERIVE et regenere a chaque edition,
    // au chargement de scene et en build. D'ou l'absence de bouton "build".
    //
    // Le point cle des croisements : les BRAS de l'asset (SM_Croisement_*) donnent la position
    // ET la tangente de depart de chaque segment. Le yaw de l'asset est ajuste sur les vraies
    // directions des branches par moyenne circulaire (optimum moindres carres sur le cercle).
    // Resultat : raccord C1 entre le mesh du croisement et la route deformee, a n'importe quel
    // angle — ni couture, ni trou, ni bitume en double.
    //
    // Les objets generes portent hideFlags.DontSave : la scene ne serialise que les listes
    // (quelques centaines d'octets), jamais les meshes, et rien n'entre dans la pile d'undo.
    [ExecuteAlways]
    public class RoadNetwork : MonoBehaviour
    {
        [System.Serializable]
        public class Node
        {
            public Vector3 pos;          // LOCAL au RoadNetwork -> deplacer le GO deplace la route
            public bool roundabout;      // variante rond-point du carrefour, valable a 4 branches
            public float yaw;            // rotation MANUELLE ajoutee au yaw calcule (deg)
            // Hauteur VOULUE au-dessus du relief (0 = rue posee au sol, 7 = tablier de pont).
            // `pos.y` reste la hauteur absolue et fait foi partout ailleurs : c'est LayOnTerrain
            // qui recalcule l'une depuis l'autre quand le relief change.
            public float lift;
        }

        [System.Serializable]
        public class Segment
        {
            public int a, b;
        }

        [Header("Assets de route")]
        [SerializeField] private GameObject tileStraight;
        [SerializeField] private GameObject junction3;
        [SerializeField] private GameObject junction4;
        [SerializeField] private GameObject deadEnd;
        [SerializeField] private GameObject roundabout;

        [Header("Convention des assets")]
        [Tooltip("Axe long de la tuile droite. Auto-detecte au Reset : la plus PETITE des 2 dimensions horizontales est la longueur.")]
        [SerializeField] private bool lengthOnZ = true;
        // Mesure sur SM_Croisement_3_voies : chaussee a y=-0.027, trottoir a y=+0.106 aux
        // milieux de bord -> ouvert en N/S/W, FERME en E. Le cote ferme est +X, pas -Z.
        [Tooltip("Yaw local de chaque bras du croisement 3 voies, en degres. Le gizmo dessine les bras calcules -> a corriger ici s'ils pointent mal.")]
        [SerializeField] private float[] arms3 = { 0f, 180f, 270f };
        [SerializeField] private float[] arms4 = { 0f, 90f, 180f, 270f };
        [SerializeField] private float[] armsDeadEnd = { 180f };
        [Tooltip("Rogne (positif) ou allonge (negatif) tous les bras. A regler si l'asset a une levre qui deborde.")]
        [SerializeField] private float armTrim = 0f;
        [Tooltip("Decale toute la route en Y, EN PLUS du calage automatique de la chaussee sur le " +
                 "sol vise. Un poil au-dessus de zero evite le z-fighting avec le terrain.")]
        [SerializeField] private float surfaceOffsetY = 0.02f;

        [Header("Trace")]
        [Tooltip("Longueur des tangentes, en part de la distance entre les deux bras. 0 = tout droit, 0.6 = tres arrondi.")]
        [SerializeField, Range(0f, 0.6f)] private float curvature = 0.35f;
        [Tooltip("Repose les noteuds sur le sol (raycast) quand on les pose ou les deplace. Decocher pour lever un noeud en Y (ponts).")]
        [SerializeField] private bool snapToGround = true;
        [Tooltip("Hauteur du plan de secours quand aucun collider n'est touche.")]
        [SerializeField] private float groundHeight = 0f;

        // Sans ca un pont est une feuille de papier posee en l'air : la tuile du kit n'a pas
        // d'epaisseur et rien ne descend jusqu'au sol. Tout est genere depuis la spline, aucun
        // asset a demander.
        [Header("Ouvrages")]
        [Tooltip("Retombee laterale sous le tablier, en metres. C'est elle qui fait lire une " +
                 "poutre plutot qu'une feuille. Posee sur TOUS les segments : au sol elle finit " +
                 "sous le trottoir, invisible.")]
        [SerializeField] private float deckFascia = 0.5f;
        [Tooltip("Hauteur du garde-corps, en metres. UNIQUEMENT sur les ouvrages : une rue au " +
                 "sol bordee de murets enfermerait le joueur sur la chaussee.")]
        [SerializeField] private float parapetHeight = 0.9f;
        [Tooltip("Espacement des piles, en metres. 0 = pas de piles.")]
        [SerializeField] private float pierEvery = 14f;
        [Tooltip("Cote de la section carree d'une pile, en metres.")]
        [SerializeField] private float pierSize = 1.2f;

        [Header("Rendu")]
        [SerializeField] private bool toonShading = true;
        [SerializeField] private Color asphaltColor = new Color(0.24f, 0.24f, 0.26f);
        [Tooltip("Optionnel : materiau explicite. Prioritaire sur le toon.")]
        [SerializeField] private Material roadMaterial;

        [Header("Debug")]
        [SerializeField] private bool showArms = true;

        // --- graphe (seule donnee serialisee) ---
        [SerializeField, HideInInspector] private List<Node> nodes = new List<Node>();
        [SerializeField, HideInInspector] private List<Segment> segments = new List<Segment>();
        // Faux tant que les hauteurs des noeuds sont des valeurs saisies a la main sur un sol
        // plat. Voir LayOnTerrain : c'est ce drapeau qui distingue la premiere pose (capture des
        // hauteurs d'ouvrage) des suivantes (le relief commande).
        [SerializeField, HideInInspector] private bool liftCaptured;

        private const string ContainerName = "Generated";
        private const string CapName = "Dessous";
        private const string PierName = "Pile_";
        private const float CapAbove = 1.5f;      // au-dela, le croisement est un OUVRAGE : on ferme son dessous
        private const HideFlags GenFlags = HideFlags.DontSave | HideFlags.NotEditable;
        private const float MinSegment = 2f;      // bras plus proches que ca -> segment saute
        // Pas des points de suivi du relief, en metres. Chaque point de plus alourdit TOUTES les
        // requetes de proximite (elles echantillonnent la spline), et le sol en fait des dizaines
        // de milliers : 16 m suffit face a des collines de 90 m.
        private const float TerrainKnotEvery = 16f;
        private const float SharpLimit = -0.25f;  // cos de l'angle max entre corde et tangente (~105 deg)
        private const float BadFitDeg = 35f;      // ecart-type angulaire au-dela -> gizmo rouge
        private const float MinBranchGap = 10f;   // deux branches plus serrees que ca -> gizmo rouge

        // --- caches derives, jamais serialises ---
        [System.NonSerialized] private List<int>[] adj;
        [System.NonSerialized] private Junction[] junctions;
        [System.NonSerialized] private Dictionary<GameObject, Bounds> boundsCache;
        [System.NonSerialized] private Dictionary<GameObject, float> roadwayCache;
        [System.NonSerialized] private RoadMeshWarp.Tile tile;       // brute : bandes pleine longueur
        [System.NonSerialized] private RoadMeshWarp.Tile tileFine;   // decoupee : pour les segments qui courbent
        // Fleche tolerée sur une longueur de tuile avant de passer a la tuile decoupee. Sous ce
        // seuil, une tuile pleine longueur est indiscernable de la courbe : inutile de payer
        // 5x les triangles. Une ligne droite, meme en pente, reste donc a la tuile brute.
        private const float FlatTol = 0.03f;
        private const float MaxPitch = 60f;   // garde-fou : au-dela, l'asset se cabre au lieu de suivre
        [System.NonSerialized] private bool tileReady;
        [System.NonSerialized] private Material runtimeMat;
        [System.NonSerialized] private bool dragging;
        [System.NonSerialized] private bool needsRebuild;
        [System.NonSerialized] private readonly HashSet<int> dirtyNodes = new HashSet<int>();
        [System.NonSerialized] private readonly HashSet<int> dirtySegments = new HashSet<int>();

        // Cache de splines pour l'API pinceau : ProbeRoad tourne a CHAQUE repaint de la
        // SceneView et SegmentSpline alloue une Spline + 2 BezierKnot par appel. Invalide en
        // tete de ComputeJunctions, reconstruit paresseusement.
        [System.NonSerialized] private Spline[] splineCache;
        [System.NonSerialized] private Vector3[][] polyCache;   // axe echantillonne, LOCAL
        [System.NonSerialized] private float[] splineLenCache;
        [System.NonSerialized] private Bounds[] splineBoxCache;   // emprise XZ locale, prefiltre

        private class Junction
        {
            public GameObject asset;        // null = degre 0 ou 2 (la spline traverse sans asset)
            public float yaw;               // yaw monde ajuste
            public float pitch;             // TANGAGE monde : le noeud se couche dans la pente
            public float[] armYaw;          // yaws LOCAUX des bras
            public float[] armRadius;
            public int[] armOfNeighbour;    // pour chaque voisin (ordre de adj) -> index de bras
            public Vector3 recenter;        // offset a appliquer a l'instance de l'asset
            public bool bad;                // fit degrade -> gizmo rouge
        }

        // ---------------------------------------------------------------- API editeur

        public int NodeCount => nodes.Count;
        public int SegmentCount => segments.Count;
        public bool SnapToGround => snapToGround;
        public float GroundHeight => groundHeight;

        // Faux si le conteneur a disparu (domain reload, reouverture de scene) alors que
        // Update n'a pas encore eu son tick -> l'editeur s'en sert comme filet.
        public bool IsBuilt => transform.Find(ContainerName) != null;

        // Tous les assets sont recentres sur leur CHAUSSEE (voir RoadMeshWarp.RoadwayHeight),
        // donc il ne reste que le decalage de confort contre le z-fighting.
        private float SurfaceY => surfaceOffsetY;

        public Vector3 NodeWorld(int i) => transform.TransformPoint(nodes[i].pos);
        public bool NodeRoundabout(int i) => nodes[i].roundabout;
        public Segment SegmentAt(int i) => segments[i];

        public void SetNodeRoundabout(int i, bool v)
        {
            nodes[i].roundabout = v;
            FullRebuild();
        }

        public float NodeYaw(int i) => nodes[i].yaw;

        // Hauteur du noeud AU-DESSUS DU RELIEF. C'est le chiffre qui a un sens quand on regle un
        // ouvrage : la hauteur absolue, elle, bouge des qu'on regraine le bruit.
        public float NodeLift(int i)
        {
            var t = Terrain();
            Vector3 w = NodeWorld(i);
            return t != null ? w.y - t.Height(w.x, w.z) : w.y - transform.position.y - groundHeight;
        }

        public void SetNodeLift(int i, float lift)
        {
            Vector3 w = NodeWorld(i);
            SetNodeWorld(i, new Vector3(w.x, w.y + (lift - NodeLift(i)), w.z));
        }

        public int NodeDegree(int i)
        {
            EnsureAdjacency();
            return i >= 0 && i < nodes.Count ? adj[i].Count : 0;
        }

        // Rotation manuelle du noeud : tourne l'asset ET la tangente de depart de ses segments.
        // Contrairement a un deplacement, ca n'affecte QUE ce noeud -> pas de 2-anneau, les
        // directions de branches des voisins ne dependent que des positions.
        public void SetNodeYaw(int i, float yaw)
        {
            nodes[i].yaw = Mathf.Repeat(yaw, 360f);
            EnsureAdjacency();
            MarkDirty(i);
        }

        public int AddNodeWorld(Vector3 world)
        {
            nodes.Add(new Node { pos = transform.InverseTransformPoint(world) });
            adj = null;
            return nodes.Count - 1;
        }

        // Repose tous les noeuds sur le relief, en conservant la hauteur d'ouvrage de chacun.
        //
        // `lift` n'existe pas tant qu'on n'a pas de relief : la premiere pose le CAPTURE depuis
        // les hauteurs deja saisies a la main (terrain plat -> lift = pos.y, un pont a +7 reste a
        // +7 au-dessus de la butte). Ensuite c'est lui qui commande, et regrainer le bruit ne
        // fait plus deriver les ouvrages.
        // Relief de la scene, cherche une fois. Le drag appelle SetNodeWorld a chaque frame : une
        // recherche par type a chaque appel serait payee pour rien.
        [System.NonSerialized] private CityTerrain terrainCache;
        private CityTerrain Terrain()
        {
            if (terrainCache == null) terrainCache = CityTerrain.Find();
            return terrainCache;
        }

        public bool LayOnTerrain(CityTerrain terrain)
        {
            if (terrain == null) return false;
            float baseY = terrain.transform.position.y;
            bool moved = !liftCaptured;
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                Vector3 w = transform.TransformPoint(n.pos);
                if (!liftCaptured) n.lift = w.y - baseY;
                w.y = terrain.Height(w.x, w.z) + n.lift;
                Vector3 p = transform.InverseTransformPoint(w);
                if ((p - n.pos).sqrMagnitude > 1e-8f) moved = true;
                n.pos = p;
            }
            liftCaptured = true;
            // Reconstruire seulement si un noeud a REELLEMENT bouge. Cette methode est appelee a
            // chaque generation du sol, or les hauteurs sont stables la plupart du temps : sans
            // ce test, chaque retouche de route reconstruisait tout le reseau pour rien.
            if (moved) FullRebuild();
            return moved;
        }

        // Deplacement : on ne reconstruit que le 2-ANNEAU. Bouger i change les directions de
        // branches de i ET de chaque voisin j, donc le yaw ajuste de j, donc TOUS les bras de j
        // -> un 1-anneau laisserait des trous aux croisements voisins pendant le drag.
        public void SetNodeWorld(int i, Vector3 world)
        {
            nodes[i].pos = transform.InverseTransformPoint(world);
            // Toute saisie de hauteur passe par ici (champ de l'inspecteur, fleche de scene,
            // drag) : c'est donc ICI qu'on rafraichit la hauteur au-dessus du relief, sinon la
            // prochaine generation du sol reposerait le noeud sur une valeur perimee et
            // annulerait l'edition.
            if (liftCaptured)
            {
                var t = Terrain();
                if (t != null) nodes[i].lift = world.y - t.Height(world.x, world.z);
            }
            EnsureAdjacency();
            MarkDirty(i);
            foreach (int j in adj[i]) MarkDirty(j);
        }

        // Segment dont l'AXE passe le plus pres du rayon souris. On interroge les splines et pas
        // les MeshCollider : le point rendu tombe pile sur l'axe de la chaussee, meme si le clic
        // visait le trottoir.
        public int NearestSegment(Ray worldRay, out Vector3 worldPoint)
        {
            worldPoint = Vector3.zero;
            var ray = new Ray(transform.InverseTransformPoint(worldRay.origin),
                              transform.InverseTransformDirection(worldRay.direction).normalized);
            int best = -1;
            float bestDist = float.PositiveInfinity;
            for (int k = 0; k < segments.Count; k++)
            {
                if (!SegmentSpline(k, out Spline spline)) continue;
                float d = SplineUtility.GetNearestPoint(spline, ray, out float3 near, out _);
                if (d >= bestDist) continue;
                bestDist = d;
                best = k;
                worldPoint = transform.TransformPoint((Vector3)near);
            }
            return best;
        }

        // Insere un noeud sur un segment : le segment est coupe en deux. Le point est projete sur
        // l'axe. Ne reconstruit pas -> l'appelant enchaine ses autres modifs puis FullRebuild.
        public int SplitSegment(int k, Vector3 worldPoint)
        {
            if (k < 0 || k >= segments.Count) return -1;
            var s = segments[k];
            int a = s.a, b = s.b;
            Vector3 local = transform.InverseTransformPoint(worldPoint);
            // Trop pres d'un BOUT DE BRAS (pas du noeud : l'axe demarre a 4 m d'une impasse et a
            // 6.5 m d'un croisement) -> on refuse, sinon le sous-segment naitrait degenere et
            // serait jete, laissant un trou.
            EnsureAdjacency();
            if (junctions == null || junctions.Length != nodes.Count) ComputeJunctions(false);
            if (Arm(a, b, out Vector3 arm0, out _) && Vector3.Distance(local, arm0) < MinSegment) return -1;
            if (Arm(b, a, out Vector3 arm1, out _) && Vector3.Distance(local, arm1) < MinSegment) return -1;
            segments.RemoveAt(k);
            nodes.Add(new Node { pos = local });
            int n = nodes.Count - 1;
            segments.Add(new Segment { a = a, b = n });
            segments.Add(new Segment { a = n, b = b });
            adj = null;
            return n;
        }

        public bool AddSegment(int a, int b)
        {
            if (a < 0 || b < 0 || a == b || a >= nodes.Count || b >= nodes.Count) return false;
            foreach (var s in segments)
                if ((s.a == a && s.b == b) || (s.a == b && s.b == a)) return false;
            segments.Add(new Segment { a = a, b = b });
            adj = null;
            return true;
        }

        public void RemoveNode(int i)
        {
            if (i < 0 || i >= nodes.Count) return;
            segments.RemoveAll(s => s.a == i || s.b == i);
            foreach (var s in segments)
            {
                if (s.a > i) s.a--;
                if (s.b > i) s.b--;
            }
            nodes.RemoveAt(i);
            adj = null;
        }

        // Les 5 assets du kit, pour que l'editeur puisse forcer leur Read/Write.
        public IEnumerable<GameObject> KitAssets()
        {
            yield return tileStraight;
            yield return junction3;
            yield return junction4;
            yield return deadEnd;
            yield return roundabout;
        }

        public void BeginDrag() { dragging = true; }

        public void EndDrag()
        {
            dragging = false;
            ApplyMissingColliders();
        }

        // ---------------------------------------------------------------- API pinceau
        //
        // De quoi poser des batiments le long des routes (voir CityBrush). Tout est ajoute ICI
        // et pas cote pinceau precisement pour que SegmentSpline, Arm, EnsureTile, junctions et
        // adj restent prives : le pinceau interroge, il ne fouille pas.

        // Resultat d'une requete de proximite routiere, en MONDE.
        public struct RoadProbe
        {
            public bool valid;
            public int segment;      // -1 si le plus proche est un croisement
            public int node;         // -1 si le plus proche est un segment
            public float distance;   // distance XZ a l'AXE (ou au centre du croisement)
            // Distance au BORD de l'emprise routiere, trottoirs compris. Negative = dedans.
            // C'est ce champ qu'il faut tester pour "est-ce que je peux poser un batiment ici",
            // parce qu'il donne la meme semantique pour un segment et pour un croisement -- la
            // distance a l'axe, elle, n'est pas comparable entre les deux.
            public float clearance;
            public float t;          // parametre normalise sur la spline (segment uniquement)
            public Vector3 point;    // point sur l'axe, ou centre du croisement
            public Vector3 tangent;  // monde, normalisee, a plat, sens a -> b
            public bool inside;      // le point interroge est SOUS la route (chaussee + trottoirs)
        }

        // Demi-largeur de la tuile, trottoirs compris (13 m -> 6.5). Repli sur 6.5 si le FBX
        // n'est pas lisible : mieux vaut une valeur plausible qu'un zero qui collerait les
        // batiments au milieu de la chaussee.
        public float RoadHalfWidth
        {
            get { EnsureTile(); return tile.valid && tile.width > 0.01f ? tile.width * 0.5f : 6.5f; }
        }

        // Hauteur du TROTTOIR au-dessus de la chaussee, mesuree sur la tuile droite. C'est a
        // cette hauteur que doit se caler le sol de la ville pour que la route se lise comme un
        // CREUX (voir CityGroundBuilder) : chaussee en bas, bordure, trottoir, puis le sol.
        //
        // Symetrique de RoadMeshWarp.RoadwayHeight, qui prend la bande horizontale la plus
        // BASSE d'aire notable : ici on prend la plus GRANDE en aire parmi celles situees
        // au-dessus de la chaussee. Sur SM_Tile_droit : chaussee 0.024, bordure 0.296 (2 x 0.5 m),
        // trottoir 0.176 (2 x 3.75 m) -> l'aire tranche sans ambiguite, la hauteur max se
        // tromperait et prendrait la bordure.
        [System.NonSerialized] private float sidewalkY = float.NaN;
        public float SidewalkHeight
        {
            get
            {
                if (!float.IsNaN(sidewalkY)) return sidewalkY;
                EnsureTile();
                sidewalkY = 0f;
                if (!tile.valid) return sidewalkY;

                var area = new Dictionary<int, float>();
                var v = tile.verts;
                var tr = tile.tris;
                for (int i = 0; i + 2 < tr.Length; i += 3)
                {
                    Vector3 a = v[tr[i]], b = v[tr[i + 1]], c = v[tr[i + 2]];
                    Vector3 cr = Vector3.Cross(b - a, c - a);
                    float len = cr.magnitude;
                    if (len < 1e-9f || cr.y / len < 0.999f) continue;   // ecarte les chanfreins
                    float y = (a.y + b.y + c.y) / 3f;
                    if (y < 0.02f) continue;                             // c'est la chaussee
                    int k = Mathf.RoundToInt(y / 0.01f);
                    area[k] = (area.TryGetValue(k, out float acc) ? acc : 0f) + len * 0.5f;
                }

                float best = 0f;
                foreach (var kv in area)
                    if (kv.Value > best) { best = kv.Value; sidewalkY = kv.Key * 0.01f; }
                return sidewalkY;
            }
        }

        // Rayon d'emprise d'un croisement = son plus long bras. Zero pour un noeud sans asset
        // (degre 0 ou 2 : la spline traverse, c'est le segment qui porte l'emprise).
        public float JunctionRadius(int i)
        {
            if (i < 0 || i >= nodes.Count) return 0f;
            EnsureAdjacency();
            if (junctions == null || junctions.Length != nodes.Count) ComputeJunctions(false);
            var j = junctions[i];
            if (j == null || j.asset == null || j.armRadius == null) return 0f;
            float r = 0f;
            foreach (float a in j.armRadius) r = Mathf.Max(r, a);
            return r;
        }

        public float SegmentLength(int k)
        {
            EnsureSplines();
            return k >= 0 && k < splineLenCache.Length ? splineLenCache[k] : 0f;
        }

        // Point du reseau le plus proche de `worldPos`, en distance XZ. La hauteur est ignoree
        // volontairement : un batiment 30 m plus haut sur un tablier de pont n'est pas "sur la
        // route". O(segments) avec prefiltre AABB -> gratuit en edition sur un reseau de cette
        // taille, une grille spatiale ne se justifie pas ici (celle de GrindRailNetwork existe
        // parce qu'elle est interrogee par frame au runtime).
        //
        // `maxHeightAbove` est la reciproque de ce parti-pris, pour les appelants qui raisonnent
        // depuis le SOL : au-dela de cette hauteur au-dessus du point teste, la route passe
        // par-dessus et ne compte plus. Sans ca, un tablier leve a +6 m troue quand meme le sol
        // sous lui (CityGroundBuilder) et interdit d'y peindre quoi que ce soit. Infini par
        // defaut = comportement d'origine, aucun appelant existant ne change.
        public bool ProbeRoad(Vector3 worldPos, float maxDist, out RoadProbe probe,
                              float maxHeightAbove = float.PositiveInfinity)
        {
            probe = default;
            probe.segment = -1;
            probe.node = -1;
            EnsureSplines();
            if (splineCache.Length == 0 && nodes.Count == 0) return false;

            Vector3 local = transform.InverseTransformPoint(worldPos);
            var flatXZ = new Vector2(local.x, local.z);
            float half = RoadHalfWidth;

            // On compare des GARDES (distance au bord de l'emprise), pas des distances a l'axe :
            // les deux ne sont pas comparables entre un segment et un croisement, et melanger
            // les deux laissait passer un batiment pose sur la chaussee pres d'un carrefour.
            float bestClear = maxDist;
            bool found = false;

            for (int k = 0; k < splineCache.Length; k++)
            {
                var spline = splineCache[k];
                if (spline == null) continue;

                // Prefiltre : l'emprise stockee est deja gonflee de la fleche de Bezier, on
                // n'y ajoute que la portee de recherche.
                Bounds box = splineBoxCache[k];
                float reach = bestClear + half;
                box.Expand(new Vector3(reach * 2f, 0f, reach * 2f));
                if (!box.Contains(new Vector3(local.x, 0f, local.z))) continue;

                NearestOnPoly(polyCache[k], flatXZ, out Vector3 n, out float t);
                if (n.y - local.y > maxHeightAbove) continue;   // ouvrage : passe au-dessus
                float d = Vector2.Distance(flatXZ, new Vector2(n.x, n.z));
                if (d - half >= bestClear) continue;

                bestClear = d - half;
                found = true;
                probe.segment = k;
                probe.node = -1;
                probe.t = t;
                probe.distance = d;
                probe.point = transform.TransformPoint(n);
                spline.Evaluate(t, out float3 _, out float3 tan, out float3 _2);
                Vector3 wt = transform.TransformDirection((Vector3)tan);
                wt.y = 0f;
                probe.tangent = wt.sqrMagnitude > 1e-6f ? wt.normalized : Vector3.forward;
            }

            // Les croisements ne sont couverts par aucune spline (les axes demarrent au BOUT des
            // bras) : sans ce second passage, on peut poser un immeuble en plein carrefour.
            for (int i = 0; i < nodes.Count; i++)
            {
                float r = JunctionRadius(i);
                if (r <= 0f) continue;
                Vector3 p = nodes[i].pos;
                if (p.y - local.y > maxHeightAbove) continue;   // croisement en l'air
                float d = Vector2.Distance(flatXZ, new Vector2(p.x, p.z));
                if (d - r >= bestClear) continue;

                bestClear = d - r;
                found = true;
                probe.segment = -1;
                probe.node = i;
                probe.t = 0f;
                probe.distance = d;
                probe.point = transform.TransformPoint(p);
                probe.tangent = Vector3.forward;
            }

            if (!found) return false;
            probe.valid = true;
            probe.clearance = bestClear;
            probe.inside = bestClear <= 0f;
            return true;
        }

        // Echantillonne l'AXE d'un segment a l'abscisse curviligne `distance` (metres), en MONDE.
        public bool SampleSegment(int k, float distance, out Vector3 worldPos,
                                  out Vector3 worldTangent, out float length)
        {
            worldPos = Vector3.zero;
            worldTangent = Vector3.forward;
            length = 0f;
            EnsureSplines();
            if (k < 0 || k >= splineCache.Length || splineCache[k] == null) return false;

            var spline = splineCache[k];
            length = splineLenCache[k];
            if (length <= 0f) return false;

            // Le t d'une spline n'est PAS proportionnel a la distance (meme piege que
            // RoadMeshWarp) : sans la conversion, les batiments se tassent dans les virages.
            float t = spline.ConvertIndexUnit(Mathf.Clamp(distance, 0f, length),
                                              PathIndexUnit.Distance, PathIndexUnit.Normalized);
            spline.Evaluate(t, out float3 p, out float3 tan, out float3 _);
            worldPos = transform.TransformPoint((Vector3)p);
            Vector3 wt = transform.TransformDirection((Vector3)tan);
            wt.y = 0f;
            worldTangent = wt.sqrMagnitude > 1e-6f ? wt.normalized : Vector3.forward;
            return true;
        }

        private const float PolyStep = 2f;   // pas d'echantillonnage de l'axe, en metres

        // Point de la polyligne le plus proche, en XZ. La hauteur est interpolee : c'est elle qui
        // dit si la route passe AU-DESSUS du point interroge (ouvrage).
        private static void NearestOnPoly(Vector3[] poly, Vector2 q, out Vector3 near, out float t)
        {
            near = poly[0];
            t = 0f;
            float best = float.MaxValue;
            for (int i = 0; i + 1 < poly.Length; i++)
            {
                Vector3 a = poly[i], b = poly[i + 1];
                float ex = b.x - a.x, ez = b.z - a.z;
                float len2 = ex * ex + ez * ez;
                float u = len2 > 1e-8f
                    ? Mathf.Clamp01(((q.x - a.x) * ex + (q.y - a.z) * ez) / len2)
                    : 0f;
                float px = a.x + ex * u, pz = a.z + ez * u;
                float dx = q.x - px, dz = q.y - pz;
                float d2 = dx * dx + dz * dz;
                if (d2 >= best) continue;
                best = d2;
                near = new Vector3(px, Mathf.Lerp(a.y, b.y, u), pz);
                t = (i + u) / (poly.Length - 1);
            }
        }

        private void EnsureSplines()
        {
            if (splineCache != null && splineCache.Length == segments.Count) return;

            EnsureAdjacency();
            if (junctions == null || junctions.Length != nodes.Count) ComputeJunctions(false);

            splineCache = new Spline[segments.Count];
            splineLenCache = new float[segments.Count];
            splineBoxCache = new Bounds[segments.Count];
            polyCache = new Vector3[segments.Count][];

            for (int k = 0; k < segments.Count; k++)
            {
                if (!SegmentSpline(k, out Spline spline)) continue;
                splineCache[k] = spline;
                float len = spline.GetLength();
                splineLenCache[k] = len;

                // Axe echantillonne une fois pour toutes. Les requetes de proximite cherchent
                // dessus au lieu d'appeler SplineUtility.GetNearestPoint : celui-ci reechantillonne
                // la courbe a CHAQUE appel, ce qui coutait 134 us par requete depuis que le suivi
                // du relief ajoute des noeuds intermediaires -- et le sol en fait des milliers.
                int n = Mathf.Clamp(Mathf.CeilToInt(len / PolyStep) + 1, 2, 512);
                var poly = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    spline.Evaluate((float)i / (n - 1), out float3 sp, out float3 _, out float3 _3);
                    poly[i] = (Vector3)sp;
                }
                polyCache[k] = poly;

                // Emprise XZ de la corde, gonflee de la fleche maximale de la Bezier
                // (majoree par curvature * |corde|) -> le prefiltre ne peut pas rater un point.
                // DERNIER knot et pas `spline[1]` : depuis le suivi du relief, le knot 1 est un
                // point intermediaire et l'emprise ne couvrait plus que le debut du segment.
                Vector3 p0 = (Vector3)spline[0].Position;
                Vector3 p1 = (Vector3)spline[spline.Count - 1].Position;
                var box = new Bounds(new Vector3((p0.x + p1.x) * 0.5f, 0f, (p0.z + p1.z) * 0.5f),
                                     new Vector3(Mathf.Abs(p1.x - p0.x), 0f, Mathf.Abs(p1.z - p0.z)));
                float sag = curvature * Vector3.Distance(p0, p1);
                box.Expand(new Vector3(sag * 2f, 0f, sag * 2f));
                splineBoxCache[k] = box;
            }
        }

        // ---------------------------------------------------------------- cycle de vie

        // DestroyImmediate est interdit depuis OnEnable/OnValidate -> on leve un drapeau et le
        // premier Update le consomme. [ExecuteAlways] garantit que Update tourne aussi en mode
        // edition, au chargement de scene et apres un domain reload. (EditorApplication.delayCall
        // ne se declenchait PAS de facon fiable a l'ouverture d'une scene.)
        private void OnEnable()
        {
            needsRebuild = true;
        }

        private void OnValidate()
        {
            tileReady = false;
            sidewalkY = float.NaN;   // mesure derivee de la tuile -> meme invalidation
            boundsCache = null;
            roadwayCache = null;
            runtimeMat = null;   // DontSave -> libere au prochain reload, pas de DestroyImmediate ici
            needsRebuild = true;
        }

        private void Update()
        {
            if (!needsRebuild) return;
            needsRebuild = false;
            FullRebuild();
        }

        private void Reset()
        {
#if UNITY_EDITOR
            const string dir = "Assets/Models/Roads/";
            tileStraight = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(dir + "SM_Tile_droit.fbx");
            junction3 = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(dir + "SM_Croisement_3_voies.fbx");
            junction4 = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(dir + "SM_Croisement_4_voies.fbx");
            deadEnd = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(dir + "SM_Dead_end.fbx");
            roundabout = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(dir + "SM_rondPoint.fbx");

            // Auto-detection de l'axe long : la tuile est plus LARGE que longue (13 x 8),
            // donc la plus petite dimension horizontale est la longueur.
            if (AssetBounds(tileStraight, out Bounds b))
                lengthOnZ = b.size.z <= b.size.x;
#endif
        }

        // ---------------------------------------------------------------- construction

        public void FullRebuild()
        {
            Transform old = transform.Find(ContainerName);
            if (old != null) { DestroyChildren(old); DestroyImmediate(old.gameObject); }

            dirtyNodes.Clear();
            dirtySegments.Clear();
            if (nodes.Count == 0) return;

            Transform root = new GameObject(ContainerName).transform;
            root.gameObject.hideFlags = GenFlags;
            root.SetParent(transform, false);

            EnsureAdjacency();
            EnsureTile();
            ComputeJunctions(true);

            for (int i = 0; i < nodes.Count; i++) BuildJunction(root, i);
            for (int k = 0; k < segments.Count; k++) BuildSegment(root, k);
        }

        // Reconstruit uniquement les meshes marques sales. Les croisements sont TOUS recalcules
        // (quelques dizaines d'operations flottantes par noeud) : seule la generation de mesh
        // coute, pas la trigo.
        // BuildJunction/BuildSegment REUTILISENT le GameObject et le Mesh existants : detruire
        // puis recreer a chaque frame de drag coutait ~10x plus cher que le warp lui-meme.
        public void RebuildDirty()
        {
            Transform root = transform.Find(ContainerName);
            if (root == null || dirtyNodes.Count + dirtySegments.Count == 0) { FullRebuild(); return; }

            EnsureAdjacency();
            EnsureTile();
            ComputeJunctions(false);

            foreach (int i in dirtyNodes) BuildJunction(root, i);
            foreach (int k in dirtySegments) BuildSegment(root, k);
            dirtyNodes.Clear();
            dirtySegments.Clear();
        }

        private void MarkDirty(int node)
        {
            dirtyNodes.Add(node);
            for (int k = 0; k < segments.Count; k++)
                if (segments[k].a == node || segments[k].b == node) dirtySegments.Add(k);
        }

        private void EnsureAdjacency()
        {
            if (adj != null && adj.Length == nodes.Count) return;
            adj = new List<int>[nodes.Count];
            for (int i = 0; i < nodes.Count; i++) adj[i] = new List<int>();
            foreach (var s in segments)
            {
                if (s.a < 0 || s.b < 0 || s.a >= nodes.Count || s.b >= nodes.Count) continue;
                adj[s.a].Add(s.b);
                adj[s.b].Add(s.a);
            }
        }

        private void EnsureTile()
        {
            if (tileReady) return;
            tileReady = true;
            tile = default;
            tileFine = default;
            if (!FirstMesh(tileStraight, out Mesh m, out Matrix4x4 toRoot)) return;
            tile = RoadMeshWarp.Prepare(m, toRoot, lengthOnZ, false);
            tileFine = RoadMeshWarp.Prepare(m, toRoot, lengthOnZ, true);
            if (!tile.valid)
                Debug.LogWarning($"[RoadNetwork] Mesh de tuile illisible ou vide ({tileStraight?.name}). " +
                                 "Coche Read/Write Enabled sur le FBX (l'inspector du RoadNetwork propose un bouton).", this);
        }

        // ---------------------------------------------------------------- croisements

        // `log` est faux quand l'appel vient des gizmos : sinon les warnings partiraient a
        // chaque repaint.
        private void ComputeJunctions(bool log)
        {
            splineCache = null;   // les bras bougent -> toutes les splines sont perimees
            junctions = new Junction[nodes.Count];
            if (boundsCache == null) boundsCache = new Dictionary<GameObject, Bounds>();

            for (int i = 0; i < nodes.Count; i++)
            {
                var list = adj[i];
                int m = list.Count;
                var j = new Junction { armOfNeighbour = new int[m] };
                junctions[i] = j;
                if (m == 0) { j.armYaw = new float[0]; j.armRadius = new float[0]; continue; }

                // Directions monde vers chaque voisin, projetees sur XZ.
                var dirs = new Vector3[m];
                var beta = new float[m];
                for (int n = 0; n < m; n++)
                {
                    Vector3 d = nodes[list[n]].pos - nodes[i].pos;
                    d.y = 0f;
                    dirs[n] = d.sqrMagnitude < 1e-6f ? Vector3.forward : d.normalized;
                    beta[n] = Mathf.Atan2(dirs[n].x, dirs[n].z) * Mathf.Rad2Deg;
                }

                // Degre 2 : AUCUN asset, la spline traverse. La tangente est la bissectrice
                // traversante (Catmull-Rom) -> pas de pli au noeud. Le drapeau rond-point est
                // ignore ici, il ne vaut qu'a 4 branches.
                if (m == 2)
                {
                    Vector3 through = dirs[1] - dirs[0];
                    if (through.sqrMagnitude < 1e-6f) through = dirs[1];
                    through.Normalize();
                    j.yaw = Mathf.Atan2(through.x, through.z) * Mathf.Rad2Deg + nodes[i].yaw;
                    j.armYaw = new[] { 0f, 180f };
                    j.armRadius = new[] { 0f, 0f };
                    int fwd = Vector3.Dot(dirs[0], through) >= Vector3.Dot(dirs[1], through) ? 0 : 1;
                    j.armOfNeighbour[fwd] = 0;
                    j.armOfNeighbour[1 - fwd] = 1;
                    j.bad = Mathf.Abs(Mathf.DeltaAngle(beta[0], beta[1])) < MinBranchGap;
                    continue;
                }

                // Au-dela de 4 branches le kit n'a AUCUN asset : le rond-point est une variante
                // du carrefour 4 voies, pas un fourre-tout. On degrade en etoile (bras au centre)
                // + gizmo rouge, c'est a l'auteur de scinder le noeud.
                if (m >= 5)
                {
                    if (log)
                        Debug.LogWarning($"[RoadNetwork] Noeud {i} a {m} branches : aucun asset ne " +
                                         "gere ca. Scinde-le en deux carrefours.", this);
                    FallbackStar(j, beta, i, m);
                    continue;
                }

                // Rond-point : variante MANUELLE du carrefour 4 voies uniquement. Geometrie de
                // bras continue -> chaque bras pointe exactement vers son voisin.
                if (m == 4 && nodes[i].roundabout)
                {
                    j.asset = roundabout;
                    float r = AssetRadius(roundabout, out Vector3 rc);
                    j.recenter = rc;
                    j.yaw = 0f;   // asset circulaire : le tourner ne change rien, on ignore node.yaw
                    j.armYaw = new float[m];
                    j.armRadius = new float[m];
                    for (int n = 0; n < m; n++)
                    {
                        j.armYaw[n] = beta[n];
                        j.armRadius[n] = Mathf.Max(0f, r - armTrim);
                        j.armOfNeighbour[n] = n;
                    }
                    continue;
                }

                // Degres 1 / 3 / 4 : asset a bras fixes -> on AJUSTE son yaw sur les vraies
                // directions des branches.
                float[] armsSrc;
                if (m == 1) { j.asset = deadEnd; armsSrc = armsDeadEnd; }
                else if (m == 3) { j.asset = junction3; armsSrc = arms3; }
                else { j.asset = junction4; armsSrc = arms4; }

                // Asset manquant ou tableau de bras mal renseigne : meme degradation.
                if (j.asset == null || armsSrc == null || armsSrc.Length < m)
                {
                    FallbackStar(j, beta, i, m);
                    continue;
                }

                if (!AssetBounds(j.asset, out Bounds ab)) { j.bad = true; }
                Vector3 ext = ab.extents;
                j.recenter = new Vector3(-ab.center.x, -RoadwayY(j.asset, ab), -ab.center.z);

                // bras tries croissants (un bras n'a pas d'identite au-dela de son yaw)
                var alpha = new float[m];
                System.Array.Copy(armsSrc, alpha, m);
                System.Array.Sort(alpha);

                // branches triees croissantes : order[k] = index voisin de la k-eme branche
                var order = new int[m];
                for (int n = 0; n < m; n++) order[n] = n;
                System.Array.Sort(order, (x, y) => beta[x].CompareTo(beta[y]));

                j.yaw = FitYaw(beta, order, alpha, out int shift, out float residual) + nodes[i].yaw;
                j.armYaw = alpha;
                j.armRadius = new float[m];
                for (int n = 0; n < m; n++)
                {
                    j.armRadius[n] = ArmRadius(ext, alpha[n], armTrim);
                    j.armOfNeighbour[order[(n + shift) % m]] = n;
                }

                j.bad |= residual > BadFitDeg;
                for (int n = 0; n < m && !j.bad; n++)
                    for (int p = n + 1; p < m && !j.bad; p++)
                        if (Mathf.Abs(Mathf.DeltaAngle(beta[n], beta[p])) < MinBranchGap) j.bad = true;
            }

            // Seconde passe : le tangage a besoin des bras deja attribues, et chaque branche
            // ci-dessus sort par `continue`. Une passe separee evite de la repeter cinq fois.
            for (int i = 0; i < nodes.Count; i++) ComputePitch(i);
        }

        // Degradation quand aucun asset ne convient : pas de croisement, les bras pointent droit
        // sur les voisins avec un rayon nul -> les routes convergent au centre du noeud. Moche
        // mais connecte, et le gizmo rouge dit a l'auteur qu'il y a quelque chose a corriger.
        private void FallbackStar(Junction j, float[] beta, int node, int m)
        {
            j.asset = null;
            j.yaw = nodes[node].yaw;
            j.armYaw = new float[m];
            j.armRadius = new float[m];
            for (int n = 0; n < m; n++)
            {
                j.armYaw[n] = beta[n] - nodes[node].yaw;
                j.armOfNeighbour[n] = n;
            }
            j.bad = true;
        }

        // Ajuste le yaw de l'asset sur les directions reelles des branches. Forme fermee : pour
        // chaque decalage cyclique du couplage bras<->branche, la MOYENNE CIRCULAIRE des erreurs
        // est l'optimum moindres carres sur le cercle. (Une moyenne arithmetique casserait a la
        // couture +-180 deg : c'est le seul endroit ou une implementation naive se plante.)
        private static float FitYaw(float[] beta, int[] order, float[] alpha, out int shift, out float residual)
        {
            int m = alpha.Length;
            float best = float.MaxValue, bestTheta = 0f;
            shift = 0;
            for (int s = 0; s < m; s++)
            {
                float sumSin = 0f, sumCos = 0f;
                for (int n = 0; n < m; n++)
                {
                    float e = Mathf.DeltaAngle(alpha[n], beta[order[(n + s) % m]]) * Mathf.Deg2Rad;
                    sumSin += Mathf.Sin(e);
                    sumCos += Mathf.Cos(e);
                }
                float theta = Mathf.Atan2(sumSin, sumCos) * Mathf.Rad2Deg;
                float r = 0f;
                for (int n = 0; n < m; n++)
                {
                    float d = Mathf.DeltaAngle(alpha[n] + theta, beta[order[(n + s) % m]]);
                    r += d * d;
                }
                if (r < best) { best = r; bestTheta = theta; shift = s; }
            }
            residual = Mathf.Sqrt(best / m);   // ecart-type angulaire, en degres
            return bestTheta;
        }

        // Distance du centre au bout du bras de yaw local `yawLocal`, depuis les demi-extents.
        // Pour un bras axe-aligne ca vaut simplement ext.z (0/180) ou ext.x (90/270), et ca reste
        // juste si quelqu'un configure un bras en diagonale.
        private static float ArmRadius(Vector3 ext, float yawLocal, float trim)
        {
            Vector3 d = Quaternion.Euler(0f, yawLocal, 0f) * Vector3.forward;
            float rx = Mathf.Abs(d.x) > 1e-4f ? ext.x / Mathf.Abs(d.x) : float.MaxValue;
            float rz = Mathf.Abs(d.z) > 1e-4f ? ext.z / Mathf.Abs(d.z) : float.MaxValue;
            return Mathf.Max(0f, Mathf.Min(rx, rz) - trim);
        }

        private float AssetRadius(GameObject go, out Vector3 recenter)
        {
            recenter = Vector3.zero;
            if (!AssetBounds(go, out Bounds b)) return 0f;
            recenter = new Vector3(-b.center.x, -RoadwayY(go, b), -b.center.z);
            return Mathf.Max(b.extents.x, b.extents.z);
        }

        // Direction et bout du bras du noeud `node` vers son voisin `neighbour`, en LOCAL.
        // Repere du noeud : yaw autour de la verticale, puis tangage autour de son propre axe
        // transversal. Dans cet ordre un bras perpendiculaire reste horizontal pendant que le
        // bras dans l'axe monte -- exactement ce que fait un vrai carrefour en devers.
        private static Quaternion NodeRotation(Junction j)
            => Quaternion.AngleAxis(j.yaw, Vector3.up) * Quaternion.AngleAxis(-j.pitch, Vector3.right);

        // PIVOT du tangage. Un carrefour en devers bascule autour de son centre : ce qui
        // s'enfonce d'un cote ressort de l'autre, et les routes qui en partent suivent.
        //
        // Une IMPASSE, non : rien ne continue derriere elle. En la basculant sur son centre,
        // son bout ferme passe SOUS le sol -- c'est le pied de rampe qui s'enterre. Elle doit
        // donc basculer autour de ce bout ferme, qui reste posé au niveau du noeud pendant que
        // le bras se leve. Le pied de rampe devient un coin pose par terre.
        private static float PitchLift(Junction j, int degree)
        {
            if (degree != 1 || j.armYaw == null || j.armYaw.Length == 0) return 0f;
            Vector3 arm = Quaternion.AngleAxis(j.armYaw[0], Vector3.up) * Vector3.forward;
            Vector3 closed = -arm * j.armRadius[0];
            return -(NodeRotation(j) * closed).y;
        }

        // TANGAGE du noeud, au sens des moindres carres sur ses branches.
        //
        // Coucher le noeud de `p` donne au bras d'azimut theta une pente de p*cos(theta) : le
        // bras dans l'axe prend toute la pente, le bras oppose la prend a l'envers, le bras
        // perpendiculaire reste plat. On cherche donc le p qui approche au mieux les pentes
        // reelles vers chaque voisin. Exact a 1 et 2 branches (impasse, virage) -- les seuls
        // cas ou un ouvrage se termine -- et compromis a 3/4, comme le fit de yaw juste au-dessus.
        private void ComputePitch(int i)
        {
            var j = junctions[i];
            if (j == null || j.armYaw == null || j.armYaw.Length == 0) return;

            var list = adj[i];
            float num = 0f, den = 0f;
            // Bornes : AUCUN bras ne doit partir vers le bas quand sa branche ne descend pas.
            // Sans elles, un noeud entre une rampe qui monte et une rue plate se couche a
            // mi-chemin : la branche plate part en piquant, s'enfonce dans le sol et remonte.
            // Un bras qui part trop HAUT, lui, ne gene pas -- il est en l'air, la spline le
            // rattrape sans rien traverser. D'ou une contrainte asymetrique.
            float lo = float.NegativeInfinity, hi = float.PositiveInfinity;

            for (int n = 0; n < list.Count && n < j.armOfNeighbour.Length; n++)
            {
                Vector3 d = nodes[list[n]].pos - nodes[i].pos;
                float dxz = new Vector2(d.x, d.z).magnitude;
                if (dxz < 1e-3f) continue;
                int arm = j.armOfNeighbour[n];
                if (arm < 0 || arm >= j.armYaw.Length) continue;
                float c = Mathf.Cos(j.armYaw[arm] * Mathf.Deg2Rad);
                float s = d.y / dxz;
                num += s * c;
                den += c * c;

                // Plancher du bras : la pente de sa branche, ou l'horizontale si elle monte.
                // (L'horizontale tient lieu de sol : le projet n'a pas encore de relief.)
                if (Mathf.Abs(c) < 1e-3f) continue;
                float floorSlope = Mathf.Min(s, 0f);
                if (c > 0f) lo = Mathf.Max(lo, floorSlope / c);
                else hi = Mathf.Min(hi, floorSlope / c);
            }

            if (den <= 1e-4f) { j.pitch = 0f; return; }

            float t = num / den;
            if (lo <= hi) t = Mathf.Clamp(t, lo, hi);
            else t = 0f;                       // bornes incompatibles -> on reste a plat
            j.pitch = Mathf.Clamp(Mathf.Atan(t) * Mathf.Rad2Deg, -MaxPitch, MaxPitch);
        }

        private bool Arm(int node, int neighbour, out Vector3 pos, out Vector3 dir)
        {
            pos = nodes[node].pos;
            dir = Vector3.forward;
            var j = junctions[node];
            int n = adj[node].IndexOf(neighbour);
            if (j == null || n < 0 || j.armYaw.Length == 0) return false;
            int arm = j.armOfNeighbour[n];
            // Le bras suit le TANGAGE du noeud, il ne sort plus a plat. C'est ce qui rend une
            // rampe droite droite : sans ca la spline part horizontale et doit remonter en S
            // pour rattraper la pente, avec un plat au noeud et une bosse au milieu.
            dir = NodeRotation(j) * (Quaternion.AngleAxis(j.armYaw[arm], Vector3.up) * Vector3.forward);
            // Meme releve que l'asset, sinon le segment repart du sol et laisse une marche.
            pos = nodes[node].pos + dir * j.armRadius[arm]
                + Vector3.up * PitchLift(j, adj[node].Count);
            return true;
        }

        // ---------------------------------------------------------------- meshes

        private void BuildJunction(Transform root, int i)
        {
            var j = junctions[i];
            Transform existing = root.Find("Jct_" + i);

            if (j == null || j.asset == null)
            {
                if (existing != null) { DestroyChildren(existing); DestroyImmediate(existing.gameObject); }
                return;
            }

            // Les assets du kit sont des coques ouvertes (SM_Dead_end : 10 triangles vers le bas
            // sur 184). Au sol ca ne se voit pas ; en l'air -- une impasse qui coiffe une rampe
            // de saut, un croisement sur tablier -- on voit le marquage au sol par en dessous.
            bool needCap = nodes[i].pos.y > CapAbove;
            bool hasCap = existing != null && existing.Find(CapName) != null;

            // Meme asset qu'avant (cas du drag) : on ne touche que le transform.
            if (existing != null && existing.childCount >= 1 && hasCap == needCap
                && existing.GetChild(0).name == j.asset.name)
            {
                existing.localPosition = nodes[i].pos + Vector3.up * (SurfaceY + PitchLift(j, NodeDegree(i)));
                existing.localRotation = NodeRotation(j);
                return;
            }
            if (existing != null) { DestroyChildren(existing); DestroyImmediate(existing.gameObject); }

            var wrapper = new GameObject("Jct_" + i);
            wrapper.transform.SetParent(root, false);
            wrapper.transform.localPosition = nodes[i].pos + Vector3.up * (SurfaceY + PitchLift(j, NodeDegree(i)));
            wrapper.transform.localRotation = NodeRotation(j);

            GameObject inst = Instantiate(j.asset);
            inst.name = j.asset.name;
            inst.transform.SetParent(wrapper.transform, false);
            inst.transform.localPosition = j.recenter;
            inst.transform.localRotation = Quaternion.identity;

            Material mat = RoadMat();
            foreach (var mr in inst.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mats = new Material[mr.sharedMaterials.Length == 0 ? 1 : mr.sharedMaterials.Length];
                for (int k = 0; k < mats.Length; k++) mats[k] = mat;
                mr.sharedMaterials = mats;
            }
            if (!dragging)
                foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(true))
                    AddCollider(mf.gameObject, mf.sharedMesh);

            if (needCap && AssetBounds(j.asset, out Bounds cb)) AddCap(wrapper.transform, cb, j.recenter.y, mat);

            SetFlagsRecursive(wrapper.transform);
        }

        // Quad plein sous un croisement pose en l'air, a l'aplomb exact de son emprise. Le
        // winding (L0,R0,L1)/(R0,R1,L1) donne une normale vers le bas : plein vu d'en dessous,
        // culle vu d'en haut, donc incapable de masquer la chaussee.
        private void AddCap(Transform wrapper, Bounds ab, float recenterY, Material mat)
        {
            float hx = ab.extents.x, hz = ab.extents.z;
            float y = ab.min.y + recenterY - 0.01f;   // 1 cm plus bas : anti z-fighting avec les rares faces basses de l'asset

            var mesh = new Mesh { name = "JunctionCap" };
            mesh.SetVertices(new System.Collections.Generic.List<Vector3> {
                new Vector3(-hx, y, -hz), new Vector3(hx, y, -hz),
                new Vector3(-hx, y,  hz), new Vector3(hx, y,  hz) });
            mesh.SetNormals(new System.Collections.Generic.List<Vector3> {
                Vector3.down, Vector3.down, Vector3.down, Vector3.down });
            mesh.SetTriangles(new int[] { 0, 1, 2, 1, 3, 2 }, 0);
            mesh.RecalculateBounds();

            var go = new GameObject(CapName);
            go.transform.SetParent(wrapper, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        // La spline s'ecarte-t-elle assez d'une droite pour qu'une tuile pleine longueur se voie ?
        //
        // On mesure exactement l'erreur que ferait la tuile brute : sur chaque fenetre d'une
        // longueur de tuile, l'ecart entre le milieu de la courbe et le milieu de la corde --
        // c'est la fleche, donc le creux au milieu de la tuile. Pas la courbure globale : une
        // longue courbe douce s'ecarte de plusieurs metres d'une droite tout en restant plate
        // a l'echelle d'une tuile, et n'a pas besoin d'etre decoupee.
        private bool Curves(Spline spline)
        {
            if (!tileFine.valid) return false;
            float len = spline.GetLength();
            float win = Mathf.Min(tile.length, len);
            if (win < 1e-3f) return false;

            for (float d = 0f; d <= len - win + 1e-3f; d += Mathf.Max(0.5f, win * 0.25f))
                if (Sag(spline, d, win) > FlatTol) return true;
            return false;
        }

        private static float Sag(Spline spline, float d, float win)
        {
            Vector3 a = At(spline, d), b = At(spline, d + win), m = At(spline, d + win * 0.5f);
            return Vector3.Distance(m, (a + b) * 0.5f);
        }

        private static Vector3 At(Spline spline, float distance)
        {
            float t = spline.ConvertIndexUnit(distance, PathIndexUnit.Distance, PathIndexUnit.Normalized);
            spline.Evaluate(t, out float3 p, out float3 _, out float3 _2);
            return (Vector3)p;
        }

        // Axe d'un segment, en LOCAL : de bout de bras a bout de bras, tangentes imposees par les
        // croisements. Faux si le segment est degenere. Sert au mesh ET au picking a la souris.
        private bool SegmentSpline(int k, out Spline spline)
        {
            spline = null;
            if (k < 0 || k >= segments.Count) return false;
            var s = segments[k];
            if (s.a < 0 || s.b < 0 || s.a >= nodes.Count || s.b >= nodes.Count) return false;
            EnsureAdjacency();
            if (junctions == null || junctions.Length != nodes.Count) ComputeJunctions(false);
            if (!Arm(s.a, s.b, out Vector3 p0, out Vector3 d0)) return false;
            if (!Arm(s.b, s.a, out Vector3 p1, out Vector3 d1)) return false;

            d1 = -d1;   // sens de marche a l'arrivee
            Vector3 span = p1 - p0;
            Vector3 dir = span.normalized;
            float dot0 = Vector3.Dot(dir, d0), dot1 = Vector3.Dot(dir, d1);
            // Degenere : bras trop proches, ou bras qui SE CROISENT (l'arrivee est trop derriere
            // la direction de depart) -> la courbe se replie, mesh retourne. Un virage a angle
            // droit reste dessinable, d'ou une limite au-dela de 90 deg.
            if (span.magnitude < MinSegment || dot0 <= SharpLimit || dot1 <= SharpLimit) return false;

            float sharp = Mathf.Clamp01(Mathf.Min(dot0, dot1));

            // --- suivi du relief ---
            // Deux noeuds distants de 80 m relies en droite ENJAMBENT les creux du terrain : la
            // rue traverse la vallee en viaduc, avec garde-corps et piles a la clef. On pose donc
            // des points intermediaires cales sur le relief.
            //
            // Seulement si les DEUX bouts sont au sol : un ouvrage doit rester droit, c'est tout
            // son interet. Meme seuil que partout ailleurs.
            var ground = Terrain();
            int inner = 0;
            float lift0 = 0f, lift1 = 0f;
            if (ground != null)
            {
                Vector3 w0 = transform.TransformPoint(p0), w1 = transform.TransformPoint(p1);
                lift0 = w0.y - ground.Height(w0.x, w0.z);
                lift1 = w1.y - ground.Height(w1.x, w1.z);
                if (lift0 < CapAbove && lift1 < CapAbove)
                    inner = Mathf.Clamp(Mathf.FloorToInt(span.magnitude / TerrainKnotEvery) - 1, 0, 24);
            }

            // Tangentes raccourcies dans les virages serres : a pleine longueur la courbe fait
            // une boucle sur elle-meme des qu'on depasse ~70 deg. Mesurees sur la CORDE entre
            // deux points consecutifs et pas sur le segment entier, sinon les tangentes des bouts
            // depassent leur premier voisin et la route ondule.
            float chord = span.magnitude / (inner + 1);
            float h = Mathf.Max(0.01f, curvature * chord * Mathf.Lerp(0.45f, 1f, sharp));

            var knots = new BezierKnot[inner + 2];
            knots[0] = new BezierKnot(p0, new float3(0f, 0f, -h), new float3(0f, 0f, h),
                quaternion.LookRotationSafe(d0, math.up()));
            knots[inner + 1] = new BezierKnot(p1, new float3(0f, 0f, -h), new float3(0f, 0f, h),
                quaternion.LookRotationSafe(d1, math.up()));

            for (int i = 1; i <= inner; i++)
            {
                float t = (float)i / (inner + 1);
                Vector3 q = Vector3.Lerp(p0, p1, t);
                Vector3 w = transform.TransformPoint(q);
                // La hauteur des bouts est interpolee EN PLUS du relief : un bout legerement
                // leve (bordure de rampe) ne se fait pas rabattre au sol des le premier point.
                w.y = ground.Height(w.x, w.z) + Mathf.Lerp(lift0, lift1, t);
                knots[i] = Smooth(transform.InverseTransformPoint(w), p0, p1, ground, t, inner,
                                  lift0, lift1);
            }

            // TangentMode par defaut = Broken -> nos tangentes sont conservees telles quelles.
            spline = new Spline(knots, false);
            return true;
        }

        // Noeud intermediaire en Catmull-Rom : la tangente regarde ses deux voisins, ce qui donne
        // une pente continue d'un bout a l'autre. Les voisins sont recalcules et pas memorises --
        // la boucle appelante n'a alors rien a se trainer, et le cout est celui d'un Perlin.
        private BezierKnot Smooth(Vector3 pos, Vector3 p0, Vector3 p1, CityTerrain ground,
                                  float t, int inner, float lift0, float lift1)
        {
            float dt = 1f / (inner + 1);
            Vector3 prev = OnTerrain(p0, p1, t - dt, ground, lift0, lift1);
            Vector3 next = OnTerrain(p0, p1, t + dt, ground, lift0, lift1);
            Vector3 m = (next - prev) * 0.5f;
            float mag = Mathf.Max(0.01f, m.magnitude) / 3f;
            Vector3 dir = m.sqrMagnitude > 1e-8f ? m.normalized : (p1 - p0).normalized;
            return new BezierKnot(pos, new float3(0f, 0f, -mag), new float3(0f, 0f, mag),
                                  quaternion.LookRotationSafe(dir, math.up()));
        }

        private Vector3 OnTerrain(Vector3 p0, Vector3 p1, float t, CityTerrain ground,
                                  float lift0, float lift1)
        {
            Vector3 w = transform.TransformPoint(Vector3.Lerp(p0, p1, t));
            w.y = ground.Height(w.x, w.z) + Mathf.Lerp(lift0, lift1, t);
            return transform.InverseTransformPoint(w);
        }

        private void BuildSegment(Transform root, int k)
        {
            Transform existing = root.Find("Seg_" + k);
            if (!tile.valid || !SegmentSpline(k, out Spline spline))
            {
                if (existing != null) { DestroyChildren(existing); DestroyImmediate(existing.gameObject); }
                return;
            }

            // Reutilise le GameObject et le Mesh existants : c'est ce qui rend le drag fluide.
            GameObject go;
            MeshFilter mf;
            if (existing != null)
            {
                go = existing.gameObject;
                mf = go.GetComponent<MeshFilter>();
            }
            else
            {
                go = new GameObject("Seg_" + k);
                go.transform.SetParent(root, false);
                mf = go.AddComponent<MeshFilter>();
                go.AddComponent<MeshRenderer>().sharedMaterial = RoadMat();
                go.hideFlags = GenFlags;
            }
            go.transform.localPosition = Vector3.up * SurfaceY;

            var deck = new RoadMeshWarp.Deck
            {
                fascia = Mathf.Max(0f, deckFascia),
                parapet = IsOverpass(spline) ? Mathf.Max(0f, parapetHeight) : 0f,
                parapetBase = SidewalkHeight
            };

            Mesh mesh = RoadMeshWarp.Warp(Curves(spline) ? tileFine : tile, spline, mf.sharedMesh, deck);
            if (mesh == null) { DestroyChildren(go.transform); DestroyImmediate(go); return; }
            mesh.name = "RoadSeg_" + k;
            mesh.hideFlags = HideFlags.DontSave;
            mf.sharedMesh = mesh;

            var mc = go.GetComponent<MeshCollider>();
            if (dragging)
            {
                // Un MeshCollider garde une copie cuisinee du mesh : le laisser branche pendant
                // le drag relancerait un Physics.BakeMesh a chaque frame.
                if (mc != null) DestroyImmediate(mc);
            }
            else AddCollider(go, mesh);

            // Les piles sont des objets a part et pas des sommets du tablier : elles ont besoin
            // de la hauteur du SOL sous chaque point, que le warp ignore, et un cube portant son
            // BoxCollider donne la collision gratuitement (on peut se planter dedans).
            BuildPiers(go.transform, spline, deck);
        }

        // Un segment est un OUVRAGE des qu'il passe franchement au-dessus du sol. Meme seuil que
        // le dessous des croisements et que la non-perforation du sol : un seul chiffre a bouger.
        private bool IsOverpass(Spline spline)
        {
            return DeckClearance(spline) > CapAbove;
        }

        // Hauteur maximale du tablier au-dessus du terrain, en metres.
        private float DeckClearance(Spline spline)
        {
            float best = 0f;
            for (int i = 0; i <= 8; i++)
            {
                Vector3 w = transform.TransformPoint(SplinePoint(spline, i / 8f));
                best = Mathf.Max(best, w.y - GroundUnder(w));
            }
            return best;
        }

        private Vector3 SplinePoint(Spline spline, float t)
        {
            spline.Evaluate(t, out float3 p, out _, out _);
            return (Vector3)p;
        }

        // Sol sous un point du monde : le relief s'il y en a un, sinon le plan de secours.
        private float GroundUnder(Vector3 world)
        {
            var t = Terrain();
            return t != null ? t.Height(world.x, world.z) : transform.position.y + groundHeight;
        }

        private void BuildPiers(Transform seg, Spline spline, RoadMeshWarp.Deck deck)
        {
            for (int i = seg.childCount - 1; i >= 0; i--)
                if (seg.GetChild(i).name.StartsWith(PierName)) DestroyImmediate(seg.GetChild(i).gameObject);

            if (pierEvery < 1f || dragging || !IsOverpass(spline)) return;

            float len = spline.GetLength();
            // Une pile a chaque bout ferait doublon avec la culee, qui est deja un remblai : on
            // repartit les piles A L'INTERIEUR de la portee.
            int count = Mathf.FloorToInt(len / pierEvery);
            if (count < 1) return;

            float deckBottom = tile.bottom - deck.fascia;
            for (int i = 1; i <= count; i++)
            {
                Vector3 local = SplinePoint(spline, (float)i / (count + 1));
                Vector3 world = transform.TransformPoint(local);
                float ground = GroundUnder(world);
                float top = world.y + deckBottom + SurfaceY;
                float h = top - ground;
                // Sous cette hauteur la pile serait un caillou coince entre le sol et le tablier :
                // c'est le cas des abords d'ouvrage, ou le remblai monte deja chercher la route.
                if (h < 1f) continue;

                var pier = GameObject.CreatePrimitive(PrimitiveType.Cube);
                pier.name = PierName + i;
                pier.hideFlags = GenFlags;
                pier.GetComponent<MeshRenderer>().sharedMaterial = RoadMat();
                pier.transform.SetParent(seg, false);
                // seg porte deja SurfaceY : on repasse en local pour que la pile suive le segment.
                pier.transform.position = new Vector3(world.x, ground + h * 0.5f, world.z);
                pier.transform.rotation = Quaternion.identity;
                pier.transform.localScale = new Vector3(pierSize, h, pierSize);
            }
        }

        private static void AddCollider(GameObject go, Mesh mesh)
        {
            if (mesh == null || go.GetComponent<MeshCollider>() != null) return;
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
        }

        // Les colliders sont differes pendant le drag : le warp est peu cher, mais chaque
        // affectation de MeshCollider.sharedMesh declenche un Physics.BakeMesh (2-5 ms).
        private void ApplyMissingColliders()
        {
            Transform root = transform.Find(ContainerName);
            if (root == null) return;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                AddCollider(mf.gameObject, mf.sharedMesh);
        }

        private Material RoadMat()
        {
            if (roadMaterial != null) return roadMaterial;
            if (runtimeMat != null) return runtimeMat;
            Shader sh = toonShading ? Shader.Find("GMTK/ToonLit") : null;
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
            runtimeMat = new Material(sh) { name = "RoadMat", hideFlags = HideFlags.DontSave };
            if (runtimeMat.HasProperty("_BaseColor")) runtimeMat.SetColor("_BaseColor", asphaltColor);
            if (runtimeMat.HasProperty("_Smoothness")) runtimeMat.SetFloat("_Smoothness", 0.1f);
            return runtimeMat;
        }

        private static void SetFlagsRecursive(Transform t)
        {
            t.gameObject.hideFlags = GenFlags;
            for (int i = 0; i < t.childCount; i++) SetFlagsRecursive(t.GetChild(i));
        }

        // Detruit les meshes generes avant de jeter le GameObject : DontSave ne les libere
        // qu'au reload de domaine, sinon ils s'accumulent a chaque drag.
        private static void DestroyChildren(Transform t)
        {
            foreach (var mf in t.GetComponentsInChildren<MeshFilter>(true))
            {
                var m = mf.sharedMesh;
                // Egalite EXACTE et pas un test de bit : les meshes integres de Unity (le cube
                // des piles) portent HideAndDontSave, donc DontSave parmi d'autres drapeaux, et
                // les detruire est refuse -- "Destroying assets is not permitted". Nos meshes a
                // nous portent DontSave et rien d'autre.
                if (m != null && m.hideFlags == HideFlags.DontSave) DestroyImmediate(m);
            }
        }

        // ---------------------------------------------------------------- lecture d'assets

        // Bounds combinees d'un asset dans l'espace local de sa racine (sans l'instancier).
        private bool AssetBounds(GameObject go, out Bounds b)
        {
            b = new Bounds();
            if (go == null) return false;
            if (boundsCache != null && boundsCache.TryGetValue(go, out b)) return true;

            bool any = false;
            Matrix4x4 w2l = go.transform.worldToLocalMatrix;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh m = mf.sharedMesh;
                if (m == null) continue;
                Matrix4x4 mat = w2l * mf.transform.localToWorldMatrix;
                Bounds mb = m.bounds;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents, new Vector3(
                        (c & 1) == 0 ? -1f : 1f, (c & 2) == 0 ? -1f : 1f, (c & 4) == 0 ? -1f : 1f));
                    Vector3 p = mat.MultiplyPoint3x4(corner);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                    else b.Encapsulate(p);
                }
            }
            if (any && boundsCache != null) boundsCache[go] = b;
            return any;
        }

        // Hauteur de la chaussee d'un asset, cachee. Repli sur bounds.max.y si le mesh n'est pas
        // lisible -> l'asset sera trop bas, mais rien ne casse.
        private float RoadwayY(GameObject go, Bounds fallback)
        {
            if (roadwayCache == null) roadwayCache = new Dictionary<GameObject, float>();
            if (roadwayCache.TryGetValue(go, out float y)) return y;
            y = FirstMesh(go, out Mesh m, out Matrix4x4 toRoot)
                && RoadMeshWarp.RoadwayHeight(m, toRoot, out float rh) ? rh : fallback.max.y;
            roadwayCache[go] = y;
            return y;
        }

        // Premier mesh de l'asset + sa matrice vers la racine. Les SM_* n'ont qu'un mesh chacun ;
        // si un kit en avait plusieurs, seul le premier serait deforme.
        private static bool FirstMesh(GameObject go, out Mesh mesh, out Matrix4x4 toRoot)
        {
            mesh = null;
            toRoot = Matrix4x4.identity;
            if (go == null) return false;
            Matrix4x4 w2l = go.transform.worldToLocalMatrix;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                mesh = mf.sharedMesh;
                toRoot = w2l * mf.transform.localToWorldMatrix;
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- gizmos

        private void OnDrawGizmosSelected()
        {
            if (nodes.Count == 0) return;
            EnsureAdjacency();
            if (junctions == null || junctions.Length != nodes.Count) ComputeJunctions(false);

            for (int i = 0; i < nodes.Count; i++)
            {
                Vector3 p = NodeWorld(i);
                var j = junctions[i];
                Gizmos.color = j != null && j.bad ? Color.red : new Color(0.3f, 1f, 0.4f);
                Gizmos.DrawWireSphere(p, 1.2f);

                if (!showArms || j == null || j.armYaw == null) continue;
                Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.9f);
                for (int n = 0; n < j.armYaw.Length; n++)
                {
                    Vector3 d = transform.TransformDirection(
                        Quaternion.Euler(0f, j.yaw + j.armYaw[n], 0f) * Vector3.forward);
                    Vector3 tip = p + d * Mathf.Max(1f, j.armRadius[n]);
                    Gizmos.DrawLine(p, tip);
                    Gizmos.DrawWireCube(tip, Vector3.one * 0.6f);
                }
            }
        }
    }
}
