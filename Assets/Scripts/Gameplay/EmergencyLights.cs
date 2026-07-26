using UnityEngine;

namespace Gameplay
{
    // Gyrophare : fait clignoter les slots de neon d'un vehicule de secours, un groupe
    // apres l'autre (rouge puis cyan sur l'ambulance).
    //
    // Le clignotement passe par un MaterialPropertyBlock POSE PAR SOUS-MESH, jamais par
    // le materiau : Veh_Neon_Cyan est partage avec le bus, l'ecrire ferait clignoter le
    // bus aussi. Le MPB reste local a ce Renderer.
    //
    // La teinte "allumee" est la couleur du materiau telle qu'elle est dans le projet
    // (base > 1, calee sur le seuil de bloom) ; le script ne fait que la multiplier, il
    // ne decide d'aucune couleur. Regler les couleurs se fait donc toujours dans les .mat.
    public class EmergencyLights : MonoBehaviour
    {
        [Header("Cible")]
        [SerializeField] private Renderer target;
        // Noms de materiaux (sans le .mat) formant chaque groupe. Les deux groupes
        // alternent : quand A flashe, B est eteint.
        [SerializeField] private string[] groupA = { "Veh_Neon_Rouge" };
        [SerializeField] private string[] groupB = { "Veh_Neon_Cyan" };

        [Header("Rythme")]
        [SerializeField] private float period = 1.1f;          // cycle complet, les deux groupes
        [SerializeField] private int flashesPerGroup = 2;      // double flash facon strobe reel
        [SerializeField, Range(0.05f, 0.9f)] private float duty = 0.34f; // part allumee de chaque flash
        [SerializeField] private float tail = 0.55f;           // 0 = coupure nette, 1 = extinction molle
        [SerializeField] private float phaseOffset;            // decale ce vehicule par rapport aux autres

        [Header("Niveaux")]
        [SerializeField, Range(0f, 1f)] private float off = 0.16f; // eteint : sous le seuil de bloom
        [SerializeField] private float on = 1.25f;                 // allume : leger overdrive

        [Header("Lumiere projetee")]
        // Vraies lumieres, pour que le flash morde sur la route et les facades.
        [SerializeField] private Light[] lightsA;
        [SerializeField] private Light[] lightsB;
        [SerializeField] private float lightIntensity = 5f;
        // Sous ce niveau la lumiere est carrement DESACTIVEE : le pipeline ne compte que
        // 8 lumieres additionnelles par objet, une lampe eteinte qui occupe un slot
        // ferait sauter un neon de facade a cote.
        [SerializeField] private float lightCutoff = 0.04f;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        private int[] slotsA;
        private int[] slotsB;
        private Color[] baseA;
        private Color[] baseB;
        private MaterialPropertyBlock mpb;

        private void Awake()
        {
            if (target == null) target = GetComponentInChildren<Renderer>();
            if (target == null) { enabled = false; return; }

            mpb = new MaterialPropertyBlock();
            CollectSlots(groupA, out slotsA, out baseA);
            CollectSlots(groupB, out slotsB, out baseB);

            if (slotsA.Length == 0 && slotsB.Length == 0)
            {
                Debug.LogWarning($"{name} : EmergencyLights n'a trouve aucun slot de neon, effet desactive.", this);
                enabled = false;
                return;
            }
            // Depart aleatoire : deux ambulances cote a cote ne doivent pas etre en phase.
            if (Mathf.Approximately(phaseOffset, 0f)) phaseOffset = Random.value * period;
        }

        // Repere les index de sous-mesh dont le materiau porte un des noms demandes, et
        // memorise leur couleur d'origine (= la couleur "allumee" de reference).
        private void CollectSlots(string[] names, out int[] slots, out Color[] colors)
        {
            var mats = target.sharedMaterials;
            var foundSlots = new System.Collections.Generic.List<int>();
            var foundColors = new System.Collections.Generic.List<Color>();
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null || !m.HasProperty(BaseColorId)) continue;
                foreach (var n in names)
                {
                    if (m.name != n) continue;
                    foundSlots.Add(i);
                    foundColors.Add(m.GetColor(BaseColorId));
                    break;
                }
            }
            slots = foundSlots.ToArray();
            colors = foundColors.ToArray();
        }

        private void LateUpdate()
        {
            float t = Mathf.Repeat(Time.time + phaseOffset, period) / period; // 0..1 sur le cycle
            // Premiere moitie du cycle au groupe A, seconde au groupe B.
            float levelA = t < 0.5f ? Strobe(t * 2f) : 0f;
            float levelB = t < 0.5f ? 0f : Strobe((t - 0.5f) * 2f);

            Apply(slotsA, baseA, levelA);
            Apply(slotsB, baseB, levelB);
            ApplyLights(lightsA, levelA);
            ApplyLights(lightsB, levelB);
        }

        private void ApplyLights(Light[] lights, float level)
        {
            if (lights == null) return;
            float intensity = level * lightIntensity;
            bool lit = level > lightCutoff;
            foreach (var l in lights)
            {
                if (l == null) continue;
                if (l.enabled != lit) l.enabled = lit;
                if (lit) l.intensity = intensity;
            }
        }

        // Suite de flashes courts sur la demi-periode. `tail` etale l'extinction : a 0 le
        // flash coupe net, a 1 il retombe en douceur (le bloom le lit comme une remanence).
        private float Strobe(float phase01)
        {
            float f = phase01 * flashesPerGroup;
            float local = f - Mathf.Floor(f);
            if (local < duty) return 1f;
            if (tail <= 0f) return 0f;
            float fade = (local - duty) / (1f - duty);       // 0 juste apres le flash -> 1 en fin de creneau
            return Mathf.Clamp01(1f - fade / tail);
        }

        private void Apply(int[] slots, Color[] colors, float level)
        {
            float mul = Mathf.Lerp(off, on, level);
            for (int i = 0; i < slots.Length; i++)
            {
                target.GetPropertyBlock(mpb, slots[i]);
                var c = colors[i] * mul;
                c.a = colors[i].a;
                mpb.SetColor(BaseColorId, c);
                target.SetPropertyBlock(mpb, slots[i]);
            }
        }

        private void OnDisable()
        {
            // On rend les slots a leur couleur de materiau, sinon ils restent figes
            // sur le dernier niveau applique.
            if (mpb == null || target == null) return;
            Restore(slotsA);
            Restore(slotsB);
        }

        private void Restore(int[] slots)
        {
            foreach (var s in slots) target.SetPropertyBlock(null, s);
        }
    }
}
