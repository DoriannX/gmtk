using UnityEngine;

namespace Gameplay.City
{
    // Type d'une cellule de la grille ville.
    //  Road    = rue (le trafic roule dessus, waypoints derives de la).
    //  Block   = pate de maison generique (rempli de batiments a l'etape 3).
    //  Special = spot reserve (enigme + collectible), laisse ouvert / hand-author.
    public enum CellType { Road, Block, Special }

    // SEULE SOURCE DE VERITE de la map (cf. docs/map-generation-plan.md).
    // Route, waypoints, blocs et spawns derivent TOUS de cette grille.
    //
    // Layout dessine en ASCII (une string par ligne, ligne 0 = haut) :
    //   '#' = Road   '.' = Block   '*' = Special   (tout autre char -> Block)
    // Serializable : se pose comme champ sur un MonoBehaviour (CityGridAuthoring).
    [System.Serializable]
    public class CityGrid
    {
        [Tooltip("Layout ASCII, une ligne = une rangee (ligne 0 en haut). " +
                 "'#'=rue  '.'=pate de maison  '*'=spot enigme.")]
        [SerializeField]
        private string[] rows = DefaultRows;

        [Tooltip("Taille monde d'une cellule (unites). Une rue fait 1 cellule de large.")]
        [SerializeField]
        private float cellSize = 11f;

        // Layout par defaut : quartier 7x5 = 35 blocs, 5 spots enigme (**).
        public static readonly string[] DefaultRows =
        {
            "######################",
            "#..#..#**#..#..#**#..#",
            "#..#..#**#..#..#**#..#",
            "######################",
            "#..#..#..#..#..#..#..#",
            "#..#..#..#..#..#..#..#",
            "######################",
            "#..#..#..#**#..#..#..#",
            "#..#..#..#**#..#..#..#",
            "######################",
            "#**#..#..#..#..#..#..#",
            "#**#..#..#..#..#..#..#",
            "######################",
            "#..#..#..#..#..#..#**#",
            "#..#..#..#..#..#..#**#",
            "######################",
        };

        public float CellSize => cellSize;
        public int Height => rows == null ? 0 : rows.Length;
        public int Width
        {
            get
            {
                int w = 0;
                if (rows != null)
                    foreach (string r in rows)
                        if (r != null && r.Length > w) w = r.Length;
                return w;
            }
        }

        // Type de la cellule (x = colonne, y = rangee depuis le haut).
        // Hors grille ou char inconnu -> Block (fond neutre).
        public CellType At(int x, int y)
        {
            if (rows == null || y < 0 || y >= rows.Length) return CellType.Block;
            string row = rows[y];
            if (row == null || x < 0 || x >= row.Length) return CellType.Block;
            switch (row[x])
            {
                case '#': return CellType.Road;
                case '*': return CellType.Special;
                default: return CellType.Block;
            }
        }

        // Centre monde d'une cellule. Grille centree sur l'origine, dans le plan XZ,
        // y=0. Rangee 0 (haut ASCII) -> +Z ; colonne 0 -> -X.
        public Vector3 CellToWorld(int x, int y)
        {
            float wx = (x - (Width - 1) * 0.5f) * cellSize;
            float wz = ((Height - 1) * 0.5f - y) * cellSize;
            return new Vector3(wx, 0f, wz);
        }

        // Inverse de CellToWorld : cellule contenant un point (espace local grille).
        // Hors grille -> clamp aux bords (pas d'exception).
        public void WorldToCell(Vector3 local, out int x, out int y)
        {
            x = Mathf.RoundToInt(local.x / cellSize + (Width - 1) * 0.5f);
            y = Mathf.RoundToInt((Height - 1) * 0.5f - local.z / cellSize);
        }

        // Type de la cellule contenant un point (espace local grille).
        public CellType TypeAtLocal(Vector3 local)
        {
            WorldToCell(local, out int x, out int y);
            return At(x, y);
        }
    }
}
