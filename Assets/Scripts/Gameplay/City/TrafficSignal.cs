using UnityEngine;

namespace Gameplay.City
{
    // Feux tricolores GLOBAUX synchronises : tous les carrefours partagent la meme
    // horloge. Cycle = NS vert -> NS orange -> EW vert -> EW orange. Deriver la phase
    // de Time.time (pas d'etat a serialiser, un seul cycle pour toute la ville).
    // Les voitures interrogent CanGo(gridDir) a l'approche d'un carrefour ; les
    // panneaux lumineux (TrafficLightVisual) lisent NsGreen/EwGreen pour la couleur.
    public static class TrafficSignal
    {
        public const float Green = 6f;
        public const float Yellow = 1.8f;
        public static float Cycle => 2f * (Green + Yellow);

        public static bool NsGreen(float t)
        {
            float m = Mathf.Repeat(t, Cycle);
            return m < Green;
        }

        public static bool EwGreen(float t)
        {
            float m = Mathf.Repeat(t, Cycle);
            return m >= Green + Yellow && m < Green + Yellow + Green;
        }

        // Vrai si l'axe de deplacement a le vert (orange/rouge -> false = stop).
        // gridDir.y != 0 => axe Nord-Sud ; gridDir.x != 0 => axe Est-Ouest.
        public static bool CanGo(Vector2Int gridDir)
        {
            float t = Time.time;
            return gridDir.y != 0 ? NsGreen(t) : EwGreen(t);
        }
    }

    // Tete de feu (prop Prop_TrafficLight_A) : la voiture voit 3 ampoules (rouge/
    // orange/vert) sur des sous-mailles distinctes. On allume (emission) celle de
    // l'etat courant selon l'axe (NS ou EW). Purement visuel, pose par le CityBuilder.
    public class TrafficLightVisual : MonoBehaviour
    {
        [SerializeField] private Renderer rend;   // mesh unique du prop (multi-sous-mailles)
        [SerializeField] private int redIdx = -1, amberIdx = -1, greenIdx = -1; // slots des ampoules
        [SerializeField] private bool nsAxis = true; // sert l'axe Nord-Sud (sinon Est-Ouest)

        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        private static readonly Color Red = new Color(1f, 0.12f, 0.08f);
        private static readonly Color Amber = new Color(1f, 0.62f, 0.05f);
        private static readonly Color Green = new Color(0.2f, 1f, 0.28f);
        private MaterialPropertyBlock mpb;

        public void Bind(Renderer r, int red, int amber, int green, bool ns)
        { rend = r; redIdx = red; amberIdx = amber; greenIdx = green; nsAxis = ns; }

        private void Awake()
        {
            mpb = new MaterialPropertyBlock();
            if (rend == null) return;
            // active l'emission sur les 3 materiaux d'ampoule (instances) pour qu'elles
            // puissent s'allumer ; le reste (metal/carter) reste inchange.
            var mats = rend.materials; // instances propres a ce prop
            EnableEmission(mats, redIdx); EnableEmission(mats, amberIdx); EnableEmission(mats, greenIdx);
            rend.materials = mats;
        }

        private static void EnableEmission(Material[] mats, int i)
        {
            if (i < 0 || i >= mats.Length || mats[i] == null) return;
            mats[i].EnableKeyword("_EMISSION");
            mats[i].globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }

        private void Update()
        {
            if (rend == null) return;
            int s = State(Time.time); // 0 rouge, 1 orange, 2 vert
            Glow(redIdx, s == 0, Red);
            Glow(amberIdx, s == 1, Amber);
            Glow(greenIdx, s == 2, Green);
        }

        // Etat de l'ampoule pour cet axe. NS : vert -> orange -> (rouge pendant EW).
        // EW : rouge (pendant NS) -> vert -> orange.
        private int State(float t)
        {
            float m = Mathf.Repeat(t, TrafficSignal.Cycle);
            float g = TrafficSignal.Green, y = TrafficSignal.Yellow;
            if (nsAxis)
            {
                if (m < g) return 2;
                if (m < g + y) return 1;
                return 0;
            }
            if (m < g + y) return 0;
            if (m < g + y + g) return 2;
            return 1;
        }

        private void Glow(int idx, bool on, Color c)
        {
            if (idx < 0) return;
            rend.GetPropertyBlock(mpb, idx);
            mpb.SetColor(EmissionColor, on ? c * 3.2f : Color.black);
            rend.SetPropertyBlock(mpb, idx);
        }
    }
}
