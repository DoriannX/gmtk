using Gameplay.City;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Gameplay.EditorTools
{
    // POSE LE JOUEUR DANS LA SCENE DE VILLE, cable et jouable.
    //
    // Reproduit le montage de GymRoom.unity, qui est la reference : une instance de
    // PlayerTruck.prefab + une instance de FollowCamera.prefab dont le champ `target` pointe le
    // transform du joueur. Rien d'autre n'est necessaire -- ArcadeCarController.Input tombe tout
    // seul sur KeyboardCarInput.I, et TrickSystem/TrickHud sont deja sur le prefab.
    //
    // Ce qui est DELIBEREMENT laisse de cote (GymRoom les ajoute a la main sur son instance) :
    // ScoreGauge et GrindBalanceHud. C'est du HUD de scoring, ca ne conditionne pas le fait de
    // rouler, et leurs references croisees se recablent mal a la regeneration.
    public static class CityPlayerSpawner
    {
        const string PlayerPath = "Assets/Prefabs/PlayerTruck.prefab";
        const string CameraPath = "Assets/Prefabs/FollowCamera.prefab";

        // Position de depart : sur la chaussee, a un cinquieme du premier segment, dans le sens
        // de la route. Mieux que l'origine, qui tombe regulierement dans un pate de maisons.
        const float StartAlong = 0.2f;
        const float DropHeight = 1.4f;

        [MenuItem("Tools/Ville/Spawner le joueur")]
        public static void Spawn()
        {
            var playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath);
            var cameraPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CameraPath);
            if (playerPrefab == null || cameraPrefab == null)
            {
                Debug.LogError($"[Ville] Prefab introuvable : {PlayerPath} ou {CameraPath}.");
                return;
            }

            Vector3 pos;
            Quaternion rot;
            StartPose(out pos, out rot);

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Spawner le joueur");

            // Recycle au lieu d'empiler : relancer le menu doit REPOSER le joueur au depart,
            // pas semer une deuxieme voiture qui se percute avec la premiere.
            Destroy(FindInstance(playerPrefab));
            Destroy(FindInstance(cameraPrefab));

            var player = (GameObject)PrefabUtility.InstantiatePrefab(playerPrefab);
            player.transform.SetPositionAndRotation(pos, rot);
            Undo.RegisterCreatedObjectUndo(player, "Spawner le joueur");

            var cam = (GameObject)PrefabUtility.InstantiatePrefab(cameraPrefab);
            cam.transform.SetPositionAndRotation(pos + rot * new Vector3(0f, 4f, -8f), rot);
            Undo.RegisterCreatedObjectUndo(cam, "Spawner le joueur");

            Wire(cam, player.transform);
            SilenceOtherCameras(cam);

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(player.scene);
            Selection.activeGameObject = player;
            Debug.Log($"[Ville] Joueur pose en {pos}, camera cablee dessus. Entrer en play pour rouler.");
        }

        [MenuItem("Tools/Ville/Retirer le joueur")]
        public static void Remove()
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Retirer le joueur");
            Destroy(FindInstance(AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath)));
            Destroy(FindInstance(AssetDatabase.LoadAssetAtPath<GameObject>(CameraPath)));
            Undo.CollapseUndoOperations(group);
        }

        // Le champ `target` de CarFollowCamera est prive : passer par SerializedObject et pas
        // par SetTarget(). Sur une INSTANCE DE PREFAB c'est la seule voie qui enregistre la
        // valeur comme un override -- un SetTarget + SetDirty se ferait ecraser au premier
        // revert du prefab.
        static void Wire(GameObject cam, Transform target)
        {
            var follow = cam.GetComponentInChildren<Gameplay.CarFollowCamera>();
            if (follow == null)
            {
                Debug.LogWarning("[Ville] Pas de CarFollowCamera sur le prefab de camera : rien a cabler.");
                return;
            }
            var so = new SerializedObject(follow);
            var prop = so.FindProperty("target");
            if (prop == null)
            {
                Debug.LogWarning("[Ville] CarFollowCamera n'expose plus de champ 'target'.");
                return;
            }
            prop.objectReferenceValue = target;
            so.ApplyModifiedProperties();

            // La scene porte l'ambiance cyberpunk : sans ca la nouvelle camera rend sans bloom
            // ni tonemapping et tout parait delave.
            var c = cam.GetComponentInChildren<Camera>();
            if (c != null)
            {
                var data = c.GetComponent<UniversalAdditionalCameraData>();
                if (data == null) data = c.gameObject.AddComponent<UniversalAdditionalCameraData>();
                data.renderPostProcessing = true;
            }
        }

        // Deux cameras actives = celle de la scene gagne au hasard, et deux AudioListener
        // crachent un avertissement par frame. On DESACTIVE l'ancienne au lieu de la detruire :
        // c'est le point de vue d'edition de l'auteur, il le recuperera en decochant.
        static void SilenceOtherCameras(GameObject keep)
        {
            var mine = keep.GetComponentInChildren<Camera>();
            foreach (var c in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                if (c == mine || c.transform.IsChildOf(keep.transform)) continue;
                if (!c.gameObject.activeSelf) continue;
                Undo.RecordObject(c.gameObject, "Spawner le joueur");
                c.gameObject.SetActive(false);
                Debug.Log($"[Ville] Camera '{c.name}' desactivee (une seule camera a la fois).");
            }
        }

        // Depart sur la route : on echantillonne l'axe du premier segment puis on tombe sur le
        // collider pour se poser sur la CHAUSSEE et pas dans le vide au-dessus.
        static void StartPose(out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;

            var net = Object.FindFirstObjectByType<RoadNetwork>();
            if (net != null && net.SegmentCount > 0)
            {
                float len = net.SegmentLength(0);
                Vector3 p, tan;
                if (net.SampleSegment(0, len * StartAlong, out p, out tan, out len))
                {
                    pos = p;
                    if (tan.sqrMagnitude > 1e-6f) rot = Quaternion.LookRotation(tan, Vector3.up);
                }
            }

            RaycastHit hit;
            if (Physics.Raycast(pos + Vector3.up * 60f, Vector3.down, out hit, 200f,
                                ~0, QueryTriggerInteraction.Ignore))
                pos.y = hit.point.y;
            pos.y += DropHeight;
        }

        static GameObject FindInstance(GameObject prefab)
        {
            if (prefab == null) return null;
            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
                if (PrefabUtility.GetCorrespondingObjectFromSource(root) == prefab) return root;
            return null;
        }

        static void Destroy(GameObject go)
        {
            if (go != null) Undo.DestroyObjectImmediate(go);
        }
    }
}
