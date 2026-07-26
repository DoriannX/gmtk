using Gameplay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Gameplay.EditorTools
{
    // POSER / RETIRER LE TUTO EN UN CLIC.
    // Le tuto n'a ni prefab ni asset : c'est un seul composant sans champ obligatoire. Le
    // poser, c'est donc juste AddComponent sur l'objet qui porte deja les managers (celui du
    // MenuFlow). D'ou le bouton place sur l'inspecteur de MenuFlow : c'est l'objet qu'on
    // selectionne de toute facon quand on regle une scene, et le bouton BASCULE (ajouter /
    // retirer) pour qu'il n'y ait jamais deux boutons a se demander lequel est actif.
    //
    // Les entrees Tools/Tuto/... font la meme chose depuis le menu, comme les paires
    // "Spawner / Retirer le joueur" de CityPlayerSpawner.
    internal static class TutorialFlowTool
    {
        internal static TutorialFlow Find()
        {
            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            {
                var found = root.GetComponentInChildren<TutorialFlow>(true);
                if (found != null) return found;
            }
            return null;
        }

        // Hote par defaut : l'objet qui porte MenuFlow (les "Managers"). Sinon un objet dedie.
        private static GameObject Host()
        {
            var flow = Object.FindAnyObjectByType<UI.MenuFlow>(FindObjectsInactive.Include);
            if (flow != null) return flow.gameObject;

            var go = new GameObject("Tuto");
            Undo.RegisterCreatedObjectUndo(go, "Ajouter le tuto a la scene");
            return go;
        }

        [MenuItem("Tools/Tuto/Ajouter le tuto a la scene", false, 100)]
        internal static void Add()
        {
            var existing = Find();
            if (existing != null)
            {
                Debug.LogWarning("[Tuto] Deja present sur " + existing.gameObject.name + ".", existing);
                Selection.activeGameObject = existing.gameObject;
                return;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Ajouter le tuto a la scene");

            var host = Host();
            var tuto = Undo.AddComponent<TutorialFlow>(host);

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(host.scene);
            Selection.activeGameObject = host;
            Debug.Log("[Tuto] Pose sur " + host.name + ". Rien a cabler : les etapes sont en dur.", tuto);
        }

        [MenuItem("Tools/Tuto/Retirer le tuto de la scene", false, 101)]
        internal static void Remove()
        {
            var tuto = Find();
            if (tuto == null) { Debug.LogWarning("[Tuto] Aucun tuto dans cette scene."); return; }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Retirer le tuto de la scene");

            var scene = tuto.gameObject.scene;
            // Objet dedie cree par Add() : on retire l'objet entier, pas juste le composant.
            if (tuto.gameObject.name == "Tuto" && tuto.GetComponents<Component>().Length == 2)
                Undo.DestroyObjectImmediate(tuto.gameObject);
            else
                Undo.DestroyObjectImmediate(tuto);

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log("[Tuto] Retire de la scene.");
        }

        [MenuItem("Tools/Tuto/Oublier la sauvegarde", false, 120)]
        internal static void Forget() => TutorialFlow.ForgetSave();

        // Le bouton bascule, partage par les deux inspecteurs.
        internal static void ToggleButton()
        {
            var tuto = Find();
            string label = tuto == null ? "Ajouter le tuto a la scene" : "Retirer le tuto de la scene";
            if (GUILayout.Button(label, GUILayout.Height(32)))
            {
                if (tuto == null) Add(); else Remove();
            }
        }
    }

    [CustomEditor(typeof(UI.MenuFlow))]
    public class MenuFlowInspector : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Tuto : un seul composant, aucune reference a cabler. Pose-le dans une scene " +
                "jouable, il se lance au demarrage de la partie et se desactive une fois vu " +
                "(Options > Revoir le tuto pour le rejouer en accelere).",
                MessageType.Info);
            TutorialFlowTool.ToggleButton();
        }
    }

    [CustomEditor(typeof(TutorialFlow))]
    public class TutorialFlowInspector : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Retirer le tuto de la scene", GUILayout.Height(28)))
                TutorialFlowTool.Remove();
            if (GUILayout.Button("Oublier la sauvegarde", GUILayout.Height(28)))
                TutorialFlow.ForgetSave();
            EditorGUILayout.EndHorizontal();

            if (Application.isPlaying && GUILayout.Button("Rejouer maintenant", GUILayout.Height(24)))
                ((TutorialFlow)target).Replay();
        }
    }
}
