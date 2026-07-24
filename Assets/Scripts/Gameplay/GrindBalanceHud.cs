using UnityEngine;

namespace Gameplay
{
    // HUD d'equilibre du grind : CADRAN ROND cartoon avec aiguille, affiche a cote de la moto.
    // Aiguille = ArcadeCarController.GrindBalanceNorm (0 = haut/centre, +/-1 = seuil de chute).
    // Track colore vert(centre) -> rouge(bords). Juice : pop-in elastique a l'accroche, shake +
    // flash rouge quand on approche du seuil. Visible seulement en grind. OnGUI -> zero Canvas.
    public class GrindBalanceHud : MonoBehaviour
    {
        [SerializeField] private ArcadeCarController car;
        [SerializeField] private float radius = 92f;
        [SerializeField] private float needleWidth = 10f;
        [SerializeField] private float maxAngle = 72f;             // angle aiguille au seuil (deg)
        [SerializeField] private Vector2 screenOffset = new Vector2(230f, -20f); // / centre moto a l'ecran
        [SerializeField] private float dial = -1f;                // sens de l'aiguille (-1/+1)

        private Camera cam;
        private Texture2D disc;
        private bool wasRail;
        private float pop;        // 0..1 anim d'apparition
        private float danger;     // 0..1 proximite du seuil
        private float shakeT;

        private void Awake()
        {
            if (car == null) car = GetComponent<ArcadeCarController>();
            if (car == null) car = FindAnyObjectByType<ArcadeCarController>();
            disc = MakeDisc(128);
        }

        private void Update()
        {
            bool r = car != null && car.OnRail;
            if (r && !wasRail) pop = 0f;      // (re)accroche -> rejoue le pop
            wasRail = r;
            float ddt = Time.unscaledDeltaTime;
            pop = r ? Mathf.Min(1f, pop + ddt * 3.2f) : 0f;
            float target = r ? Mathf.InverseLerp(0.55f, 1f, Mathf.Abs(car.GrindBalanceNorm)) : 0f;
            danger = Mathf.Lerp(danger, target, ddt * 12f);
            shakeT += ddt * 47f;
        }

        private void OnGUI()
        {
            if (car == null || !car.OnRail) return;
            if (cam == null) cam = Camera.main;
            if (cam == null) return;
            Vector3 sp = cam.WorldToScreenPoint(car.transform.position);
            if (sp.z < 0f) return;

            float norm = Mathf.Clamp(car.GrindBalanceNorm, -1.05f, 1.05f) * dial;
            float pulse = 1f + 0.06f * Mathf.Sin(shakeT * 1.7f) * danger;

            // shake cartoon en zone rouge
            float sh = danger * 7f;
            float gx = sp.x + screenOffset.x + Mathf.Sin(shakeT) * sh;
            float gy = (Screen.height - sp.y) + screenOffset.y + Mathf.Cos(shakeT * 1.3f) * sh;

            // pop-in elastique (EaseOutBack) * pulse danger
            float s = EaseOutBack(pop) * pulse;
            Matrix4x4 m0 = GUI.matrix;
            GUIUtility.ScaleAroundPivot(new Vector2(s, s), new Vector2(gx, gy));

            // halo (rouge quand danger, sinon doux)
            Color halo = Color.Lerp(new Color(0.5f, 0.7f, 1f, 0.18f), new Color(1f, 0.2f, 0.15f, 0.5f), danger);
            Blit(gx, gy, radius * 1.32f, halo);

            // corps : anneau clair puis disque sombre -> bord net cartoon
            Blit(gx, gy, radius * 1.06f, new Color(0.98f, 0.98f, 1f, 0.95f));       // contour blanc
            Blit(gx, gy, radius, new Color(0.16f, 0.17f, 0.28f, 0.98f));            // fond
            Blit(gx, gy, radius * 0.9f, new Color(0.10f, 0.11f, 0.19f, 1f));        // creux

            // TRACK colore vert(centre) -> rouge(bords)
            int seg = 26;
            for (int i = 0; i < seg; i++)
            {
                float u = i / (seg - 1f);
                float ang = Mathf.Lerp(-maxAngle, maxAngle, u);
                float edge = Mathf.Abs(u * 2f - 1f);
                Color c = edge < 0.66f
                    ? Color.Lerp(new Color(0.3f, 1f, 0.45f), new Color(1f, 0.85f, 0.2f), edge / 0.66f)
                    : Color.Lerp(new Color(1f, 0.85f, 0.2f), new Color(1f, 0.2f, 0.15f), (edge - 0.66f) / 0.34f);
                RotRect(gx, gy, ang, radius * 0.62f, radius * 0.9f, radius * 0.14f, c); // pastille sur le rim
            }

            // reperes de chute (gros traits rouges) + repere centre
            RotRect(gx, gy, maxAngle, radius * 0.5f, radius, 7f, new Color(1f, 0.15f, 0.1f, 1f));
            RotRect(gx, gy, -maxAngle, radius * 0.5f, radius, 7f, new Color(1f, 0.15f, 0.1f, 1f));
            RotRect(gx, gy, 0f, radius * 0.5f, radius, 4f, new Color(0.5f, 1f, 0.6f, 0.9f));

            // AIGUILLE : ombre portee + corps colore + bout rond
            float ang2 = norm * maxAngle;
            float len = radius * 0.8f;
            RotRect(gx + 3f, gy + 4f, ang2, 0f, len, needleWidth, new Color(0f, 0f, 0f, 0.35f)); // ombre
            Color needleCol = Color.Lerp(new Color(0.35f, 1f, 0.5f), new Color(1f, 0.25f, 0.15f), Mathf.Abs(norm));
            RotRect(gx, gy, ang2, 0f, len, needleWidth, needleCol);
            NeedleTip(gx, gy, ang2, len, needleWidth * 1.5f, needleCol);

            // moyeu
            Blit(gx, gy, 15f, new Color(1f, 1f, 1f, 1f));
            Blit(gx, gy, 10f, new Color(0.16f, 0.17f, 0.28f, 1f));

            GUI.matrix = m0;
            GUI.color = Color.white;
        }

        // rectangle radial : de 'from' a 'to' (distance au centre), largeur w, pivote de angleDeg.
        private void RotRect(float cx, float cy, float angleDeg, float from, float to, float w, Color c)
        {
            Matrix4x4 m = GUI.matrix;
            GUIUtility.RotateAroundPivot(angleDeg, new Vector2(cx, cy));
            GUI.color = c;
            GUI.DrawTexture(new Rect(cx - w * 0.5f, cy - to, w, to - from), Texture2D.whiteTexture);
            GUI.matrix = m;
        }

        private void NeedleTip(float cx, float cy, float angleDeg, float len, float d, Color c)
        {
            Matrix4x4 m = GUI.matrix;
            GUIUtility.RotateAroundPivot(angleDeg, new Vector2(cx, cy));
            GUI.color = c;
            GUI.DrawTexture(new Rect(cx - d * 0.5f, cy - len - d * 0.5f, d, d), disc);
            GUI.matrix = m;
        }

        private void Blit(float cx, float cy, float r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(new Rect(cx - r, cy - r, r * 2f, r * 2f), disc);
        }

        private static float EaseOutBack(float x)
        {
            const float c1 = 2.2f, c3 = c1 + 1f;
            float p = x - 1f;
            return 1f + c3 * p * p * p + c1 * p * p;
        }

        private static Texture2D MakeDisc(int size)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - d)));
                }
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }
    }
}
