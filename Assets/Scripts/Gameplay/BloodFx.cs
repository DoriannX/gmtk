using UnityEngine;
using DG.Tweening;

namespace Gameplay
{
    // VFX gore cartoon spawn 100% au runtime : burst de globs 3D rouges qui
    // giclent + flaque au sol (sprite splat dessine par code). Aucun asset a
    // cabler dans l'inspecteur, on appelle juste BloodFx.Spawn(pos, dir).
    public static class BloodFx
    {
        private static Material globMat;   // materiau rouge partage des globs
        private static Mesh sphereMesh;    // mesh sphere recupere d'un primitive
        private static Sprite puddleSprite; // texture splat generee une fois

        public static void Spawn(Vector3 pos, Vector3 dir)
        {
            SpawnBurst(pos + Vector3.up * 0.8f);
            SpawnPuddle(pos);
        }

        // Fontaine de barbaque : Emit manuel d'un paquet de spheres, gravite +
        // vitesse radiale -> arc cartoon. Auto-destruction apres la retombee.
        private static void SpawnBurst(Vector3 pos)
        {
            var go = new GameObject("BloodBurst");
            go.transform.position = pos;
            var ps = go.AddComponent<ParticleSystem>();
            // un PS fraichement ajoute joue deja (playOnAwake) : on l'arrete avant
            // de toucher main.duration, sinon "Setting the duration while playing".
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = false;
            main.playOnAwake = false;
            main.duration = 0.4f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.7f, 1.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(4f, 9f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.12f, 0.4f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.85f, 0.8f, 0.8f), Color.white); // le rouge vient du materiau
            main.gravityModifier = 2.2f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 200;

            var emission = ps.emission;
            emission.enabled = false; // on gicle a la main, pas de flux continu

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.12f;

            var col = ps.colorOverLifetime; // fade en fin de vie
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.7f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(grad);

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Mesh;
            r.mesh = GetSphereMesh();
            r.material = GetGlobMat();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            ps.Play();
            ps.Emit(Random.Range(28, 40));
            Object.Destroy(go, 3f);
        }

        // Flaque : sprite splat pose a plat au sol (raycast pour coller au sol),
        // yaw aleatoire, pop OutBack. Reste en place (proto -> pas de cleanup).
        // ponytail: pas de pooling ni fade, a ajouter si ca sature la scene.
        private static void SpawnPuddle(Vector3 pos)
        {
            Vector3 ground = pos;
            if (Physics.Raycast(pos + Vector3.up, Vector3.down, out var hit, 5f))
                ground = hit.point;
            ground.y += 0.02f;

            var go = new GameObject("BloodPuddle");
            go.transform.position = ground;
            go.transform.rotation = Quaternion.Euler(90f, Random.Range(0f, 360f), 0f);

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = GetPuddleSprite();
            sr.color = new Color(0.5f, 0.02f, 0.02f, 0.96f);
            sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            float size = Random.Range(1.6f, 2.4f);
            go.transform.localScale = Vector3.zero;
            go.transform.DOScale(size, 0.35f).SetEase(Ease.OutBack);
        }

        private static Material GetGlobMat()
        {
            if (globMat != null) return globMat;
            var sh = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                     ?? Shader.Find("Sprites/Default");
            globMat = new Material(sh);
            var red = new Color(0.55f, 0.02f, 0.02f);
            globMat.color = red;
            globMat.SetColor("_BaseColor", red); // URP
            return globMat;
        }

        private static Mesh GetSphereMesh()
        {
            if (sphereMesh != null) return sphereMesh;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            // Destroy est differe/no-op hors play -> DestroyImmediate en edit mode
            if (Application.isPlaying) Object.Destroy(tmp);
            else Object.DestroyImmediate(tmp);
            return sphereMesh;
        }

        // Splat dessine par code : blob central a rayon irregulier (quelques
        // harmoniques) + gouttelettes satellites. Cache -> genere une fois.
        private static Sprite GetPuddleSprite()
        {
            if (puddleSprite != null) return puddleSprite;

            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];

            Vector2 c = new Vector2(size * 0.5f, size * 0.5f);
            float baseR = size * 0.33f;
            float a0 = Random.value * 6.28f, a1 = Random.value * 6.28f, a2 = Random.value * 6.28f;
            float RadAt(float ang) => baseR * (1f
                + 0.18f * Mathf.Sin(ang * 3f + a0)
                + 0.11f * Mathf.Sin(ang * 5f + a1)
                + 0.07f * Mathf.Sin(ang * 8f + a2));

            var red = new Color32(120, 6, 6, 255);
            var clear = new Color32(120, 6, 6, 0);

            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x - c.x, dy = y - c.y;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float rr = RadAt(Mathf.Atan2(dy, dx));
                    // bord adouci sur ~5px
                    float a = Mathf.Clamp01((rr - d) / 5f);
                    px[y * size + x] = a > 0f
                        ? new Color32(red.r, red.g, red.b, (byte)(a * 255))
                        : clear;
                }

            // gouttelettes autour
            for (int k = 0; k < 7; k++)
            {
                float ang = Random.value * 6.28f;
                float dist = baseR * Random.Range(1.0f, 1.55f);
                Vector2 dc = c + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * dist;
                float dr = Random.Range(3f, 9f);
                int x0 = Mathf.Max(0, (int)(dc.x - dr)), x1 = Mathf.Min(size - 1, (int)(dc.x + dr));
                int y0 = Mathf.Max(0, (int)(dc.y - dr)), y1 = Mathf.Min(size - 1, (int)(dc.y + dr));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x, y), dc);
                        float a = Mathf.Clamp01((dr - d) / 2f);
                        if (a <= 0f) continue;
                        int i = y * size + x;
                        byte na = (byte)Mathf.Max(px[i].a, (byte)(a * 255));
                        px[i] = new Color32(red.r, red.g, red.b, na);
                    }
            }

            tex.SetPixels32(px);
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            puddleSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
            return puddleSprite;
        }
    }
}
