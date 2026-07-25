using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Gameplay.EditorTools
{
    // Applique le look cyberpunk cartoon a la scene ouverte, en un menu.
    // C'est la traduction executable de docs/cyberpunk-ambiance.md : skybox, ambient, fog,
    // clair de lune, Global Volume et post sur la camera. Les valeurs sont les memes que
    // celles reglees a la main sur road.unity, qui reste la scene de reference.
    //
    // Ce que ce menu ne fait PAS, volontairement :
    // - les neons : c'est Tools/Ville/Semer des neons, qui a besoin des routes et des
    //   batiments deja poses. Sans eux la ville reste illisible (le neon est la key light).
    // - les materiaux : ils portent deja GMTK/ToonLit, le look ne se decide pas ici.
    // - PC_Renderer / PC_RPAsset (outline, grading HDR) : reglages projet, pas scene.
    public static class CyberpunkLookApplier
    {
        const string SkyboxPath = "Assets/Materials/SkyCyberpunk.mat";
        const string ProfilePath = "Assets/Settings/CyberpunkPostProfile.asset";
        const string VolumeName = "Global Volume - Cyberpunk";

        // Ambient : mode Trilight. Dans ce projet il n'y a ni lightmap, ni light probe, ni GI
        // temps reel -> l'ambient est 100 % de l'indirect. Volontairement modeste : monter
        // ces valeurs delave sans rien resoudre, le gain de lisibilite vient des neons.
        static readonly Color AmbientSky = new Color(0.19f, 0.21f, 0.35f);
        static readonly Color AmbientEquator = new Color(0.14f, 0.16f, 0.27f);
        static readonly Color AmbientGround = new Color(0.07f, 0.07f, 0.12f);

        static readonly Color FogColor = new Color(0.09f, 0.11f, 0.20f);
        const float FogDensity = 0.0035f;

        // Couleur peu saturee : un (0.62,0.70,1.0) perd 38 % d'energie sur le rouge pour rien.
        static readonly Color MoonColor = new Color(0.74f, 0.80f, 1.00f);
        const float MoonIntensity = 1.6f;
        // Alignee sur _MoonDir du skybox, sinon la lune est peinte d'un cote et eclaire de l'autre.
        static readonly Vector3 MoonEuler = new Vector3(38f, 200f, 0f);

        [MenuItem("Tools/Ambiance/Nouvelle scene cyberpunk cartoon")]
        public static void NewScene()
        {
            if (EditorApplication.isPlaying) { WarnPlayMode(); return; }

            // Demande a sauver la scene courante si elle est sale ; si l'utilisateur annule,
            // on ne cree rien.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            Apply();
        }

        [MenuItem("Tools/Ambiance/Appliquer le look cyberpunk cartoon")]
        public static void Apply()
        {
            if (EditorApplication.isPlaying) { WarnPlayMode(); return; }

            var skybox = AssetDatabase.LoadAssetAtPath<Material>(SkyboxPath);
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
            if (skybox == null || profile == null)
            {
                EditorUtility.DisplayDialog("Look cyberpunk",
                    $"Asset manquant :\n{(skybox == null ? SkyboxPath : "")}\n{(profile == null ? ProfilePath : "")}",
                    "OK");
                return;
            }

            var scene = SceneManager.GetActiveScene();
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Look cyberpunk cartoon");

            ApplyRenderSettings(skybox);
            string lightInfo = ApplyMoonlight();
            string volumeInfo = ApplyVolume(profile);
            string cameraInfo = ApplyCameras();

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            string sceneName = string.IsNullOrEmpty(scene.name) ? "(scene non sauvegardee)" : scene.name;
            Debug.Log($"[Look cyberpunk] applique a '{sceneName}'.\n" +
                      $"  skybox : {skybox.name}\n" +
                      $"  {lightInfo}\n  {volumeInfo}\n  {cameraInfo}\n" +
                      "  Neons NON semes : lancer Tools/Ville/Semer des neons une fois les routes et batiments poses.");
        }

        // Le play mode fait lever "cannot be used during play mode" a MarkSceneDirty.
        static void WarnPlayMode()
        {
            EditorUtility.DisplayDialog("Look cyberpunk",
                "Sortir du play mode d'abord : Unity refuse de marquer la scene modifiee pendant l'execution.",
                "OK");
        }

        static void ApplyRenderSettings(Material skybox)
        {
            // Pas d'Undo ici : l'objet qui porte les RenderSettings n'est pas accessible
            // publiquement. Ctrl+Z ne rend donc pas l'ancien ciel/ambient/fog.
            RenderSettings.skybox = skybox;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = AmbientSky;
            RenderSettings.ambientEquatorColor = AmbientEquator;
            RenderSettings.ambientGroundColor = AmbientGround;
            RenderSettings.ambientIntensity = 1f;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = FogColor;
            RenderSettings.fogDensity = FogDensity;

            DynamicGI.UpdateEnvironment();
        }

        static string ApplyMoonlight()
        {
            Light moon = null;
            int extras = 0;

            // Filtrer explicitement sur le type : FindFirstObjectByType<Light>() renvoie
            // n'importe quelle lumiere (dans road.unity, une neon parmi 112) et on repeint
            // alors un lampadaire en blanc lunaire sans que rien ne le signale.
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (l.type != LightType.Directional) continue;
                if (moon == null) moon = l; else extras++;
            }

            if (moon == null)
            {
                var go = new GameObject("Directional Light");
                Undo.RegisterCreatedObjectUndo(go, "Creer le clair de lune");
                moon = go.AddComponent<Light>();
                moon.type = LightType.Directional;
            }

            Undo.RecordObject(moon, "Clair de lune");
            Undo.RecordObject(moon.transform, "Clair de lune");
            moon.color = MoonColor;
            moon.intensity = MoonIntensity;
            moon.shadows = LightShadows.Soft;
            moon.transform.rotation = Quaternion.Euler(MoonEuler);
            EditorUtility.SetDirty(moon);

            return extras > 0
                ? $"clair de lune : '{moon.name}' regle, {extras} autre(s) directionnelle(s) laissee(s) telle(s) quelle(s)"
                : $"clair de lune : '{moon.name}' regle";
        }

        static string ApplyVolume(VolumeProfile profile)
        {
            Volume volume = null;
            foreach (var v in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
                if (v.isGlobal) { volume = v; break; }

            bool created = volume == null;
            if (created)
            {
                var go = new GameObject(VolumeName);
                Undo.RegisterCreatedObjectUndo(go, "Creer le Global Volume");
                volume = go.AddComponent<Volume>();
            }

            Undo.RecordObject(volume, "Global Volume cyberpunk");
            Undo.RecordObject(volume.gameObject, "Global Volume cyberpunk");
            volume.isGlobal = true;
            volume.priority = 1f;   // passe devant le DefaultVolumeProfile du projet
            volume.weight = 1f;
            volume.sharedProfile = profile;
            volume.gameObject.name = VolumeName;
            EditorUtility.SetDirty(volume);

            return $"volume : {(created ? "cree" : "recycle")} -> {profile.name}";
        }

        static string ApplyCameras()
        {
            int touched = 0;
            foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                // GetUniversalAdditionalCameraData() AJOUTE le composant s'il manque. Un simple
                // GetComponent renvoie null sur une camera de scene neuve (URP l'ajoute
                // paresseusement) : le post serait alors silencieusement saute, et le menu
                // n'aurait aucun effet visible sur le cas d'usage principal.
                var data = cam.GetUniversalAdditionalCameraData();
                if (data == null) continue;
                if (data.renderPostProcessing && cam.clearFlags == CameraClearFlags.Skybox) continue;

                Undo.RecordObject(data, "Post process camera");
                Undo.RecordObject(cam, "Post process camera");
                data.renderPostProcessing = true;
                cam.clearFlags = CameraClearFlags.Skybox;   // sinon le ciel etoile ne s'affiche pas
                EditorUtility.SetDirty(data);
                touched++;
            }
            return $"cameras : {touched} passee(s) en post-process + clear Skybox";
        }
    }
}
