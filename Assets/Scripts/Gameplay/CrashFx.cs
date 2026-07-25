using UnityEngine;
using DG.Tweening;

namespace Gameplay
{
    // VFX de collision cartoon, 100% runtime (aucun asset a cabler) :
    //  - gerbe d'etincelles/etoiles (particules mesh) ;
    //  - onde de choc (anneau qui s'agrandit et fond) ;
    //  - mot comic qui pop ("POW!", "SBAM!"...) en billboard.
    // On appelle juste CrashFx.Play(point, hard) ; hard = 0..1 = intensite.
    public static class CrashFx
    {
        private static Material sparkMat;
        private static Mesh sphereMesh;
        private static Sprite ringSprite;
        private static Font comicFont;

        private static readonly string[] Words =
            { "POW!", "BAM!", "SBAM!", "BOINK!", "VLAN!", "BADABOUM!", "CRUNCH!", "SPLONK!" };
        private static readonly Color[] WordCols =
        {
            new Color(1f, 0.85f, 0.15f), new Color(1f, 0.4f, 0.2f),
            new Color(1f, 0.25f, 0.35f), new Color(0.35f, 0.8f, 1f),
        };

        public static void Play(Vector3 point, float hard)
        {
            hard = Mathf.Clamp01(hard);
            SparkBurst(point, hard);              // toujours, mais nb de particules ~ hard
            if (hard > 0.25f) Shockwave(point, hard); // onde reservee aux vrais chocs
            if (hard > 0.35f) ComicPop(point, hard);  // mot comic seulement si ca claque
        }

        // Gerbe d'etoiles : Emit manuel, vitesse radiale + gravite -> arc cartoon.
        private static void SparkBurst(Vector3 pos, float hard)
        {
            var go = new GameObject("CrashSparks");
            go.transform.position = pos;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = false;
            main.playOnAwake = false;
            main.duration = 0.5f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.35f, 0.85f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(4f, 8f + hard * 8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.22f + hard * 0.2f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.95f, 0.5f), new Color(1f, 0.6f, 0.15f));
            main.gravityModifier = 1.8f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 300;

            var emission = ps.emission; emission.enabled = false;
            var shape = ps.shape; shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = 0.15f;

            var col = ps.colorOverLifetime; col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.6f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(grad);

            var sz = ps.sizeOverLifetime; sz.enabled = true;
            sz.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 1f), new Keyframe(1f, 0.2f)));

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Mesh;
            r.mesh = GetSphereMesh();
            r.material = GetSparkMat();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            ps.Play();
            ps.Emit(Mathf.RoundToInt(3 + hard * 40)); // frole = 3 etincelles, plein pot = ~43
            Object.Destroy(go, 2f);
        }

        // Onde de choc : anneau plat billboard qui s'ouvre et fond.
        private static void Shockwave(Vector3 pos, float hard)
        {
            var cam = Camera.main != null ? Camera.main : Object.FindAnyObjectByType<Camera>();
            var go = new GameObject("Shockwave");
            go.transform.position = pos;
            if (cam) go.transform.rotation = Quaternion.LookRotation(-cam.transform.forward, cam.transform.up);

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = GetRingSprite();
            sr.color = new Color(1f, 0.95f, 0.7f, 0.9f);
            sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            float size = 1.4f + hard * 3.2f;
            go.transform.localScale = Vector3.one * 0.2f;
            go.transform.DOScale(size, 0.35f).SetEase(Ease.OutCubic);
            sr.DOFade(0f, 0.35f).SetEase(Ease.InQuad).OnComplete(() => Object.Destroy(go));
        }

        // Mot comic qui pop, monte, tourne et disparait.
        private static void ComicPop(Vector3 pos, float hard)
        {
            var cam = Camera.main != null ? Camera.main : Object.FindAnyObjectByType<Camera>();
            var go = new GameObject("ComicPop");
            go.transform.position = pos + Vector3.up * 0.6f;
            if (cam) go.transform.rotation = cam.transform.rotation; // face cam, texte lisible (pas miroir)
            go.transform.Rotate(0f, 0f, Random.Range(-14f, 14f));

            var tm = go.AddComponent<TextMesh>();
            tm.text = Words[Random.Range(0, Words.Length)];
            tm.font = GetComicFont();
            tm.fontSize = 90;
            tm.fontStyle = FontStyle.Bold;
            tm.characterSize = 0.05f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = WordCols[Random.Range(0, WordCols.Length)];
            var mr = go.GetComponent<MeshRenderer>();
            mr.material = tm.font.material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            float big = 1f + hard * 0.9f;
            go.transform.localScale = Vector3.zero;
            var seq = DOTween.Sequence();
            seq.Append(go.transform.DOScale(big, 0.22f).SetEase(Ease.OutBack, 2.2f));
            seq.Join(go.transform.DOMoveY(go.transform.position.y + 0.8f, 0.6f).SetEase(Ease.OutQuad));
            seq.AppendInterval(0.18f);
            seq.Append(go.transform.DOScale(0f, 0.18f).SetEase(Ease.InBack));
            seq.OnComplete(() => Object.Destroy(go));
        }

        // ---- ressources cachees ----

        private static Material GetSparkMat()
        {
            if (sparkMat != null) return sparkMat;
            var sh = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Sprites/Default");
            sparkMat = new Material(sh);
            var c = new Color(1f, 0.85f, 0.3f);
            sparkMat.color = c; sparkMat.SetColor("_BaseColor", c);
            return sparkMat;
        }

        private static Mesh GetSphereMesh()
        {
            if (sphereMesh != null) return sphereMesh;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            if (Application.isPlaying) Object.Destroy(tmp); else Object.DestroyImmediate(tmp);
            return sphereMesh;
        }

        private static Font GetComicFont()
        {
            if (comicFont != null) return comicFont;
            comicFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return comicFont;
        }

        // Anneau dessine par code (annulus a bord adouci), genere une fois.
        private static Sprite GetRingSprite()
        {
            if (ringSprite != null) return ringSprite;
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            Vector2 c = new Vector2(size * 0.5f, size * 0.5f);
            float rMid = size * 0.4f;   // rayon de l'anneau
            float band = size * 0.09f;  // demi-epaisseur
            var white = new Color(1f, 1f, 1f, 1f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c);
                    float a = Mathf.Clamp01(1f - Mathf.Abs(d - rMid) / band);
                    var col = white; col.a = a;
                    px[y * size + x] = col;
                }
            tex.SetPixels32(px);
            tex.Apply(); tex.wrapMode = TextureWrapMode.Clamp;
            ringSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
            return ringSprite;
        }
    }
}
