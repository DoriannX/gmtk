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
        // trou beant a ses pieds.
        //
        // 4 m et pas 1,5 : en dessous, une route levee est un REMBLAI, le sol monte la chercher et
        // il n'y a rien d'autre a faire. A 1,5 m, une rampe qui commence a peine a grimper etait
        // deja declaree ouvrage : le sol l'ignorait, restait au niveau du terrain, et ressortait
        // donc EN TRAVERS de la chaussee. La rupture existe toujours mais elle est desormais a
        // 4 m de haut, ou elle se lit comme la culee d'un ouvrage -- ce qu'elle est.
        //
        // Doit rester en phase avec RoadNetwork.CapAbove, qui decide garde-corps et piles : une
        // route sur remblai n'a ni l'un ni l'autre.
        const float OverheadIgnore = 4f;
        // La grille fine ne couvre que le voisinage des routes ; au-dela le sol est plat et
        // 8 grands quads suffisent. Evite 300 000 cellules pour du vide.
        const float NearMargin = 40f;
        // Bande de raccord entre le terrain et la surface de la route, en metres. La route reste
        // PLATE EN TRAVERS (le kit de tuiles n'a pas de sommets pour se coucher lateralement) :
        // c'est le sol qui vient la chercher.
        const float BlendBand = 8f;
        // Pente maximale de ce raccord. Une route enfoncee de 6 m rattrapee sur 8 m ferait un mur
        // a 37 deg ; la bande s'elargit donc avec le denivele pour que le talus reste roulable.
        const float BlendSlope = 0.5f;
        // Retrait du sol sous la surface de la route, et pente de sa plongee sous l'emprise.
        // 0.06 + 3.3 * 0.15 = 0.55 m au plus profond, soit encore au-dessus du bas de la tuile
        // (0.63 avec la retombee) : le sol reste cache DANS l'epaisseur de la route.
        const float RoadSink = 0.06f;
        const float UnderSlope = 0.15f;
        // Accotement plat autour de l'emprise, en metres. Doit depasser le pas de la grille, sinon
        // un triangle a cheval sur le bord de la tuile passe par-dessus la chaussee.
        const float Shoulder = 3f;
        // Distance maximale interrogee autour d'un point pour trouver une route. Elle borne le
        // COUT de la generation bien plus que sa justesse : une requete de proximite echantillonne
        // la spline, et la portee sert aussi de prefiltre. A 60 m, aucun segment n'etait ecarte et
        // le sol mettait une seconde a se refaire. 30 m couvre le raccord le plus large qu'on
        // fabrique (une tranchee de 15 m sous le terrain, talus a 0.5 de pente).
        const float ProbeRange = 30f;
        // Le bord exterieur doit finir hors de vue : le brouillard ne le cache pas (sa couleur
        // est plus claire que le ciel juste au-dessus de l'horizon, l'arete se lit en vue
        // aerienne).
        //
        // Dimensionne sur le PLAN LOINTAIN DE LA CAMERA (1000 m) et non sur le brouillard, qui
        // sature vers 610 m : au-dela du plan lointain il n'y a plus rien a couvrir, en deca le
        // bord peut se lire en vue aerienne ou le brouillard porte moins. 350 m ne suffisait
        // pas -- depuis le bord de la ville, la fin du monde tombait en plein champ visible.
        //
        // Le surcout est nul par construction : cet anneau est PLAT, donc fait de grandes dalles
        // et pas de la grille fine. Passer de 350 a 1400 m triple l'emprise du sol pour environ
        // un millier de sommets, sur les 60 000 que pese la grille fine. C'est la grille fine
        // qui coute, et elle ne bouge pas d'un pouce ici.
        const float FarMargin = 1400f;
        const float MinSize = 1200f;

        [MenuItem("Tools/Ville/Generer le sol")]
        public static void Build() { Build(true); }

        // `select` : la generation par le menu selectionne le sol produit, la generation
        // automatique apres une edition de route ne le fait surtout PAS -- elle volerait la
        // selection du noeud qu'on est en train de regler.
        // Ecrire l'asset de mesh sur le disque coute ~100 ms sur 52 000 sommets. Le menu le fait,
        // la generation automatique non : Unity ecrira de toute facon a la prochaine sauvegarde
        // du projet, et le mesh est entierement regenerable en attendant.
        static bool writeAsset = true;

        public static void Build(bool select)
        {
            writeAsset = select;
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

            // Les routes se posent sur le relief AVANT que le sol ne soit calcule : le sol se
            // raccorde a leur surface, donc l'ordre inverse le ferait viser des hauteurs perimees.
            // Les jalons d'UNDO ne sont poses que pour une generation DEMANDEE (le menu). Une
            // regeneration AUTOMATIQUE n'empile rien, et c'est important : le sol est du contenu
            // entierement derive, mais tant qu'il apparaissait dans la pile d'annulation, le
            // premier Ctrl+Z apres une edition annulait "Generer le sol" au lieu de l'edition.
            // Pire, ca restaurait l'ancien maillage -- donc l'ecran montrait le resultat attendu
            // pendant que la donnee, elle, n'avait pas bougé.
            var terrain = CityTerrain.Find();
            foreach (var net in nets)
            {
                if (select) Undo.RecordObject(net, "Poser les routes sur le relief");
                if (net.LayOnTerrain(terrain))
                    EditorUtility.SetDirty(net);
            }

            var mesh = BuildMesh(nets, terrain, all, roads, groundY);
            CaptureLive(mesh, nets, terrain, groundY);

            int group = -1;
            if (select)
            {
                Undo.IncrementCurrentGroup();
                group = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Generer le sol");
            }

            GameObject old = Find();
            if (old != null)
            {
                if (select) Undo.DestroyObjectImmediate(old);
                else Object.DestroyImmediate(old);
            }

            var go = new GameObject(ObjectName, typeof(MeshFilter), typeof(MeshRenderer),
                                    typeof(MeshCollider));
            if (select) Undo.RegisterCreatedObjectUndo(go, "Generer le sol");
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

            if (group >= 0) Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(go.scene);
            if (!select) return;

            Selection.activeGameObject = go;
            Debug.Log($"[Ville] Sol : trottoir a y = {groundY:F3}, {mesh.vertexCount} sommets, " +
                      $"{mesh.triangles.Length / 3} triangles. Couleur reglable sur {MatPath}.");
        }

        // ---------------------------------------------------------------- mise a jour vivante
        //
        // Le pinceau de relief doit montrer ce qu'il fait PENDANT le trait. Une generation
        // complete coute ~250 ms : impossible a lancer par evenement souris, et c'est ce delai
        // qui rendait le pinceau aveugle.
        //
        // On garde donc la disposition de la derniere grille generee, et on ne recalcule QUE les
        // sommets sous le disque -- quelques centaines au lieu de 50 000. Ce qui n'est PAS
        // recalcule : la liste des cellules gardees. Sculpter ne deplace pas une route, donc
        // l'emprise routiere ne bouge pas ; le seul cas ou elle bougerait est un terrain remonte
        // au point de transformer un pont en remblai, et la generation complete du relachement
        // de la souris s'en charge.
        //
        // Le collider n'est PAS recuisiné ici (Physics.BakeMesh coute plus cher que tout le
        // reste). La visee du pinceau ne passe pas par lui : elle interroge le champ de hauteur
        // directement (CityTerrainTool.RayTerrain).
        static Mesh liveMesh;
        static Vector3[] liveVerts;
        static int[] gridIndex;
        static int gridCx, gridCz;
        static float gridX0, gridZ0, gridY;
        static RoadNetwork[] liveNets;
        static CityTerrain liveTerrain;
        static Vector4[] liveChords;
        static float liveReach2;

        static void CaptureLive(Mesh mesh, RoadNetwork[] nets, CityTerrain terrain, float y)
        {
            liveMesh = mesh;
            liveVerts = mesh != null ? mesh.vertices : null;
            liveNets = nets;
            liveTerrain = terrain;
            gridY = y;
            liveChords = Chords(nets);
            float reach = ProbeRange + MaxHalfWidth(nets) + ChordSlack;
            liveReach2 = reach * reach;
        }

        // Recalcule les hauteurs du sol dans un disque. Rend faux si aucune grille n'est en
        // cache -- a l'appelant de retomber sur une generation complete.
        public static bool RefreshArea(Vector3 centre, float radius)
        {
            if (liveMesh == null || liveVerts == null || gridIndex == null) return false;
            if (liveTerrain == null) return false;

            int ri0 = Mathf.FloorToInt((centre.x - radius - gridX0) / Cell);
            int ri1 = Mathf.CeilToInt((centre.x + radius - gridX0) / Cell);
            int rj0 = Mathf.FloorToInt((centre.z - radius - gridZ0) / Cell);
            int rj1 = Mathf.CeilToInt((centre.z + radius - gridZ0) / Cell);

            // Le pinceau deborde-t-il de la grille fine ? Si oui, il peint la ou le sol n'a pas
            // encore de sommets : on met a jour ce qu'on peut, mais on rend FAUX pour que
            // l'appelant declenche une generation complete, seule capable d'etendre la grille.
            bool covered = ri0 >= 0 && rj0 >= 0 && ri1 <= gridCx && rj1 <= gridCz;

            int i0 = Mathf.Max(0, ri0), i1 = Mathf.Min(gridCx, ri1);
            int j0 = Mathf.Max(0, rj0), j1 = Mathf.Min(gridCz, rj1);
            if (i0 > i1 || j0 > j1) return false;

            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    int k = j * (gridCx + 1) + i;
                    int vi = gridIndex[k];
                    if (vi < 0 || vi >= liveVerts.Length) continue;
                    var p = new Vector3(gridX0 + i * Cell, gridY, gridZ0 + j * Cell);
                    bool ignored;
                    liveVerts[vi].y = Sample(liveNets, liveTerrain, gridY, p, liveChords,
                                             liveReach2, out ignored);
                }

            liveMesh.SetVertices(liveVerts);
            // Sans les normales, une butte fraiche s'eclaire comme un plan : on ne voit RIEN de
            // ce qu'on sculpte, ce qui etait tout le probleme.
            liveMesh.RecalculateNormals();
            liveMesh.RecalculateBounds();
            return covered;
        }

        public static void InvalidateLive() { liveMesh = null; liveVerts = null; gridIndex = null; }

        // ---------------------------------------------------------------- geometrie

        static Mesh BuildMesh(RoadNetwork[] nets, CityTerrain terrain, Bounds all, Bounds roads,
                              float y)
        {
            // Emprise totale, et emprise de la grille fine (alignee sur le pas de grille pour
            // que l'anneau exterieur se raccorde pile sur le bord de la grille).
            float fx0 = Mathf.Min(all.min.x, roads.min.x) - FarMargin;
            float fx1 = Mathf.Max(all.max.x, roads.max.x) + FarMargin;
            float fz0 = Mathf.Min(all.min.z, roads.min.z) - FarMargin;
            float fz1 = Mathf.Max(all.max.z, roads.max.z) + FarMargin;
            Grow(ref fx0, ref fx1, MinSize);
            Grow(ref fz0, ref fz1, MinSize);

            // La grille fine doit couvrir tout le relief, sinon une bosse tomberait dans
            // l'anneau exterieur -- qui est plat par construction (4 quads) et se fendrait au
            // raccord. Le disque de CityTerrain est donc englobe, marge comprise.
            Bounds fine = roads;
            fine.Expand(new Vector3(NearMargin * 2f, 0f, NearMargin * 2f));
            if (terrain != null)
            {
                Vector3 c = terrain.transform.position;
                float r = terrain.radius;
                fine.Encapsulate(new Vector3(c.x - r, 0f, c.z - r));
                fine.Encapsulate(new Vector3(c.x + r, 0f, c.z + r));

                // Et tout ce qui a ete SCULPTE A LA MAIN, ou qu'il soit. On suit l'emprise
                // reellement peinte et pas le rayon peignable : sculpter une butte a 500 m ne
                // doit tesseller que cette butte, pas le kilometre carre autour.
                Bounds painted;
                if (terrain.SculptBounds(out painted))
                {
                    fine.Encapsulate(new Vector3(painted.min.x, 0f, painted.min.z));
                    fine.Encapsulate(new Vector3(painted.max.x, 0f, painted.max.z));
                }
            }

            float nx0 = Mathf.Floor(fine.min.x / Cell) * Cell;
            float nx1 = Mathf.Ceil(fine.max.x / Cell) * Cell;
            float nz0 = Mathf.Floor(fine.min.z / Cell) * Cell;
            float nz1 = Mathf.Ceil(fine.max.z / Cell) * Cell;
            nx0 = Mathf.Max(nx0, fx0); nx1 = Mathf.Min(nx1, fx1);
            nz0 = Mathf.Max(nz0, fz0); nz1 = Mathf.Min(nz1, fz1);

            int cx = Mathf.Max(1, Mathf.RoundToInt((nx1 - nx0) / Cell));
            int cz = Mathf.Max(1, Mathf.RoundToInt((nz1 - nz0) / Cell));

            var verts = new List<Vector3>();
            var tris = new List<int>();

            // Retenu pour la mise a jour vivante du pinceau de relief (voir RefreshArea).
            gridX0 = nx0; gridZ0 = nz0; gridCx = cx; gridCz = cz;

            // --- grille fine, une cellule gardee seulement si ses 4 coins sont hors route ---
            // Table des sommets : -1 tant que le sommet n'a servi a aucune cellule gardee, pour
            // ne pas emettre des milliers de sommets orphelins au milieu de la chaussee.
            var index = new int[(cx + 1) * (cz + 1)];
            for (int i = 0; i < index.Length; i++) index[i] = -1;
            gridIndex = index;

            // Cordes des segments, en XZ. Elles servent de PREFILTRE aux requetes de proximite :
            // une requete echantillonne la spline et coute ~30 us, la distance a un bout de
            // droite coute quelques nanosecondes. Sans ce filtre, la grille fine couvrant tout le
            // relief, on payait le prix fort sur des dizaines de milliers de sommets qui n'ont
            // aucune route a moins de 200 m.
            //
            // La corde suffit : depuis que les points intermediaires de suivi du relief sont
            // interpoles en XZ, une spline ne s'ecarte de sa corde que par la courbure des
            // raccords aux croisements -- d'ou la marge.
            var chords = Chords(nets);
            float reach = ProbeRange + MaxHalfWidth(nets) + ChordSlack;
            float reach2 = reach * reach;

            // Hauteur et garde routiere sont calculees dans la MEME passe : les deux sortent de
            // la meme requete de proximite, et les recalculer separement doublerait le cout du
            // poste le plus lourd de la generation.
            var free = new bool[(cx + 1) * (cz + 1)];
            var height = new float[(cx + 1) * (cz + 1)];
            for (int j = 0; j <= cz; j++)
                for (int i = 0; i <= cx; i++)
                {
                    var p = new Vector3(nx0 + i * Cell, y, nz0 + j * Cell);
                    int k = j * (cx + 1) + i;
                    height[k] = Sample(nets, terrain, y, p, chords, reach2, out free[k]);
                }

            for (int j = 0; j < cz; j++)
                for (int i = 0; i < cx; i++)
                {
                    int a = j * (cx + 1) + i, b = a + 1;
                    int c = (j + 1) * (cx + 1) + i, d = c + 1;
                    if (!free[a] || !free[b] || !free[c] || !free[d]) continue;
                    Quad(verts, tris, index, cx, nx0, nz0, height, i, j);
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
            // UV planaires : le sol n'a pas de depliage, et une projection sur XZ tient tant que
            // les pentes restent celles d'une butte (l'etirement vaut 1/cos(pente)).
            var uv = new Vector2[verts.Count];
            for (int i = 0; i < verts.Count; i++)
                uv[i] = new Vector2(verts[i].x, verts[i].z) * 0.1f;
            mesh.uv = uv;
            // Normales calculees et non forcees a Vector3.up : avec du relief, un sol tout droit
            // s'eclaire comme un plan et la butte disparait completement au rendu.
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            return SaveMesh(mesh);
        }

        static void Quad(List<Vector3> verts, List<int> tris, int[] index, int cx,
                         float x0, float z0, float[] height, int i, int j)
        {
            int a = Vertex(verts, index, cx, x0, z0, height, i, j);
            int b = Vertex(verts, index, cx, x0, z0, height, i + 1, j);
            int c = Vertex(verts, index, cx, x0, z0, height, i, j + 1);
            int d = Vertex(verts, index, cx, x0, z0, height, i + 1, j + 1);
            tris.Add(a); tris.Add(c); tris.Add(b);
            tris.Add(b); tris.Add(c); tris.Add(d);
        }

        static int Vertex(List<Vector3> verts, int[] index, int cx,
                          float x0, float z0, float[] height, int i, int j)
        {
            int key = j * (cx + 1) + i;
            if (index[key] < 0)
            {
                index[key] = verts.Count;
                verts.Add(new Vector3(x0 + i * Cell, height[key], z0 + j * Cell));
            }
            return index[key];
        }

        // L'anneau exterieur est plat, donc un seul quad suffirait au rendu -- mais PhysX refuse
        // les triangles de plus de 500 unites ("The resulting Triangle Mesh can impact simulation
        // and query stability") et le crie a chaque cuisson du collider. On le decoupe donc en
        // dalles, assez grandes pour ne rien couter et assez petites pour tenir sous le seuil.
        const float SlabMax = 200f;

        static void Strip(List<Vector3> verts, List<int> tris, float y,
                          float x0, float z0, float x1, float z1)
        {
            if (x1 - x0 < 0.01f || z1 - z0 < 0.01f) return;
            int nx = Mathf.Max(1, Mathf.CeilToInt((x1 - x0) / SlabMax));
            int nz = Mathf.Max(1, Mathf.CeilToInt((z1 - z0) / SlabMax));
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                {
                    float ax = Mathf.Lerp(x0, x1, (float)i / nx), bx = Mathf.Lerp(x0, x1, (float)(i + 1) / nx);
                    float az = Mathf.Lerp(z0, z1, (float)j / nz), bz = Mathf.Lerp(z0, z1, (float)(j + 1) / nz);
                    int b = verts.Count;
                    verts.Add(new Vector3(ax, y, az));
                    verts.Add(new Vector3(bx, y, az));
                    verts.Add(new Vector3(ax, y, bz));
                    verts.Add(new Vector3(bx, y, bz));
                    tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                    tris.Add(b + 1); tris.Add(b + 2); tris.Add(b + 3);
                }
        }

        // Hauteur du sol en un point, et au passage : la cellule est-elle hors emprise routiere ?
        //
        // Deux choses se superposent ici. Le RELIEF donne la hauteur de base. Puis, a l'approche
        // d'une route, le sol va CHERCHER la surface de celle-ci : la tuile reste plate en
        // travers (le kit n'a pas les sommets pour se coucher lateralement), donc c'est au sol de
        // se raccorder. Une route posee plus bas que le terrain se retrouve ainsi au fond d'un
        // creux, une route posee plus haut sur un remblai -- sans un asset de plus.
        //
        // Au-dela de OverheadIgnore, la route est un OUVRAGE : `ProbeRoad` ne la voit plus, le sol
        // reste au niveau du terrain et passe dessous. C'est ce qui distingue le pont du creux, et
        // c'est le meme seuil qui empeche deja le pont de percer le sol a ses pieds.
        static float Sample(RoadNetwork[] nets, CityTerrain terrain, float baseY, Vector3 p,
                            Vector4[] chords, float reach2, out bool free)
        {
            float y = baseY + (terrain != null ? terrain.Height(p.x, p.z) - terrain.transform.position.y : 0f);

            free = true;
            if (!NearAnyChord(chords, p.x, p.z, reach2)) return y;

            // La requete part de la hauteur du TERRAIN : c'est elle qui decide ce qui est un
            // ouvrage au-dessus de nous. Faite depuis y = 0, un pont sur une butte de 6 m
            // passerait pour une route au ras du sol.
            var q = new Vector3(p.x, y, p.z);
            float best = float.PositiveInfinity, roadY = y;
            foreach (var net in nets)
            {
                RoadNetwork.RoadProbe probe;
                if (!net.ProbeRoad(q, ProbeRange, out probe, OverheadIgnore)) continue;
                if (probe.clearance >= best) continue;
                best = probe.clearance;
                // Un croisement est couche dans la pente : sa surface a l'aplomb du point n'est
                // PAS la hauteur de son centre. Au pied d'une rampe, l'ecart atteignait un metre
                // et le sol enterrait la route.
                // La surface visee depend de la NATURE du trace. Une route se raccorde par ses
                // trottoirs, un escalier par sa volee nue, un egout par la LEVRE de ses parois
                // -- soit tout en haut du canal, pas au fond : c'est ce qui fait que le sol
                // reste a plat autour d'un egout au lieu de plonger dedans.
                roadY = probe.node >= 0
                    ? net.JunctionSurfaceY(probe.node, q)
                    : probe.point.y + net.SurfaceRise(probe.kind);
            }

            free = best >= -Overlap;
            if (float.IsPositiveInfinity(best)) return y;

            // Le sol vise un poil SOUS le trottoir, et plonge d'autant plus qu'il s'enfonce sous
            // l'emprise. Cale a la meme hauteur, il se battait avec le trottoir dans le tampon de
            // profondeur : deux surfaces distantes de 2 cm sur 3,3 m de recouvrement, ca ressort
            // en pointilles le long de chaque route. La plongee reste sous l'epaisseur de la
            // tuile, donc invisible.
            roadY -= RoadSink + Mathf.Max(0f, -best) * UnderSlope;

            // Le sol reste cale sur la route jusqu'a `Shoulder` metres AU-DELA du bord de
            // l'emprise. Sans cet accotement, la remontee vers le terrain commence pile au bord
            // de la tuile : un triangle a cheval sur le bord relie alors un sommet cale sur la
            // route a un sommet deja remonte, et sa face passe par-dessus la chaussee. La grille
            // fait 2 m de pas, il faut donc plus que ca.
            float over = Mathf.Max(0f, best - Shoulder);

            // Bande de raccord elargie avec le denivele : le talus garde une pente constante au
            // lieu de se raidir avec la profondeur du creux.
            float band = Mathf.Max(BlendBand, Mathf.Abs(roadY - y) / BlendSlope);
            float t = 1f - Mathf.Clamp01(over / band);
            return Mathf.Lerp(y, roadY, t * t * (3f - 2f * t));
        }

        // Marge sur la corde : une spline s'en ecarte par la courbure de ses raccords.
        const float ChordSlack = 12f;

        // (x0, z0, x1, z1) par segment, tous reseaux confondus.
        static Vector4[] Chords(RoadNetwork[] nets)
        {
            var list = new List<Vector4>();
            foreach (var net in nets)
                for (int k = 0; k < net.SegmentCount; k++)
                {
                    var s = net.SegmentAt(k);
                    Vector3 a = net.NodeWorld(s.a), b = net.NodeWorld(s.b);
                    list.Add(new Vector4(a.x, a.z, b.x, b.z));
                }
            return list.ToArray();
        }

        // Emprise la plus large du reseau, toutes natures confondues : elle dimensionne le
        // prefiltre par cordes. Prendre la seule largeur de chaussee laisserait un canal
        // d'egout, deux fois plus large, echapper au filtre -- et le sol se refermerait dessus.
        static float MaxHalfWidth(RoadNetwork[] nets)
        {
            float w = 0f;
            foreach (var net in nets)
            {
                w = Mathf.Max(w, net.RoadHalfWidth);
                for (int k = 0; k < net.SegmentCount; k++) w = Mathf.Max(w, net.SegmentHalfWidth(k));
            }
            return w;
        }

        // Distance au carre point-segment en XZ, comparee a la portee. Sort des que c'est proche :
        // sur un reseau dense, la plupart des points tombent tot.
        static bool NearAnyChord(Vector4[] chords, float x, float z, float reach2)
        {
            for (int i = 0; i < chords.Length; i++)
            {
                Vector4 c = chords[i];
                float ex = c.z - c.x, ez = c.w - c.y;
                float px = x - c.x, pz = z - c.y;
                float len2 = ex * ex + ez * ez;
                float t = len2 > 1e-6f ? Mathf.Clamp01((px * ex + pz * ez) / len2) : 0f;
                float dx = px - ex * t, dz = pz - ez * t;
                if (dx * dx + dz * dz <= reach2) return true;
            }
            return false;
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
                if (writeAsset) AssetDatabase.SaveAssets();
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
