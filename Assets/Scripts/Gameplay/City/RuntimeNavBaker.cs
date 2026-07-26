using UnityEngine;
using Unity.AI.Navigation;

namespace Gameplay.City
{
    // Re-bake le NavMeshSurface au lancement du jeu. La ville est generee en editeur
    // (CityBuilder) : plutot que de serialiser un asset NavMeshData fragile, on
    // reconstruit le NavMesh a partir des trottoirs (deja dans la scene) au demarrage.
    //
    // PAS dans Awake : la geometrie du RoadNetwork porte hideFlags.DontSave, elle n'est donc
    // pas serialisee dans la scene et le reseau la reconstruit dans son PREMIER Update. Baker
    // avant, c'est ne ramasser que le sol -- mesure : 1258 triangles plafonnes a y=5, sans
    // aucune des voies surelevees (qui montent a 19), et les pietons poses dessus restaient
    // inertes. Le premier LateUpdate passe apres cette reconstruction.
    [DefaultExecutionOrder(-200)]
    [RequireComponent(typeof(NavMeshSurface))]
    public class RuntimeNavBaker : MonoBehaviour
    {
        private bool baked;

        private void LateUpdate()
        {
            if (baked) return;
            baked = true;
            var surface = GetComponent<NavMeshSurface>();
            if (surface != null) surface.BuildNavMesh();
        }
    }
}
