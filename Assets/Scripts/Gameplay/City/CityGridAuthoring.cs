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

        // Acces rapide pour l'IA des pietons (evite un FindObject par frame).
        public static CityGridAuthoring Active { get; private set; }
        private void OnEnable() { if (Application.isPlaying) Active = this; }
        private void OnDisable() { if (Active == this) Active = null; }

        // Type de la cellule sous un point MONDE (convertit via ce transform).
        public CellType CellTypeAtWorld(Vector3 world)
            => grid == null ? CellType.Block : grid.TypeAtLocal(transform.InverseTransformPoint(world));

        public bool IsRoadAtWorld(Vector3 world) => CellTypeAtWorld(world) == CellType.Road;

        public float CellSize => grid != null ? grid.CellSize : 0f;

        // Le point est-il sur la CHAUSSEE (bande centrale d'une cellule route = voie
        // des voitures / passage), par opposition au trottoir ? Les trottoirs vivent
        // sur les BORDS d'une cellule route ; le type de cellule seul ne suffit donc
        // pas. On teste l'ecart au centre de la cellule : < laneHalf = expose.
        public bool IsOnRoadway(Vector3 world, float laneHalf)
        {
            if (grid == null) return false;
            Vector3 local = transform.InverseTransformPoint(world);
            grid.WorldToCell(local, out int x, out int y);
            if (grid.At(x, y) != CellType.Road) return false;
            Vector3 c = grid.CellToWorld(x, y);
            float ox = Mathf.Abs(local.x - c.x), oz = Mathf.Abs(local.z - c.z);
            return Mathf.Max(ox, oz) < laneHalf;
        }

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
