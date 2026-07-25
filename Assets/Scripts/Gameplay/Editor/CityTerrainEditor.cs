using Gameplay.City;
using UnityEditor;
using UnityEngine;

namespace Gameplay.EditorTools
{
    // Inspecteur du champ de hauteur. Ne sert qu'a une chose que l'inspecteur par defaut ne peut
    // pas faire : remettre la couche PEINTE a plat.
    //
    // Le bruit procedural, lui, n'a pas besoin de bouton -- il se "reinitialise" en changeant sa
    // graine, et c'est deja un champ. Les deux couches sont independantes et s'additionnent :
    // effacer le sculpt rend donc le terrain au Perlin seul, sans toucher aux reglages.
    [CustomEditor(typeof(CityTerrain))]
    public class CityTerrainEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var terrain = (CityTerrain)target;
            EditorGUILayout.Space();

            if (terrain.sculpt == null)
            {
                EditorGUILayout.HelpBox(
                    "Rien de peint : le terrain est le bruit seul. La couche de sculpt se cree " +
                    "au premier coup de pinceau (outil Sculpter le terrain, barre d'outils de " +
                    "la vue).", MessageType.None);
                return;
            }

            // Volontairement PAS le nombre de cellules peintes ici : le compter demande de
            // parcourir toute la couche, et OnInspectorGUI repasse a chaque survol de souris.
            // Le chiffre est calcule une seule fois, au moment de confirmer.
            EditorGUILayout.LabelField(
                $"Couche : {terrain.sculpt.resolution} cellules de {terrain.sculptCell} m " +
                $"({terrain.sculpt.resolution * terrain.sculptCell * 0.5f:F0} m de portee)",
                EditorStyles.miniLabel);

            if (GUILayout.Button("Effacer le relief peint (revenir au bruit seul)"))
                ClearSculpt(terrain);
        }

        private static void ClearSculpt(CityTerrain terrain)
        {
            int painted = terrain.sculpt.PaintedCount();
            if (painted == 0)
            {
                EditorUtility.DisplayDialog("Effacer le relief peint",
                    "La couche est deja vide : le terrain est deja le bruit seul.", "D'accord");
                return;
            }

            // Geste destructeur -> on confirme, meme s'il est annulable. Effacer une heure de
            // sculpt sur un clic mal vise n'est pas rattrapable si on ne s'en rend pas compte
            // tout de suite.
            if (!EditorUtility.DisplayDialog("Effacer le relief peint",
                    $"{painted} cellules sculptees vont etre remises a plat.\n\n" +
                    "Le bruit procedural n'est pas touche : le terrain redevient exactement ce " +
                    "que la graine, l'amplitude et l'echelle decrivent.\n\n" +
                    "Annulable par Ctrl+Z.", "Effacer", "Annuler")) return;

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Effacer le relief peint");
            Undo.RegisterCompleteObjectUndo(terrain.sculpt, "Effacer le relief peint");
            terrain.sculpt.Clear();
            terrain.InvalidateSculpt();
            EditorUtility.SetDirty(terrain.sculpt);
            Undo.CollapseUndoOperations(group);

            // L'empreinte de reference suit l'ecriture volontaire, sinon la prochaine annulation
            // -- meme sans rapport -- croirait que la couche a change et refereait le sol.
            CityTerrainTool.Stamp(terrain);

            AssetDatabase.SaveAssets();
            if (RoadNetworkEditor.AutoGround) CityGroundBuilder.Build(false);
            SceneView.RepaintAll();
            Debug.Log($"[Ville] Relief peint efface ({painted} cellules). Le terrain est revenu " +
                      "au bruit seul.", terrain);
        }
    }
}
