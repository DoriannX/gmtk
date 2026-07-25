using System.Collections.Generic;
using System.Text.RegularExpressions;
using Gameplay.City;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Gameplay.EditorTools
{
    // IMPORT du lot batiments.fbx et fabrication de la palette du pinceau, en trois menus a
    // lancer dans l'ordre. Tout est imperatif et rejouable : relancer un menu ne casse rien, il
    // recalcule. Meme parti-pris que RoadNetworkEditor.EnsureReadable, qui bricole deja le
    // ModelImporter depuis du code plutot que de demander un reglage manuel.
    //
    // Ce que le FBX a dans le ventre (mesure en le parsant, pas devine) :
    //   batiments (racine, S=0.3561)
    //     +- Immeuble_02 / Immeuble_03 / Immeuble__01           (S=2.8081)
    //     +- Immeuble_neon_01..03                               (S=2.8081)
    //     +- batiment_02..06   -> enfants bat0X_01..05          (S=1)
    // Soit 11 batiments. Les bat02_01..05 sont les 5 MORCEAUX d'une tour, pas 5 tours, et les
    // noms qui ressemblent a des props (clim, antenne, escalier) sont des enfants repetes dans
    // plusieurs groupes. 0.3561 * 2.8081 = 1.0006 : residu de freeze Maya.
    public static class BatimentsImporter
    {
        const string FbxPath = "Assets/Models/City/batiments.fbx";
        const string PrefabDir = "Assets/Prefabs/City";
        const string PropDir = "Assets/Prefabs/City/Props";
        const string MatDir = "Assets/Materials/City";
        const string PalettePath = PrefabDir + "/CityPalette.asset";

        // Regex de garde : tout groupe trouve qui ne matche pas est signale, jamais supprime.
        static readonly Regex GroupName = new Regex(@"^(Immeuble|batiment)", RegexOptions.IgnoreCase);

        // Pieces sorties EN PLUS dans Assets/Prefabs/City/Props, pour le pinceau Props manuel.
        //
        // A ne pas confondre avec l'habillage des batiments : les prefabs de batiments gardent
        // deja tous leurs enfants, donc Immeuble_01/02/03 embarquent leurs 7 props et
        // Immeuble_neon_01/02/03 les leurs, aux positions de l'artiste. Ces copies isolees ne
        // servent qu'a en reposer a la main ailleurs.
        //
        // clim / antenne / loupiote_toit / base_immeuble ne sont pas extraits : ce sont des
        // GRAPPES ou des bandeaux tailles pour un batiment precis, a decouper en amont
        // (Maya/Blender) avant de devenir brushables.
        private struct PropSpec
        {
            public string node;
            public PropAlign align;
        }

        static readonly PropSpec[] PropNodes =
        {
            new PropSpec { node = "Engine_toit",  align = PropAlign.Normale },
            new PropSpec { node = "Paneau_neon",  align = PropAlign.Mur },
            new PropSpec { node = "panneau_neon", align = PropAlign.Mur },
            new PropSpec { node = "Porte_garage", align = PropAlign.Mur },
            new PropSpec { node = "distibuteur",  align = PropAlign.Sol },
            new PropSpec { node = "planche_bois", align = PropAlign.Sol },
        };

        private static bool FindPropSpec(string name, out PropSpec spec)
        {
            foreach (var p in PropNodes)
                if (string.Equals(p.node, name, System.StringComparison.OrdinalIgnoreCase))
                { spec = p; return true; }
            spec = default;
            return false;
        }

        // ---------------------------------------------------------------- 1. import

        [MenuItem("Tools/Ville/1 - Regler l'import de batiments.fbx", false, 100)]
        private static void ConfigureImport()
        {
            if (AssetImporter.GetAtPath(FbxPath) is not ModelImporter imp)
            {
                Debug.LogError($"[Ville] {FbxPath} introuvable. Copie le FBX dans Assets/Models/City/ d'abord.");
                return;
            }

            // 1 unite FBX = 1 METRE, malgre un UnitScaleFactor qui annonce des centimetres.
            // Mesures d'ancrage dans le fichier : Porte_garage 3.58 x 1.92, distibuteur 1.48 de
            // haut, escalier 12.2 de haut. Laisser useFileScale a true (convention Roads) ferait
            // appliquer la conversion cm->m et tout arriverait 100x trop petit.
            imp.useFileScale = false;
            imp.globalScale = 1f;

            // Racine explicite : sans ca la profondeur des groupes depend de la fusion de racine
            // faite par Unity, et le parcours d'extraction devient dependant de la version.
            imp.preserveHierarchy = true;

            // GrindRailBaker ignore les meshes non lisibles. C'est un jeu de skate : sans ca,
            // Tools/Grind/Bake Rails saute silencieusement tous les rebords de toit peints.
            imp.isReadable = true;

            imp.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            imp.materialLocation = ModelImporterMaterialLocation.InPrefab;
            imp.materialName = ModelImporterMaterialName.BasedOnMaterialName;
            imp.materialSearch = ModelImporterMaterialSearch.RecursiveUp;

            imp.animationType = ModelImporterAnimationType.None;   // vire le "Take 001" vide
            imp.importAnimation = false;
            imp.importCameras = false;
            imp.importLights = false;
            imp.importBlendShapes = false;
            imp.importVisibility = false;
            imp.importConstraints = false;

            imp.generateSecondaryUV = false;   // convention projet : pas de GI bakee
            imp.bakeAxisConversion = false;    // deja Y-up
            imp.addCollider = false;

            imp.SaveAndReimport();

            var go = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            if (go == null) { Debug.LogError("[Ville] Reimport rate."); return; }

            var inst = Object.Instantiate(go);
            Bounds b = Fit(inst);
            Object.DestroyImmediate(inst);
            Debug.Log($"[Ville] Import regle. Emprise totale {b.size.x:F1} x {b.size.y:F1} x {b.size.z:F1} m. " +
                      "Attendu ~153 x ~33 x ~50. Si tu lis des centiemes, useFileScale est reste a true.");
        }

        // ---------------------------------------------------------------- 2. materiaux

        // Nom Maya -> (nom de projet, couleur, emissif, transparent). Table FIGEE : les couleurs
        // ont ete lues dans le FBX, ca ne vaut pas la peine de les redetecter a chaque lancement.
        private struct MatSpec
        {
            public string source, target;
            public Color color;
            public bool emissive, transparent;
        }

        static readonly MatSpec[] Materials =
        {
            new MatSpec { source = "standardSurface1", target = "Bat_Beton_A",     color = new Color(0.78f, 0.74f, 0.68f) },
            new MatSpec { source = "standardSurface3", target = "Bat_Beton_B",     color = new Color(0.68f, 0.71f, 0.78f) },
            new MatSpec { source = "lambert7",         target = "Bat_Neon_Rouge",  color = new Color(0.857f, 0.033f, 0.033f), emissive = true },
            new MatSpec { source = "lambert9",         target = "Bat_Neon_Ambre",  color = new Color(0.900f, 0.590f, 0.092f), emissive = true },
            new MatSpec { source = "lambert11",        target = "Bat_Neon_Cyan",   color = new Color(0.050f, 0.890f, 1.000f), emissive = true },
            new MatSpec { source = "lambert8",         target = "Bat_Neon_Vert",   color = new Color(0.218f, 1.000f, 0.218f) },
            new MatSpec { source = "lambert10",        target = "Bat_Neon_Magenta",color = new Color(0.780f, 0.200f, 0.760f) },
            new MatSpec { source = "lambert2",         target = "Bat_Verre",       color = new Color(0.191f, 0.045f, 0.185f), transparent = true },
        };

        [MenuItem("Tools/Ville/2 - Creer et remapper les materiaux", false, 101)]
        private static void RemapMaterials()
        {
            if (AssetImporter.GetAtPath(FbxPath) is not ModelImporter imp)
            {
                Debug.LogError($"[Ville] {FbxPath} introuvable.");
                return;
            }
            EnsureDir(MatDir);

            Shader toon = Shader.Find("GMTK/ToonLit");
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null) lit = Shader.Find("Standard");

            foreach (var spec in Materials)
            {
                string path = $"{MatDir}/{spec.target}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);

                // GMTK/ToonLit n'a AUCUNE propriete d'emission : les neons qui doivent briller
                // passent obligatoirement par URP/Lit, le reste garde le rendu toon du jeu.
                Shader want = spec.emissive || spec.transparent ? lit : (toon != null ? toon : lit);
                if (want == null) { Debug.LogError("[Ville] Aucun shader utilisable."); return; }

                if (mat == null)
                {
                    mat = new Material(want);
                    AssetDatabase.CreateAsset(mat, path);
                }
                else if (mat.shader != want) mat.shader = want;

                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", spec.color);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", spec.color);
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", spec.transparent ? 0.9f : 0.15f);

                if (spec.emissive)
                {
                    mat.EnableKeyword("_EMISSION");
                    mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                    if (mat.HasProperty("_EmissionColor"))
                        mat.SetColor("_EmissionColor", spec.color * 2.5f);
                }

                if (spec.transparent) MakeTransparent(mat, 0.113f);

                EditorUtility.SetDirty(mat);
                imp.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), spec.source), mat);
            }

            AssetDatabase.SaveAssets();
            imp.SaveAndReimport();   // le remap persiste dans .meta/externalObjects
            Debug.Log($"[Ville] {Materials.Length} materiaux crees dans {MatDir} et remappes sur le FBX.");
        }

        private static void MakeTransparent(Material mat, float alpha)
        {
            Color c = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.white;
            c.a = alpha;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);

            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);   // URP : Transparent
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);       // Alpha
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetShaderPassEnabled("ShadowCaster", false);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        // ---------------------------------------------------------------- 3. extraction

        [MenuItem("Tools/Ville/3 - Extraire les prefabs et construire la palette", false, 102)]
        private static void ExtractPrefabs()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            if (model == null) { Debug.LogError($"[Ville] {FbxPath} introuvable. Lance le menu 1 d'abord."); return; }

            EnsureDir(PrefabDir);
            EnsureDir(PropDir);

            // Instantiate NU et pas PrefabUtility.InstantiatePrefab : une instance de model
            // prefab est verrouillee structurellement (Cannot restructure Prefab instance des
            // qu'on reparente). Un Instantiate ordinaire donne une hierarchie editable dont les
            // sharedMesh/sharedMaterials pointent toujours sur les sous-assets du FBX -- soit
            // exactement la topologie de references voulue.
            var src = Object.Instantiate(model);
            src.name = "BatimentsSource";

            // Scene de previsualisation : la scene ouverte de l'artiste n'est jamais salie.
            Scene tmp = EditorSceneManager.NewPreviewScene();
            SceneManager.MoveGameObjectToScene(src, tmp);

            var buildings = new List<(string name, string path, Vector3 footprint, float yaw, string note)>();
            var props = new List<(string name, string path, Vector3 footprint, PropAlign align)>();

            try
            {
                List<Transform> groups = FindGroups(src.transform);
                if (groups.Count != 11)
                    Debug.LogWarning($"[Ville] {groups.Count} groupes trouves, 11 attendus. " +
                                     "Verifie le parcours de hierarchie ou les reglages d'import.");

                // Les props sont clones AVANT que les groupes soient depeces.
                ExtractProps(groups, props);

                foreach (Transform g in groups)
                {
                    if (!GroupName.IsMatch(g.name))
                        Debug.LogWarning($"[Ville] Groupe au nom inattendu : {g.name}. Extrait quand meme.");

                    string original = g.name;
                    float yaw = GuessFacadeYaw(g, out string note);

                    g.SetParent(null, true);   // conserve le transform monde -> 0.3561 * 2.8081 => ~1
                    g.localRotation = Quaternion.identity;
                    Vector3 s = g.localScale;
                    if (Mathf.Abs(s.x - 1f) < 0.01f && Mathf.Abs(s.y - 1f) < 0.01f && Mathf.Abs(s.z - 1f) < 0.01f)
                        g.localScale = Vector3.one;   // tue le 1.0006 de Maya

                    Vector3 size = NormalizePivot(g);
                    AddColliders(g);

                    string clean = Clean(original);
                    g.name = clean;
                    string path = $"{PrefabDir}/{clean}.prefab";
                    PrefabUtility.SaveAsPrefabAsset(g.gameObject, path, out bool ok);
                    if (!ok) { Debug.LogError($"[Ville] Echec de sauvegarde pour {clean}."); continue; }

                    if (clean != original) note = $"FBX: {original}. {note}";
                    buildings.Add((clean, path, size, yaw, note));
                }
            }
            finally
            {
                Object.DestroyImmediate(src);
                EditorSceneManager.ClosePreviewScene(tmp);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            BuildPalette(buildings, props);

            Debug.Log($"[Ville] {buildings.Count} batiments dans {PrefabDir}, {props.Count} props dans {PropDir}. " +
                      "Palette : " + PalettePath);
        }

        // Groupes = transforms SANS mesh propre mais avec au moins un enfant DIRECT qui en a un.
        // Le test est volontairement independant de la profondeur : elle change selon
        // preserveHierarchy et la fusion de racine faite par Unity.
        private static List<Transform> FindGroups(Transform root)
        {
            var found = new List<Transform>();
            var stack = new Stack<Transform>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                Transform t = stack.Pop();
                bool ownMesh = t.GetComponent<MeshFilter>() != null;
                bool directMeshChild = false;
                foreach (Transform c in t)
                {
                    if (c.GetComponent<MeshFilter>() != null) directMeshChild = true;
                    stack.Push(c);
                }
                if (!ownMesh && directMeshChild) found.Add(t);
            }
            return found;
        }

        private static void ExtractProps(List<Transform> groups,
                                         List<(string, string, Vector3, PropAlign)> outProps)
        {
            // OrdinalIgnoreCase : Paneau_neon et panneau_neon ne different que par la casse et
            // l'orthographe, et NTFS ne les distingue pas. GenerateUniqueAssetPath n'est pas
            // fiable sur la casse -> on garde nous-memes la trace de ce qui est deja sorti.
            var done = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            foreach (Transform g in groups)
            {
                foreach (Transform c in g)
                {
                    if (!FindPropSpec(c.name, out PropSpec spec)) continue;

                    string clean = Clean($"Prop_{c.name}");
                    if (!done.Add(clean)) continue;

                    // Wrapper systematique : la piece est un mesh unique, et NormalizePivot a
                    // besoin d'un enfant a decaler (on ne peut pas bouger le pivot d'un mesh).
                    var wrapper = new GameObject(clean);
                    SceneManager.MoveGameObjectToScene(wrapper, g.gameObject.scene);
                    var copy = Object.Instantiate(c.gameObject);
                    copy.name = c.name;
                    copy.transform.SetParent(wrapper.transform, true);
                    copy.transform.localRotation = Quaternion.identity;

                    Vector3 size = NormalizePivot(wrapper.transform);
                    AddColliders(wrapper.transform);

                    string path = $"{PropDir}/{clean}.prefab";
                    PrefabUtility.SaveAsPrefabAsset(wrapper, path, out bool ok);
                    Object.DestroyImmediate(wrapper);
                    if (ok) outProps.Add((clean, path, size, spec.align));
                    else Debug.LogError($"[Ville] Echec de sauvegarde pour le prop {clean}.");
                }
            }
        }

        // Ramene le pivot au CENTRE XZ, base a y = 0, echelle de racine a 1, et rend la taille de
        // l'emprise.
        //
        // On ne peut pas deplacer le pivot d'un mesh : il faut decaler les ENFANTS. L'ORDRE est
        // la subtilite -- on met la racine a l'origine D'ABORD, sinon la remettre a zero apres
        // avoir decale les enfants les traine une seconde fois et le pivot finit n'importe ou.
        private static Vector3 NormalizePivot(Transform g)
        {
            g.position = Vector3.zero;

            var kids = new List<Transform>();
            foreach (Transform c in g) kids.Add(c);

            // L'echelle de racine heritee de Maya (0.3561 sur les batiment_0X) est POUSSEE dans
            // les enfants pour que le prefab sorte a 1. Sinon le pinceau, qui ecrit
            // localScale = Vector3.one * jitter, gonflerait ces batiments de 1 / 0.3561 = 2.8x.
            Vector3 s = g.localScale;
            if (Mathf.Abs(s.x - s.y) < 1e-4f && Mathf.Abs(s.x - s.z) < 1e-4f
                && Mathf.Abs(s.x - 1f) > 1e-4f && s.x > 1e-4f)
            {
                float k = s.x;
                foreach (Transform c in kids)
                {
                    c.localPosition *= k;
                    c.localScale *= k;
                }
                g.localScale = Vector3.one;
            }

            Bounds b = Fit(g.gameObject);
            var p = new Vector3(b.center.x, b.min.y, b.center.z);
            foreach (Transform c in kids) c.position -= p;
            return b.size;
        }

        private static void AddColliders(Transform g)
        {
            // Un MeshCollider par mesh, non convexe (legal sur du statique). Un BoxCollider
            // racine -- l'approche de CityBuilder -- serait FAUX ici : son sommet serait celui de
            // bat0X_05, une petite boite de toit, et les props peints flotteraient en l'air.
            foreach (var mf in g.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || mf.GetComponent<MeshCollider>() != null) continue;
                mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
            }
        }

        // Devine quelle face porte la FACADE : centroide de detail pondere par le nombre de
        // triangles. Le gros corps est au centre et contribue ~0 ; les details (porte de garage,
        // enseigne, escalier) sont d'un cote. Arrondi a 90 deg parce que tous les batiments du
        // lot sont des boites alignees sur les axes : ca transforme une estimation bruitee en la
        // bonne reponse et reduit la correction manuelle a un choix entre 4.
        private static float GuessFacadeYaw(Transform g, out string note)
        {
            Bounds whole = Fit(g.gameObject);
            Vector2 d = Vector2.zero;
            long total = 0;

            foreach (Transform c in g)
            {
                var mf = c.GetComponentInChildren<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                Mesh m = mf.sharedMesh;
                int tris = m.isReadable ? m.triangles.Length / 3 : m.vertexCount / 3;
                if (tris <= 0) continue;

                Bounds cb = Fit(c.gameObject);
                d += new Vector2(cb.center.x - whole.center.x, cb.center.z - whole.center.z) * tris;
                total += tris;
            }

            float half = 0.5f * new Vector2(whole.size.x, whole.size.z).magnitude;
            float confidence = 0f;
            float raw = 0f;
            if (total > 0 && half > 0.01f)
            {
                Vector2 avg = d / total;
                confidence = Mathf.Clamp01(avg.magnitude / half);
                raw = Mathf.Atan2(avg.x, avg.y) * Mathf.Rad2Deg;
            }

            float snapped = Mathf.Repeat(Mathf.Round(raw / 90f) * 90f, 360f);
            note = confidence < 0.08f
                ? $"facade auto {raw:F0} -> {snapped:F0} deg, confiance {confidence:F2} : A VERIFIER (batiment symetrique ?)"
                : $"facade auto {raw:F0} -> {snapped:F0} deg, confiance {confidence:F2}";
            return snapped;
        }

        // ---------------------------------------------------------------- palette

        private static void BuildPalette(List<(string name, string path, Vector3 footprint, float yaw, string note)> buildings,
                                         List<(string name, string path, Vector3 footprint, PropAlign align)> props)
        {
            var palette = AssetDatabase.LoadAssetAtPath<CityPalette>(PalettePath);
            if (palette == null)
            {
                palette = ScriptableObject.CreateInstance<CityPalette>();
                AssetDatabase.CreateAsset(palette, PalettePath);
            }

            foreach (var b in buildings)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(b.path);
                if (prefab == null) continue;
                var e = palette.Find(prefab);
                if (e == null)
                {
                    e = new CityPalette.Entry { prefab = prefab, facadeYaw = b.yaw };
                    palette.entries.Add(e);
                }
                // On NE reecrit PAS facadeYaw d'une entree existante : c'est peut-etre une
                // correction faite a la main, et une reextraction ne doit pas l'ecraser.
                e.layer = BrushLayer.Batiments;
                e.footprint = b.footprint;
                e.note = b.note;
            }

            foreach (var p in props)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(p.path);
                if (prefab == null) continue;
                var e = palette.Find(prefab);
                if (e == null)
                {
                    e = new CityPalette.Entry { prefab = prefab };
                    palette.entries.Add(e);
                }
                e.layer = BrushLayer.Props;
                e.footprint = p.footprint;
                e.align = p.align;
                e.note = $"collage : {p.align}";
            }

            EditorUtility.SetDirty(palette);
            AssetDatabase.SaveAssets();
        }

        // ---------------------------------------------------------------- utilitaires

        // Emprise MONDE des renderers. Copie de ZooBuilder.Fit (prive la-bas) ; celui de
        // CityBuilder exclut les enfants "Veg", inutile ici.
        public static Bounds Fit(GameObject g)
        {
            Renderer[] rs = g.GetComponentsInChildren<Renderer>();
            bool has = false;
            var b = new Bounds(g.transform.position, Vector3.zero);
            foreach (Renderer r in rs)
            {
                if (!has) { b = r.bounds; has = true; }
                else b.Encapsulate(r.bounds);
            }
            return b;
        }

        private static string Clean(string n)
        {
            n = Regex.Replace(n, "_{2,}", "_").Trim('_');
            return Regex.Replace(n, @"[^A-Za-z0-9_]", "_");
        }

        private static void EnsureDir(string dir)
        {
            if (AssetDatabase.IsValidFolder(dir)) return;
            int cut = dir.LastIndexOf('/');
            string parent = dir.Substring(0, cut);
            EnsureDir(parent);
            AssetDatabase.CreateFolder(parent, dir.Substring(cut + 1));
        }
    }
}
