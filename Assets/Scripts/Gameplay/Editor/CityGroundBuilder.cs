using System.Collections.Generic;
using Gameplay.City;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Gameplay.EditorTools
{
    // SOL DE LA VILLE, cale sur le TROTTOIR et troue sous les routes.
    //
    // Le point de tout le fichier : un simple plan ne marche pas. Les tuiles de route portent
    // leur propre trottoir (SM_Tile_droit : chaussee a 0.024, bordure a 0.296, trottoir a 0.176
    // sur 3.75 m de chaque bord) et c'est ce denivele qui fait lire la route comme un CREUX.
    // Un plan pose au niveau de la chaussee met tout a plat ; un plan pose au niveau du trottoir
    // enterre la chaussee. Il faut donc un sol AU NIVEAU DU TROTTOIR, avec un trou au-dessus de
    // la route.
    //
    // Le trou n'est PAS decoupe au bord exact de l'emprise : le sol se glisse SOUS le trottoir
    // de la tuile sur `Overlap` metres. C'est ce recouvrement qui rend la grille possible --
    // la bande de trottoir (3.75 m) avale la marche d'escalier de la decoupe par cellules, donc
    // pas besoin de decoupe polygonale exacte. Sans lui il faudrait trianguler un polygone a
    // trous, ce qui est un projet en soi.
    //
    // Un shader a `clip()` sur un masque de route percerait le trou visuel, mais pas celui du
    // COLLIDER : la voiture roulerait sur un sol plat invisible au-dessus de la chaussee.
    public static class CityGroundBuilder
    {
        const string ObjectName = "Sol";
        // UN SEUL materiau pour la chaussee, le trottoir ET le sol -- d'ou le nom "rue" et pas
        // "sol". Deux materiaux, meme regles a l'identique, redonnent une jointure visible au
        // premier reglage oublie ; RoadNetwork expose justement un champ `roadMaterial`
        // prioritaire sur son materiau runtime, donc on lui impose le notre.
        const string MatPath = "Assets/Materials/City/City_Rue.mat";
        const string MeshDir = "Assets/Meshes/City";

        // Pas de la grille fine. Doit rester petit devant la bande de trottoir : l'erreur de
        // quantification vaut au pire Cell * sqrt(2) (~2.8 m a 2 m), et il faut qu'elle tienne
        // entre le bord exterieur de la bordure (2.75 m de l'axe) et le bord de la tuile (6.5).
        const float Cell = 2f;
        // Recouvrement du sol sous le trottoir de la tuile. 3.3 -> la decoupe tombe a 3.2 m de
        // l'axe au plus pres, soit franchement au-dela de la bordure, et a 6.0 m au plus loin,
        // soit encore sous la tuile. Les deux bornes sont dans la bande de trottoir : aucun
        // trou beant, aucun debord sur la chaussee.
        const float Overlap = 3.3f;
        // Au-dela de cette hauteur au-dessus du sol, une route est un OUVRAGE : elle passe par
        // -dessus et ne doit plus percer le sol sous elle, sinon un pont leve a +6 m laisse un
        // trou beant a ses pieds. Le seuil est franchement au-dessus du denivele interne d'une
        // tuile (chaussee a 0.024, trottoir a 0.296) et franchement en dessous d'une hauteur
        // franchissable a pied : rien d'ambigu ne tombe entre les deux.
        const float OverheadIgnore = 1.5f;
        // La grille fine ne couvre que le voisinage des routes ; au-dela le sol est plat et
        // 8 grands quads suffisent. Evite 300 000 cellules pour du vide.
        const float NearMargin = 40f;
        // Le bord exterieur doit finir hors de vue : le brouillard ne le cache pas (sa couleur
        // est plus claire que le ciel juste au-dessus de l'horizon, l'arete se lit en vue
        // aerienne). Un quad de plus ne coute rien.
        const float FarMargin = 350f;
        const float MinSize = 400f;

        [MenuItem("Tools/Ville/Generer le sol")]
        public static void Build()
        {
            var nets = Object.FindObjectsByType<RoadNetwork>(FindObjectsSortMode.None);

            Bounds all, roads;
            bool hasRoads = RoadBounds(nets, out roads);
            if (!CityBounds(nets, out all))
            {
                Debug.LogWarning("[Ville] Aucun RoadNetwork ni batiment peint dans la scene : " +
                                 "rien a couvrir.");
                return;
            }
            if (!hasRoads) roads = all;

            // Hauteur du trottoir : mesuree sur la tuile, pas ecrite en dur (les assets bougent).
            float groundY = 0f;
            foreach (var net in nets) groundY = Mathf.Max(groundY, net.transform.position.y + net.SidewalkHeight);

            var mesh = BuildMesh(nets, all, roads, groundY);

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Generer le sol");

            GameObject old = Find();
            if (old != null) Undo.DestroyObjectImmediate(old);

            var go = new GameObject(ObjectName, typeof(MeshFilter), typeof(MeshRenderer),
                                    typeof(MeshCollider));
            Undo.RegisterCreatedObjectUndo(go, "Generer le sol");
            go.transform.position = Vector3.zero;
            Material shared = EnsureMaterial(nets);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshCollider>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = shared;
            ShareWithRoads(nets, shared);

            // Pas de ContributeGI : les FBX de ville sont importes sans UV2, la GI bakee
            // cracherait un avertissement par objet (meme raison que pour le pinceau).
            GameObjectUtility.SetStaticEditorFlags(go,
                StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;

            Debug.Log($"[Ville] Sol : trottoir a y = {groundY:F3}, {mesh.vertexCount} sommets, " +
                      $"{mesh.triangles.Length / 3} triangles. Couleur reglable sur {MatPath}.");
        }

        // ---------------------------------------------------------------- geometrie

        static Mesh BuildMesh(RoadNetwork[] nets, Bounds all, Bounds roads, float y)
        {
            // Emprise totale, et emprise de la grille fine (alignee sur le pas de grille pour
            // que l'anneau exterieur se raccorde pile sur le bord de la grille).
            float fx0 = Mathf.Min(all.min.x, roads.min.x) - FarMargin;
            float fx1 = Mathf.Max(all.max.x, roads.max.x) + FarMargin;
            float fz0 = Mathf.Min(all.min.z, roads.min.z) - FarMargin;
            float fz1 = Mathf.Max(all.max.z, roads.max.z) + FarMargin;
            Grow(ref fx0, ref fx1, MinSize);
            Grow(ref fz0, ref fz1, MinSize);

            float nx0 = Mathf.Floor((roads.min.x - NearMargin) / Cell) * Cell;
            float nx1 = Mathf.Ceil((roads.max.x + NearMargin) / Cell) * Cell;
            float nz0 = Mathf.Floor((roads.min.z - NearMargin) / Cell) * Cell;
            float nz1 = Mathf.Ceil((roads.max.z + NearMargin) / Cell) * Cell;
            nx0 = Mathf.Max(nx0, fx0); nx1 = Mathf.Min(nx1, fx1);
            nz0 = Mathf.Max(nz0, fz0); nz1 = Mathf.Min(nz1, fz1);

            int cx = Mathf.Max(1, Mathf.RoundToInt((nx1 - nx0) / Cell));
            int cz = Mathf.Max(1, Mathf.RoundToInt((nz1 - nz0) / Cell));

            var verts = new List<Vector3>();
            var tris = new List<int>();

            // --- grille fine, une cellule gardee seulement si ses 4 coins sont hors route ---
            // Table des sommets : -1 tant que le sommet n'a servi a aucune cellule gardee, pour
            // ne pas emettre des milliers de sommets orphelins au milieu de la chaussee.
            var index = new int[(cx + 1) * (cz + 1)];
            for (int i = 0; i < index.Length; i++) index[i] = -1;

            var free = new bool[(cx + 1) * (cz + 1)];
            for (int j = 0; j <= cz; j++)
                for (int i = 0; i <= cx; i++)
                {
                    var p = new Vector3(nx0 + i * Cell, y, nz0 + j * Cell);
                    free[j * (cx + 1) + i] = Clearance(nets, p) >= -Overlap;
                }

            for (int j = 0; j < cz; j++)
                for (int i = 0; i < cx; i++)
                {
                    int a = j * (cx + 1) + i, b = a + 1;
                    int c = (j + 1) * (cx + 1) + i, d = c + 1;
                    if (!free[a] || !free[b] || !free[c] || !free[d]) continue;
                    Quad(verts, tris, index, cx, nx0, nz0, y, i, j);
                }

            // --- anneau exterieur : 8 grands quads autour de la grille fine ---
            Strip(verts, tris, y, fx0, fz0, fx1, nz0);   // bas
            Strip(verts, tris, y, fx0, nz1, fx1, fz1);   // haut
            Strip(verts, tris, y, fx0, nz0, nx0, nz1);   // gauche
            Strip(verts, tris, y, nx1, nz0, fx1, nz1);   // droite

            var mesh = new Mesh { name = "Sol_Ville" };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            var uv = new Vector2[verts.Count];
            var nrm = new Vector3[verts.Count];
            for (int i = 0; i < verts.Count; i++)
            {
                uv[i] = new Vector2(verts[i].x, verts[i].z) * 0.1f;
                nrm[i] = Vector3.up;
            }
            mesh.uv = uv;
            mesh.normals = nrm;
            mesh.RecalculateBounds();

            return SaveMesh(mesh);
        }

        static void Quad(List<Vector3> verts, List<int> tris, int[] index, int cx,
                         float x0, float z0, float y, int i, int j)
        {
            int a = Vertex(verts, index, cx, x0, z0, y, i, j);
            int b = Vertex(verts, index, cx, x0, z0, y, i + 1, j);
            int c = Vertex(verts, index, cx, x0, z0, y, i, j + 1);
            int d = Vertex(verts, index, cx, x0, z0, y, i + 1, j + 1);
            tris.Add(a); tris.Add(c); tris.Add(b);
            tris.Add(b); tris.Add(c); tris.Add(d);
        }

        static int Vertex(List<Vector3> verts, int[] index, int cx,
                          float x0, float z0, float y, int i, int j)
        {
            int key = j * (cx + 1) + i;
            if (index[key] < 0)
            {
                index[key] = verts.Count;
                verts.Add(new Vector3(x0 + i * Cell, y, z0 + j * Cell));
            }
            return index[key];
        }

        static void Strip(List<Vector3> verts, List<int> tris, float y,
                          float x0, float z0, float x1, float z1)
        {
            if (x1 - x0 < 0.01f || z1 - z0 < 0.01f) return;
            int b = verts.Count;
            verts.Add(new Vector3(x0, y, z0));
            verts.Add(new Vector3(x1, y, z0));
            verts.Add(new Vector3(x0, y, z1));
            verts.Add(new Vector3(x1, y, z1));
            tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
            tris.Add(b + 1); tris.Add(b + 2); tris.Add(b + 3);
        }

        // Garde minimale au bord de l'emprise routiere, tous reseaux confondus. Positive =
        // dehors, negative = sous la route. +inf quand aucune route n'est a portee.
        static float Clearance(RoadNetwork[] nets, Vector3 p)
        {
            float best = float.PositiveInfinity;
            foreach (var net in nets)
            {
                RoadNetwork.RoadProbe probe;
                if (!net.ProbeRoad(p, 60f, out probe, OverheadIgnore)) continue;
                if (probe.clearance < best) best = probe.clearance;
            }
            return best;
        }

        // ---------------------------------------------------------------- emprises

        // Emprise des seules ROUTES : c'est elle qui decide ou la grille fine est necessaire.
        static bool RoadBounds(RoadNetwork[] nets, out Bounds b)
        {
            b = default(Bounds);
            bool any = false;
            foreach (var net in nets)
            {
                float pad = net.RoadHalfWidth;
                for (int i = 0; i < net.NodeCount; i++)
                {
                    var n = new Bounds(net.NodeWorld(i), new Vector3(pad * 2f, 0f, pad * 2f));
                    if (any) b.Encapsulate(n); else { b = n; any = true; }
                }
            }
            return any;
        }

        // Emprise a couvrir : routes + batiments peints. Volontairement PAS "tous les renderers
        // de la scene" : road.unity contient un CharaTest_Preview parque a y = 200.
        static bool CityBounds(RoadNetwork[] nets, out Bounds b)
        {
            bool any = RoadBounds(nets, out b);
            foreach (var brush in Object.FindObjectsByType<CityBrush>(FindObjectsSortMode.None))
            {
                Transform c = brush.Container;
                if (c == null) continue;
                foreach (var r in c.GetComponentsInChildren<Renderer>())
                {
                    if (any) b.Encapsulate(r.bounds); else { b = r.bounds; any = true; }
                }
            }
            return any;
        }

        static void Grow(ref float lo, ref float hi, float min)
        {
            float missing = min - (hi - lo);
            if (missing <= 0f) return;
            lo -= missing * 0.5f;
            hi += missing * 0.5f;
        }

        // ---------------------------------------------------------------- assets

        // Le mesh part en ASSET et pas dans la scene : 15 000 sommets serialises en YAML dans
        // le .unity rendraient chaque diff illisible. Il est entierement regenerable.
        static Mesh SaveMesh(Mesh mesh)
        {
            EnsureFolder(MeshDir);
            string path = $"{MeshDir}/Sol_{EditorSceneManager.GetActiveScene().name}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                // On ECRASE le contenu du mesh existant au lieu de recreer l'asset : les
                // MeshFilter/MeshCollider d'autres objets qui le referencent restent valides.
                existing.Clear();
                existing.indexFormat = mesh.indexFormat;
                existing.vertices = mesh.vertices;
                existing.triangles = mesh.triangles;
                existing.uv = mesh.uv;
                existing.normals = mesh.normals;
                existing.RecalculateBounds();
                Object.DestroyImmediate(mesh);
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssets();
                return existing;
            }
            AssetDatabase.CreateAsset(mesh, path);
            AssetDatabase.SaveAssets();
            return mesh;
        }

        // Cree le materiau une seule fois, en RECOPIANT la recette de RoadNetwork.RoadMat() :
        // meme shader, meme couleur d'asphalte, meme smoothness. C'est la seule facon d'avoir
        // le sol et le trottoir identiques des la premiere generation, sans reglage a la main.
        // Une fois l'asset cree on n'y touche plus : les retouches de l'inspecteur survivent
        // a toutes les regenerations, et comme les routes partagent l'asset elles suivent.
        static Material EnsureMaterial(RoadNetwork[] nets)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (mat != null) return mat;

            // Reglages lus sur le reseau plutot que devines : les champs sont prives, d'ou le
            // SerializedObject. Repli sur les valeurs par defaut de RoadNetwork si pas de route.
            Color asphalt = new Color(0.24f, 0.24f, 0.26f);
            bool toon = true;
            if (nets.Length > 0)
            {
                var so = new SerializedObject(nets[0]);
                var c = so.FindProperty("asphaltColor");
                var t = so.FindProperty("toonShading");
                if (c != null) asphalt = c.colorValue;
                if (t != null) toon = t.boolValue;
            }

            Shader sh = toon ? Shader.Find("GMTK/ToonLit") : null;
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
            mat = new Material(sh) { name = "City_Rue" };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", asphalt);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.1f);

            EnsureFolder(System.IO.Path.GetDirectoryName(MatPath).Replace('\\', '/'));
            AssetDatabase.CreateAsset(mat, MatPath);
            AssetDatabase.SaveAssets();
            return mat;
        }

        // Impose le materiau partage aux routes. RoadMat() rend `roadMaterial` prioritaire sur
        // son materiau runtime, mais ne le relit qu'a la construction -> FullRebuild derriere.
        static void ShareWithRoads(RoadNetwork[] nets, Material shared)
        {
            foreach (var net in nets)
            {
                var so = new SerializedObject(net);
                var prop = so.FindProperty("roadMaterial");
                if (prop == null) continue;
                if (prop.objectReferenceValue == shared) continue;
                prop.objectReferenceValue = shared;
                so.ApplyModifiedProperties();
                net.FullRebuild();
                Debug.Log($"[Ville] '{net.name}' passe sur le materiau partage {shared.name} : " +
                          "plus de jointure entre le trottoir et le sol.");
            }
        }

        static void EnsureFolder(string dir)
        {
            if (AssetDatabase.IsValidFolder(dir)) return;
            string parent = System.IO.Path.GetDirectoryName(dir).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(dir));
        }

        static GameObject Find()
        {
            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
                if (root.name == ObjectName) return root;
            return null;
        }
    }
}
