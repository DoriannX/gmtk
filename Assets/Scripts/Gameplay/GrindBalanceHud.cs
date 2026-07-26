using UnityEngine;

namespace Gameplay
{
    // HUD d'equilibre du grind, d'apres la maquette Figma "In Game equilibre" : ARC ouvert
    // (anneau de 90 deg, trou central a 65 % du rayon) cerne de noir, degrade vert au centre
    // -> rouge aux extremites, aiguille noire fine posee sur un point-pivot.
    // Aiguille = ArcadeCarController.GrindBalanceNorm (0 = haut/centre, +/-1 = seuil de chute).
    // Juice : pop-in elastique a l'accroche, shake + nappe rouge floue ("In Game equilibre
    // chute") quand on approche du seuil. Visible seulement en grind. OnGUI -> zero Canvas.
    public class GrindBalanceHud : MonoBehaviour
    {
        // Maquette : cadran 184x184 (rayon 92), trou a 0.65, arc de 90 deg, aiguille de 50.
        private static readonly Color ArcGreen = new Color(0.023f, 0.938f, 0.084f);   // #06EF15
        private static readonly Color ArcRed = new Color(0.885f, 0.021f, 0.021f);     // #E20505

        [SerializeField] private ArcadeCarController car;
        [SerializeField] private float radius = 92f;
        [SerializeField] private float innerRatio = 0.65f;         // trou central, cf maquette
        [SerializeField] private float needleWidth = 2f;
        [SerializeField] private float maxAngle = 45f;             // angle aiguille au seuil (deg)
        [SerializeField] private Vector2 screenOffset = new Vector2(230f, -20f); // / centre moto a l'ecran
        [SerializeField] private float dial = -1f;                // sens de l'aiguille (-1/+1)

        private Camera cam;
        private Texture2D disc, glow;
        private bool wasRail;
        private float pop;        // 0..1 anim d'apparition
        private float danger;     // 0..1 proximite du seuil
        private float shakeT;

        private void Awake()
        {
            if (car == null) car = GetComponent<ArcadeCarController>();
            if (car == null) car = FindAnyObjectByType<ArcadeCarController>();
            disc = MakeDisc(128, false);
            glow = MakeDisc(128, true);
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
            if (RunEnd.Finished || UI.MenuFlow.Blocking) return;   // ecran de fin / menus
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

            // Nappe rouge floue de l'etat "chute" : dans la maquette c'est un arc rouge
            // flouté (15 px) POSE DERRIERE le cadran. Un disque degrade fait le meme office
            // ici et coute un seul quad.
            Glow(gx, gy - radius * 0.35f, radius * 1.5f, new Color(0.885f, 0.021f, 0.021f, 0.75f * danger));

            float inner = radius * innerRatio;
            // L'ARC est peint en tranches : OnGUI ne trace que des quads. Une tranche noire
            // legerement plus large et plus longue passe d'abord -> c'est elle qui fait le
            // cerne 3 px de la maquette (stroke centre : 1.5 de chaque cote).
            const int seg = 40;
            float step = 2f * maxAngle / (seg - 1);
            float chord = 2f * radius * Mathf.Sin(step * 0.5f * Mathf.Deg2Rad) + 1.5f;
            for (int i = 0; i < seg; i++)
            {
                float u = i / (seg - 1f);
                float ang = Mathf.Lerp(-maxAngle, maxAngle, u);
                RotRect(gx, gy, ang, inner - 1.5f, radius + 1.5f, chord + 3f, Color.black);
            }
            for (int i = 0; i < seg; i++)
            {
                float u = i / (seg - 1f);
                float ang = Mathf.Lerp(-maxAngle, maxAngle, u);
                // vert au centre, rouge aux deux bouts (degrade angulaire de la maquette)
                float edge = Mathf.Abs(u * 2f - 1f);
                RotRect(gx, gy, ang, inner, radius, chord, Color.Lerp(ArcGreen, ArcRed, Mathf.Pow(edge, 0.8f)));
            }
            // Bouchons noirs aux deux extremites : les tranches laissent le degrade a nu sur
            // les tranches de bout, le cerne doit faire le tour complet.
            RotRect(gx, gy, -maxAngle - step * 0.5f, inner - 1.5f, radius + 1.5f, 3f, Color.black);
            RotRect(gx, gy, maxAngle + step * 0.5f, inner - 1.5f, radius + 1.5f, 3f, Color.black);

            // AIGUILLE : trait noir fin (2 px) avec son ombre portee, plante dans un point-pivot.
            float ang2 = norm * maxAngle;
            float len = radius * 0.54f;                 // 50 px pour un rayon de 92, cf maquette
            RotRect(gx, gy + 3f, ang2, 0f, len, needleWidth, new Color(0f, 0f, 0f, 0.25f));
            RotRect(gx, gy, ang2, 0f, len, needleWidth, Color.black);
            Blit(gx, gy, 3f, Color.black);              // point aiguille (diametre 6)

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

        private void Blit(float cx, float cy, float r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(new Rect(cx - r, cy - r, r * 2f, r * 2f), disc);
        }

        // Tache douce : c'est ce qui remplace le flou de 15 px de la maquette (etat "chute").
        private void Glow(float cx, float cy, float r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(new Rect(cx - r, cy - r, r * 2f, r * 2f), glow);
        }

        private static float EaseOutBack(float x)
        {
            const float c1 = 2.2f, c3 = c1 + 1f;
            float p = x - 1f;
            return 1f + c3 * p * p * p + c1 * p * p;
        }

        // `soft` : degrade radial (tache floue) au lieu du disque a bord net.
        private static Texture2D MakeDisc(int size, bool soft)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                    float a = soft ? Mathf.Clamp01(1f - d / r) : Mathf.Clamp01(r - d);
                    if (soft) a *= a;                       // retombee douce, pas de bord visible
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }
    }
}
