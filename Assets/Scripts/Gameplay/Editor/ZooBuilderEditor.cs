using UnityEditor;
using UnityEngine;

namespace Gameplay.EditorTools
{
    // Bouton "Build Zoo" dans l'inspecteur.
    [CustomEditor(typeof(ZooBuilder))]
    public class ZooBuilderInspector : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space();
            if (GUILayout.Button("Build Zoo", GUILayout.Height(32)))
                ((ZooBuilder)target).Build();
        }
    }

    // Auto-update : quand un asset est importe/supprime/deplace, si un ZooBuilder est
    // present dans une scene ouverte et qu'un des chemins touche ses dossiers scannes,
    // on reconstruit. delayCall -> hors du cycle d'import (evite les avertissements).
    public class ZooAutoRefresh : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            var builders = Object.FindObjectsByType<ZooBuilder>(FindObjectsSortMode.None);
            if (builders.Length == 0) return;

            // Ignore nos propres artefacts (labels/instances) : ils vivent en scene, pas
            // en asset -> les changements ci-dessous sont forcement des assets projet.
            bool relevant = false;
            foreach (var b in builders)
            {
                var so = new SerializedObject(b);
                var folders = so.FindProperty("folders");
                for (int i = 0; i < folders.arraySize && !relevant; i++)
                {
                    string dir = folders.GetArrayElementAtIndex(i).stringValue;
                    if (string.IsNullOrEmpty(dir)) continue;
                    dir = dir.TrimEnd('/');
                    relevant |= Touches(imported, dir) || Touches(deleted, dir)
                             || Touches(moved, dir) || Touches(movedFrom, dir);
                }
                if (relevant) break;
            }
            if (!relevant) return;

            EditorApplication.delayCall += () =>
            {
                foreach (var b in Object.FindObjectsByType<ZooBuilder>(FindObjectsSortMode.None))
                    if (b != null) b.Build();
            };
        }

        private static bool Touches(string[] paths, string dir)
        {
            foreach (string p in paths)
                if (p.StartsWith(dir + "/") || p == dir) return true;
            return false;
        }
    }
}
