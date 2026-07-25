using Gameplay.City;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Gameplay.EditorTools
{
    // ECLAIRAGE NEON DE TEST : seme des Light ponctuelles colorees le long des routes et au pied
    // des batiments peints. C'est l'etape 6 de docs/cyberpunk-ambiance.md, celle qui manquait :
    // sans elle il ne reste que le clair de lune et la ville est illisible.
    //
    // PREREQUIS non evident : GMTK/ToonLit doit gerer les lumieres additionnelles. En Forward+
    // (PC_Renderer m_RenderingMode = 2) URP ETEINT explicitement _ADDITIONAL_LIGHTS
    // (ForwardLights.cs : SetKeyword(AdditionalLightsPixel, ... && !m_UseForwardPlus)) et passe
    // par _CLUSTER_LIGHT_LOOP. Tant que le #if du shader ne teste que _ADDITIONAL_LIGHTS, ces
    // lumieres n'eclairent RIEN de toon et ce menu semble ne servir a rien.
    //
    // Aucune ombre : 100+ point lights avec ombres coulerait, et le rendu toon ne les exploite
    // pas. Conteneur "Neons" recycle a chaque passe -> relancable sans empiler.
    public static class CityNeonSeeder
    {
        const string ContainerName = "Neons";

        const float RoadSpacing = 16f;   // un lampadaire tous les 16 m, cotes alternes
        const float RoadHeight = 4.5f;
        // Les neons sont la key light de la ville, pas de la decoration : ToonLit calcule
        // ses bandes a partir d'eux.
        // ATTENTION : dans ToonLit la couverture de la flaque vaut
        // saturate(distanceAttenuation * intensite) -> l'intensite fixe la taille du COEUR
        // SATURE de la flaque, pas sa brillance. Monter trop haut (teste a 31) fait un
        // disque franc sans degrade, et les flaques finissent par se recouvrir en un aplat
        // uniforme sur toute la chaussee. La brillance se regle avec _NeonPoolStrength.
        const float RoadIntensity = 15f;
        const float RoadRange = 32f;

        const int BuildingEvery = 5;     // une enseigne un batiment sur 5
        const float BuildingHeight = 3.5f;
        const float BuildingIntensity = 11f;
        const float BuildingRange = 24f;

        // Palette du theme (cf docs/cyberpunk-ambiance.md).
        static readonly Color[] Palette =
        {
            new Color(0.13f, 0.90f, 1.00f),  // cyan
            new Color(1.00f, 0.15f, 0.80f),  // magenta
            new Color(1.00f, 0.55f, 0.12f),  // orange
            new Color(0.45f, 1.00f, 0.25f),  // vert acide
            new Color(0.60f, 0.30f, 1.00f),  // violet
            new Color(1.00f, 0.25f, 0.35f),  // rose-rouge
        };

        [MenuItem("Tools/Ville/Semer des neons")]
        public static void Seed()
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Semer des neons");

            GameObject old = Find();
            if (old != null) Undo.DestroyObjectImmediate(old);

            var root = new GameObject(ContainerName);
            Undo.RegisterCreatedObjectUndo(root, "Semer des neons");

            int n = 0;
            int roads = SeedRoads(root.transform, ref n);
            int walls = SeedBuildings(root.transform, ref n);

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(root.scene);
            Debug.Log($"[Ville] Neons : {roads} en rue + {walls} sur batiments = {n} lumieres.");
        }

        [MenuItem("Tools/Ville/Retirer les neons")]
        public static void Clear()
        {
            GameObject old = Find();
            if (old == null) { Debug.Log("[Ville] Pas de conteneur Neons."); return; }
            Undo.DestroyObjectImmediate(old);
        }

        static int SeedRoads(Transform root, ref int n)
        {
            int made = 0;
            foreach (var net in Object.FindObjectsByType<RoadNetwork>(FindObjectsSortMode.None))
            {
                float half = net.RoadHalfWidth;
                for (int k = 0; k < net.SegmentCount; k++)
                {
                    float len = net.SegmentLength(k);
                    int side = 1;
                    for (float d = RoadSpacing * 0.5f; d < len - 4f; d += RoadSpacing)
                    {
                        Vector3 pos, tan;
                        if (!net.SampleSegment(k, d, out pos, out tan, out len)) continue;
                        Vector3 nrm = Vector3.Cross(Vector3.up, tan).normalized * side;
                        side = -side;
                        // Sur le trottoir, pas au milieu de la chaussee.
                        Make(root, "Neon_rue_" + made, pos + nrm * (half - 1.2f) + Vector3.up * RoadHeight,
                             Palette[n % Palette.Length], RoadIntensity, RoadRange);
                        n++; made++;
                    }
                }
            }
            return made;
        }

        static int SeedBuildings(Transform root, ref int n)
        {
            int made = 0;
            foreach (var brush in Object.FindObjectsByType<CityBrush>(FindObjectsSortMode.None))
            {
                Transform c = brush.Container;
                if (c == null) continue;
                for (int i = 0; i < c.childCount; i += BuildingEvery)
                {
                    Transform b = c.GetChild(i);

                    // Pousser la lumiere HORS du volume : posee au pivot elle se retrouve dans
                    // le mesh et n'eclaire plus la rue. On sort par la face, dans une direction
                    // deterministe tiree de l'index.
                    float r = 4f;
                    var rend = b.GetComponentInChildren<Renderer>();
                    if (rend != null) r = Mathf.Max(rend.bounds.extents.x, rend.bounds.extents.z) + 1.5f;
                    float ang = (i * 37) % 360 * Mathf.Deg2Rad;
                    var off = new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang)) * r;

                    Make(root, "Neon_bat_" + made, b.position + off + Vector3.up * BuildingHeight,
                         Palette[(n * 3 + 1) % Palette.Length], BuildingIntensity, BuildingRange);
                    n++; made++;
                }
            }
            return made;
        }

        static void Make(Transform root, string name, Vector3 pos, Color col, float intensity, float range)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.transform.position = pos;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = col;
            l.intensity = intensity;
            l.range = range;
            l.shadows = LightShadows.None;   // 100+ lumieres a ombres = mort du framerate
        }

        static GameObject Find()
        {
            foreach (var r in EditorSceneManager.GetActiveScene().GetRootGameObjects())
                if (r.name == ContainerName) return r;
            return null;
        }
    }
}
