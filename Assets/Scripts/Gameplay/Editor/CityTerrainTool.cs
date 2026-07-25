using System.Collections.Generic;
using Gameplay.City;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Gameplay.EditorTools
{
    // PINCEAU DE RELIEF : creuser, soulever, et tirer une pente propre entre deux points.
    //
    // Le sculpt ne remplace pas le bruit de CityTerrain, il s'AJOUTE par-dessus (voir
    // CityTerrain.Sculpt). Consequence a garder en tete : regrainer le bruit ne detruit pas ce
    // qui a ete peint, et inversement -- les deux couches sont independantes, ce qui permet de
    // chercher un relief general au bruit puis de corriger a la main sans repartir de zero.
    //
    // Comme CityBrushTool c'est un EditorTool et pas un [CustomEditor] : le geste est MODAL, le
    // clic nu doit sculpter et pas selectionner. A l'inverse exact du traceur de routes, qui
    // tient a preserver la selection Unity.
    [EditorTool("Sculpter le terrain", typeof(CityTerrain))]
    public class CityTerrainTool : EditorTool
    {
        private enum Mode { Creuser, Rampe }

        const string RadiusKey = "gmtk.ville.sculptRayon";
        const string ForceKey = "gmtk.ville.sculptForce";
        const string ModeKey = "gmtk.ville.sculptMode";
        const string SculptDir = "Assets/Meshes/City";

        const float MinRadius = 3f;
        const float MaxRadius = 120f;
        // Bord du disque adouci pour creuser (cloche), large plateau pour une rampe : une rampe
        // dont le milieu serait bombe n'est pas une rampe.
        const float DiscHardness = 0f;
        const float RampHardness = 0.55f;
        const double Debounce = 0.35;

        private static float Radius
        {
            get { return EditorPrefs.GetFloat(RadiusKey, 14f); }
            set { EditorPrefs.SetFloat(RadiusKey, value); }
        }

        // En METRES PAR SECONDE de peinture : le geste doit dependre du temps passe dessus, pas
        // du nombre d'evenements souris que Unity a bien voulu emettre.
        private static float Force
        {
            get { return EditorPrefs.GetFloat(ForceKey, 6f); }
            set { EditorPrefs.SetFloat(ForceKey, value); }
        }

        private static Mode Current
        {
            get { return (Mode)EditorPrefs.GetInt(ModeKey, 0); }
            set { EditorPrefs.SetInt(ModeKey, (int)value); }
        }

        private bool cursorValid;
        private Vector3 cursorPos;
        private double lastPaint;
        private int undoGroup = -1;
        private Vector3 rampStart;
        private bool rampArmed;

        public override GUIContent toolbarIcon =>
            EditorGUIUtility.IconContent("TerrainInspector.TerrainToolRaise", "|Sculpter le terrain");

        // Undo restaure la donnee de la couche, mais personne ne le dit ni au cache de lecture ni
        // au sol deja genere : sans ce raccrochage, Ctrl+Z annule bien le relief EN MEMOIRE et ne
        // change rien a l'ecran, ce qui se lit comme un outil casse.
        //
        // Abonnement GLOBAL et non lie a l'activation de l'outil. Deux raisons, toutes deux
        // vecues : apres une recompilation, un outil deja actif ne repasse pas par OnActivated et
        // le raccrochage disparaissait en silence ; et Ctrl+Z se tape aussi depuis la Hierarchy,
        // ou l'outil n'a pas la main.
        [InitializeOnLoadMethod]
        private static void HookUndo()
        {
            Undo.undoRedoPerformed -= AfterUndoRedo;
            Undo.undoRedoPerformed += AfterUndoRedo;
        }

        // Empreinte de chaque couche a la derniere synchronisation, pour ne regenerer le sol que
        // si l'annulation a REELLEMENT touche au relief. Sans ce filtre, chaque Ctrl+Z du projet
        // -- y compris un simple deplacement d'objet -- paierait 330 ms de generation.
        private static readonly Dictionary<CityTerrainSculpt, int> stamps =
            new Dictionary<CityTerrainSculpt, int>();

        private static void AfterUndoRedo()
        {
            bool touched = false;
            foreach (var t in Object.FindObjectsByType<CityTerrain>(FindObjectsSortMode.None))
            {
                if (t.sculpt == null) continue;
                int now = t.sculpt.Checksum();
                int before;
                if (stamps.TryGetValue(t.sculpt, out before) && before == now) continue;
                stamps[t.sculpt] = now;
                t.InvalidateSculpt();
                touched = true;
            }
            if (!touched) return;

            // File DIFFEREE, surtout pas une generation directe : on est dans le traitement de
            // l'annulation, et CityGroundBuilder pose ses propres jalons d'undo. Unity refuse net
            // -- "RecordCreation cannot be called while an undo is already processing".
            QueueGround();
            SceneView.RepaintAll();
        }

        // A appeler apres toute ecriture volontaire, pour que l'empreinte de reference suive.
        private static void Stamp(CityTerrain terrain)
        {
            if (terrain.sculpt != null) stamps[terrain.sculpt] = terrain.sculpt.Checksum();
        }

        public override void OnToolGUI(EditorWindow window)
        {
            if (window is not SceneView) return;
            if (target is not CityTerrain terrain) return;

            Event e = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(id);

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            cursorValid = RayTerrain(terrain, ray, out cursorPos);

            HandleResize(e);
            HandleKeys(e);
            HandleMouse(terrain, e, id);
            Draw(e);
            DrawHud(terrain);

            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
                SceneView.RepaintAll();
        }

        // ---------------------------------------------------------------- entrees

        // Maj + molette : geste standard (Unreal, Blender, Photoshop). Sur Windows Maj+molette
        // devient un defilement HORIZONTAL, donc delta.y tombe a zero et la valeur passe dans
        // delta.x -- lire le seul delta.y donnait un pinceau qui grossissait dans les deux sens.
        private void HandleResize(Event e)
        {
            if (e.type != EventType.ScrollWheel || !e.shift || e.alt) return;
            float raw = Mathf.Abs(e.delta.y) >= Mathf.Abs(e.delta.x) ? e.delta.y : e.delta.x;
            if (Mathf.Abs(raw) < 1e-4f) return;

            Radius = Mathf.Clamp(Radius * (raw > 0f ? 1f / 1.12f : 1.12f), MinRadius, MaxRadius);
            e.Use();
            SceneView.RepaintAll();
        }

        private void HandleKeys(Event e)
        {
            if (e.type != EventType.KeyDown) return;
            bool used = true;
            switch (e.keyCode)
            {
                case KeyCode.M:
                    Current = Current == Mode.Creuser ? Mode.Rampe : Mode.Creuser;
                    rampArmed = false;
                    break;
                case KeyCode.LeftBracket:
                case KeyCode.Minus:
                case KeyCode.KeypadMinus:
                    Radius = Mathf.Clamp(Radius / 1.18f, MinRadius, MaxRadius);
                    break;
                case KeyCode.RightBracket:
                case KeyCode.Equals:
                case KeyCode.Plus:
                case KeyCode.KeypadPlus:
                    Radius = Mathf.Clamp(Radius * 1.18f, MinRadius, MaxRadius);
                    break;
                default: used = false; break;
            }
            if (!used) return;
            e.Use();
            SceneView.RepaintAll();
        }

        private void HandleMouse(CityTerrain terrain, Event e, int id)
        {
            if (e.alt) return;   // Alt = orbite de la SceneView, on n'y touche jamais

            switch (e.type)
            {
                case EventType.MouseDown when e.button == 0:
                    if (!cursorValid) return;
                    if (!EnsureLayer(terrain)) return;
                    GUIUtility.hotControl = id;
                    lastPaint = EditorApplication.timeSinceStartup;

                    // UN GROUPE PAR TRAIT. L'increment doit precede la capture de l'index, sinon
                    // le repli final avalerait l'action precedente de l'utilisateur. Sans ce
                    // groupe, l'enregistrement se greffait sur le groupe courant -- une selection,
                    // un reglage d'inspecteur -- et le premier Ctrl+Z annulait donc autre chose
                    // que le coup de pinceau qu'on venait de donner.
                    Undo.IncrementCurrentGroup();
                    undoGroup = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName("Sculpter le terrain");
                    // L'objet complet : la donnee de la couche est un champ serialise, donc Undo
                    // sait la copier. C'est tout l'interet d'avoir quitte la Texture2D, dont les
                    // pixels ne sont pas serialises et n'etaient donc jamais restaures.
                    Undo.RegisterCompleteObjectUndo(terrain.sculpt, "Sculpter le terrain");
                    if (Current == Mode.Rampe) { rampStart = cursorPos; rampArmed = true; }
                    else Paint(terrain, e);
                    e.Use();
                    break;

                case EventType.MouseDrag when GUIUtility.hotControl == id:
                    if (Current == Mode.Creuser) Paint(terrain, e);
                    e.Use();
                    break;

                case EventType.MouseUp when GUIUtility.hotControl == id:
                    GUIUtility.hotControl = 0;
                    if (Current == Mode.Rampe && rampArmed && cursorValid)
                    {
                        terrain.SculptRamp(rampStart, cursorPos, Radius, RampHardness);
                        terrain.InvalidateSculpt();
                        Touch(terrain);
                    }
                    rampArmed = false;
                    if (undoGroup >= 0) Undo.CollapseUndoOperations(undoGroup);
                    undoGroup = -1;
                    Stamp(terrain);   // l'empreinte de reference suit l'ecriture volontaire
                    // Generation COMPLETE en fin de trait : la mise a jour vivante ne touche que
                    // les hauteurs, pas le decoupage des cellules ni le collider. C'est ici que
                    // le sol redevient exact et qu'on peut a nouveau rouler dessus.
                    QueueGround();
                    e.Use();
                    break;
            }
        }

        private void Paint(CityTerrain terrain, Event e)
        {
            if (!cursorValid) return;
            double now = EditorApplication.timeSinceStartup;
            // Borne haute sur le pas de temps : apres une reconstruction du sol ou un decrochage
            // de l'editeur, un dt de deux secondes creuserait un puits d'un seul coup.
            float dt = Mathf.Clamp((float)(now - lastPaint), 0f, 0.1f);
            lastPaint = now;
            if (dt <= 0f) return;

            float delta = Force * dt * (e.control ? 1f : -1f);   // nu = creuse, Ctrl = souleve
            terrain.SculptDisc(cursorPos, Radius, delta, DiscHardness);
            terrain.InvalidateSculpt();
            Touch(terrain);
            Live(cursorPos, Radius);
        }

        // Repercute le geste sur le sol TOUT DE SUITE, en ne recalculant que les sommets
        // touches. La marge couvre la bande de raccord aux routes, qui suit le terrain qu'on
        // vient de bouger. Si aucune grille n'est en cache (premiere ouverture de scene), on
        // retombe sur la generation differee.
        private static void Live(Vector3 centre, float radius)
        {
            if (!RoadNetworkEditor.AutoGround) return;
            if (!CityGroundBuilder.RefreshArea(centre, radius + 24f)) QueueGround();
        }

        private static void Touch(CityTerrain terrain)
        {
            EditorUtility.SetDirty(terrain.sculpt);
            EditorSceneManager.MarkSceneDirty(terrain.gameObject.scene);
        }

        // ---------------------------------------------------------------- couche

        // Cree l'asset de sculpt a la premiere utilisation. A cote du mesh du sol et pour la
        // meme raison : c'est de la donnee derivable-a-la-main, volumineuse, qu'on ne veut pas
        // voir dans le diff de la scene.
        private static bool EnsureLayer(CityTerrain terrain)
        {
            // Deja la, mais a la mauvaise taille : le rayon peignable a ete change APRES la
            // creation de la couche. On la reecrit en conservant ce qui est peint, sinon
            // agrandir le rayon ne servirait a rien tant qu'on n'a pas jete son travail.
            if (terrain.sculpt != null)
            {
                int was = terrain.sculpt.resolution;
                if (terrain.ResizeSculpt(terrain.SculptResolution))
                {
                    EditorUtility.SetDirty(terrain.sculpt);
                    AssetDatabase.SaveAssets();
                    Debug.Log($"[Ville] Couche de sculpt redimensionnee {was} -> " +
                              $"{terrain.sculpt.resolution} cellules " +
                              $"({terrain.sculpt.resolution * terrain.sculptCell * 0.5f:F0} m de portee). " +
                              "Le relief deja peint est conserve.", terrain);
                }
                return true;
            }

            if (!AssetDatabase.IsValidFolder(SculptDir))
            {
                string parent = System.IO.Path.GetDirectoryName(SculptDir).Replace('\\', '/');
                if (!AssetDatabase.IsValidFolder(parent)) return false;
                AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(SculptDir));
            }

            string scene = EditorSceneManager.GetActiveScene().name;
            string path = $"{SculptDir}/Sculpt_{scene}.asset";
            var layer = AssetDatabase.LoadAssetAtPath<CityTerrainSculpt>(path);
            if (layer == null)
            {
                layer = terrain.CreateSculptLayer();
                Migrate(terrain, $"{SculptDir}/Relief_{scene}.asset", layer);
                AssetDatabase.CreateAsset(layer, path);
                AssetDatabase.SaveAssets();
                Debug.Log($"[Ville] Couche de sculpt : {path} ({layer.resolution} cellules de " +
                          $"{terrain.sculptCell} m).", terrain);
            }

            Undo.RecordObject(terrain, "Couche de sculpt");
            terrain.sculpt = layer;
            terrain.InvalidateSculpt();
            EditorUtility.SetDirty(terrain);
            return true;
        }

        // Reprend le relief de l'ancienne couche, qui etait une Texture2D. Elle a ete abandonnee
        // parce qu'Undo ne sait pas copier les pixels d'une texture : Ctrl+Z ne rendait rien. Le
        // vieil asset n'est pas supprime -- a toi de le jeter une fois le report verifie.
        private static void Migrate(CityTerrain terrain, string oldPath, CityTerrainSculpt layer)
        {
            var old = AssetDatabase.LoadAssetAtPath<Texture2D>(oldPath);
            if (old == null || old.width < 2) return;

            var src = old.GetPixelData<float>(0);
            var flat = new float[src.Length];
            src.CopyTo(flat);

            // Les deux grilles sont centrees au meme endroit et partagent la taille de cellule :
            // report par simple decalage entier, comme un redimensionnement.
            int res = layer.resolution, oldRes = old.width;
            var dst = new float[res * res];
            int off = (res - oldRes) / 2;
            int moved = 0;
            for (int j = 0; j < oldRes; j++)
            {
                int dj = j + off;
                if (dj < 0 || dj >= res) continue;
                for (int i = 0; i < oldRes; i++)
                {
                    int di = i + off;
                    if (di < 0 || di >= res) continue;
                    float v = flat[j * oldRes + i];
                    dst[dj * res + di] = v;
                    if (Mathf.Abs(v) > 0.02f) moved++;
                }
            }
            layer.Write(dst);
            Debug.Log($"[Ville] Relief repris depuis l'ancienne couche texture : {moved} cellules " +
                      $"peintes reportees. '{oldPath}' n'est plus utilise, tu peux le supprimer.",
                      terrain);
        }

        // ---------------------------------------------------------------- sol

        // Le sol se refait apres le geste et pas pendant : une generation coute ~160 ms, la
        // relancer a chaque evenement souris rendrait le pinceau inutilisable.
        private static double groundDue;

        private static void QueueGround()
        {
            if (!RoadNetworkEditor.AutoGround) return;
            groundDue = EditorApplication.timeSinceStartup + Debounce;
            EditorApplication.update -= PumpGround;
            EditorApplication.update += PumpGround;
        }

        private static void PumpGround()
        {
            if (EditorApplication.timeSinceStartup < groundDue) return;
            EditorApplication.update -= PumpGround;
            CityGroundBuilder.Build(false);
        }

        // ---------------------------------------------------------------- visee

        // Intersection du rayon avec le CHAMP DE HAUTEUR, pas avec le collider du sol. Deux
        // raisons : le sol genere peut ne pas exister encore, et surtout il est perime entre
        // deux reconstructions -- viser dessus ferait deriver le pinceau de ce qu'on voit.
        private static bool RayTerrain(CityTerrain t, Ray r, out Vector3 hit)
        {
            hit = Vector3.zero;
            if (t == null) return false;
            if (r.origin.y - t.Height(r.origin.x, r.origin.z) < 0f) return false;   // deja sous le sol

            float d = 0f, step = 2f;
            const float Max = 4000f;
            while (d < Max)
            {
                float prev = d;
                d += step;
                step = Mathf.Min(step * 1.05f, 40f);
                Vector3 p = r.GetPoint(d);
                if (p.y - t.Height(p.x, p.z) > 0f) continue;

                // Traversee encadree -> dichotomie. 24 tours ramenent l'erreur sous le
                // millimetre quel que soit le pas d'approche.
                float lo = prev, hi = d;
                for (int i = 0; i < 24; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    Vector3 q = r.GetPoint(mid);
                    if (q.y - t.Height(q.x, q.z) > 0f) lo = mid; else hi = mid;
                }
                hit = r.GetPoint((lo + hi) * 0.5f);
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- affichage

        private void Draw(Event e)
        {
            if (e.type != EventType.Repaint || !cursorValid) return;

            bool raise = e.control;
            Color c = Current == Mode.Rampe ? new Color(1f, 0.8f, 0.3f)
                    : raise ? new Color(0.5f, 1f, 0.5f)
                    : new Color(1f, 0.5f, 0.35f);

            if (Current == Mode.Rampe && rampArmed)
            {
                Handles.color = c;
                Handles.DrawWireDisc(rampStart, Vector3.up, Radius, 2f);
                Handles.DrawLine(rampStart, cursorPos, 3f);
                Handles.Label((rampStart + cursorPos) * 0.5f,
                              $"pente {(cursorPos.y - rampStart.y):F1} m sur " +
                              $"{Vector3.ProjectOnPlane(cursorPos - rampStart, Vector3.up).magnitude:F0} m");
            }

            Handles.color = new Color(c.r, c.g, c.b, 0.08f);
            Handles.DrawSolidDisc(cursorPos, Vector3.up, Radius);
            Handles.color = c;
            Handles.DrawWireDisc(cursorPos, Vector3.up, Radius, 2f);
        }

        private void DrawHud(CityTerrain terrain)
        {
            Handles.BeginGUI();
            GUILayout.BeginArea(new Rect(12f, 12f, 300f, 88f), GUI.skin.box);
            GUILayout.Label(Current == Mode.Rampe ? "Relief — RAMPE" : "Relief — CREUSER",
                            EditorStyles.boldLabel);
            GUILayout.Label($"rayon {Radius:F0} m   force {Force:F0} m/s" +
                            (terrain.sculpt == null ? "   (couche non creee)" : ""),
                            EditorStyles.miniLabel);
            GUILayout.Label(Current == Mode.Rampe
                    ? "glisser d'un point a l'autre : pose la pente entre les deux\n" +
                      "M : revenir au creusage   Maj+molette ou [ ] : rayon"
                    : "glisser : creuse   Ctrl+glisser : souleve\n" +
                      "M : mode rampe   Maj+molette ou [ ] : rayon",
                    EditorStyles.miniLabel);
            GUILayout.EndArea();
            Handles.EndGUI();
        }
    }
}
