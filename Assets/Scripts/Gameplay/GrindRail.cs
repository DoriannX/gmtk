using UnityEngine;

namespace Gameplay
{
    // Rail de grind POSE A LA MAIN (pas de bake) : segment droit le long de l'axe Z local, la
    // ligne de grind passe par l'origine de l'objet. S'enregistre tout seul dans le
    // GrindRailNetwork au demarrage (le reseau est cree s'il n'existe pas) -> il suffit de
    // deposer le prefab dans la scene et de le tourner/allonger.
    public class GrindRail : MonoBehaviour
    {
        [SerializeField] private float length = 8f; // longueur du rail (metres, axe Z local)
        [SerializeField] private Transform visual;  // tube cosmetique, mis a l'echelle sur length

        public Vector3 Start => transform.position - transform.forward * (length * 0.5f);
        public Vector3 End => transform.position + transform.forward * (length * 0.5f);

        private void Awake()
        {
            var net = FindAnyObjectByType<GrindRailNetwork>();
            if (net == null) net = new GameObject("GrindRailNetwork").AddComponent<GrindRailNetwork>();
            net.AddPath(new[] { Start, End });
        }

        private void OnValidate()
        {
            if (length < 0.1f) length = 0.1f;
            // le tube est un cylindre Unity (hauteur 2 sur son Y) couche sur Z -> scale Y = length/2
            if (visual != null)
            {
                var s = visual.localScale;
                visual.localScale = new Vector3(s.x, length * 0.5f, s.z);
            }
        }

        // ponytail: rail droit uniquement. Rail courbe = enchainer plusieurs rails bout a bout,
        // ou passer la polyligne au reseau si un jour on veut des splines.
        private void OnDrawGizmos()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(Start, End);
            Gizmos.color = new Color(1f, 0.5f, 0f);
            Gizmos.DrawSphere(Start, 0.1f);
            Gizmos.DrawSphere(End, 0.1f);
        }
    }
}
