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

        // Hauteur du terrain en un point du monde. Le seul point d'entree : tout le reste du
        // projet passe par la.
        public float Height(float x, float z)
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
