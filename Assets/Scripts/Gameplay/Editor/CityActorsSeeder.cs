using System.Collections.Generic;
using Gameplay.City;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Gameplay.EditorTools
{
    // ACTEURS de la ville d'un bouton : le trafic sur la chaussee, les pietons sur les trottoirs.
    //
    // Meme geometrie que le mobilier de rue (CityBrushPlacement.BuildRowPath / SampleRowPath) :
    // on marche la polyligne DECALEE de chaque segment, pas son axe, sinon les acteurs se tassent
    // a l'interieur des virages. Meme rejet de carrefour aussi (ProbeRoad -> probe.node >= 0) :
    // l'emprise d'un croisement deborde largement la demi-chaussee, et une voiture posee dedans
    // demarre en travers de la voie transversale.
    //
    // Conteneurs recycles a chaque passe : relancable sans empiler.
    //
    // Ce menu POSE les acteurs, il ne les cable pas. Sur le reseau RoadNetwork ils sont encore
    // inertes -- AICarController conduit sur CityGrid (absent de la scene Game) et Pedestrian
    // marche sur un NavMesh qu'aucun NavMeshSurface ne bake ici. C'est le portage a faire.
    public static class CityActorsSeeder
    {
        const string TrafficContainer = "Trafic";
        const string PedestrianContainer = "Pietons";

        const string VehicleFolder = "Assets/Prefabs/Vehicles";
        const string PedestrianFolder = "Assets/Prefabs/Pedestrians";
        const string ActorLayer = "Actors";

        // Les deux decalages se DEDUISENT du kit (RoadwayHalfWidth / RoadHalfWidth) au lieu
        // d'etre tapes en dur : voie de droite au milieu de la demi-chaussee, pietons au milieu
        // du trottoir. Changer de tuile de route deplace les acteurs avec elle.
        static float LaneOffset(RoadNetwork net) => net.RoadwayHalfWidth * 0.5f;
        static float SidewalkOffset(RoadNetwork net) => (net.RoadwayHalfWidth + net.RoadHalfWidth) * 0.5f;

        // Part de vehicules qui roulent d'eux-memes. Le reste reste gare : c'est la reserve
        // dans laquelle les pietons montent (Pedestrian.TrySeekCar).
        const float AutoDriveShare = 0.6f;

        const float CarSpacing = 26f;
        const float PedSpacing = 11f;
        const float StepJitter = 0.3f;    // +/- 30 % sur le pas, pour casser le cordeau
        const float CarLift = 0.12f;      // la chaussee est bombee : on pose au-dessus de l'axe

        const int MaxActors = 600;

        [MenuItem("Tools/Ville/Acteurs/Peupler la ville", false, 140)]
        public static void Seed()
        {
            var nets = Object.FindObjectsByType<RoadNetwork>(FindObjectsSortMode.None);
            if (nets.Length == 0)
            {
                Debug.LogError("[Acteurs] Aucun RoadNetwork dans la scene : il n'y a pas de rue a peupler.");
                return;
            }

            var vehicles = LoadPrefabs(VehicleFolder, "Traffic_");
            var peds = LoadPrefabs(PedestrianFolder, "Pedestrian_");
            if (vehicles.Count == 0 && peds.Count == 0)
            {
                Debug.LogError($"[Acteurs] Aucun prefab trouve dans {VehicleFolder} ni {PedestrianFolder}.");
                return;
            }

            var brush = Object.FindFirstObjectByType<CityBrush>();
            int seed = brush != null ? brush.seed : 12345;

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Peupler la ville");

            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
                if (root.name == TrafficContainer || root.name == PedestrianContainer)
                    Undo.DestroyObjectImmediate(root);

            var traffic = NewContainer(TrafficContainer);
            var walkers = NewContainer(PedestrianContainer);

            int cars = 0, walkerCount = 0;
            for (int n = 0; n < nets.Length; n++)
            {
                var net = nets[n];
                for (int k = 0; k < net.SegmentCount; k++)
                {
                    for (int side = -1; side <= 1; side += 2)
                    {
                        if (vehicles.Count > 0)
                            cars += Walk(net, k, side, LaneOffset(net), CarSpacing, vehicles,
                                         traffic.transform, seed, n, true, cars);
                        if (peds.Count > 0)
                            walkerCount += Walk(net, k, side, SidewalkOffset(net), PedSpacing, peds,
                                               walkers.transform, seed + 7777, n, false, walkerCount);
                    }
                }
            }

            EditorSceneManager.MarkSceneDirty(traffic.scene);
            Undo.CollapseUndoOperations(group);

            Debug.Log($"[Acteurs] {cars} vehicules et {walkerCount} pietons poses le long de " +
                      $"{nets.Length} reseau(x). Ils sont INERTES : AICarController attend un " +
                      "CityGridAuthoring et Pedestrian un NavMesh -- c'est le portage sur RoadNetwork.");
        }

        // Marche un cote d'un segment et instancie un prefab tous les `spacing` metres.
        // `alongLane` : vehicule oriente dans le sens de circulation, sinon yaw libre (pieton).
        private static int Walk(RoadNetwork net, int segment, int side, float offset, float spacing,
                                List<GameObject> prefabs, Transform parent, int seed, int netIndex,
                                bool alongLane, int total)
        {
            var path = CityBrushPlacement.BuildRowPath(net, segment, side, offset);
            if (path == null) return 0;

            uint h = CityBrushPlacement.Hash(seed, netIndex, segment, side);
            int placed = 0, index = 0;

            // Le pas est decide AVANT tout rejet, et indexe sur le NUMERO DE PAS et non sur le
            // nombre de poses : un carrefour saute ne doit pas decaler toute la suite de la rue
            // (meme raison que CityBrushPlacement.WalkStreet).
            for (float s = spacing * 0.5f; s <= path.total && total + placed < MaxActors; index++)
            {
                CityBrushPlacement.SampleRowPath(path, s, out Vector3 p, out Vector3 nr);
                uint ph = CityBrushPlacement.Hash((int)h, 0, index, 991);
                s += Mathf.Max(1f, spacing * (1f + Signed(ph, 10) * StepJitter));

                if (net.ProbeRoad(p, 80f, out RoadNetwork.RoadProbe probe) && probe.valid)
                {
                    if (probe.node >= 0) continue;                         // emprise du carrefour
                    // Chaussee d'une AUTRE rue : pres d'un croisement la perpendiculaire passe a
                    // portee sans que son noeud soit le plus proche. Un pieton est cense etre a
                    // `offset` de SON axe ; nettement plus pres d'un axe quelconque, il mord une
                    // voie transverse. Le test ne vaut rien pour un vehicule, qui EST sur l'axe.
                    if (!alongLane && probe.distance < offset - 0.5f) continue;
                }

                var prefab = prefabs[Mathf.Clamp((int)(CityBrushPlacement.Rand(ph, 3) * prefabs.Count),
                                                 0, prefabs.Count - 1)];
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
                if (go == null) continue;

                // Layer "Actors" : le NavMeshSurface l'exclut de son bake. Sans ca chaque
                // voiture et chaque pieton creuse un trou dans le NavMesh a sa position de
                // depart -- trou qui reste apres qu'ils aient bouge.
                int actorLayer = LayerMask.NameToLayer(ActorLayer);
                if (actorLayer >= 0) go.layer = actorLayer;

                // -nr regarde la chaussee ; le sens de circulation est la tangente orientee par le
                // cote, soit exactement Cross(nr, up) (conduite a droite).
                //
                // Un pieton, lui, garde la pose du prefab : Pedestrian capture sa rotation de
                // depart comme tilt d'import et la reapplique a chaque cap. Le semeur lui
                // donnait un cap au hasard -> chacun marchait de travers, de son angle de
                // spawn. C'est Pedestrian.Start qui varie l'orientation, apres cette capture.
                float yaw = alongLane
                    ? Quaternion.LookRotation(Vector3.Cross(nr, Vector3.up)).eulerAngles.y
                    : 0f;

                // Hauteur du trottoir, pas un raycast : la polyligne est echantillonnee sur l'AXE
                // de la route et SidewalkHeight est par definition son elevation au-dessus (meme
                // convention que le mobilier de rue, sinon props et pietons ne s'accordent pas).
                float y = p.y + (alongLane ? CarLift : net.SidewalkHeight);
                go.transform.SetPositionAndRotation(new Vector3(p.x, y, p.z),
                                                    Quaternion.Euler(0f, yaw, 0f));

                // Une part du trafic roule sans conducteur, sinon la ville n'a de circulation
                // qu'une fois les pietons montes dans les caisses (et rien ne bouge au demarrage).
                if (alongLane && CityBrushPlacement.Rand(ph, 5) < AutoDriveShare)
                {
                    var ai = go.GetComponent<Gameplay.AICarController>();
                    if (ai != null)
                    {
                        var so = new SerializedObject(ai);
                        so.FindProperty("autoDrive").boolValue = true;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                }

                Undo.RegisterCreatedObjectUndo(go, "Peupler la ville");
                placed++;
            }

            return placed;
        }

        private static float Signed(uint hash, int salt) => CityBrushPlacement.Rand(hash, salt) * 2f - 1f;

        private static GameObject NewContainer(string name)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Peupler la ville");
            return go;
        }

        // Les prefabs de pietons cohabitent avec leurs modeles nus (Pedestrians/Models/Chara_*) :
        // le prefixe tranche, pas le dossier.
        private static List<GameObject> LoadPrefabs(string folder, string prefix)
        {
            var list = new List<GameObject>();
            if (!AssetDatabase.IsValidFolder(folder)) return list;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!System.IO.Path.GetFileNameWithoutExtension(path).StartsWith(prefix)) continue;
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go != null) list.Add(go);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));   // ordre stable -> tirage deterministe
            return list;
        }
    }
}
