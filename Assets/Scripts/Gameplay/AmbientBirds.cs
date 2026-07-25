using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;

namespace Gameplay
{
    // Oiseaux cosmetiques isoles qui planent en cercles lents en altitude,
    // disperses autour de ce transform. Purement decoratif : aucune interaction,
    // aucun collider. Complete le BirdFlock (banc) par des rapaces solitaires.
    // Silhouettes procedurales (ProcBird), une seule Material partagee.
    [DisallowMultipleComponent]
    public class AmbientBirds : MonoBehaviour
    {
        [Header("Nombre / dispersion")]
        [SerializeField] private int count = 6;
        [SerializeField] private float scatterRadius = 90f;      // etalement horizontal autour du transform
        [SerializeField] private Vector2 altitude = new Vector2(22f, 38f);

        [Header("Vol plane")]
        [SerializeField] private Vector2 orbitRadius = new Vector2(8f, 22f);
        [SerializeField] private Vector2 orbitPeriod = new Vector2(12f, 24f); // s par tour (lent)
        [SerializeField] private float bobHeight = 1.2f;
        [SerializeField] private Vector2 flapPeriod = new Vector2(0.4f, 0.7f);

        [Header("Look")]
        [Tooltip("Optionnel. Vide = silhouette procedurale (ProcBird). Rempli = ce modele (ex: SkyBird).")]
        [SerializeField] private GameObject birdPrefab;
        [SerializeField] private float birdScale = 1.6f;
        [SerializeField] private Color silhouette = new Color(0.18f, 0.18f, 0.22f);

        private class Bird { public Transform t; public Vector3 center; public float radius, speed, angle, bobPhase; }
        private readonly List<Bird> birds = new();
        private readonly List<Tween> tweens = new();
        private Material mat;

        private void Start()
        {
            if (birdPrefab == null)
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = silhouette };
            for (int i = 0; i < count; i++) Spawn(i);
        }

        private void Spawn(int i)
        {
            var go = birdPrefab != null ? Instantiate(birdPrefab) : ProcBird.Build(mat);
            go.name = $"AmbientBird{i}";
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * birdScale;

            Vector2 flat = Random.insideUnitCircle * scatterRadius;
            birds.Add(new Bird
            {
                t = go.transform,
                center = transform.position + new Vector3(flat.x, Random.Range(altitude.x, altitude.y), flat.y),
                radius = Random.Range(orbitRadius.x, orbitRadius.y),
                // sens horaire ou anti-horaire au hasard
                speed = Mathf.PI * 2f / Random.Range(orbitPeriod.x, orbitPeriod.y) * (Random.value < 0.5f ? 1f : -1f),
                angle = Random.Range(0f, Mathf.PI * 2f),
                bobPhase = Random.Range(0f, Mathf.PI * 2f),
            });

            float fp = Random.Range(flapPeriod.x, flapPeriod.y);
            var wl = ProcBird.FindDeep(go.transform, "WingL");
            var wr = ProcBird.FindDeep(go.transform, "WingR");
            if (wl != null) tweens.Add(wl.DOLocalRotate(new Vector3(0, 0, 32f), fp).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetDelay(i * 0.13f));
            if (wr != null) tweens.Add(wr.DOLocalRotate(new Vector3(0, 0, -32f), fp).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetDelay(i * 0.13f));
        }

        private void Update()
        {
            float t = Time.time;
            foreach (var b in birds)
            {
                b.angle += b.speed * Time.deltaTime;
                Vector3 offset = new Vector3(
                    Mathf.Cos(b.angle) * b.radius,
                    Mathf.Sin(t * 0.8f + b.bobPhase) * bobHeight,
                    Mathf.Sin(b.angle) * b.radius);
                b.t.position = b.center + offset;
                // face a la tangente du cercle (sens selon speed)
                Vector3 vel = new Vector3(-Mathf.Sin(b.angle), 0f, Mathf.Cos(b.angle)) * Mathf.Sign(b.speed);
                if (vel.sqrMagnitude > 0.001f) b.t.rotation = Quaternion.LookRotation(vel, Vector3.up);
            }
        }

        private void OnDestroy()
        {
            foreach (var tw in tweens) tw.Kill();
            tweens.Clear();
        }
    }
}
