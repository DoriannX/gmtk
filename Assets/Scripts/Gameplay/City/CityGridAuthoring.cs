using UnityEngine;

namespace Gameplay.City
{
    // Porte la CityGrid dans la scene et la dessine en gizmos (preview d'auteur).
    // Etape 0 : rien n'est genere encore, on visualise juste le layout pour le
    // valider avant de poser routes/batiments. Le generateur (etape 1) lira ce
    // meme composant.
    [ExecuteAlways]
    public class CityGridAuthoring : MonoBehaviour
    {
        [SerializeField] private CityGrid grid = new CityGrid();

        [Header("Preview gizmos")]
        [SerializeField] private bool drawGizmos = true;
        [SerializeField] private Color roadColor = new Color(0.35f, 0.35f, 0.38f);
        [SerializeField] private Color blockColor = new Color(0.85f, 0.72f, 0.5f);   // tan chaud
        [SerializeField] private Color specialColor = new Color(1f, 0.55f, 0.15f);   // orange enigme

        public CityGrid Grid => grid;

        private void OnDrawGizmos()
        {
            if (!drawGizmos || grid == null) return;
            float cs = grid.CellSize;
            Vector3 size = new Vector3(cs * 0.92f, 0.1f, cs * 0.92f);
            Vector3 origin = transform.position;

            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    switch (grid.At(x, y))
                    {
                        case CellType.Road: Gizmos.color = roadColor; break;
                        case CellType.Special: Gizmos.color = specialColor; break;
                        default: Gizmos.color = blockColor; break;
                    }
                    Gizmos.DrawCube(origin + grid.CellToWorld(x, y), size);
                }
            }
        }
    }
}
