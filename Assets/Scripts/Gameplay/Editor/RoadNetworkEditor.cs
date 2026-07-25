using System.Collections.Generic;
using Gameplay.City;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Gameplay.EditorTools
{
    // OUTIL DE TRACE des routes : Shift+clic pose un noeud et le relie au precedent, Shift+clic
    // sur un noeud existant le raccorde, Shift+Ctrl+clic le supprime. Sans shift, les poignees
    // deplacent les noeuds normalement et la route se reconstruit pendant le drag.
    //
    // Pourquoi un [CustomEditor] + OnSceneGUI et pas un EditorTool : notre geste est un
    // MODIFICATEUR. Le clic simple doit continuer a faire la selection Unity habituelle, alors
    // qu'un EditorTool existe justement pour la capturer. On traite le clic AVANT de dessiner
    // les poignees et on fait e.Use() -> ni les poignees ni le picker de Unity ne le voient.
    //
    // Les FBX du kit arrivent avec isReadable=0 : impossible de lire leurs vertices pour la
    // deformation. On force le Read/Write a la selection, une fois.
    [CustomEditor(typeof(RoadNetwork))]
    public class RoadNetworkEditor : Editor
    {
        const float PickRadius = 18f;   // rayon de pick d'un noeud, en PIXELS -> constant au zoom
        const float SegmentPickRadius = 40f;   // idem pour l'axe d'une route (cible plus large)
        const float RayLength = 5000f;
        const float GroundSize = 50f;   // Plane de 10 u -> 500 m de cote

        // ---------------------------------------------------------------- creation

        // Scene vierge pretes a tracer : sol cliquable + reseau. Le SaveCurrentModifiedScenes...
        // affiche le dialogue standard de Unity, et rend false si l'utilisateur annule -> aucune
        // perte possible.
        [MenuItem("Tools/Routes/Nouvelle scene de routes")]
        private static void NewRoadScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(GroundSize, 1f, GroundSize);

            SpawnNetwork();
            if (SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.LookAt(Vector3.zero, Quaternion.Euler(50f, 0f, 0f), 80f);
        }

        [MenuItem("Tools/Routes/Ajouter un reseau a la scene")]
        private static void AddNetworkToScene() => SpawnNetwork();

        private static RoadNetwork SpawnNetwork()
        {
            var go = new GameObject("Roads");
            Undo.RegisterCreatedObjectUndo(go, "Creer reseau de routes");
            // ObjectFactory (et pas AddComponent) : c'est lui qui declenche Reset(), donc le
            // remplissage automatique des 5 slots depuis Assets/Models/Roads/.
            var net = ObjectFactory.AddComponent<RoadNetwork>(go);
            EnsureReadable(net, false);
            Selection.activeGameObject = go;
            EditorSceneManager.MarkSceneDirty(go.scene);
            return net;
        }

        private int selected = -1;
        private bool draggingNode;

        private void OnEnable()
        {
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.delayCall += () => EnsureReadable(target as RoadNetwork, false);
            // La poignee de transform de Unity vise le GameObject "Roads", pas les noeuds : elle
            // se superpose a la notre, et on deplace le reseau entier en croyant deplacer un
            // point. On la masque tant que l'outil est actif. L'outil Rotate reste utile, il est
            // relu plus bas pour sortir les disques de yaw.
            Tools.hidden = true;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            Tools.hidden = false;
        }

        private void OnUndoRedo()
        {
            if (target is RoadNetwork net && net != null) net.FullRebuild();
        }

        // ---------------------------------------------------------------- inspector

        // Un editeur en arriere-plan ne "tick" pas : le Update de RoadNetwork peut ne pas avoir
        // encore consomme son drapeau apres un domain reload ou une reouverture de scene. Des
        // que l'utilisateur regarde le composant, on garantit que la route est la.
        private static void EnsureBuilt(RoadNetwork net)
        {
            if (net != null && net.NodeCount > 0 && !net.IsBuilt) net.FullRebuild();
        }

        public override void OnInspectorGUI()
        {
            var net = (RoadNetwork)target;
            EnsureBuilt(net);
            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Clic sur un noeud OU sur une route : selectionne le noeud (poignee XYZ, " +
                "fleche verte = hauteur).\n" +
                "Suppr : supprime le noeud selectionne.\n" +
                "Shift+clic : pose un noeud et le relie au precedent.\n" +
                "Shift+clic sur un noeud : le raccorde (ou termine la chaine si c'est le courant).\n" +
                "Shift+clic sur une route : insere un noeud dessus (et raccorde -> cree un T).\n" +
                "Shift+Ctrl+clic sur un noeud : le supprime.\n" +
                "Ctrl+clic sur une route : variante de la tuile visee " +
                "(auto -> passage pieton -> egouts -> normale -> auto).\n" +
                "Outil Rotate (E) : disque sur le noeud selectionne, Ctrl pour un pas de 15 deg.",
                MessageType.Info);

            EditorGUILayout.LabelField($"{net.NodeCount} noeuds / {net.SegmentCount} segments");

            if (selected >= 0 && selected < net.NodeCount)
            {
                // Le rond-point est une VARIANTE du carrefour 4 voies : le toggle n'a de sens
                // qu'a 4 branches, on le grise ailleurs plutot que de le laisser mentir.
                int deg = net.NodeDegree(selected);
                bool r = net.NodeRoundabout(selected);
                using (new EditorGUI.DisabledScope(deg != 4))
                {
                    bool nr = EditorGUILayout.Toggle(
                        $"Noeud {selected} ({deg} branches) : rond-point", r);
                    if (nr != r)
                    {
                        Undo.RecordObject(net, "Rond-point");
                        net.SetNodeRoundabout(selected, nr);
                        MarkDirty(net);
                    }
                }
                if (deg >= 5)
                    EditorGUILayout.HelpBox($"{deg} branches : aucun asset du kit ne gere ca. " +
                        "Scinde le noeud en deux carrefours.", MessageType.Warning);

                float y = net.NodeYaw(selected);
                EditorGUILayout.BeginHorizontal();
                float ny = EditorGUILayout.Slider($"Noeud {selected} : rotation", y, 0f, 360f);
                if (GUILayout.Button("0", GUILayout.Width(24f))) ny = 0f;
                EditorGUILayout.EndHorizontal();
                if (!Mathf.Approximately(ny, y))
                {
                    Undo.RecordObject(net, "Tourner noeud route");
                    net.SetNodeYaw(selected, ny);
                    net.RebuildDirty();
                    MarkDirty(net);
                }

                // HAUTEUR AU-DESSUS DU RELIEF, et pas hauteur absolue : c'est elle qui a un sens
                // (un pont est "a +7 au-dessus du terrain", pas "a y = 5.4"), et c'est elle que
                // le noeud garde quand on regraine le bruit. La poignee verte de la scene fait
                // la meme chose a la souris ; le champ reste pour saisir une valeur exacte.
                float lift = net.NodeLift(selected);
                EditorGUILayout.BeginHorizontal();
                float nl = EditorGUILayout.FloatField(
                    new GUIContent($"Noeud {selected} : hauteur / relief (m)",
                                   "0 = pose au sol. Au-dela de 1,5 m le segment devient un " +
                                   "OUVRAGE : garde-corps, piles, et le sol passe dessous."),
                    lift);
                if (GUILayout.Button("0", GUILayout.Width(24f))) nl = 0f;
                EditorGUILayout.EndHorizontal();
                if (!Mathf.Approximately(nl, lift))
                {
                    Undo.RecordObject(net, "Lever noeud route");
                    net.SetNodeLift(selected, nl);
                    net.RebuildDirty();
                    MarkDirty(net);
                }
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Tout reconstruire")) net.FullRebuild();
            if (GUILayout.Button("Activer Read/Write sur les FBX")) EnsureReadable(net, true);
            EditorGUILayout.EndHorizontal();

            AutoGround = EditorGUILayout.ToggleLeft(
                new GUIContent("Refaire le sol automatiquement",
                               "Le sol se regenere ~0,3 s apres chaque edition de route. " +
                               "A decocher sur une grosse ville si la reconstruction se sent."),
                AutoGround);
        }

        // ---------------------------------------------------------------- scene

        private void OnSceneGUI()
        {
            var net = (RoadNetwork)target;
            EnsureBuilt(net);
            Event e = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);

            // Sous shift on capture le clic dans le vide : sinon Unity lancerait sa selection
            // rectangle. Hors shift, le comportement normal est intact.
            if (e.type == EventType.Layout && e.shift) HandleUtility.AddDefaultControl(id);

            HandleShiftClick(net, e);
            HandleTileClick(net, e);
            HandlePlainClick(net, e);
            HandleKeys(net, e);
            DrawForcedTiles(net);
            DrawNodes(net, e);
            // APRES les poignees, et c'est tout l'interet : ce rattrapage fait e.Use(), donc mis
            // avant il volait le clic aux fleches de la poignee XYZ -- elles tombent a une
            // poignee de distance du noeud, souvent pile au-dessus d'une route, et le noeud
            // devenait impossible a deplacer.
            HandleRoadClick(net, e);
            DrawPendingLink(net, e);
        }

        // Clic simple : SELECTIONNER UN NOEUD, pas le pan de route.
        //
        // Sans ca, cliquer une route attrape le `Seg_k` genere, l'inspecteur bascule dessus et on
        // perd l'outil -- alors qu'un segment est derive, il n'y a rien a y regler. On rattrape
        // donc le clic tant qu'il tombe sur un noeud OU sur une route, et on selectionne le noeud
        // le plus proche : c'est lui qu'on voulait editer.
        //
        // Le clic dans le vide n'est PAS capture : la selection normale de Unity doit continuer
        // de marcher, sinon on ne peut plus sortir de l'outil en cliquant a cote.
        private void HandlePlainClick(RoadNetwork net, Event e)
        {
            if (e.type != EventType.MouseDown || e.button != 0) return;
            if (e.shift || e.alt || e.control) return;

            int hit = PickNode(net, e.mousePosition);
            if (hit < 0) return;

            // Pas de e.Use() : la poignee dessinee juste apres doit pouvoir prendre le drag dans
            // le meme evenement, sinon il faut cliquer deux fois pour bouger un noeud.
            selected = hit;
            Repaint();
        }

        // Clic sur un pan de route : selectionne le noeud le plus proche plutot que de laisser
        // Unity attraper le `Seg_k` genere -- un segment est derive, il n'y a rien a y regler.
        private void HandleRoadClick(RoadNetwork net, Event e)
        {
            if (e.type != EventType.MouseDown || e.button != 0) return;
            if (e.shift || e.alt || e.control) return;
            // Une poignee a deja pris le clic : c'est un deplacement, pas une selection.
            if (GUIUtility.hotControl != 0) return;

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            int seg = net.NearestSegment(ray, out Vector3 onAxis);
            if (seg < 0) return;
            if ((HandleUtility.WorldToGUIPoint(onAxis) - e.mousePosition).sqrMagnitude
                >= SegmentPickRadius * SegmentPickRadius) return;

            var s = net.SegmentAt(seg);
            float da = (net.NodeWorld(s.a) - onAxis).sqrMagnitude;
            float db = (net.NodeWorld(s.b) - onAxis).sqrMagnitude;
            selected = da <= db ? s.a : s.b;
            Repaint();
            SceneView.RepaintAll();
            e.Use();
        }

        // Ctrl+clic sur une route : fait tourner la variante de la tuile visee.
        // auto -> passage pieton -> egouts -> normale -> auto. Le premier clic pose donc ce qu'on
        // vient chercher, et un tour complet rend la tuile au tirage automatique au lieu de la
        // laisser figee.
        private void HandleTileClick(RoadNetwork net, Event e)
        {
            if (e.type != EventType.MouseDown || e.button != 0) return;
            if (!e.control || e.shift || e.alt) return;

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            int seg = net.NearestSegment(ray, out Vector3 onAxis);
            if (seg < 0) return;
            if ((HandleUtility.WorldToGUIPoint(onAxis) - e.mousePosition).sqrMagnitude
                >= SegmentPickRadius * SegmentPickRadius) return;

            Undo.RecordObject(net, "Variante de tuile");
            net.CycleTileVariant(onAxis, out _);
            net.RebuildDirty();
            MarkDirty(net);
            SceneView.RepaintAll();
            e.Use();
        }

        private static readonly List<Vector3> forcedPos = new List<Vector3>();
        private static readonly List<int> forcedVar = new List<int>();

        // Repere sur les tuiles POSEES A LA MAIN : sans lui, impossible de distinguer un passage
        // pieton force d'un passage pieton tire au sort, donc impossible de savoir ce qu'un
        // changement de graine va emporter.
        private static void DrawForcedTiles(RoadNetwork net)
        {
            if (Event.current.type != EventType.Repaint) return;
            net.ForcedTiles(forcedPos, forcedVar);
            for (int i = 0; i < forcedPos.Count; i++)
            {
                Handles.color = forcedVar[i] == RoadNetwork.VariantPieton ? new Color(0.4f, 1f, 0.6f)
                              : forcedVar[i] == RoadNetwork.VariantEgouts ? new Color(1f, 0.7f, 0.2f)
                              : new Color(0.6f, 0.6f, 0.6f);
                float s = HandleUtility.GetHandleSize(forcedPos[i]);
                Handles.DrawWireDisc(forcedPos[i] + Vector3.up * 0.3f, Vector3.up, s * 0.35f);
            }
        }

        private void HandleKeys(RoadNetwork net, Event e)
        {
            if (e.type != EventType.KeyDown || selected < 0 || selected >= net.NodeCount) return;
            if (e.keyCode != KeyCode.Delete && e.keyCode != KeyCode.Backspace) return;

            Undo.RecordObject(net, "Supprimer noeud route");
            net.RemoveNode(selected);
            selected = -1;
            net.FullRebuild();
            MarkDirty(net);
            e.Use();
        }

        private void HandleShiftClick(RoadNetwork net, Event e)
        {
            if (e.type != EventType.MouseDown || e.button != 0 || !e.shift) return;

            int hit = PickNode(net, e.mousePosition);

            if (hit >= 0 && e.control)                       // suppression
            {
                Undo.RecordObject(net, "Supprimer noeud route");
                net.RemoveNode(hit);
                if (selected == hit) selected = -1;
                else if (selected > hit) selected--;
                net.FullRebuild();
                MarkDirty(net);
                e.Use();
                return;
            }

            if (hit >= 0)                                    // raccord / fin de chaine
            {
                if (hit == selected) { selected = -1; }
                else
                {
                    Undo.RecordObject(net, "Relier noeuds route");
                    if (selected >= 0) net.AddSegment(selected, hit);
                    selected = hit;
                    net.FullRebuild();
                    MarkDirty(net);
                }
                e.Use();
                return;
            }

            // Clic sur une route existante -> on INSERE un noeud dessus (le segment est coupe en
            // deux). Si une chaine est en cours, on raccorde dans la foulee : un seul clic cree
            // donc un T sur une route existante.
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            int seg = net.NearestSegment(ray, out Vector3 onAxis);
            if (seg >= 0 && (HandleUtility.WorldToGUIPoint(onAxis) - e.mousePosition).sqrMagnitude
                            < SegmentPickRadius * SegmentPickRadius)
            {
                Undo.RecordObject(net, "Inserer un noeud sur la route");
                int inserted = net.SplitSegment(seg, onAxis);
                if (inserted >= 0)   // -1 = trop pres d'une extremite, on retombe sur la pose normale
                {
                    if (selected >= 0 && selected != inserted) net.AddSegment(selected, inserted);
                    selected = inserted;
                    net.FullRebuild();
                    MarkDirty(net);
                    e.Use();
                    return;
                }
            }

            Vector3 world = RayToGround(net, ray);
            Undo.RecordObject(net, "Poser noeud route");
            int added = net.AddNodeWorld(world);
            if (selected >= 0) net.AddSegment(selected, added);
            selected = added;
            net.FullRebuild();
            MarkDirty(net);
            e.Use();
        }

        private void DrawNodes(RoadNetwork net, Event e)
        {
            // Outil Rotate de Unity (touche E) -> disques de rotation au lieu des poignees de
            // deplacement. C'est ce qui permet d'orienter un bout de route : sans ca, la
            // direction d'un noeud est entierement derivee de la position de ses voisins.
            bool rotate = Tools.current == Tool.Rotate;

            for (int i = 0; i < net.NodeCount; i++)
            {
                Vector3 p = net.NodeWorld(i);
                float handle = HandleUtility.GetHandleSize(p);
                Handles.color = i == selected ? new Color(1f, 0.85f, 0.2f) : new Color(0.3f, 0.9f, 1f);

                // Le disque de rotation ne sort que sur le noeud SELECTIONNE. Sur tous les
                // noeuds, les disques se recouvrent des que la ville est un peu dense et on
                // tourne systematiquement le mauvais.
                if (rotate && i == selected)
                {
                    EditorGUI.BeginChangeCheck();
                    // Ctrl = pas de 15 deg, comme le Rotate de Unity. Sans lui, aligner un bout
                    // de route sur un axe se joue au pixel.
                    float snap = e.control ? 15f : 0f;
                    Quaternion nq = Handles.Disc(Quaternion.Euler(0f, net.NodeYaw(i), 0f),
                        p, Vector3.up, handle * 0.7f, false, snap);
                    if (EditorGUI.EndChangeCheck())
                    {
                        if (!draggingNode) { draggingNode = true; net.BeginDrag(); }
                        Undo.RecordObject(net, "Tourner noeud route");
                        net.SetNodeYaw(i, nq.eulerAngles.y);
                        net.RebuildDirty();
                        MarkDirty(net);
                    }
                    Label(net, i, p, handle);
                    continue;
                }
                // Sous l'outil Rotate, les autres noeuds sont dessines mais pas attrapables : une
                // poignee de deplacement active pendant qu'on tourne fait bouger un noeud voisin
                // par accident. Le clic simple suffit pour changer de noeud selectionne.
                if (rotate)
                {
                    if (e.type == EventType.Repaint)
                        Handles.SphereHandleCap(0, p, Quaternion.identity, handle * 0.12f,
                                                EventType.Repaint);
                    continue;
                }

                if (i == selected)
                {
                    // Poignee XYZ complete sur le noeud selectionne : la fleche verte leve, les
                    // fleches rouge et bleue trainent au sol. Une FreeMoveHandle travaille dans
                    // le PLAN CAMERA, ce qui rend la hauteur inatteignable a la souris -- c'est
                    // ce qui obligeait a passer par le champ de l'inspecteur.
                    EditorGUI.BeginChangeCheck();
                    Vector3 moved = Handles.PositionHandle(p, Quaternion.identity);
                    if (!EditorGUI.EndChangeCheck()) { Label(net, i, p, handle); continue; }

                    if (!draggingNode) { draggingNode = true; net.BeginDrag(); }
                    // Deplacement horizontal seul -> on recolle au sol en gardant la hauteur
                    // d'ouvrage. Des que la hauteur est touchee, c'est l'utilisateur qui commande
                    // et on ne recolle plus : sinon la fleche verte serait annulee a chaque frame.
                    if (net.SnapToGround && Mathf.Abs(moved.y - p.y) < 1e-4f)
                        moved = Reground(net, p, moved);

                    Undo.RecordObject(net, "Deplacer noeud route");
                    net.SetNodeWorld(i, moved);
                    net.RebuildDirty();
                    MarkDirty(net);
                    continue;
                }

                PickHandle(net, i, p, handle);
            }

            // `net.Dragging` en plus de notre propre drapeau : un bouton relache hors de la vue
            // scene ne nous envoie pas de MouseUp, le reseau restait en mode drag et donc sans
            // colliders ni piles jusqu'a un "Tout reconstruire".
            if ((draggingNode || net.Dragging)
                && (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp))
            {
                draggingNode = false;
                net.EndDrag();       // pose les MeshCollider differes pendant le drag
            }
        }

        // Noeud non selectionne : une bille qu'on peut attraper directement. Elle traine au sol
        // en conservant la hauteur d'ouvrage -- pas de poignee XYZ, elle encombrerait la vue sur
        // les dizaines de noeuds qu'on n'edite pas.
        private void PickHandle(RoadNetwork net, int i, Vector3 p, float handle)
        {
            EditorGUI.BeginChangeCheck();
            Vector3 np = Handles.FreeMoveHandle(p, handle * 0.12f, Vector3.zero, Handles.SphereHandleCap);
            if (!EditorGUI.EndChangeCheck()) return;

            if (!draggingNode) { draggingNode = true; net.BeginDrag(); }
            selected = i;   // on edite ce qu'on attrape
            if (net.SnapToGround) np = Reground(net, p, np);

            Undo.RecordObject(net, "Deplacer noeud route");
            net.SetNodeWorld(i, np);
            net.RebuildDirty();
            MarkDirty(net);
        }

        // Repose un noeud deplace horizontalement, en gardant sa hauteur au-dessus du sol : sans
        // ca, deplacer un tablier de pont le rabattrait a la rue et un ouvrage deviendrait
        // intouchable.
        private static Vector3 Reground(RoadNetwork net, Vector3 from, Vector3 to)
        {
            float lift = from.y - RayToGround(net, new Ray(from + Vector3.up * 500f, Vector3.down)).y;
            return RayToGround(net, new Ray(to + Vector3.up * 500f, Vector3.down)) + Vector3.up * lift;
        }

        // Etiquette du noeud selectionne : son index, et sa hauteur AU-DESSUS DU RELIEF -- le
        // seul chiffre stable, la hauteur absolue bougeant des qu'on regraine le bruit.
        private static void Label(RoadNetwork net, int i, Vector3 p, float handle)
        {
            if (Event.current.type != EventType.Repaint) return;
            float lift = net.NodeLift(i);
            Handles.Label(p + Vector3.up * (handle * 0.6f),
                          $"n{i}  {(lift >= 0f ? "+" : "")}{lift:F1} m");
        }

        // Ligne fantome entre le noeud courant et la souris tant que shift est tenu.
        private void DrawPendingLink(RoadNetwork net, Event e)
        {
            if (!e.shift || selected < 0 || selected >= net.NodeCount) return;
            if (e.type == EventType.Repaint)
            {
                Vector3 from = net.NodeWorld(selected);
                Vector3 to = RayToGround(net, HandleUtility.GUIPointToWorldRay(e.mousePosition));
                Handles.color = new Color(1f, 0.85f, 0.2f, 0.8f);
                Handles.DrawDottedLine(from, to, 4f);
            }
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
                SceneView.RepaintAll();
        }

        private int PickNode(RoadNetwork net, Vector2 mouse)
        {
            int best = -1;
            float bestD = PickRadius * PickRadius;
            for (int i = 0; i < net.NodeCount; i++)
            {
                Vector2 g = HandleUtility.WorldToGUIPoint(net.NodeWorld(i));
                float d = (g - mouse).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        // Raycast en ignorant NOS PROPRES colliders : sinon la route qu'on vient de poser
        // attrape le rayon et chaque nouveau noeud s'empile dessus. Plan de secours obligatoire,
        // les scenes de travail n'ont pas forcement de sol.
        private static Vector3 RayToGround(RoadNetwork net, Ray r)
        {
            var hits = Physics.RaycastAll(r, RayLength);
            float best = float.MaxValue;
            Vector3 p = Vector3.zero;
            bool found = false;
            foreach (var h in hits)
            {
                if (h.collider.transform.IsChildOf(net.transform)) continue;
                if (h.distance >= best) continue;
                best = h.distance;
                p = h.point;
                found = true;
            }
            if (found) return p;

            var plane = new Plane(Vector3.up, new Vector3(0f, net.GroundHeight, 0f));
            return plane.Raycast(r, out float d) ? r.GetPoint(d) : r.origin + r.direction * 50f;
        }

        // ---------------------------------------------------------------- import

        private static void EnsureReadable(RoadNetwork net, bool verbose)
        {
            if (net == null) return;
            int fixedCount = 0;
            foreach (var go in net.KitAssets())
            {
                if (go == null) continue;
                string path = AssetDatabase.GetAssetPath(go);
                if (string.IsNullOrEmpty(path)) continue;
                if (AssetImporter.GetAtPath(path) is not ModelImporter imp || imp.isReadable) continue;
                imp.isReadable = true;
                imp.SaveAndReimport();
                fixedCount++;
            }
            if (fixedCount > 0)
            {
                Debug.Log($"[RoadNetwork] Read/Write active sur {fixedCount} FBX.");
                net.FullRebuild();
            }
            else if (verbose)
            {
                Debug.Log("[RoadNetwork] Read/Write deja actif sur tout le kit.");
            }
        }

        private static void MarkDirty(RoadNetwork net)
        {
            EditorUtility.SetDirty(net);
            EditorSceneManager.MarkSceneDirty(net.gameObject.scene);
            QueueGround();
        }

        // ---------------------------------------------------------------- sol automatique
        //
        // Le sol est CALE sur les routes : trou sous la chaussee, creux sous une route enfoncee,
        // remblai sous une route levee. Le laisser perime jusqu'au prochain clic de menu donne
        // une ville fausse a chaque retouche -- c'est justement quand on deplace une route qu'on
        // a besoin de voir le sol suivre.
        //
        // Il est DIFFERE et pas immediat : sa reconstruction coute une centaine de millisecondes
        // (52 000 sommets, plus la cuisson du MeshCollider), et MarkDirty est appele a chaque
        // frame de drag. Chaque edition repousse l'echeance, donc un drag ne declenche qu'UNE
        // reconstruction, a la fin.
        private const string AutoKey = "gmtk.ville.solAuto";
        private const double Debounce = 0.6;
        private static double groundDue;
        private static bool hooked;

        public static bool AutoGround
        {
            get { return EditorPrefs.GetBool(AutoKey, true); }
            set { EditorPrefs.SetBool(AutoKey, value); }
        }

        private static void QueueGround()
        {
            if (!AutoGround) return;
            groundDue = EditorApplication.timeSinceStartup + Debounce;
            if (hooked) return;
            hooked = true;
            EditorApplication.update += PumpGround;
        }

        private static void PumpGround()
        {
            if (groundDue <= 0d || EditorApplication.timeSinceStartup < groundDue) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) { groundDue = 0d; return; }
            // Poignee encore tenue : on repousse au lieu d'annuler. Un drag lent envoie des
            // MarkDirty espaces de plus que le debounce, et reconstruire le sol au milieu du
            // geste fait sauter la souris -- c'est ce qui donnait la sensation de lag.
            if (GUIUtility.hotControl != 0)
            {
                groundDue = EditorApplication.timeSinceStartup + Debounce;
                return;
            }
            groundDue = 0d;
            CityGroundBuilder.Build(false);
        }
    }
}
