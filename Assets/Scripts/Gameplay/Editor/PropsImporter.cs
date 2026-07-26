using System.Collections.Generic;
using Gameplay;
using Gameplay.City;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Gameplay.EditorTools
{
    // Sort les kits props.fbx / Killian_Asset_2.fbx en prefabs brushables + entrees de palette.
    // Frere de BatimentsImporter, dont il reutilise NormalizePivot / AddColliders / Fit.
    //
    // DIFFERENCE ESSENTIELLE avec lui : on n'extrait PAS depuis le FBX, mais depuis l'objet
    // PropsPreview de la scene ouverte. Les deux kits arrivent de Maya avec des referentiels de
    // racine differents (props a 0.3979 bake, Killian_Asset_2 a 1.0) et chaque prop a recu une
    // correction d'echelle a la main, calee sur la taille du pieton plutot que sur le metre reel
    // (cf. le commit d'import). Cette calibration ne vit QUE dans la scene. La relire ici, c'est
    // la seule facon de ne pas la retaper dans une table qui divergerait du jour ou l'artiste
    // reregle une taille dans la scene.
    //
    // Corollaire a savoir : les prefabs sortis figent la calibration du moment. Reregler une
    // taille dans PropsPreview demande de relancer ce menu pour que ca se propage.
    public static class PropsImporter
    {
        const string PreviewName = "PropsPreview";
        const string PropSolDir = "Assets/Prefabs/City/PropsSol";
        const string PalettePath = "Assets/Prefabs/City/CityPalette.asset";

        static readonly string[] FbxPaths =
        {
            "Assets/Models/Props/props.fbx",
            "Assets/Models/Props/Killian_Asset_2.fbx",
        };

        private struct Spec
        {
            public string node;        // nom dans PropsPreview
            public string prefab;      // nom du prefab sorti (sans le prefixe)
            public BrushLayer layer;
            public float spacing;      // PropsRue : distance entre deux exemplaires
            public float inset;        // PropsRue : recul depuis le bord exterieur du trottoir
            public bool grindable;     // PropsRue : forme une ligne de grind
            public float weight;       // PropsSol : poids du tirage au pinceau
        }

        private static Spec Rue(string node, float spacing, float inset, bool grind = false,
                                string prefab = null)
            => new Spec { node = node, prefab = prefab ?? node, layer = BrushLayer.PropsRue,
                          spacing = spacing, inset = inset, grindable = grind, weight = 1f };

        private static Spec Sol(string node, float weight)
            => new Spec { node = node, prefab = node, layer = BrushLayer.PropsSol,
                          spacing = 8f, inset = 1f, weight = weight };

        // Les 25 props DE SOL des deux kits. Les espacements viennent des largeurs mesurees :
        // proche de la largeur = pose bout a bout (l'entree de palette le deduit toute seule),
        // grand = mobilier ponctuel.
        //
        // Les 10 autres props des kits ne sont PAS extraits, et c'est deliberé : antenne est du
        // mobilier de TOIT, Cable_01/02/03 sont aeriens (ils se tendent entre deux facades, ca ne
        // se seme pas), Panneau_Neon_* et Ecran_pub sont plaques contre un mur -- moins de 20 cm
        // d'epaisseur pour 5 m de haut. Tout ce lot releve de la couche Props (surfaces deja
        // peintes), qui a deja son habillage.
        // Les RECULS rangent le trottoir en profondeur, et c'est ce qui l'empeche de se lire comme
        // une rangee au cordeau. Repere de la tuile : demi-largeur 6.5 m, chaussee jusqu'a 2.25,
        // bordure, trottoir de 2.75 a 6.5 -- soit 3.75 m de large. Le recul se compte depuis le
        // bord EXTERIEUR, donc 3.4 = contre le caniveau, 0.6 = contre les batiments.
        // Repartition : ce qui protege la chaussee au bord, ce qui s'habite au fond.
        static readonly Spec[] Specs =
        {
            // --- alignes sur le trottoir ---   spacing, recul
            Rue("poteau_01", 3f, 3.40f),
            Rue("poteau_02", 3f, 3.40f),
            Rue("poteau_03", 3f, 3.40f),
            Rue("Barriere_01", 1.30f, 3.30f, grind: true),
            Rue("Barriere_02", 1.33f, 3.30f, grind: true),
            Rue("rambarde", 1.67f, 3.25f, grind: true),
            Rue("Rambarde_beton", 1.00f, 3.25f, grind: true),
            Rue("panneau_signalisation", 30f, 3.10f),
            Rue("lampadaire", 22f, 3.00f),
            Rue("Asset", 26f, 3.00f, prefab: "lampadaire_solo"),   // 2e reverbere, 3.90 m contre 5.82
            Rue("Socle_plante", 12f, 2.20f),
            Rue("Banc_01", 24f, 1.20f, grind: true),
            Rue("Banc_02", 28f, 1.20f, grind: true),
            Rue("panneau_affichage", 45f, 0.90f),
            Rue("abriBus", 90f, 0.80f),

            // --- disperses au sol, au pinceau ---
            Sol("arbre_01", 1.0f),
            Sol("buisson_01", 1.5f),
            Sol("buisson_fleurs", 1.0f),
            Sol("baril", 1.5f),
            Sol("barils_abime", 1.5f),
            Sol("poubelle", 0.8f),
            Sol("Poubelle_petite", 1.2f),
            Sol("distributeur", 0.8f),
            Sol("Tuyau", 0.6f),
            Sol("Tuyau_courbe", 0.6f),
        };

        // ---------------------------------------------------------------- 1. import

        [MenuItem("Tools/Ville/Props/1 - Regler l'import des kits", false, 120)]
        private static void ConfigureImport()
        {
            int done = 0;
            foreach (string path in FbxPaths)
            {
                if (AssetImporter.GetAtPath(path) is not ModelImporter imp)
                {
                    Debug.LogError($"[Props] {path} introuvable.");
                    continue;
                }

                // On ne touche a RIEN d'autre. L'echelle des deux kits (useFileScale + globalScale
                // 100) est deja reglee et verifiee, et la hierarchie importee est celle sur
                // laquelle PropsPreview a ete monte : changer preserveHierarchy casserait les
                // surcharges de la scene, donc la calibration qu'on vient lire.
                //
                // isReadable en revanche est indispensable : GrindRailExtractor ignore
                // silencieusement les meshes non lisibles, et sans ca les rambardes ne
                // grinderaient jamais sans qu'aucun message ne l'explique.
                if (imp.isReadable) { done++; continue; }
                imp.isReadable = true;
                imp.SaveAndReimport();
                done++;
            }
            Debug.Log($"[Props] {done}/{FbxPaths.Length} kit(s) regles (Read/Write active pour le grind).");
        }

        // ---------------------------------------------------------------- 2. extraction

        [MenuItem("Tools/Ville/Props/2 - Extraire les prefabs de sol", false, 121)]
        private static void Extract()
        {
            Transform preview = FindPreview();
            if (preview == null)
            {
                Debug.LogError($"[Props] Aucun objet '{PreviewName}' dans la scene ouverte. " +
                               "C'est lui qui porte la calibration des tailles : ouvre la scene " +
                               "Game avant de lancer ce menu.");
                return;
            }

            BatimentsImporter.EnsureDir(PropSolDir);

            // Index nom -> transform, sur TOUTE la descendance du preview : les props sont
            // ranges par kit, et les deux kits n'ont pas la meme profondeur de racine.
            var found = new Dictionary<string, Transform>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var t in preview.GetComponentsInChildren<Transform>(true))
                if (!found.ContainsKey(t.name)) found[t.name] = t;

            Scene tmp = EditorSceneManager.NewPreviewScene();
            var made = new List<(Spec spec, string path, Vector3 footprint)>();
            var missing = new List<string>();

            try
            {
                foreach (var spec in Specs)
                {
                    if (!found.TryGetValue(spec.node, out Transform src)) { missing.Add(spec.node); continue; }

                    string clean = BatimentsImporter.Clean($"Prop_{spec.prefab}");
                    var wrapper = new GameObject(clean);
                    SceneManager.MoveGameObjectToScene(wrapper, tmp);

                    var copy = Object.Instantiate(src.gameObject);
                    copy.name = src.name;
                    SceneManager.MoveGameObjectToScene(copy, tmp);

                    // Instantiate recopie l'echelle LOCALE, or la calibration est le produit de
                    // l'echelle du prop ET de celle de la racine du kit (0.3979 pour props, 1.0
                    // pour Killian). Sans ce report explicite, tout le kit props sortirait 2.5x
                    // trop grand -- exactement le bug que le commit d'import a mis a plat.
                    copy.transform.SetParent(wrapper.transform, false);
                    copy.transform.localPosition = Vector3.zero;
                    copy.transform.localRotation = Quaternion.identity;
                    copy.transform.localScale = src.lossyScale;

                    Vector3 size = BatimentsImporter.NormalizePivot(wrapper.transform);
                    BatimentsImporter.AddColliders(wrapper.transform);

                    // Le prefab transporte ses propres rails, figes une fois ici. Le wrapper est
                    // a l'identite a ce stade, donc les points locaux sortent directement bons.
                    // C'est ce qui permet d'en poser des centaines : au demarrage chacun ne fait
                    // que transformer une poignee de points, la ou une extraction depuis les
                    // meshes coutait 1134 ms pour 201 rambardes.
                    if (spec.grindable) BakeRails(wrapper, size, clean);

                    string path = $"{PropSolDir}/{clean}.prefab";
                    PrefabUtility.SaveAsPrefabAsset(wrapper, path, out bool ok);
                    Object.DestroyImmediate(wrapper);

                    if (ok) made.Add((spec, path, size));
                    else Debug.LogError($"[Props] Echec de sauvegarde pour {clean}.");
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(tmp);
            }

            BuildPalette(made);
            AssetDatabase.SaveAssets();

            Debug.Log($"[Props] {made.Count} prefabs dans {PropSolDir} et ajoutes a la palette. " +
                      "Les tailles sont celles de PropsPreview : si tu en reregles une, relance ce menu.");
            if (missing.Count > 0)
                Debug.LogWarning($"[Props] Introuvables dans {PreviewName} : {string.Join(", ", missing)}.");
        }

        // Pose UNE barre droite et de niveau au sommet du prop, le long de son grand axe.
        //
        // On n'extrait PAS les aretes du mesh ici, alors que GrindRailSource sait le faire, et
        // les deux raisons sont mesurees sur ce lot precis :
        //
        //   - rambarde et bancs ont un barreau TUBULAIRE. Une section ronde n'a aucune arete
        //     vive (facettes a ~15 deg contre un seuil diedre a 22), l'extraction ne rend rien.
        //   - Barriere_01/02 en rendent, et c'est pire : leurs barreaux sont modelises DE
        //     TRAVERS (le haut descend de 3 cm sur 1.27 m, le bas de 16). C'est du cabossage
        //     voulu sur un objet isole, mais pose tous les 1.30 m ca fait une tole ondulee. Le
        //     recollage refusait alors 2662 raccords sur 2668 -- le trou entre deux elements
        //     etait a 81 deg de la direction du rail, donc lu comme deux rails paralleles et
        //     non comme une suite. La rue ressortait en morceaux de 2.6 m.
        //
        // Une barre de niveau donne au contraire une ligne continue sur toute la rue (mesure :
        // 40 m d'un coup), et c'est de toute facon la hauteur a laquelle la moto se pose --
        // le sommet de l'emprise est le point haut reel de l'objet.
        //
        // Limite assumee : si le point haut d'un prop est un ornement isole et pas une barre, le
        // rail flotte dessus. Ca se voit au gizmo du GrindRailSource et se corrige sur le prefab.
        // ponytail: barre deduite de l'emprise, suffisant pour du mobilier de rue
        private static void BakeRails(GameObject wrapper, Vector3 size, string clean)
        {
            // Pivot centre en XZ et base a y = 0 (NormalizePivot) -> le sommet est a size.y et
            // l'axe passe par 0. 95 % de la longueur : on s'arrete avant les montants de bout.
            bool alongX = size.x >= size.z;
            float half = (alongX ? size.x : size.z) * 0.5f * 0.95f;
            if (half < 0.1f)
            {
                Debug.LogWarning($"[Props] {clean} est marque grindable mais est trop petit " +
                                 "pour porter un rail. Decoche-le dans la palette.");
                return;
            }

            Vector3 a = alongX ? new Vector3(-half, size.y, 0f) : new Vector3(0f, size.y, -half);
            Vector3 b = alongX ? new Vector3(half, size.y, 0f) : new Vector3(0f, size.y, half);
            wrapper.AddComponent<GrindRailSource>().SetBaked(new[] { a, b });
        }

        private static Transform FindPreview()
        {
            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name == PreviewName) return root.transform;
                Transform t = root.transform.Find(PreviewName);
                if (t != null) return t;
            }
            return null;
        }

        private static void BuildPalette(List<(Spec spec, string path, Vector3 footprint)> made)
        {
            var palette = AssetDatabase.LoadAssetAtPath<CityPalette>(PalettePath);
            if (palette == null) { Debug.LogError($"[Props] Palette introuvable : {PalettePath}"); return; }

            Undo.RecordObject(palette, "Importer les props de sol");
            foreach (var (spec, path, footprint) in made)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                var e = palette.Find(prefab);
                if (e == null)
                {
                    palette.entries.Add(e = new CityPalette.Entry { prefab = prefab });
                    // facadeYaw 0 = le +Z du prefab regarde la rue. C'est le bon defaut ici :
                    // tous ces props sont plus larges que profonds (barriere 1.28 x 0.49,
                    // rambarde 1.67 x 0.10), donc a yaw 0 leur grand axe longe deja le trottoir.
                    // Les cas a verifier a la boussole sont ceux qui ont un DEVANT : abriBus doit
                    // s'ouvrir vers la chaussee, un banc doit y faire face.
                    e.facadeYaw = 0f;
                    e.weight = spec.weight;
                }
                // Comme BatimentsImporter : on ne reecrit ni facadeYaw ni weight d'une entree
                // existante, ce sont des reglages a la main qu'une reextraction ne doit pas
                // ecraser. La mesure, elle, se refait.
                e.layer = spec.layer;
                e.footprint = footprint;
                e.spacing = spec.spacing;
                e.inset = spec.inset;
                e.grindable = spec.grindable;
                e.align = PropAlign.Sol;      // mobilier de rue : debout, on ignore la pente
                e.note = spec.layer == BrushLayer.PropsRue
                    ? $"rue, tous les {spec.spacing:F2} m, recul {spec.inset:F2} m" +
                      (spec.grindable ? ", grindable" : "")
                    : "sol, au pinceau";
            }
            EditorUtility.SetDirty(palette);
        }
    }
}
