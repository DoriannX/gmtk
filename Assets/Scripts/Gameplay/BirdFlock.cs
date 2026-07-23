using UnityEngine;
using DG.Tweening;

namespace Gameplay
{
    // Banc d'oiseaux qui traverse le ciel tranquillement. Le banc (root) derive
    // en avant avec un cap qui ondule doucement ; chaque oiseau garde sa place
    // en formation, tangue un peu et bat des ailes lentement. Wrap dans une
    // boite autour du point de depart pour tourner en boucle.
    // Oiseaux procéduraux (silhouettes en V) si aucun prefab fourni : zero asset.
    [DisallowMultipleComponent]
    public class BirdFlock : MonoBehaviour
    {
        [Header("Banc")]
        [SerializeField] private int count = 9;
        [SerializeField] private float speed = 6f;
        [SerializeField] private float spread = 7f;      // etalement de la formation
        [SerializeField] private float turnAmplitude = 25f; // deg de louvoiement du cap
        [SerializeField] private float turnPeriod = 8f;  // s par oscillation de cap
        [SerializeField] private Vector3 area = new Vector3(120f, 0f, 120f); // demi-boite de wrap

        [Header("Oiseau")]
        [Tooltip("Optionnel. Vide = silhouette procédurale.")]
        [SerializeField] private GameObject birdPrefab;
        [SerializeField] private float birdScale = 1f;
        [SerializeField] private Vector2 flapPeriod = new Vector2(0.35f, 0.6f);
        [SerializeField] private float bobHeight = 0.4f;
        [SerializeField] private Color silhouette = new Color(0.18f, 0.18f, 0.22f);

        private Vector3 startPos;
        private float heading;      // yaw courant (rad de phase pour l'ondulation)
        private float baseYaw;
        private Material mat;        // une seule instance partagee par tous les oiseaux
        private readonly System.Collections.Generic.List<Tween> tweens = new();

        private void Start()
        {
            startPos = transform.position;
            baseYaw = transform.eulerAngles.y;
            if (birdPrefab == null)
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = silhouette };
            for (int i = 0; i < count; i++) SpawnBird(i);
        }

        private void SpawnBird(int i)
        {
            GameObject bird = birdPrefab != null ? Instantiate(birdPrefab, transform) : ProcBird.Build(mat);
            bird.name = $"Bird{i}";
            bird.transform.SetParent(transform, false);
            bird.transform.localPosition = FormationOffset(i);
            bird.transform.localScale = Vector3.one * birdScale;

            // tangage doux, déphasé par oiseau (pas de Random-based-on-time : ok en Start)
            float phase = i * 0.37f;
            tweens.Add(bird.transform.DOLocalMoveY(bird.transform.localPosition.y + bobHeight,
                    Random.Range(1.4f, 2.2f))
                .SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetDelay(phase));

            FlapWings(bird.transform, Random.Range(flapPeriod.x, flapPeriod.y), phase);
        }

        // V loose : birds ecartes lateralement + recules par rang
        private Vector3 FormationOffset(int i)
        {
            int rank = (i + 1) / 2;
            float side = (i % 2 == 0) ? 1f : -1f;
            return new Vector3(side * rank * spread * 0.5f, Random.Range(-1f, 1f), -rank * spread * 0.7f);
        }

        private void FlapWings(Transform bird, float period, float phase)
        {
            var wl = bird.Find("WingL");
            var wr = bird.Find("WingR");
            if (wl == null || wr == null) return; // prefab sans ailes nommees : tant pis
            tweens.Add(wl.DOLocalRotate(new Vector3(0, 0, 35f), period)
                .SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetDelay(phase));
            tweens.Add(wr.DOLocalRotate(new Vector3(0, 0, -35f), period)
                .SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetDelay(phase));
        }

        private void Update()
        {
            // cap = cap de base + ondulation sinus (louvoiement tranquille)
            heading += Time.deltaTime / Mathf.Max(0.1f, turnPeriod) * Mathf.PI * 2f;
            float yaw = baseYaw + Mathf.Sin(heading) * turnAmplitude;
            transform.rotation = Quaternion.Euler(0, yaw, 0);
            transform.position += transform.forward * (speed * Time.deltaTime);

            // wrap dans la boite autour du depart
            Vector3 o = transform.position - startPos;
            if (Mathf.Abs(o.x) > area.x) transform.position -= new Vector3(Mathf.Sign(o.x) * area.x * 2f, 0, 0);
            if (Mathf.Abs(o.z) > area.z) transform.position -= new Vector3(0, 0, Mathf.Sign(o.z) * area.z * 2f);
        }

        private void OnDestroy()
        {
            foreach (var t in tweens) t.Kill();
            tweens.Clear();
        }
    }
}
