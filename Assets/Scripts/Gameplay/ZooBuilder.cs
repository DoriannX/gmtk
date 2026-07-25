using System.Collections.Generic;
using UnityEngine;

namespace Gameplay
{
    // "Zoo" d'assets : outil d'AUTEUR (editeur) qui scanne des dossiers, instancie
    // TOUS les modeles/prefabs trouves et les pose en grille, groupes par sous-dossier,
    // avec un label flottant (nom + taille reelle) au-dessus de chacun. Sert de
    // catalogue visuel pour reperer les trous d'echelle / choisir un asset.
    //
    // Espacement : chaque item est mesure par ses bounds reels. normalizeScale (defaut)
    // met tout a la meme taille cible -> grille reguliere + tout visible malgre les
    // echelles FBX incoherentes. Le label affiche la taille reelle (avant normalisation)
    // pour reperer les bugs d'echelle.
    //
    // Regenerable : "Build Zoo" detruit l'ancien conteneur et refait tout. Le
    // ZooBuilderEditor (AssetPostprocessor) rappelle Build() quand un asset est
    // ajoute/supprime sous `folders` -> la scene se met a jour toute seule.
    [ExecuteAlways]
    public class ZooBuilder : MonoBehaviour
    {
        [Header("Dossiers scannes (recursif)")]
        [SerializeField] private string[] folders = { "Assets/Models/City" };

        [Header("Grille")]
        [Tooltip("0 = auto (racine carree du nombre d'items par groupe).")]
        [SerializeField] private int columns = 0;
        [Tooltip("Marge entre cellules (1 = jointif, 1.8 = 80% d'air pour laisser respirer les labels).")]
        [SerializeField, Range(1f, 3f)] private float padding = 1.8f;

        [Header("Echelle")]
        [Tooltip("TAILLE REELLE par defaut (le but d'un zoo). Coche pour tout ramener a `targetSize` (grille reguliere, utile si les echelles d'import sont incoherentes).")]
        [SerializeField] private bool normalizeScale = false;
        [Tooltip("Taille cible (plus grande dimension, unites) quand normalizeScale est actif. Sert aussi d'echelle de reference pour l'espacement/labels.")]
        [SerializeField] private float targetSize = 3f;

        [Header("Options")]
        [SerializeField] private bool groupByFolder = true;
        [SerializeField] private bool showLabels = true;

        private const string ContainerName = "ZooItems";

        [ContextMenu("Build Zoo")]
        public void Build()
        {
#if UNITY_EDITOR
            Transform old = transform.Find(ContainerName);
            if (old != null) DestroyImmediate(old.gameObject);
            Transform root = new GameObject(ContainerName).transform;
            root.SetParent(transform, false);

            List<Entry> entries = Scan();
            if (entries.Count == 0)
            {
                Debug.LogWarning("ZooBuilder: aucun asset trouve dans " + string.Join(", ", folders));
                return;
            }

            // Instancie + mesure tout d'abord (cell size depend du plus gros footprint
            // quand on ne normalise pas).
            var items = new List<Item>();
            float maxFoot = 0f;
            foreach (Entry e in entries)
            {
                GameObject src = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(e.path);
                if (src == null) continue;
                GameObject go = Instantiate(src);
                go.name = e.name;
                go.transform.SetParent(root, false);

                Bounds raw = Fit(go);                 // taille reelle (pour le label)
                if (normalizeScale)
                {
                    // Normalise par la PLUS GRANDE dimension (x,y,z) -> aucun asset ne
                    // depasse targetSize : grille nette, pas de tour geante (lampadaire,
                    // panneau) qui ecrase ses voisins.
                    float m = Mathf.Max(raw.size.x, Mathf.Max(raw.size.y, Mathf.Max(raw.size.z, 0.0001f)));
                    go.transform.localScale *= targetSize / m;
                }
                Bounds b = Fit(go);                   // apres normalisation
                float foot = Mathf.Max(b.size.x, b.size.z);
                maxFoot = Mathf.Max(maxFoot, foot);
                items.Add(new Item { go = go, bounds = b, rawSize = raw.size, folder = e.folder });
            }

            Layout(root, items);
            Debug.Log($"ZooBuilder: {items.Count} assets poses (max footprint {maxFoot:0.0}u).");
#else
            Debug.LogWarning("ZooBuilder: outil editeur uniquement.");
#endif
        }

#if UNITY_EDITOR
        private struct Entry { public string path, name, folder; }
        private class Item { public GameObject go; public Bounds bounds; public Vector3 rawSize; public string folder; }

        // Tous les GameObject (prefabs + modeles importes) sous `folders`, tries par
        // dossier puis nom -> groupes coherents.
        private List<Entry> Scan()
        {
            var valid = new List<string>();
            foreach (string f in folders)
                if (!string.IsNullOrEmpty(f) && UnityEditor.AssetDatabase.IsValidFolder(f.TrimEnd('/')))
                    valid.Add(f.TrimEnd('/'));
            var list = new List<Entry>();
            if (valid.Count == 0) return list;

            foreach (string guid in UnityEditor.AssetDatabase.FindAssets("t:GameObject", valid.ToArray()))
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".prefab" && ext != ".fbx" && ext != ".obj" && ext != ".blend") continue;
                list.Add(new Entry
                {
                    path = path,
                    name = System.IO.Path.GetFileNameWithoutExtension(path),
                    folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path))
                });
            }
            list.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.folder, b.folder);
                return c != 0 ? c : string.CompareOrdinal(a.name, b.name);
            });
            return list;
        }

        // Flow layout : chaque item occupe sa VRAIE emprise (largeur/profondeur reelles),
        // avance a l'horizontale, retour a la ligne quand la rangee depasse rowWidth.
        // Groupe par dossier (nouvelle section = retour ligne + header). Gere les
        // echelles heterogenes (une miette a cote d'un gros) sans trou geant.
        private void Layout(Transform root, List<Item> items)
        {
            float gap = targetSize * (padding - 1f) + 0.4f;  // espace entre items
            float rowWidth = 70f;                            // largeur avant retour ligne
            if (columns > 0)                                 // si colonnes forcees : largeur ~ cols * plus gros
            {
                float maxW = 0f; foreach (var it in items) maxW = Mathf.Max(maxW, it.bounds.size.x);
                rowWidth = columns * (maxW + gap);
            }
            float cursorX = 0f, cursorZ = 0f, rowDepth = 0f;
            string curFolder = null;

            foreach (Item it in items)
            {
                float sx = Mathf.Max(it.bounds.size.x, 0.05f);
                float sz = Mathf.Max(it.bounds.size.z, 0.05f);

                if (groupByFolder && it.folder != curFolder)
                {
                    if (curFolder != null) cursorZ += rowDepth + gap * 2f;   // fin de section
                    curFolder = it.folder;
                    cursorX = 0f; rowDepth = 0f;
                    if (showLabels) MakeLabel(root, curFolder, new Vector3(-gap * 1.5f, targetSize, -cursorZ), targetSize * 0.5f, true);
                }
                else if (cursorX + sx > rowWidth)            // retour a la ligne
                {
                    cursorZ += rowDepth + gap;
                    cursorX = 0f; rowDepth = 0f;
                }

                float cx = cursorX + sx * 0.5f;
                float cz = -(cursorZ + sz * 0.5f);
                // Centre le footprint reel de l'item sur (cx,cz), base posee a y=0.
                Vector3 p = new Vector3(
                    cx - it.bounds.center.x,
                    -(it.bounds.center.y - it.bounds.size.y * 0.5f),
                    cz - it.bounds.center.z);
                it.go.transform.localPosition += p;

                if (showLabels)
                {
                    float top = it.bounds.size.y;
                    string txt = $"{it.go.name}\n<{it.rawSize.x:0.#}x{it.rawSize.y:0.#}x{it.rawSize.z:0.#}>";
                    float lblSize = Mathf.Clamp(Mathf.Max(sx, sz), 0.8f, 8f) * 0.08f;
                    MakeLabel(root, txt, new Vector3(cx, top + Mathf.Max(0.3f, top * 0.08f), cz), lblSize, false);
                }

                cursorX += sx + gap;
                rowDepth = Mathf.Max(rowDepth, sz);
            }
        }

        private static void MakeLabel(Transform root, string text, Vector3 pos, float size, bool header)
        {
            var go = new GameObject("Label_" + text.Split('\n')[0]);
            go.transform.SetParent(root, false);
            go.transform.localPosition = pos;
            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.anchor = TextAnchor.LowerCenter;
            tm.alignment = TextAlignment.Center;
            tm.characterSize = size;
            tm.fontSize = header ? 64 : 48;
            tm.color = header ? new Color(1f, 0.85f, 0.3f) : Color.white;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            tm.font = font;
            go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            go.AddComponent<Billboard>();
        }
#endif

        // Emprise monde des renderers (meme logique que CityBuilder, sans exclusion Veg
        // car ici on veut la taille COMPLETE de l'asset).
        private static Bounds Fit(GameObject g)
        {
            Renderer[] rs = g.GetComponentsInChildren<Renderer>();
            bool has = false;
            Bounds b = new Bounds(g.transform.position, Vector3.zero);
            foreach (Renderer r in rs)
            {
                if (!has) { b = r.bounds; has = true; }
                else b.Encapsulate(r.bounds);
            }
            return b;
        }
    }
}
