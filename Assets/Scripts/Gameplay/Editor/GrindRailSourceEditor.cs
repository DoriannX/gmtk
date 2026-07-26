using Gameplay;
using UnityEditor;
using UnityEngine;

namespace Gameplay.EditorTools
{
    // Le bouton qui fige les rails d'un GrindRailSource. C'est le SEUL point d'entree de
    // l'extraction geometrique : un objet a la fois, sur demande, resultat visible au gizmo avant
    // d'etre serialise (cf. l'avertissement en tete de GrindRailExtractor).
    [CustomEditor(typeof(GrindRailSource))]
    public class GrindRailSourceEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var src = (GrindRailSource)target;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                src.HasBaked
                    ? "Rails figes : le demarrage ne fait que les transformer, cout nul. A " +
                      "refiger si la geometrie change."
                    : "Aucun rail fige : cet objet NE GRINDERA PAS. L'extraction ne tourne jamais " +
                      "au demarrage -- clique 'Figer les rails', puis verifie le gizmo cyan.",
                src.HasBaked ? MessageType.Info : MessageType.Warning);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Figer les rails"))
            {
                Undo.RecordObject(src, "Figer les rails");
                int n = src.Bake();
                EditorUtility.SetDirty(src);
                Debug.Log($"[GrindRailSource] {src.name} : {n} rail(s) figes.", src);
            }
            if (GUILayout.Button("Effacer les rails"))
            {
                Undo.RecordObject(src, "Effacer les rails");
                src.ClearBaked();
                EditorUtility.SetDirty(src);
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
