using System.Collections.Generic;
using Gameplay.City;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Gameplay.EditorTools
{
    // Inspecteur du pinceau + creation dans la scene + menus de TEST sans souris.
    //
    // Les menus de test existent parce qu'un geste de SceneView n'est pilotable ni par un test
    // automatise ni par MCP : ils appellent directement CityBrushPlacement, qui est justement du
    // calcul pur, et instancient le resultat comme le ferait l'outil.
    [CustomEditor(typeof(CityBrush))]
    public class CityBrushEditor : Editor
    {
        [MenuItem("Tools/Ville/Ajouter un pinceau a la scene", false, 120)]
        private static void AddBrush()
        {
            var go = new GameObject("Ville");
            Undo.RegisterCreatedObjectUndo(go, "Creer le pinceau de ville");
            var brush = ObjectFactory.AddComponent<CityBrush>(go);
            brush.palette = AssetDatabase.LoadAssetAtPath<CityPalette>("Assets/Prefabs/City/CityPalette.asset");
            Selection.activeGameObject = go;
            EditorSceneManager.MarkSceneDirty(go.scene);
        }

        public override void OnInspectorGUI()
        {
            var brush = (CityBrush)target;

            using (new EditorGUI.DisabledScope(ToolManager.activeToolType == typeof(CityBrushTool)))
            {
                if (GUILayout.Button("Activer le pinceau (ou bouton dans la barre d'outils)",
                                     GUILayout.Height(28f)))
                    ToolManager.SetActiveTool<CityBrushTool>();
            }

            EditorGUILayout.HelpBox(
                "Glisser : peindre. Ctrl+glisser : effacer.\n" +
                "[ et ] : rayon.   B : batiments / props.   R : nouvelle ville.\n\n" +
                "Aucun mode a choisir : le placement se decide tout seul selon l'endroit. " +
                "Contre une route -> front de rue aligne, facade vers la chaussee. Derriere -> " +
                "voies arriere. Loin de tout -> grille de coeur d'ilot, orientee sur la rue la " +
                "plus proche tant qu'elle est a portee.\n\n" +
                "Le plan est deterministe : repasser au meme endroit rebouche encore quelques " +
                "trous (grace aux essais multiples par case) puis ne fait plus rien. Pour " +
                "densifier : baisser 'grid Spacing', monter 'grid Tries', baisser " +
                "'grid Hole Chance', ou monter 'lanes'.",
                MessageType.Info);

            if (brush.palette == null)
                EditorGUILayout.HelpBox("Aucune palette. Lance Tools/Ville/3 pour la construire.",
                                        MessageType.Warning);
            else
                EditorGUILayout.LabelField(
                    $"Palette : {brush.palette.CountUsable(BrushLayer.Batiments)} batiments, " +
                    $"{brush.palette.CountUsable(BrushLayer.Props)} props");

            if (brush.layer == BrushLayer.Props && brush.Container == null)
                EditorGUILayout.HelpBox("La couche Props se pose sur les batiments DEJA peints. " +
                                        "Peins d'abord des batiments.", MessageType.Warning);

            if (Object.FindObjectsByType<RoadNetwork>(FindObjectsSortMode.None).Length == 0)
                EditorGUILayout.HelpBox("Aucun RoadNetwork dans la scene : pas de front de rue, " +
                                        "seule la grille de coeur d'ilot s'appliquera.",
                                        MessageType.Warning);

            EditorGUILayout.Space();
            DrawDefaultInspector();

            EditorGUILayout.Space();
            Transform c = brush.Container;
            EditorGUILayout.LabelField($"{(c == null ? 0 : c.childCount)} objets peints");
            using (new EditorGUI.DisabledScope(c == null || c.childCount == 0))
            {
                if (GUILayout.Button("Tout effacer") &&
                    EditorUtility.DisplayDialog("Pinceau ville",
                        $"Supprimer les {c.childCount} objets peints ?", "Supprimer", "Annuler"))
                {
                    Undo.DestroyObjectImmediate(c.gameObject);
                    EditorSceneManager.MarkSceneDirty(brush.gameObject.scene);
                }
            }
        }

        // ---------------------------------------------------------------- tests sans souris

        // Revele le plan de ville ENTIER, sans disque de pinceau : c'est la version testable de
        // ce que fait un trait, et le moyen le plus rapide de juger la densite d'un coup d'oeil.
        [MenuItem("Tools/Ville/Test - Peindre toute la ville", false, 200)]
        private static void TestWholeCity()
        {
            if (!Setup(out CityBrush brush)) return;

            var nets = Object.FindObjectsByType<RoadNetwork>(FindObjectsSortMode.None);
            var plan = new List<CityBrushPlacement.Placement>();
            CityBrushPlacement.BuildLaneCandidates(brush, nets, plan);

            // Emprise a couvrir par la grille : celle du reseau, elargie.
            Bounds area;
            if (plan.Count > 0)
            {
                area = new Bounds(plan[0].position, Vector3.zero);
                foreach (var p in plan) area.Encapsulate(p.position);
                area.Expand(brush.gridSpacing * 6f);
            }
            else area = new Bounds(brush.transform.position, Vector3.one * 200f);

            // Amorce avec ce qui est DEJA en scene, exactement comme CityBrushTool.RebuildPlaced
            // au debut d'un trait. Sans ca, relancer le menu doublerait la ville au lieu de ne
            // rien faire -- et l'idempotence du plan ne serait plus verifiable par ce chemin.
            var placed = new List<Bounds>();
            Transform deja = brush.Container;
            if (deja != null)
                foreach (Transform t in deja)
                    placed.Add(CityBrushPlacement.PlacedBounds(brush.palette, t,
                        PrefabUtility.GetCorrespondingObjectFromSource));

            // Les voies d'abord : c'est le front de rue qui gagne quand deux candidats se
            // disputent la meme place, parce que c'est lui qui porte la lecture du quartier.
            var final = new List<CityBrushPlacement.Placement>();
            var reserved = new List<Bounds>();
            foreach (var p in plan)
            {
                reserved.Add(p.footprintXZ);
                if (CityBrushPlacement.Overlaps(p.footprintXZ, placed)) continue;
                final.Add(p);
                placed.Add(p.footprintXZ);
            }

            var grid = new List<CityBrushPlacement.Placement>();
            CityBrushPlacement.GridCandidates(brush, area.center, area.extents.magnitude,
                delegate (Vector3 w, float d, out RoadNetwork.RoadProbe pr)
                {
                    pr = default;
                    RoadNetwork.RoadProbe best = default;
                    bool any = false;
                    float bestD = d;
                    foreach (var n in nets)
                    {
                        if (!n.ProbeRoad(w, bestD, out RoadNetwork.RoadProbe q)) continue;
                        if (q.clearance > bestD) continue;
                        bestD = q.clearance; best = q; any = true;
                    }
                    pr = best;
                    return any;
                },
                reserved, placed, grid);

            // GridCandidates a deja fait son rejet et alimente `placed` : rien a refiltrer.
            final.AddRange(grid);

            Spawn(brush, final, "Peindre toute la ville");
            Debug.Log($"[Ville][test] {final.Count} batiments ({plan.Count} candidats de rue, " +
                      $"{grid.Count} de coeur d'ilot).");
        }

        [MenuItem("Tools/Ville/Test - Saupoudrer des props", false, 201)]
        private static void TestProps()
        {
            var brush = Object.FindFirstObjectByType<CityBrush>();
            if (brush == null || brush.palette == null) { Debug.LogError("[Ville][test] Pas de pinceau/palette."); return; }
            Transform c = brush.Container;
            if (c == null || c.childCount == 0) { Debug.LogError("[Ville][test] Peins d'abord des batiments."); return; }
            if (brush.palette.CountUsable(BrushLayer.Props) == 0) { Debug.LogError("[Ville][test] Palette sans props."); return; }

            CityBrushPlacement.GroundProbe surf = delegate (Vector3 a, out Vector3 p, out Vector3 n)
            {
                p = Vector3.zero; n = Vector3.up;
                var r = new Ray(a + Vector3.up * 500f, Vector3.down);
                float best = float.MaxValue; bool found = false;
                foreach (var h in Physics.RaycastAll(r, 5000f))
                {
                    if (!h.collider.transform.IsChildOf(c)) continue;
                    if (h.distance >= best) continue;
                    best = h.distance; p = h.point; n = h.normal; found = true;
                }
                return found;
            };

            Bounds bb = new Bounds(c.GetChild(0).position, Vector3.zero);
            foreach (Transform t in c) bb.Encapsulate(t.position);

            // Amorce avec les PROPS deja en scene, et surtout PAS avec les batiments : un prop
            // pose sur un toit est dans l'emprise XZ de son batiment, les melanger rejetterait
            // tout. Le test passait auparavant une liste vide, ce qui masquait exactement ce
            // bug-la dans l'outil -- il doit suivre le meme chemin que la peinture reelle.
            var placed = new List<Bounds>();
            foreach (Transform t in c)
            {
                GameObject src = PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject);
                var pe = src != null ? brush.palette.Find(src) : null;
                if (pe != null && pe.layer == BrushLayer.Props)
                    placed.Add(CityBrushPlacement.PlacedBounds(brush.palette, t,
                        PrefabUtility.GetCorrespondingObjectFromSource));
            }

            var outp = new List<CityBrushPlacement.Placement>();
            int steps = Mathf.Clamp(Mathf.CeilToInt(bb.size.magnitude / brush.radius), 1, 40);
            for (int i = 0; i < steps; i++)
                for (int j = 0; j < steps; j++)
                {
                    var centre = new Vector3(
                        Mathf.Lerp(bb.min.x, bb.max.x, (i + 0.5f) / steps), bb.center.y,
                        Mathf.Lerp(bb.min.z, bb.max.z, (j + 0.5f) / steps));
                    // Pas de sonde de route : ce test-ci saupoudre des TOITS, ou le cap par
                    // rapport a la rue ne veut rien dire. Le cap reste tire au quart de tour.
                    CityBrushPlacement.PropCandidates(brush, BrushLayer.Props, centre,
                                                      i * 100 + j, outp.Count, surf, null, placed, outp);
                }

            Spawn(brush, outp, "Saupoudrer des props");
            Debug.Log($"[Ville][test] {outp.Count} props poses.");
        }

        private static bool Setup(out CityBrush brush)
        {
            brush = Object.FindFirstObjectByType<CityBrush>();
            if (brush == null) { Debug.LogError("[Ville][test] Aucun CityBrush dans la scene."); return false; }
            if (brush.palette == null) { Debug.LogError("[Ville][test] Le CityBrush n'a pas de palette."); return false; }
            if (brush.palette.CountUsable(BrushLayer.Batiments) == 0)
            {
                Debug.LogError("[Ville][test] Palette sans batiment utilisable.");
                return false;
            }
            return true;
        }

        private static void Spawn(CityBrush brush, List<CityBrushPlacement.Placement> list, string label)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(label);

            Transform parent = brush.Container;
            if (parent == null)
            {
                var go = new GameObject(CityBrush.ContainerName);
                go.transform.SetParent(brush.transform, false);
                Undo.RegisterCreatedObjectUndo(go, "Conteneur de batiments");
                parent = go.transform;
            }

            foreach (var p in list)
            {
                if (p.entry == null || p.entry.prefab == null) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(p.entry.prefab, parent);
                if (go == null) continue;
                var pos = new Vector3(p.position.x, brush.groundHeight - p.sink, p.position.z);
                // Les props sont deja poses sur une surface reelle : leur y ne doit pas etre
                // rabattu au sol comme celui des batiments.
                if (p.entry.layer == BrushLayer.Props) pos = p.position;
                go.transform.SetPositionAndRotation(pos, p.rotation);
                go.transform.localScale = Vector3.one * p.scale;
                GameObjectUtility.SetStaticEditorFlags(go,
                    StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic
                    | StaticEditorFlags.OccludeeStatic);
                Undo.RegisterCreatedObjectUndo(go, label);
            }

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(brush.gameObject.scene);
        }
    }
}
