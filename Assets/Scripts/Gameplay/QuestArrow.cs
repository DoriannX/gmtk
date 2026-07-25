using DG.Tweening;
using UnityEngine;

namespace Gameplay
{
    // Fleche 3D diegetique qui flotte au-dessus du joueur et pointe vers la zone de quete.
    // YAW SEULEMENT : elle reste toujours a plat, jamais inclinee vers le haut ou le bas,
    // meme si la cible est plus haute/basse -> la direction se lit comme sur une boussole.
    // Pas parentee au joueur : la moto part en vrille pendant les tricks/grinds et la fleche
    // doit rester droite. On la place en LateUpdate, apres le mouvement du joueur.
    // Mesh genere en code (aucun asset a importer).
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class QuestArrow : MonoBehaviour
    {
        [SerializeField] private Transform follow;        // joueur (auto : ArcadeCarController)
        [SerializeField] private QuestZone target;        // zone visee ; null = pas de guidage
        [SerializeField] private bool autoFindTarget;     // off quand un script pilote la cible
        [SerializeField] private float height = 2.8f;     // au-dessus du joueur
        [SerializeField] private float turnSpeed = 9f;    // lissage de la rotation
        [SerializeField] private float bobAmount = 0.18f; // flottement vertical
        [SerializeField] private float bobSpeed = 2.4f;
        [SerializeField] private bool hideInsideZone = true;

        [Header("Mesh")]
        [SerializeField] private float length = 1.5f;
        [SerializeField] private float headLength = 0.65f;
        [SerializeField] private float shaftWidth = 0.34f;
        [SerializeField] private float headWidth = 0.9f;
        [SerializeField] private float thickness = 0.16f;

        private Quaternion aim = Quaternion.identity;
        private bool shown = true;
        private Tween showTween;

        // UNE seule fleche pour toute la partie, spawnee depuis Resources/QuestArrow.prefab :
        // aucune scene n'a besoin d'en poser une, et une quete en prefab n'a rien a brancher.
        // Une fleche deja posee en scene s'inscrit toute seule dans OnEnable et sert d'instance.
        private static QuestArrow instance;

        public static QuestArrow Instance
        {
            get
            {
                if (instance == null)
                    instance = Instantiate(Resources.Load<GameObject>("QuestArrow")).GetComponent<QuestArrow>();
                return instance;
            }
        }

        private void OnEnable()
        {
            if (instance == null) instance = this;
            GetComponent<MeshFilter>().sharedMesh = BuildMesh();
            if (follow == null)
            {
                var car = FindAnyObjectByType<ArcadeCarController>();
                if (car != null) follow = car.transform;
            }
            if (target == null && autoFindTarget) target = FindAnyObjectByType<QuestZone>();
            ApplyVisibility(true);   // pas de tween au demarrage : on est deja a la bonne taille
        }

        private void OnDisable()
        {
            if (instance == this) instance = null;
        }

        public QuestZone Target => target;

        // Change de cible a chaud (le script de quete s'en sert). null = la fleche se range.
        public void SetTarget(QuestZone z)
        {
            target = z;
            if (Application.isPlaying) ApplyVisibility(false);
        }

        private void OnValidate()
        {
            if (isActiveAndEnabled) GetComponent<MeshFilter>().sharedMesh = BuildMesh();
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying || follow == null) return;

            Vector3 p = follow.position + Vector3.up * height;
            p.y += Mathf.Sin(Time.time * bobSpeed) * bobAmount;
            transform.position = p;

            if (target != null)
            {
                // direction MISE A PLAT : on annule la composante verticale avant de viser,
                // sinon la fleche piquerait du nez des que la zone est plus bas que le joueur.
                Vector3 d = target.transform.position - follow.position;
                d.y = 0f;
                if (d.sqrMagnitude > 0.0001f)
                    aim = Quaternion.LookRotation(d.normalized, Vector3.up);
            }
            transform.rotation = Quaternion.Slerp(transform.rotation, aim,
                1f - Mathf.Exp(-turnSpeed * Time.deltaTime));

            ApplyVisibility(false);
        }

        private bool WantVisible() =>
            target != null && target.isActiveAndEnabled && !target.Completed
            && !(hideInsideZone && target.PlayerInside);

        private void ApplyVisibility(bool instant)
        {
            bool v = WantVisible();
            if (!instant && v == shown) return;
            shown = v;
            showTween?.Kill();
            if (instant) { transform.localScale = v ? Vector3.one : Vector3.zero; return; }
            showTween = transform.DOScale(v ? Vector3.one : Vector3.zero, v ? 0.45f : 0.25f)
                .SetEase(v ? Ease.OutBack : Ease.InBack).SetTarget(this);
        }

        private void OnDestroy() => DOTween.Kill(this);

        // Fleche pointant vers +Z, a plat dans le plan XZ, extrudee sur Y.
        // Profil ferme de 7 points -> 2 capots (3 tris chacun) + 7 quads de cote.
        private Mesh BuildMesh()
        {
            float sw = shaftWidth * 0.5f, hw = headWidth * 0.5f;
            float shaftEnd = Mathf.Max(0.01f, length - headLength);
            float t = thickness * 0.5f;

            // contour, sens horaire vu de dessus
            Vector2[] o =
            {
                new(-sw, 0f), new(-sw, shaftEnd), new(-hw, shaftEnd), new(0f, length),
                new(hw, shaftEnd), new(sw, shaftEnd), new(sw, 0f),
            };

            var verts = new Vector3[o.Length * 2];
            for (int i = 0; i < o.Length; i++)
            {
                verts[i] = new Vector3(o[i].x, t, o[i].y);              // dessus
                verts[i + o.Length] = new Vector3(o[i].x, -t, o[i].y);  // dessous
            }

            // Capot : hampe (quad) + tete (triangle). Pas de fan depuis un sommet du contour :
            // le decrochement de la tete le rendrait invalide.
            int n = o.Length;
            var tris = new System.Collections.Generic.List<int>(48);
            void Cap(int off, bool up)
            {
                int[] f = { 0, 1, 5, 0, 5, 6, 2, 3, 4 };  // quad hampe (2 tris) + tri tete
                for (int i = 0; i < f.Length; i += 3)
                {
                    if (up) { tris.Add(off + f[i]); tris.Add(off + f[i + 1]); tris.Add(off + f[i + 2]); }
                    else { tris.Add(off + f[i + 2]); tris.Add(off + f[i + 1]); tris.Add(off + f[i]); }
                }
            }
            Cap(0, true);
            Cap(n, false);

            for (int i = 0; i < n; i++)   // parois laterales (winding aligne sur les capots)
            {
                int a = i, b = (i + 1) % n;
                tris.Add(a); tris.Add(b + n); tris.Add(b);
                tris.Add(a); tris.Add(a + n); tris.Add(b + n);
            }

            var m = new Mesh { name = "QuestArrow" };
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
