using System.Collections.Generic;
using Gameplay.City;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Gameplay.EditorTools
{
    // PINCEAU DE VILLE, facon peinture de feuillage Unreal.
    //
    // Pourquoi un EditorTool et pas un [CustomEditor] + OnSceneGUI comme RoadNetworkEditor : ce
    // geste-ci est MODAL, pas un modificateur. Le clic nu doit PEINDRE, pas selectionner -- soit
    // l'inverse exact du traceur de routes, qui tient a preserver la selection Unity. En
    // pratique la difference tient en un mot : on avale le clic sans condition de shift.
    //   RoadNetworkEditor : if (e.type == Layout && e.shift) HandleUtility.AddDefaultControl(id);
    //   ici               : if (e.type == Layout)            HandleUtility.AddDefaultControl(id);
    // Et Unity gere l'entree/sortie d'outil (barre d'outils, Echap, W/E/R), donc pas de booleen
    // "pinceau arme" a maintenir a travers les domain reloads.
    //
    // Cote peinture il n'y a AUCUN mode a choisir sur la couche Batiments : le plan de ville est
    // calcule par CityBrushPlacement au debut du trait, et le pinceau ne fait que reveler les
    // candidats sous son disque. La couche Props, elle, reste une bascule explicite (touche B) :
    // saupoudrer des cheminees n'est pas la meme intention que batir un quartier.
    [EditorTool("Pinceau ville", typeof(CityBrush))]
    public class CityBrushTool : EditorTool
    {
        const float RayLength = 5000f;

        // --- etat du TRAIT ---
        private bool painting;
        private bool erasing;
        private int undoGroup = -1;
        private int strokeId;
        private int emitted;
        private Vector3 lastEmit;
        private bool hasLastEmit;

        // Plan de ville cote rues, rebati au debut de chaque trait : quelques centaines
        // d'entrees, ca ne vaut pas une invalidation de cache fine, et ca ramasse au passage les
        // reglages changes entre deux traits.
        private readonly List<CityBrushPlacement.Placement> lanePlan = new List<CityBrushPlacement.Placement>();
        // Emprises de TOUT le plan des rues, disque ou pas : la grille doit les respecter meme
        // hors du coup de pinceau courant, sinon un coeur d'ilot pose maintenant bloquerait le
        // front de rue qu'on peindra deux traits plus tard.
        private readonly List<Bounds> laneReserved = new List<Bounds>();
        // Deux listes SEPAREES, et c'est structurel : un prop vit sur le toit d'un batiment,
        // donc dans son emprise XZ. Les melanger revient a interdire tout prop.
        private readonly List<Bounds> placed = new List<Bounds>();       // batiments
        private readonly List<Bounds> propPlaced = new List<Bounds>();   // props
        // Plan du mobilier de RUE, meme nature que lanePlan : un champ deterministe calcule au
        // debut du trait, dont le disque ne fait que reveler une part.
        private readonly List<CityBrushPlacement.Placement> streetPlan = new List<CityBrushPlacement.Placement>();
        private readonly List<CityBrushPlacement.Placement> buffer = new List<CityBrushPlacement.Placement>();

        private Vector3 cursorPos;
        private Vector3 cursorNormal = Vector3.up;
        private bool cursorValid;

        public override GUIContent toolbarIcon =>
            EditorGUIUtility.IconContent("Grid.PaintTool", "|Pinceau ville");

        public override void OnActivated()
        {
            ResetStroke();
            Undo.undoRedoPerformed += OnUndoRedo;
            SceneView.RepaintAll();
        }

        public override void OnWillBeDeactivated()
        {
            EndStroke();
            Undo.undoRedoPerformed -= OnUndoRedo;
        }

        // Les emprises en cache referencent des objets qui viennent peut-etre d'etre annules :
        // sans ca, le rejet de chevauchement se bat contre des fantomes.
        private void OnUndoRedo() { placed.Clear(); propPlaced.Clear(); }

        // ---------------------------------------------------------------- boucle

        public override void OnToolGUI(EditorWindow window)
        {
            if (window is not SceneView) return;
            if (target is not CityBrush brush) return;

            Event e = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(id);

            erasing = e.control && !e.alt;

            if (!Ready(brush, out string probleme))
            {
                DrawHud(brush, probleme);
                return;
            }

            UpdateCursor(brush, e);
            HandleResize(brush, e);
            HandleKeys(brush, e);
            HandleMouse(brush, e, id);
            DrawCursor(brush, e);
            DrawHud(brush, null);

            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
                SceneView.RepaintAll();
        }

        private static bool Ready(CityBrush brush, out string probleme)
        {
            probleme = null;
            if (brush.palette == null) { probleme = "Aucune palette assignee sur le CityBrush."; return false; }
            if (brush.palette.CountUsable(brush.layer) == 0)
            {
                probleme = $"La palette n'a aucune entree utilisable sur la couche {brush.layer}.";
                return false;
            }
            return true;
        }

        private void UpdateCursor(CityBrush brush, Event e)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            cursorValid = Probe(brush, brush.layer, ray, out cursorPos, out cursorNormal);
        }

        // ---------------------------------------------------------------- entrees

        // Maj + molette redimensionne. C'est le geste standard (Unreal, Blender, Photoshop) et le
        // seul qui ne depende ni de la disposition du clavier ni d'un modificateur deja pris :
        // Ctrl efface, Alt appartient a l'orbite de la SceneView.
        private void HandleResize(CityBrush brush, Event e)
        {
            if (e.type != EventType.ScrollWheel || !e.shift || e.alt) return;

            // Windows traduit Maj+molette en defilement HORIZONTAL : delta.y tombe a zero et la
            // valeur passe dans delta.x. Lire le seul delta.y donnait un signe toujours faux,
            // donc un pinceau qui grossissait dans les deux sens. On prend l'axe qui bouge.
            float raw = Mathf.Abs(e.delta.y) >= Mathf.Abs(e.delta.x) ? e.delta.y : e.delta.x;
            if (Mathf.Abs(raw) < 1e-4f) return;

            Undo.RecordObject(brush, "Rayon du pinceau");
            float k = raw > 0f ? 1f / 1.12f : 1.12f;
            brush.radius = Mathf.Clamp(brush.radius * k, CityBrush.MinRadius, CityBrush.MaxRadius);
            EditorUtility.SetDirty(brush);
            e.Use();               // sinon la SceneView zoome par-dessus
            SceneView.RepaintAll();
        }

        private void HandleKeys(CityBrush brush, Event e)
        {
            if (e.type != EventType.KeyDown) return;
            bool used = true;
            switch (e.keyCode)
            {
                case KeyCode.LeftBracket:
                case KeyCode.Minus:
                case KeyCode.KeypadMinus:
                    Undo.RecordObject(brush, "Rayon du pinceau");
                    brush.radius = Mathf.Clamp(brush.radius / 1.18f, CityBrush.MinRadius, CityBrush.MaxRadius);
                    break;
                case KeyCode.RightBracket:
                case KeyCode.Equals:
                case KeyCode.Plus:
                case KeyCode.KeypadPlus:
                    Undo.RecordObject(brush, "Rayon du pinceau");
                    brush.radius = Mathf.Clamp(brush.radius * 1.18f, CityBrush.MinRadius, CityBrush.MaxRadius);
                    break;
                case KeyCode.B:
                    Undo.RecordObject(brush, "Couche du pinceau");
                    // PropsRue n'est pas dans le cycle : elle se pose au bouton
                    // (Tools/Ville/Props/Garnir les rues), pas a la souris.
                    brush.layer = brush.layer switch
                    {
                        BrushLayer.Batiments => BrushLayer.Props,
                        BrushLayer.Props => BrushLayer.PropsRue,
                        BrushLayer.PropsRue => BrushLayer.PropsSol,
                        _ => BrushLayer.Batiments,
                    };
                    streetPlan.Clear();   // le plan appartient a la couche qu'on quitte
                    break;
                case KeyCode.R:
                    Undo.RecordObject(brush, "Regrainer la ville");
                    brush.seed = Random.Range(1, int.MaxValue);
                    lanePlan.Clear();
                    break;
                default: used = false; break;
            }
            if (!used) return;
            EditorUtility.SetDirty(brush);
            e.Use();
            SceneView.RepaintAll();
        }

        private void HandleMouse(CityBrush brush, Event e, int id)
        {
            if (e.alt) return;   // Alt = orbite de la SceneView, on n'y touche jamais

            switch (e.type)
            {
                case EventType.MouseDown when e.button == 0:
                    GUIUtility.hotControl = id;
                    BeginStroke(brush);
                    Paint(brush, true);
                    e.Use();
                    break;

                case EventType.MouseDrag when GUIUtility.hotControl == id:
                    Paint(brush, false);
                    e.Use();
                    break;

                case EventType.MouseUp when GUIUtility.hotControl == id:
                    GUIUtility.hotControl = 0;
                    EndStroke();
                    e.Use();
                    break;
            }
        }

        // ---------------------------------------------------------------- trait

        private void BeginStroke(CityBrush brush)
        {
            // IncrementCurrentGroup AVANT de capturer l'index : sinon le collapse final avalerait
            // l'action precedente de l'utilisateur, qui n'a rien a voir avec la peinture.
            Undo.IncrementCurrentGroup();
            undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(erasing ? "Effacer des batiments" : "Peindre la ville");

            painting = true;
            strokeId++;
            emitted = 0;
            hasLastEmit = false;
            RebuildPlaced(brush);

            lanePlan.Clear();
            laneReserved.Clear();
            streetPlan.Clear();
            if (erasing) return;

            if (brush.layer == BrushLayer.Batiments)
            {
                CityBrushPlacement.BuildLaneCandidates(
                    brush, Object.FindObjectsByType<RoadNetwork>(FindObjectsSortMode.None), lanePlan);
                foreach (var p in lanePlan) laneReserved.Add(p.footprintXZ);
            }
            else if (brush.layer == BrushLayer.PropsRue)
            {
                CityBrushPlacement.BuildStreetCandidates(
                    brush.palette, brush.seed,
                    Object.FindObjectsByType<RoadNetwork>(FindObjectsSortMode.None),
                    brush.maxPerStroke * 8, streetPlan);
            }
        }

        private void EndStroke()
        {
            if (!painting) return;
            painting = false;
            if (undoGroup >= 0) Undo.CollapseUndoOperations(undoGroup);
            undoGroup = -1;
        }

        private void ResetStroke()
        {
            painting = false;
            undoGroup = -1;
            emitted = 0;
            hasLastEmit = false;
            placed.Clear();
            propPlaced.Clear();
            lanePlan.Clear();
            laneReserved.Clear();
            streetPlan.Clear();
        }

        private void Paint(CityBrush brush, bool first)
        {
            if (!cursorValid) return;
            if (erasing) { Erase(brush); return; }

            // Un pas minimum entre deux emissions : sinon chaque pixel de deplacement relance un
            // balayage complet du plan pour rien.
            if (!first && hasLastEmit &&
                Vector3.Distance(lastEmit, cursorPos) < brush.radius * brush.strokeStep) return;
            lastEmit = cursorPos;
            hasLastEmit = true;

            if (emitted >= brush.maxPerStroke)
            {
                if (first) Debug.LogWarning($"[Pinceau] Limite de {brush.maxPerStroke} objets par trait atteinte.");
                return;
            }

            buffer.Clear();
            if (brush.layer == BrushLayer.PropsRue)
            {
                RevealStreet(brush);
            }
            else if (brush.layer != BrushLayer.Batiments)
            {
                // `placed` (les BATIMENTS) ne doit surtout pas servir ici : un prop pose sur un
                // toit est par construction DANS l'emprise XZ de son batiment, donc le rejet de
                // chevauchement le tuerait a tous les coups. Un prop ne se compare qu'aux autres
                // props.
                BrushLayer layer = brush.layer;
                CityBrushPlacement.PropCandidates(
                    brush, layer, cursorPos, strokeId, emitted,
                    (Vector3 a, out Vector3 p, out Vector3 n) => ProbeAt(brush, layer, a, out p, out n),
                    (Vector3 w, float d, out RoadNetwork.RoadProbe pr) => NearestNetwork(w, d, out pr) != null,
                    propPlaced, buffer);
            }
            else
            {
                RevealCity(brush);
            }

            foreach (var p in buffer) Spawn(brush, p);
            emitted += buffer.Count;
        }

        // Revele les candidats du plan qui tombent sous le disque. Les voies d'abord : quand un
        // candidat de rue et un de coeur d'ilot se disputent la meme place, c'est le front de rue
        // qui gagne, parce que c'est lui qui porte la lecture du quartier.
        private void RevealCity(CityBrush brush)
        {
            float r2 = brush.radius * brush.radius;
            int budget = brush.maxPerStroke - emitted;

            foreach (var p in lanePlan)
            {
                if (buffer.Count >= budget) return;
                float dx = p.position.x - cursorPos.x, dz = p.position.z - cursorPos.z;
                if (dx * dx + dz * dz > r2) continue;
                if (CityBrushPlacement.Overlaps(p.footprintXZ, placed)) continue;

                var q = p;
                q.position = new Vector3(p.position.x, GroundY(brush, p.position) - p.sink, p.position.z);
                buffer.Add(q);
                placed.Add(p.footprintXZ);
            }

            // GridCandidates fait son propre rejet (il en a besoin pour retenter une case) et
            // alimente `placed` au fur et a mesure -> rien a refiltrer ici.
            var grid = new List<CityBrushPlacement.Placement>();
            CityBrushPlacement.GridCandidates(brush, cursorPos, brush.radius,
                (Vector3 w, float d, out RoadNetwork.RoadProbe pr) => NearestNetwork(w, d, out pr) != null,
                laneReserved, placed, grid);

            foreach (var p in grid)
            {
                if (buffer.Count >= budget) return;
                var q = p;
                q.position = new Vector3(p.position.x, GroundY(brush, p.position) - p.sink, p.position.z);
                buffer.Add(q);
            }
        }

        // Revele le mobilier de rue du plan qui tombe sous le disque. Le rejet se fait contre les
        // props DEJA EN SCENE (`propPlaced`), ce qui rend le geste idempotent : repasser au meme
        // endroit ne double pas la rangee, et peindre une rue deja garnie au bouton ne pose rien.
        //
        // Contrairement a RevealCity, aucune hauteur a recalculer : le plan porte deja la cote du
        // trottoir, mesuree sur la route et non sur un collider.
        private void RevealStreet(CityBrush brush)
        {
            float r2 = brush.radius * brush.radius;
            int budget = brush.maxPerStroke - emitted;

            foreach (var p in streetPlan)
            {
                if (buffer.Count >= budget) return;
                float dx = p.position.x - cursorPos.x, dz = p.position.z - cursorPos.z;
                if (dx * dx + dz * dz > r2) continue;
                if (CityBrushPlacement.Overlaps(p.footprintXZ, propPlaced)) continue;

                buffer.Add(p);
                propPlaced.Add(p.footprintXZ);
            }
        }

        private void Erase(CityBrush brush)
        {
            Transform c = brush.Container;
            if (c == null) return;

            float r2 = brush.radius * brush.radius;
            for (int i = c.childCount - 1; i >= 0; i--)
            {
                Transform t = c.GetChild(i);
                Vector3 d = t.position - cursorPos;
                d.y = 0f;
                if (d.sqrMagnitude > r2) continue;
                // Undo.DestroyObjectImmediate et pas DestroyImmediate : sans le prefixe Undo, la
                // scene serait irrecuperable.
                Undo.DestroyObjectImmediate(t.gameObject);
            }
            RebuildPlaced(brush);
        }

        // ---------------------------------------------------------------- instanciation

        // Instanciation deleguee a CityPropsSpawn, partagee avec le seeder : un prop peint et un
        // prop seme doivent etre EXACTEMENT le meme objet (memes drapeaux statiques, meme
        // enregistrement d'undo), sinon ils ne se comportent pas pareil au rendu.
        private void Spawn(CityBrush brush, CityBrushPlacement.Placement p)
        {
            if (CityPropsSpawn.Spawn(p, EnsureContainer(brush), "Peindre la ville") != null)
                EditorSceneManager.MarkSceneDirty(brush.gameObject.scene);
        }

        private static Transform EnsureContainer(CityBrush brush)
        {
            Transform c = brush.Container;
            if (c != null) return c;

            var go = new GameObject(CityBrush.ContainerName);
            // AUCUN hideFlags, contrairement au "Generated" de RoadNetwork : ici c'est du
            // contenu d'AUTEUR, il doit etre sauve, selectionnable et editable a la main.
            go.transform.SetParent(brush.transform, false);
            Undo.RegisterCreatedObjectUndo(go, "Conteneur de batiments");
            return go.transform;
        }

        private void RebuildPlaced(CityBrush brush)
        {
            placed.Clear();
            propPlaced.Clear();
            Collect(brush, brush.Container);

            // Le conteneur du SEEDER compte aussi. Sans lui, peindre du mobilier de rue sur une
            // ville deja garnie au bouton reposait toute la rangee par-dessus elle-meme : les
            // deux outils revelent le meme plan, donc aux memes endroits, et le rejet ne voyait
            // rien.
            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
                if (root.name == SeederContainer) Collect(brush, root.transform);
        }

        const string SeederContainer = "PropsRue";

        private void Collect(CityBrush brush, Transform c)
        {
            if (c == null) return;
            foreach (Transform t in c)
            {
                Bounds b = CityBrushPlacement.PlacedBounds(brush.palette, t, PrefabSource);
                (IsProp(brush.palette, t) ? propPlaced : placed).Add(b);
            }
        }

        // Un objet peint est un prop si son prefab d'origine est marque Props dans la palette.
        // Faute d'entree (pose a la main, autre lot), on le compte comme batiment : c'est le
        // choix prudent, un obstacle inconnu vaut mieux qu'un empilement.
        private static bool IsProp(CityPalette palette, Transform t)
        {
            if (palette == null) return false;
            GameObject src = PrefabSource(t.gameObject);
            var e = src != null ? palette.Find(src) : null;
            return e != null && e.layer != BrushLayer.Batiments;
        }

        // Le prefab d'origine d'une instance : c'est lui qui donne l'entree de palette, donc
        // l'emprise EXACTE avec laquelle le candidat correspondant a ete teste.
        private static GameObject PrefabSource(GameObject go)
            => PrefabUtility.GetCorrespondingObjectFromSource(go);

        // ---------------------------------------------------------------- scene

        // Generalise RoadNetworkEditor.RayToGround avec deux corrections : on ignore NOS PROPRES
        // batiments (sinon chaque nouveau se pose sur le precedent) et TOUTES les routes via
        // GetComponentInParent (marche avec plusieurs reseaux, sans reference a un net precis).
        private static bool GroundRay(CityBrush brush, Ray r, out Vector3 pos, out Vector3 normal)
        {
            pos = Vector3.zero;
            normal = Vector3.up;
            Transform container = brush.Container;

            var hits = Physics.RaycastAll(r, RayLength);
            float best = float.MaxValue;
            bool found = false;
            foreach (var h in hits)
            {
                if (container != null && h.collider.transform.IsChildOf(container)) continue;
                if (h.collider.GetComponentInParent<RoadNetwork>() != null) continue;
                if (h.distance >= best) continue;
                best = h.distance;
                pos = h.point;
                normal = h.normal;
                found = true;
            }
            if (found) return true;

            // Plan de secours obligatoire : une scene de travail n'a pas forcement de sol.
            var plane = new Plane(Vector3.up, new Vector3(0f, brush.groundHeight, 0f));
            if (!plane.Raycast(r, out float d)) return false;
            pos = r.GetPoint(d);
            normal = Vector3.up;
            return true;
        }

        // Couche Props : on ne garde QUE ce qui est sous le conteneur -> le prop se pose sur un
        // batiment deja peint, jamais sur le sol ni sur une route.
        private static bool SurfaceRay(CityBrush brush, Ray r, out Vector3 pos, out Vector3 normal)
        {
            pos = Vector3.zero;
            normal = Vector3.up;
            Transform container = brush.Container;
            if (container == null) return false;

            float best = float.MaxValue;
            bool found = false;
            foreach (var h in Physics.RaycastAll(r, RayLength))
            {
                if (!h.collider.transform.IsChildOf(container)) continue;
                if (h.distance >= best) continue;
                best = h.distance;
                pos = h.point;
                normal = h.normal;
                found = true;
            }
            return found;
        }

        // Couche PropsSol : le sol de la ville ET LES TROTTOIRS, mais jamais la chaussee.
        //
        // Le trottoir est le cas qui demande du soin, et deux pieges s'y cumulent :
        //
        //   1. `probe.inside` ne peut pas servir de rejet. RoadHalfWidth vaut 6.5 m TROTTOIRS
        //      COMPRIS, donc "dans l'emprise" est vrai sur toute la bande 0-6.5 et rejeter
        //      la-dessus interdisait aussi le trottoir (2.75-6.5) -- soit exactement l'endroit ou
        //      on veut poser une benne. Ce qui distingue vraiment les deux, c'est que le trottoir
        //      est SURELEVE de SidewalkHeight au-dessus de l'axe. C'est ce qu'on mesure.
        //
        //   2. GroundRay ecarte les colliders de route, donc au-dessus d'un trottoir il rend le
        //      sol de la ville qui passe DESSOUS (0.180 contre 0.196 mesures sur Game) et le prop
        //      s'enfoncait de 1.6 cm. Ici on garde le point de la route quand c'est un trottoir.
        //
        // Un seul RaycastAll pour les deux : les touches de route etaient deja parcourues et
        // jetees, on se contente de retenir la meilleure au lieu de l'oublier.
        private static bool GroundOrSidewalkRay(CityBrush brush, Ray r, out Vector3 pos, out Vector3 normal)
        {
            pos = Vector3.zero;
            normal = Vector3.up;
            Transform container = brush.Container;

            bool hasGround = false, hasRoad = false;
            float bestGround = float.MaxValue, bestRoad = float.MaxValue;
            Vector3 gp = Vector3.zero, gn = Vector3.up, rp = Vector3.zero, rn = Vector3.up;

            foreach (var h in Physics.RaycastAll(r, RayLength))
            {
                if (container != null && h.collider.transform.IsChildOf(container)) continue;
                if (h.collider.GetComponentInParent<RoadNetwork>() != null)
                {
                    if (h.distance >= bestRoad) continue;
                    bestRoad = h.distance; rp = h.point; rn = h.normal; hasRoad = true;
                }
                else
                {
                    if (h.distance >= bestGround) continue;
                    bestGround = h.distance; gp = h.point; gn = h.normal; hasGround = true;
                }
            }

            // La route est touchee AVANT le sol des qu'on vise un trottoir (il le surplombe), donc
            // c'est bien elle qui tranche quand les deux repondent.
            if (hasRoad && bestRoad <= bestGround)
            {
                RoadNetwork net = NearestNetwork(rp, 200f, out RoadNetwork.RoadProbe probe);
                if (net != null && probe.valid)
                {
                    // Moitie de la hauteur de bordure comme seuil : franc des deux cotes, et
                    // tolerant a la pente d'une rue qui monte.
                    if (rp.y - probe.point.y < net.SidewalkHeight * 0.5f) return false;   // chaussee
                    pos = rp; normal = rn;                                                // trottoir
                    return true;
                }
            }

            if (hasGround) { pos = gp; normal = gn; return true; }

            // Plan de secours : une scene de travail n'a pas forcement de sol.
            var plane = new Plane(Vector3.up, new Vector3(0f, brush.groundHeight, 0f));
            if (!plane.Raycast(r, out float dPlane)) return false;
            pos = r.GetPoint(dPlane);
            normal = Vector3.up;
            return true;
        }

        // La sonde EST la couche : c'est elle, et pas la fonction de semis, qui definit ou un
        // prop a le droit de tomber.
        private static bool Probe(CityBrush brush, BrushLayer layer, Ray r,
                                  out Vector3 pos, out Vector3 normal)
            => layer switch
            {
                BrushLayer.Props => SurfaceRay(brush, r, out pos, out normal),
                BrushLayer.PropsSol => GroundOrSidewalkRay(brush, r, out pos, out normal),
                // PropsRue passe par GroundRay comme les batiments : le curseur n'est qu'une
                // position de disque, les cotes viennent du plan. Une sonde qui refuserait la
                // chaussee ferait disparaitre le disque au milieu de la rue qu'on veut garnir.
                _ => GroundRay(brush, r, out pos, out normal),
            };

        private static bool GroundAt(CityBrush brush, Vector3 approx, out Vector3 pos, out Vector3 normal)
            => GroundRay(brush, new Ray(approx + Vector3.up * 500f, Vector3.down), out pos, out normal);

        private static bool ProbeAt(CityBrush brush, BrushLayer layer, Vector3 approx,
                                    out Vector3 pos, out Vector3 normal)
            => Probe(brush, layer, new Ray(approx + Vector3.up * 500f, Vector3.down), out pos, out normal);

        private static float GroundY(CityBrush brush, Vector3 approx)
            => GroundAt(brush, approx, out Vector3 p, out _) ? p.y : brush.groundHeight;

        private static RoadNetwork NearestNetwork(Vector3 world, float maxDist, out RoadNetwork.RoadProbe best)
        {
            best = default;
            RoadNetwork found = null;
            float bestD = maxDist;
            foreach (var net in Object.FindObjectsByType<RoadNetwork>(FindObjectsSortMode.None))
            {
                if (!net.ProbeRoad(world, bestD, out RoadNetwork.RoadProbe p)) continue;
                if (p.clearance > bestD) continue;
                bestD = p.clearance;
                best = p;
                found = net;
            }
            return found;
        }

        // ---------------------------------------------------------------- affichage

        private void DrawCursor(CityBrush brush, Event e)
        {
            if (e.type != EventType.Repaint) return;

            // En couche Props le curseur n'existe que sur une surface deja peinte. Sans ce
            // retour, viser le sol ne donnait AUCUN signe -- on croyait l'outil casse.
            if (!cursorValid)
            {
                if (brush.layer == BrushLayer.Batiments) return;
                Ray r = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
                if (!GroundRay(brush, r, out Vector3 gp, out Vector3 gn)) return;
                Handles.color = new Color(1f, 0.5f, 0.2f, 0.55f);
                Handles.DrawWireDisc(gp, gn, brush.radius, 2f);
                Handles.Label(gp, brush.layer == BrushLayer.Props
                    ? "vise un batiment deja peint"
                    : "sur la chaussee — vise le trottoir ou le sol");
                return;
            }

            Color c = erasing ? new Color(1f, 0.35f, 0.28f)
                    : brush.layer == BrushLayer.Props ? new Color(0.6f, 1f, 0.45f)
                    : brush.layer == BrushLayer.PropsSol ? new Color(1f, 0.85f, 0.35f)
                    : brush.layer == BrushLayer.PropsRue ? new Color(1f, 0.55f, 0.9f)
                    : new Color(0.35f, 0.95f, 1f);
            Handles.color = new Color(c.r, c.g, c.b, 0.08f);
            Handles.DrawSolidDisc(cursorPos, cursorNormal, brush.radius);
            Handles.color = c;
            Handles.DrawWireDisc(cursorPos, cursorNormal, brush.radius, 2f);

            if (erasing || brush.layer == BrushLayer.Props || brush.layer == BrushLayer.PropsSol) return;

            // Fantomes du plan sous le disque : on voit ce qui VA sortir avant de cliquer, ce qui
            // rend le geste dirigeable au lieu d'etre une surprise. Les deux couches a plan --
            // batiments et mobilier de rue -- s'affichent pareil.
            float r2 = brush.radius * brush.radius;
            Handles.color = new Color(1f, 0.85f, 0.2f, 0.55f);
            int shown = 0;
            foreach (var p in brush.layer == BrushLayer.PropsRue ? streetPlan : lanePlan)
            {
                float dx = p.position.x - cursorPos.x, dz = p.position.z - cursorPos.z;
                if (dx * dx + dz * dz > r2) continue;
                Handles.DrawWireCube(new Vector3(p.position.x, cursorPos.y, p.position.z),
                                     new Vector3(p.footprintXZ.size.x, 0.1f, p.footprintXZ.size.z));
                if (++shown > 120) break;
            }
        }

        private void DrawHud(CityBrush brush, string probleme)
        {
            Handles.BeginGUI();
            var r = new Rect(12f, 12f, 330f, probleme != null ? 64f : 96f);
            GUILayout.BeginArea(r, GUI.skin.box);

            if (probleme != null)
            {
                GUILayout.Label("Pinceau ville — inactif", EditorStyles.boldLabel);
                GUILayout.Label(probleme, EditorStyles.wordWrappedMiniLabel);
            }
            else
            {
                GUILayout.Label(brush.layer switch
                {
                    BrushLayer.Props => "Pinceau ville — PROPS (sur batiments)",
                    BrushLayer.PropsRue => "Pinceau ville — MOBILIER DE RUE",
                    BrushLayer.PropsSol => "Pinceau ville — PROPS AU SOL",
                    _ => "Pinceau ville",
                }, EditorStyles.boldLabel);
                GUILayout.Label($"rayon {brush.radius:F0} m   graine {brush.seed}   pose {emitted}",
                                EditorStyles.miniLabel);
                if (brush.layer == BrushLayer.PropsRue)
                    GUILayout.Label("aligne sur les trottoirs, comme Tools/Ville/Props/3",
                                    EditorStyles.miniLabel);
                GUILayout.Label("glisser : peindre   Ctrl+glisser : effacer\n" +
                                "Maj+molette ou [ ] ou - = : rayon\n" +
                                "B : batiments / props / rue / sol   R : nouvelle ville",
                                EditorStyles.miniLabel);
            }

            GUILayout.EndArea();
            Handles.EndGUI();
        }
    }
}
