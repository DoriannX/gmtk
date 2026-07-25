using UnityEngine;

namespace Gameplay.City
{
    // CHAMP DE HAUTEUR de la ville. Composant d'AUTEUR : rien ne tourne au runtime, il ne fait
    // que serialiser les reglages du bruit avec la scene.
    //
    // C'est la SOURCE UNIQUE de la hauteur du terrain, et c'est tout le point du fichier. Le sol
    // genere et les noeuds de route la lisent tous les deux ; si chacun avait sa propre idee du
    // relief, la route flotterait ou s'enterrerait des le premier reglage change. Personne ne
    // doit deriver la hauteur d'autre chose (un raycast sur le sol genere, par exemple : le sol
    // est CONSTRUIT a partir d'ici, le lire en retour boucle).
    //
    // Le relief ne vit qu'a l'interieur d'un disque : au-dela, hauteur nulle, franchement. Le sol
    // genere s'appuie dessus pour garder son anneau exterieur plat -- un anneau de 8 quads ne
    // peut pas suivre une bosse, et un raccord entre une grille fine bossue et un grand quad plat
    // se fendrait. Regle `radius` pour couvrir la ville, pas plus.
    public class CityTerrain : MonoBehaviour
    {
        [Header("Bruit")]
        [Tooltip("Meme graine = meme relief.")]
        public int seed = 1337;
        [Tooltip("Amplitude crete a crete, en metres. 3-8 m : de la butte roulable, pas une " +
                 "montagne.")]
        public float amplitude = 6f;
        [Tooltip("Taille d'une colline, en metres. Petit = terrain tourmente, gros = houle lente.")]
        public float scale = 90f;
        [Tooltip("Couches de bruit. 1 = collines lisses. 3 = du detail dans la pente.")]
        [Range(1, 4)] public int octaves = 3;

        [Header("Emprise")]
        [Tooltip("Rayon du relief autour de cet objet, en metres. Au-dela c'est plat.")]
        public float radius = 220f;
        [Tooltip("Largeur de la retombee vers le plat, en metres, prise A L'INTERIEUR du rayon.")]
        public float fade = 60f;

        [Header("Sculpt")]
        [Tooltip("Couche peinte a la main, AJOUTEE au bruit. Creee par l'outil Sculpter le " +
                 "terrain. Elle vit dans un asset a part et pas dans la scene : des dizaines de " +
                 "milliers de valeurs serialisees en YAML rendraient chaque diff de scene " +
                 "illisible.")]
        public CityTerrainSculpt sculpt;
        [Tooltip("Taille d'une cellule sculptee, en metres. Inutile de descendre sous le pas de " +
                 "la grille du sol (2 m) : le maillage ne saurait pas restituer le detail.")]
        public float sculptCell = 2f;
        [Tooltip("Rayon PEIGNABLE autour de cet objet, en metres. Volontairement decorrele du " +
                 "rayon du bruit : le relief procedural doit rester une butte autour de la ville, " +
                 "alors qu'on veut pouvoir sculpter loin. Le sol ne paie que ce qui est " +
                 "reellement peint, pas ce rayon -- le monter ne coute donc rien tant qu'on " +
                 "n'a pas peint au large.")]
        public float sculptRadius = 600f;

        // Hauteur du terrain en un point du monde. Le seul point d'entree : tout le reste du
        // projet passe par la.
        public float Height(float x, float z) => Noise(x, z) + Sculpt(x, z);

        // Le BRUIT seul, sans la couche peinte. Separe parce que le pinceau en a besoin : pour
        // amener un point a une hauteur voulue il doit connaitre la part qui ne lui appartient
        // pas, et la relire via Height() lui ferait lire sa propre ecriture.
        private float Noise(float x, float z)
        {
            float k = Falloff(x, z);
            if (k <= 0f) return transform.position.y;

            // Les octaves sont decalees par la graine plutot que mises a l'echelle : deux octaves
            // au meme endroit dans le champ de Perlin se sommeraient en une seule bosse plus
            // haute au lieu d'ajouter du detail.
            float h = 0f, amp = 1f, freq = 1f / Mathf.Max(1f, scale), norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                Vector2 off = Offset(o);
                // Perlin vaut 0.5 pile aux coordonnees entieres : recentre sur zero, sinon la
                // ville entiere se souleve d'une demi-amplitude.
                h += amp * (Mathf.PerlinNoise(off.x + x * freq, off.y + z * freq) - 0.5f) * 2f;
                norm += amp;
                amp *= 0.5f;
                freq *= 2f;
            }

            return transform.position.y + (h / norm) * amplitude * 0.5f * k;
        }

        public float Height(Vector3 world) => Height(world.x, world.z);

        // ---------------------------------------------------------------- couche sculptee
        //
        // La couche vit dans une Texture2D RFloat plutot que dans un float[] serialise : un
        // tableau de 48 000 flottants dans la scene, c'est autant de lignes de YAML a chaque
        // sauvegarde, sur un projet ou les scenes se merge a plusieurs. Une texture est un asset
        // a part, binaire, qui ne se merge jamais a la main -- et sa lecture bilineaire, on
        // l'ecrit en quatre lignes.
        //
        // La grille est CENTREE SUR CE TRANSFORM et sa taille vient de la texture, pas de
        // `radius` : sinon changer le rayon apres avoir peint deplacerait tout le sculpt.

        [System.NonSerialized] private float[] cache;
        [System.NonSerialized] private CityTerrainSculpt cacheOf;
        [System.NonSerialized] private int cacheRes;

        public float Sculpt(float x, float z)
        {
            if (sculpt == null || sculptCell <= 0f) return 0f;
            if (cacheOf != sculpt || cache == null) RefreshCache();

            if (cacheRes < 2) return 0f;
            float half = cacheRes * sculptCell * 0.5f;
            float fx = (x - (transform.position.x - half)) / sculptCell - 0.5f;
            float fz = (z - (transform.position.z - half)) / sculptCell - 0.5f;
            int i = Mathf.FloorToInt(fx), j = Mathf.FloorToInt(fz);
            if (i < -1 || j < -1 || i >= cacheRes || j >= cacheRes) return 0f;

            float tx = fx - i, tz = fz - j;
            return Mathf.Lerp(Mathf.Lerp(Texel(i, j), Texel(i + 1, j), tx),
                              Mathf.Lerp(Texel(i, j + 1), Texel(i + 1, j + 1), tx), tz);
        }

        // Hors grille = zero, et surtout PAS la valeur du bord repetee : un clamp etalerait le
        // dernier texel jusqu'a l'infini, donc une butte peinte au bord deviendrait un plateau
        // sans fin.
        private float Texel(int i, int j)
            => (i < 0 || j < 0 || i >= cacheRes || j >= cacheRes) ? 0f : cache[j * cacheRes + i];

        // La generation du sol appelle Height() des dizaines de milliers de fois : on ne va pas
        // interroger la texture a chaque coup.
        private void RefreshCache()
        {
            cacheRes = sculpt.resolution;
            cache = sculpt.Read();
            cacheOf = sculpt;
        }

        public void InvalidateSculpt() { cache = null; cacheOf = null; }

        // Alloue la couche EN MEMOIRE. C'est l'outil d'edition qui la sauve en asset : ce
        // fichier-ci est du code runtime, il n'a pas le droit de toucher a l'AssetDatabase.
        // Cote de la couche, en texels, pour les reglages courants.
        public int SculptResolution
        {
            get
            {
                return Mathf.Clamp(
                    Mathf.CeilToInt(Mathf.Max(radius, sculptRadius) * 2f / Mathf.Max(0.25f, sculptCell)),
                    16, 2048);
            }
        }

        // Change la taille de la couche EN CONSERVANT ce qui est peint. Rend faux s'il n'y avait
        // rien a faire. C'est ce qui permet d'agrandir le rayon peignable apres coup sans jeter
        // le travail deja fait.
        public bool ResizeSculpt(int res)
        {
            if (sculpt == null || res == sculpt.resolution) return false;
            sculpt.Resize(res);
            InvalidateSculpt();
            return true;
        }

        // Alloue la couche EN MEMOIRE. C'est l'OUTIL qui la sauve en asset : ce fichier-ci est du
        // code runtime, il n'a pas le droit de toucher a l'AssetDatabase.
        //
        // Dimensionnee sur `sculptRadius` et surtout PAS sur `radius` : le second borne le bruit
        // procedural, le premier borne le geste de l'auteur, et il n'y a aucune raison que les
        // deux soient la meme chose.
        public CityTerrainSculpt CreateSculptLayer() => CityTerrainSculpt.Create(SculptResolution);

        // Emprise XZ de ce qui a REELLEMENT ete peint, marge comprise. Faux si rien n'est peint.
        //
        // C'est ce qui rend le grand rayon peignable gratuit : le generateur de sol n'etend sa
        // grille fine que la ou il y a quelque chose a montrer. Tant qu'on n'a pas sculpte au
        // large, la carte pese exactement ce qu'elle pesait avant.
        public bool SculptBounds(out Bounds b)
        {
            b = default(Bounds);
            if (sculpt == null || sculptCell <= 0f) return false;
            if (cacheOf != sculpt || cache == null) RefreshCache();

            int minI = int.MaxValue, maxI = int.MinValue, minJ = int.MaxValue, maxJ = int.MinValue;
            for (int j = 0; j < cacheRes; j++)
                for (int i = 0; i < cacheRes; i++)
                {
                    // Seuil et pas != 0 : un residu d'un millimetre laisse par un coup de pinceau
                    // efface ne doit pas faire tesseller un kilometre carre.
                    if (Mathf.Abs(cache[j * cacheRes + i]) < 0.02f) continue;
                    if (i < minI) minI = i;
                    if (i > maxI) maxI = i;
                    if (j < minJ) minJ = j;
                    if (j > maxJ) maxJ = j;
                }
            if (minI > maxI) return false;

            // Deux cellules de marge : l'echantillonnage bilineaire deborde d'un texel, et le sol
            // a besoin d'un rang de sommets deja revenu a plat pour raccorder sans pli.
            float half = cacheRes * sculptCell * 0.5f;
            float ox = transform.position.x - half, oz = transform.position.z - half;
            var lo = new Vector3(ox + (minI - 2) * sculptCell, 0f, oz + (minJ - 2) * sculptCell);
            var hi = new Vector3(ox + (maxI + 3) * sculptCell, 0f, oz + (maxJ + 3) * sculptCell);
            b = new Bounds((lo + hi) * 0.5f, hi - lo);
            return true;
        }

        // Creuse (delta < 0) ou souleve (delta > 0) sous un disque.
        public void SculptDisc(Vector3 world, float brush, float delta, float hardness)
        {
            if (!BeginWrite(world, brush, out int i0, out int j0, out int i1, out int j1,
                            out float ox, out float oz)) return;

            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    float cx = ox + (i + 0.5f) * sculptCell, cz = oz + (j + 0.5f) * sculptCell;
                    float dx = cx - world.x, dz = cz - world.z;
                    float w = Weight(Mathf.Sqrt(dx * dx + dz * dz) / brush, hardness);
                    if (w > 0f) cache[j * cacheRes + i] += delta * w;
                }
            Flush();
        }

        // Pose une PENTE PROPRE entre deux points : la hauteur devient l'interpolation des deux
        // hauteurs actuelles le long du segment, sur une bande de `brush`.
        //
        // Aucun instantane a prendre malgre l'ecriture en place : la hauteur courante d'une
        // cellule vaut Noise + sa propre valeur, deux termes qu'on connait sans relire les
        // voisines. C'est tout l'interet d'avoir separe Noise() de Height().
        public void SculptRamp(Vector3 a, Vector3 b, float brush, float hardness)
        {
            Vector3 ab = b - a;
            ab.y = 0f;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-4f) return;

            float ya = Height(a.x, a.z), yb = Height(b.x, b.z);
            Vector3 mid = (a + b) * 0.5f;
            float reach = Mathf.Sqrt(len2) * 0.5f + brush;
            if (!BeginWrite(mid, reach, out int i0, out int j0, out int i1, out int j1,
                            out float ox, out float oz)) return;

            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    float cx = ox + (i + 0.5f) * sculptCell, cz = oz + (j + 0.5f) * sculptCell;
                    float t = Mathf.Clamp01(((cx - a.x) * ab.x + (cz - a.z) * ab.z) / len2);
                    float px = a.x + ab.x * t, pz = a.z + ab.z * t;
                    float dx = cx - px, dz = cz - pz;
                    float w = Weight(Mathf.Sqrt(dx * dx + dz * dz) / brush, hardness);
                    if (w <= 0f) continue;

                    int k = j * cacheRes + i;
                    float current = Noise(cx, cz) + cache[k];
                    cache[k] += (Mathf.Lerp(ya, yb, t) - current) * w;
                }
            Flush();
        }

        // Rectangle de cellules touche par un geste, cache pret a ecrire. Faux si la couche
        // n'existe pas ou si le geste tombe entierement hors grille.
        private bool BeginWrite(Vector3 world, float reach, out int i0, out int j0,
                                out int i1, out int j1, out float ox, out float oz)
        {
            i0 = j0 = i1 = j1 = 0;
            ox = oz = 0f;
            if (sculpt == null || sculptCell <= 0f || reach <= 0f) return false;
            if (cacheOf != sculpt || cache == null) RefreshCache();

            float half = cacheRes * sculptCell * 0.5f;
            ox = transform.position.x - half;
            oz = transform.position.z - half;
            i0 = Mathf.Max(0, Mathf.FloorToInt((world.x - reach - ox) / sculptCell));
            i1 = Mathf.Min(cacheRes - 1, Mathf.CeilToInt((world.x + reach - ox) / sculptCell));
            j0 = Mathf.Max(0, Mathf.FloorToInt((world.z - reach - oz) / sculptCell));
            j1 = Mathf.Min(cacheRes - 1, Mathf.CeilToInt((world.z + reach - oz) / sculptCell));
            return i0 <= i1 && j0 <= j1;
        }

        private void Flush()
        {
            sculpt.Write(cache);
        }

        // 1 au coeur, 0 au bord. `hard` deplace le PLATEAU : 0 donne une cloche (pinceau qui
        // creuse), 0.6 un large plateau a bords doux -- ce qu'il faut pour une rampe, dont le
        // milieu doit etre PLAT et pas bombe.
        private static float Weight(float t, float hard)
        {
            if (t >= 1f) return 0f;
            hard = Mathf.Clamp(hard, 0f, 0.95f);
            if (t <= hard) return 1f;
            float u = 1f - (t - hard) / (1f - hard);
            return u * u * (3f - 2f * u);
        }

        // 1 au coeur, 0 au bord du disque. En cosinus et pas lineaire : une retombee lineaire
        // laisse une arete nette au raccord avec le plat, qui se lit comme un pli du sol.
        private float Falloff(float x, float z)
        {
            Vector3 c = transform.position;
            float d = Mathf.Sqrt((x - c.x) * (x - c.x) + (z - c.z) * (z - c.z));
            float inner = Mathf.Max(0f, radius - Mathf.Max(0.01f, fade));
            if (d <= inner) return 1f;
            if (d >= radius) return 0f;
            float t = (d - inner) / (radius - inner);
            return 0.5f + 0.5f * Mathf.Cos(t * Mathf.PI);
        }

        private Vector2 Offset(int octave)
        {
            // Hash entier -> deux decalages stables. Le champ de Perlin d'Unity se repete tous
            // les 256, d'ou le modulo : inutile de partir a 10000 unites.
            uint h = (uint)(seed * 73856093) ^ (uint)((octave + 1) * 19349663);
            h ^= h >> 13; h *= 2654435761u; h ^= h >> 16;
            return new Vector2(h % 256u, (h / 256u) % 256u);
        }

        // Le composant est unique par scene ; les outils le cherchent par ce biais plutot que de
        // se passer une reference de main en main. Absent = ville plate, et c'est un cas normal
        // (toutes les scenes n'ont pas de relief), pas une erreur.
        public static CityTerrain Find()
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindFirstObjectByType<CityTerrain>();
#else
            return Object.FindObjectOfType<CityTerrain>();
#endif
        }

        // Hauteur du terrain, ou `fallback` si la scene n'a pas de relief.
        public static float HeightAt(CityTerrain terrain, Vector3 world, float fallback)
            => terrain != null ? terrain.Height(world.x, world.z) : fallback;

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.4f, 1f, 0.6f, 0.5f);
            DrawDisc(radius);
            Gizmos.color = new Color(0.4f, 1f, 0.6f, 0.2f);
            DrawDisc(Mathf.Max(0f, radius - fade));
        }

        private void DrawDisc(float r)
        {
            Vector3 c = transform.position;
            Vector3 prev = c + new Vector3(r, 0f, 0f);
            for (int i = 1; i <= 48; i++)
            {
                float a = i / 48f * Mathf.PI * 2f;
                Vector3 p = c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
        }
    }
}
