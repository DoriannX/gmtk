using UnityEngine;
using Unity.AI.Navigation;

namespace Gameplay.City
{
    // Re-bake le NavMeshSurface au lancement du jeu. La ville est generee en editeur
    // (CityBuilder) : plutot que de serialiser un asset NavMeshData fragile, on
    // reconstruit le NavMesh a partir des trottoirs (deja dans la scene) au demarrage.
    // Execution tres tot (-200) pour que les pietons se posent sur un NavMesh pret
    // dans leur Start.
    [DefaultExecutionOrder(-200)]
    [RequireComponent(typeof(NavMeshSurface))]
    public class RuntimeNavBaker : MonoBehaviour
    {
        private void Awake()
        {
            var surface = GetComponent<NavMeshSurface>();
            if (surface != null) surface.BuildNavMesh();
        }
    }
}
