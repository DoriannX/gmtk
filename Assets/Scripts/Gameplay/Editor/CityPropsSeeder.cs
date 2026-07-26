using System.Collections.Generic;
using Gameplay.City;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Gameplay.EditorTools
{
    // MOBILIER DE RUE, d'un bouton : garnit TOUT le reseau routier d'un coup.
    //
    // Le plan lui-meme n'est pas ici, il vit dans CityBrushPlacement.BuildStreetCandidates,
    // partage avec la couche PropsRue du pinceau. Les deux revelent le MEME champ deterministe :
    // ce menu l'instancie en entier, le pinceau n'en sort que ce qui passe sous son disque. Meme
    // graine -> meme ville, et on peut commencer au pinceau puis finir au bouton sans que rien ne
    // se contredise.
    //
    // Conteneur recycle a chaque passe, donc relancable sans empiler. Il ne touche PAS a ce qui a
    // ete peint (qui vit sous CityBrush/Peints) : les deux gestes coexistent.
    //
    // Rien a faire pour le grind : les prefabs grindables portent deja un GrindRailSource aux
    // rails figes (pose par PropsImporter), donc chaque rambarde declare son bout au demarrage et
    // GrindRailNetwork.Build les recolle en une ligne continue.
    public static class CityPropsSeeder
    {
        const string ContainerName = "PropsRue";
        const int MaxProps = 4000;

        [MenuItem("Tools/Ville/Props/3 - Garnir les rues", false, 122)]
        public static void Seed()
        {
            var palette = FindPalette();
            if (palette == null)
            {
                Debug.LogError("[PropsRue] Aucune CityPalette trouvee. Lance l'import des props d'abord.");
                return;
            }
            if (palette.CountUsable(BrushLayer.PropsRue) == 0)
            {
                Debug.LogError("[PropsRue] La palette n'a aucune entree utilisable sur la couche " +
                               "PropsRue. Lance Tools/Ville/Props/2 - Extraire les prefabs de sol.");
                return;
            }

            var nets = Object.FindObjectsByType<RoadNetwork>(FindObjectsSortMode.None);
            if (nets.Length == 0)
            {
                Debug.LogError("[PropsRue] Aucun RoadNetwork dans la scene : il n'y a pas de trottoir a garnir.");
                return;
            }

            var brush = Object.FindFirstObjectByType<CityBrush>();
            int seed = brush != null ? brush.seed : 12345;

            var plan = new List<CityBrushPlacement.Placement>();
            CityBrushPlacement.BuildStreetCandidates(palette, seed, nets, MaxProps, plan);

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Garnir les rues");

            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
                if (root.name == ContainerName) Undo.DestroyObjectImmediate(root);

            var container = new GameObject(ContainerName);
            Undo.RegisterCreatedObjectUndo(container, "Garnir les rues");

            foreach (var p in plan) CityPropsSpawn.Spawn(p, container.transform, "Garnir les rues");

            EditorSceneManager.MarkSceneDirty(container.scene);
            Undo.CollapseUndoOperations(group);

            Debug.Log($"[PropsRue] {plan.Count} props poses le long de {nets.Length} reseau(x). " +
                      "Les grindables portent leurs rails : rien a rebaker.");
            if (plan.Count >= MaxProps)
                Debug.LogWarning($"[PropsRue] Plafond de {MaxProps} props atteint, la fin du reseau " +
                                 "n'a pas ete garnie.");
        }

        private static CityPalette FindPalette()
        {
            var brush = Object.FindFirstObjectByType<CityBrush>();
            if (brush != null && brush.palette != null) return brush.palette;
            foreach (string guid in AssetDatabase.FindAssets("t:CityPalette"))
                return AssetDatabase.LoadAssetAtPath<CityPalette>(AssetDatabase.GUIDToAssetPath(guid));
            return null;
        }
    }

    // Instanciation d'un Placement, partagee par le menu et le pinceau. Le seul interet de la
    // sortir : les deux doivent poser EXACTEMENT le meme objet (memes drapeaux statiques, meme
    // enregistrement d'undo), sinon un prop peint et un prop seme ne se comportent pas pareil au
    // rendu.
    internal static class CityPropsSpawn
    {
        public static GameObject Spawn(CityBrushPlacement.Placement p, Transform parent, string undoName)
        {
            if (p.entry == null || p.entry.prefab == null) return null;

            var go = (GameObject)PrefabUtility.InstantiatePrefab(p.entry.prefab, parent);
            if (go == null) return null;

            go.transform.SetPositionAndRotation(p.position, p.rotation);
            go.transform.localScale = Vector3.one * p.scale;

            // Volontairement PAS ContributeGI : les FBX de ville n'ont pas d'UV de lightmap, ca
            // sortirait un avertissement par objet si la GI bakee etait activee.
            GameObjectUtility.SetStaticEditorFlags(go,
                StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic
                | StaticEditorFlags.OccludeeStatic);

            Undo.RegisterCreatedObjectUndo(go, undoName);
            return go;
        }
    }
}
