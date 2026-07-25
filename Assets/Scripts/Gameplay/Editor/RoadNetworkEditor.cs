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
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
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
                "Shift+clic : pose un noeud et le relie au precedent.\n" +
                "Shift+clic sur un noeud : le raccorde (ou termine la chaine si c'est le courant).\n" +
                "Shift+clic sur une route : insere un noeud dessus (et raccorde -> cree un T).\n" +
                "Shift+Ctrl+clic sur un noeud : le supprime.\n" +
                "Sans shift : les poignees deplacent les noeuds.\n" +
                "Outil Rotate (touche E) : disques pour orienter un bout de route.",
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

                // HAUTEUR : c'est tout ce qu'il faut pour un pont. Les noeuds sont deja en
                // Vector3 et RoadMeshWarp encaisse une spline qui monte, mais la poignee de
                // scene est une FreeMoveHandle (plan camera) -> lever proprement au drag est
                // illusoire. Un champ, plus la fleche verticale dessinee en scene.
                Vector3 wp = net.NodeWorld(selected);
                EditorGUILayout.BeginHorizontal();
                float nh = EditorGUILayout.FloatField($"Noeud {selected} : hauteur (m)", wp.y);
                if (GUILayout.Button("0", GUILayout.Width(24f))) nh = 0f;
                EditorGUILayout.EndHorizontal();
                if (!Mathf.Approximately(nh, wp.y))
                {
                    Undo.RecordObject(net, "Lever noeud route");
                    net.SetNodeWorld(selected, new Vector3(wp.x, nh, wp.z));
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
            DrawNodes(net, e);
            DrawPendingLink(net, e);
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

                if (rotate)
                {
                    EditorGUI.BeginChangeCheck();
                    Quaternion nq = Handles.Disc(Quaternion.Euler(0f, net.NodeYaw(i), 0f),
                        p, Vector3.up, handle * 0.7f, false, 0f);
                    if (!EditorGUI.EndChangeCheck()) continue;

                    if (!draggingNode) { draggingNode = true; net.BeginDrag(); }
                    Undo.RecordObject(net, "Tourner noeud route");
                    net.SetNodeYaw(i, nq.eulerAngles.y);
                    net.RebuildDirty();
                    MarkDirty(net);
                    continue;
                }

                // Fleche verticale sur le noeud selectionne : la seule facon de lever un noeud
                // a la souris, la FreeMoveHandle en dessous travaillant dans le plan camera.
                if (i == selected)
                {
                    Handles.color = new Color(0.4f, 1f, 0.5f);
                    EditorGUI.BeginChangeCheck();
                    Vector3 up = Handles.Slider(p, Vector3.up, handle * 0.9f,
                                                Handles.ArrowHandleCap, 0.25f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        if (!draggingNode) { draggingNode = true; net.BeginDrag(); }
                        Undo.RecordObject(net, "Lever noeud route");
                        net.SetNodeWorld(i, new Vector3(p.x, up.y, p.z));
                        net.RebuildDirty();
                        MarkDirty(net);
                        continue;
                    }
                    Handles.color = new Color(1f, 0.85f, 0.2f);
                }

                EditorGUI.BeginChangeCheck();
                Vector3 np = Handles.FreeMoveHandle(p, handle * 0.12f, Vector3.zero, Handles.SphereHandleCap);
                if (!EditorGUI.EndChangeCheck()) continue;

                if (!draggingNode) { draggingNode = true; net.BeginDrag(); }
                // FreeMoveHandle deplace dans le PLAN CAMERA : on reprojette au sol pour que le
                // drag donne bien la sensation de trainer le point sur la map. Un noeud DEJA
                // leve garde sa hauteur au-dessus du sol -- sans ca, deplacer un tablier de pont
                // le rabattrait a la rue et il n'y aurait aucun moyen de retoucher un ouvrage.
                if (net.SnapToGround)
                {
                    float lift = p.y - RayToGround(net, new Ray(p + Vector3.up * 500f, Vector3.down)).y;
                    np = RayToGround(net, new Ray(np + Vector3.up * 500f, Vector3.down))
                         + Vector3.up * lift;
                }

                Undo.RecordObject(net, "Deplacer noeud route");
                net.SetNodeWorld(i, np);
                net.RebuildDirty();
                MarkDirty(net);
            }

            if (draggingNode && (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp))
            {
                draggingNode = false;
                net.EndDrag();       // pose les MeshCollider differes pendant le drag
            }
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
        private const double Debounce = 0.35;
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
            groundDue = 0d;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            CityGroundBuilder.Build(false);
        }
    }
}
