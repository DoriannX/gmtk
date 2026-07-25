using System.Collections.Generic;
using Gameplay.City;
using UnityEditor;
using UnityEngine;

namespace Gameplay.EditorTools
{
    // Inspecteur de palette. Son vrai role est la BOUSSOLE de facade : le facadeYaw est devine a
    // l'extraction et se trompe sur les batiments symetriques, il faut pouvoir le corriger en
    // voyant ce qu'on corrige. Vignette d'asset + rectangle d'emprise + fleche : ca suffit, pas
    // besoin d'un PreviewRenderUtility.
    [CustomEditor(typeof(CityPalette))]
    public class CityPaletteEditor : Editor
    {
        const string PrefabDir = "Assets/Prefabs/City";
        const string PropDir = "Assets/Prefabs/City/Props";

        private BrushLayer filter = BrushLayer.Batiments;
        private Vector2 scroll;

        public override void OnInspectorGUI()
        {
            var palette = (CityPalette)target;

            EditorGUILayout.LabelField(
                $"{palette.CountUsable(BrushLayer.Batiments)} batiments / " +
                $"{palette.CountUsable(BrushLayer.Props)} props utilisables",
                EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Rescanner les dossiers")) Rescan(palette);
            if (GUILayout.Button("Nettoyer les entrees mortes")) Clean(palette);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                "facadeYaw = le cap vers lequel regarde la FACADE quand le prefab est a yaw 0. " +
                "Le pinceau le retranche pour que chaque batiment regarde la rue. Les entrees " +
                "marquees \"A VERIFIER\" sont symetriques : la devinette n'a rien pu trancher.",
                MessageType.Info);

            filter = (BrushLayer)EditorGUILayout.EnumPopup("Afficher la couche", filter);
            EditorGUILayout.Space();

            scroll = EditorGUILayout.BeginScrollView(scroll);
            for (int i = 0; i < palette.entries.Count; i++)
            {
                var e = palette.entries[i];
                if (e.layer != filter) continue;
                DrawEntry(palette, e, i);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawEntry(CityPalette palette, CityPalette.Entry e, int index)
        {
            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.BeginHorizontal();

            Rect thumb = GUILayoutUtility.GetRect(84f, 84f, GUILayout.Width(84f), GUILayout.ExpandWidth(false));
            Texture2D preview = e.prefab != null ? AssetPreview.GetAssetPreview(e.prefab) : null;
            if (preview != null) GUI.DrawTexture(thumb, preview, ScaleMode.ScaleToFit);
            else EditorGUI.DrawRect(thumb, new Color(0.15f, 0.15f, 0.15f));

            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(e.prefab != null ? e.prefab.name : "(prefab manquant)",
                                       EditorStyles.boldLabel);
            e.prefab = (GameObject)EditorGUILayout.ObjectField(e.prefab, typeof(GameObject), false);
            EditorGUILayout.BeginHorizontal();
            e.enabled = EditorGUILayout.ToggleLeft("actif", e.enabled, GUILayout.Width(60f));
            e.weight = EditorGUILayout.Slider(e.weight, 0f, 8f);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField($"emprise {e.footprint.x:F1} x {e.footprint.y:F1} x {e.footprint.z:F1} m",
                                       EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            // --- boussole ---
            EditorGUILayout.BeginHorizontal();
            Rect compass = GUILayoutUtility.GetRect(84f, 84f, GUILayout.Width(84f), GUILayout.ExpandWidth(false));
            DrawCompass(compass, e);

            EditorGUILayout.BeginVertical();
            e.facadeYaw = EditorGUILayout.Slider("facade (deg)", e.facadeYaw, 0f, 360f);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("N")) e.facadeYaw = 0f;
            if (GUILayout.Button("E")) e.facadeYaw = 90f;
            if (GUILayout.Button("S")) e.facadeYaw = 180f;
            if (GUILayout.Button("O")) e.facadeYaw = 270f;
            EditorGUILayout.EndHorizontal();
            if (e.layer == BrushLayer.Props)
                e.align = (PropAlign)EditorGUILayout.EnumPopup("collage", e.align);
            e.scaleJitter = EditorGUILayout.Slider("variation d'echelle", e.scaleJitter, 0f, 0.5f);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(e.note))
                EditorGUILayout.LabelField(e.note, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Retirer de la palette", GUILayout.Width(160f)))
            {
                Undo.RecordObject(palette, "Retirer une entree");
                palette.entries.RemoveAt(index);
                EditorUtility.SetDirty(palette);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(palette, "Modifier la palette");
                EditorUtility.SetDirty(palette);
            }
            EditorGUILayout.EndVertical();
        }

        // Vue de dessus : le rectangle est l'emprise reelle, la fleche part du centre vers la
        // facade. C'est ce qui doit regarder la rue.
        private static void DrawCompass(Rect r, CityPalette.Entry e)
        {
            EditorGUI.DrawRect(r, new Color(0.13f, 0.14f, 0.16f));
            Vector2 c = r.center;

            float w = Mathf.Max(0.1f, e.footprint.x), d = Mathf.Max(0.1f, e.footprint.z);
            float k = (r.width - 24f) / Mathf.Max(w, d);
            var box = new Rect(c.x - w * k * 0.5f, c.y - d * k * 0.5f, w * k, d * k);
            EditorGUI.DrawRect(box, new Color(0.42f, 0.46f, 0.55f));

            // Ecran : +X a droite, et le +Z du monde pointe vers le HAUT -> on inverse le y.
            float rad = e.facadeYaw * Mathf.Deg2Rad;
            var dir = new Vector2(Mathf.Sin(rad), -Mathf.Cos(rad));
            Handles.BeginGUI();
            Handles.color = new Color(1f, 0.82f, 0.25f);
            Vector2 tip = c + dir * (r.width * 0.44f);
            Handles.DrawAAPolyLine(3f, c, tip);
            Vector2 side = new Vector2(-dir.y, dir.x) * 5f;
            Handles.DrawAAConvexPolygon(tip, (Vector3)(tip - dir * 10f + side), (Vector3)(tip - dir * 10f - side));
            Handles.EndGUI();

            GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, 14f), "vue de dessus", EditorStyles.miniLabel);
        }

        // ---------------------------------------------------------------- maintenance

        // Ajoute les prefabs trouves sur disque et absents de la liste. Sans ca la palette
        // pourrit des qu'un nouvel asset arrive.
        private static void Rescan(CityPalette palette)
        {
            Undo.RecordObject(palette, "Rescanner la palette");
            int added = 0;
            added += Scan(palette, PrefabDir, BrushLayer.Batiments, false);
            added += Scan(palette, PropDir, BrushLayer.Props, true);
            EditorUtility.SetDirty(palette);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Ville] Rescan : {added} nouvelle(s) entree(s).");
        }

        private static int Scan(CityPalette palette, string dir, BrushLayer layer, bool recursive)
        {
            if (!AssetDatabase.IsValidFolder(dir)) return 0;
            int added = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { dir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                // Sans recursion on ignore les sous-dossiers, sinon les props remonteraient dans
                // la couche Batiments.
                if (!recursive && path.Substring(dir.Length + 1).Contains("/")) continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null || palette.Find(prefab) != null) continue;

                // Renderer.bounds sur un ASSET n'est pas fiable : on mesure une instance.
                var probe = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                var b = BatimentsImporter.Fit(probe);
                Object.DestroyImmediate(probe);

                palette.entries.Add(new CityPalette.Entry
                {
                    prefab = prefab,
                    layer = layer,
                    footprint = b.size,
                    note = "ajoute par rescan, facade a verifier",
                });
                added++;
            }
            return added;
        }

        private static void Clean(CityPalette palette)
        {
            Undo.RecordObject(palette, "Nettoyer la palette");
            int before = palette.entries.Count;
            palette.entries.RemoveAll(e => e.prefab == null);
            EditorUtility.SetDirty(palette);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Ville] {before - palette.entries.Count} entree(s) morte(s) retiree(s).");
        }
    }
}
