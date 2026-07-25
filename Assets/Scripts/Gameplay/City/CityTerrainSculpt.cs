using UnityEngine;

namespace Gameplay.City
{
    // COUCHE SCULPTEE du terrain : le relief peint a la main, en metres, ajoute au bruit de
    // CityTerrain. Un asset a part et pas un champ de la scene -- des dizaines de milliers de
    // valeurs dans le .unity rendraient chaque diff illisible, sur un projet ou les scenes se
    // merge a plusieurs.
    //
    // Pourquoi un ScriptableObject et pas la Texture2D qu'il y avait avant : L'ANNULATION.
    // Undo copie les CHAMPS SERIALISES d'un objet ; les pixels d'une Texture2D n'en sont pas,
    // et Ctrl+Z ne rendait donc rien (mesure : somme des valeurs a -9195 au lieu de -7193 apres
    // annulation). Ici la donnee EST un champ serialise, donc l'annulation native marche sans
    // rien de special a ecrire.
    //
    // Stockee en OCTETS et non en float[] : Unity serialise un tableau d'octets en base64 sur
    // une seule ligne, quand 360 000 flottants donneraient autant de lignes de YAML -- soit un
    // asset de plusieurs megaoctets a relire et reecrire a chaque sauvegarde.
    public class CityTerrainSculpt : ScriptableObject
    {
        [Tooltip("Cote de la grille, en cellules. La grille est carree et centree sur le CityTerrain.")]
        public int resolution;
        [SerializeField, HideInInspector] private byte[] data;

        public int Count => resolution * resolution;

        public static CityTerrainSculpt Create(int resolution)
        {
            var s = CreateInstance<CityTerrainSculpt>();
            s.resolution = Mathf.Max(2, resolution);
            s.data = new byte[s.Count * 4];
            return s;
        }

        // Copie de travail en flottants. L'appelant la garde en cache : la generation du sol
        // interroge la hauteur des dizaines de milliers de fois, on ne va pas deballer a chaque
        // coup.
        public float[] Read()
        {
            int n = Count;
            var values = new float[n];
            if (data != null && data.Length >= n * 4) System.Buffer.BlockCopy(data, 0, values, 0, n * 4);
            return values;
        }

        // Remet toute la couche a plat. Le bruit procedural de CityTerrain n'est pas concerne :
        // les deux sont des couches independantes qui s'additionnent.
        public void Clear() => data = new byte[Count * 4];

        // Nombre de cellules reellement peintes. Meme seuil que SculptBounds : un residu d'un
        // millimetre laisse par un effacement ne compte pas comme du relief.
        public int PaintedCount()
        {
            var v = Read();
            int n = 0;
            for (int i = 0; i < v.Length; i++) if (Mathf.Abs(v[i]) > 0.02f) n++;
            return n;
        }

        // Empreinte du contenu. Sert a l'editeur pour savoir si une annulation a touche a cette
        // couche : sans elle, il faudrait regenerer le sol a CHAQUE Ctrl+Z du projet, y compris
        // pour un deplacement d'objet qui n'a rien a voir. Balayage complet et pas echantillonne :
        // 1,4 Mo se parcourent en quelques millisecondes, et un echantillonnage raterait un jour
        // un coup de pinceau.
        public int Checksum()
        {
            if (data == null) return 0;
            unchecked
            {
                int h = 17;
                for (int i = 0; i < data.Length; i++) h = h * 31 + data[i];
                return h;
            }
        }

        public void Write(float[] values)
        {
            int n = Mathf.Min(Count, values != null ? values.Length : 0);
            if (n <= 0) return;
            if (data == null || data.Length != Count * 4) data = new byte[Count * 4];
            System.Buffer.BlockCopy(values, 0, data, 0, n * 4);
        }

        // Reecrit la grille a une autre taille en CONSERVANT ce qui est peint. Les deux grilles
        // sont centrees au meme endroit et partagent la taille de cellule, donc le report est un
        // simple decalage entier : aucun reechantillonnage, aucune perte.
        public void Resize(int res)
        {
            res = Mathf.Max(2, res);
            if (res == resolution) return;

            float[] old = Read();
            int oldRes = resolution;
            var grown = new float[res * res];
            int off = (res - oldRes) / 2;   // negatif si on retrecit : les bords sont rognes
            for (int j = 0; j < oldRes; j++)
            {
                int dj = j + off;
                if (dj < 0 || dj >= res) continue;
                for (int i = 0; i < oldRes; i++)
                {
                    int di = i + off;
                    if (di < 0 || di >= res) continue;
                    grown[dj * res + di] = old[j * oldRes + i];
                }
            }

            resolution = res;
            data = new byte[res * res * 4];
            Write(grown);
        }
    }
}
