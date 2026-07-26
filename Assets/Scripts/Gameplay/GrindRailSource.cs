using System.Collections.Generic;
using UnityEngine;

namespace Gameplay
{
    // "CET OBJET EST GRINDABLE." A poser sur un prefab (rambarde, muret, rampe) : il declare au
    // demarrage une polyligne DEJA FIGEE au GrindRailNetwork. C'est GrindRail generalise -- meme
    // principe de rail declare, mais plusieurs points, en coordonnees locales, donc portable par
    // un prefab pose n'importe ou.
    //
    // Les rails sont TOUJOURS figes, jamais extraits au demarrage. Deux raisons, et la premiere
    // suffit : deviner le grindable a partir de la geometrie ne marche pas a l'echelle d'une
    // scene (cf. le refactor qui a supprime le baker plein-scene). La seconde est le cout --
    // mesure sur les 201 rambardes/barrieres d'une passe de CityPropsSeeder (49 800 triangles) :
    // 1134 ms d'extraction. Fige, le demarrage se resume a transformer une poignee de points.
    //
    // D'ou vient le contenu fige : SetBaked quand la ligne est connue d'avance (c'est le cas des
    // props, PropsImporter ecrit la barre du haut en deux points), ou le bouton "Figer les rails"
    // de l'inspecteur, qui lance l'extraction UNE FOIS, en editeur, sur les seuls meshes de cet
    // objet -- explicite et borne, jamais automatique ni plein-scene.
    //
    // La continuite entre objets voisins n'est PAS le probleme de ce composant : chaque prop
    // apporte son bout de 1.67 m et c'est GrindRailNetwork.Build qui les recolle en une ligne.
    // Inutile donc de grouper les rambardes sous un parent commun pour qu'elles s'enchainent.
    [DisallowMultipleComponent]
    public class GrindRailSource : MonoBehaviour
    {
        // Serialisable a part : Unity ne serialise pas les tableaux de tableaux.
        [System.Serializable]
        public class LocalPath { public Vector3[] points; }

        [SerializeField] private List<LocalPath> baked = new List<LocalPath>();

        [Tooltip("Bouton 'Figer les rails' uniquement : longueur mini d'un rail, en metres, apres " +
                 "recollage. Un objet qui porte ce composant est deja une declaration " +
                 "d'intention, il n'y a pas de bruit a trier -- d'ou un seuil a la taille d'une " +
                 "rambarde et non celui, defensif, d'une extraction a l'aveugle.")]
        [SerializeField] private float minRail = 1.2f;

        [Tooltip("Trace ce qui a ete declare au demarrage. A allumer quand un objet refuse de " +
                 "grinder.")]
        [SerializeField] private bool verbose;

        public bool HasBaked => baked != null && baked.Count > 0;

        private void Awake()
        {
            // Rien de fige = rien a declarer. Pas de repli sur l'extraction : un objet muet est un
            // oubli de bake, et le dire vaut mieux que de deviner a sa place.
            if (!HasBaked)
            {
                if (verbose)
                    Debug.LogWarning($"[GrindRailSource] {name} : aucun rail fige, cet objet ne " +
                                     "grindera pas. Utilise 'Figer les rails' dans l'inspecteur.", this);
                return;
            }

            var rails = FromBaked();
            if (rails.Count > 0) GrindRailNetwork.Instance.AddPaths(rails);
            if (verbose) Debug.Log($"[GrindRailSource] {name} : {rails.Count} rail(s) declares.", this);
        }

        // Les points figes sont en LOCAL : c'est ce qui rend un prefab reutilisable a n'importe
        // quelle position, rotation et echelle. TransformPoint applique les trois.
        private List<GrindRailNetwork.Path> FromBaked()
        {
            var outp = new List<GrindRailNetwork.Path>(baked.Count);
            foreach (var lp in baked)
            {
                if (lp?.points == null || lp.points.Length < 2) continue;
                var w = new Vector3[lp.points.Length];
                for (int i = 0; i < w.Length; i++) w[i] = transform.TransformPoint(lp.points[i]);
                outp.Add(new GrindRailNetwork.Path { points = w });
            }
            return outp;
        }

        private List<GrindRailNetwork.Path> FromMeshes()
        {
            var rails = GrindRailExtractor.Extract(
                GetComponentsInChildren<MeshFilter>(true), minRail, out _, out int skipped);

            // Un FBX importe sans isReadable ne produit AUCUNE arete, et rien ne le dit : c'est
            // le seul mode d'echec silencieux de tout le systeme. On le crie une fois plutot que
            // de laisser chercher pourquoi la rambarde ne grinde pas.
            if (skipped > 0)
                Debug.LogWarning($"[GrindRailSource] {name} : {skipped} mesh(es) ignore(s), " +
                                 "Read/Write est desactive sur leur modele. Coche 'Read/Write " +
                                 "Enabled' dans l'importeur du FBX, sinon ils ne grinderont jamais.",
                                 this);
            return rails;
        }

        // Appele par l'inspecteur et par PropsImporter. Rend le nombre de rails figes.
        // Les points sont ramenes en LOCAL : l'extracteur travaille en monde, et un prefab pose
        // ailleurs doit retrouver ses rails.
        public int Bake()
        {
            var rails = FromMeshes();
            baked = new List<LocalPath>(rails.Count);
            foreach (var r in rails)
            {
                var pts = new Vector3[r.points.Length];
                for (int i = 0; i < pts.Length; i++) pts[i] = transform.InverseTransformPoint(r.points[i]);
                baked.Add(new LocalPath { points = pts });
            }
            return baked.Count;
        }

        public void ClearBaked() => baked = new List<LocalPath>();

        // Fige des rails DONNES, en local, sans passer par les meshes.
        //
        // Necessaire parce que la detection par pli a un angle mort structurel : un barreau
        // TUBULAIRE n'a aucune arete vive (ses facettes sont a ~15 deg, sous le seuil diedre de
        // 22), donc une rambarde ronde ne produit rien alors qu'elle est evidemment grindable.
        // Baisser le seuil ferait passer toutes les surfaces courbes de la ville. Quand la ligne
        // est connue d'avance -- une barre droite au sommet -- l'ecrire vaut mieux que de la
        // deduire.
        public void SetBaked(params Vector3[][] localPaths)
        {
            baked = new List<LocalPath>();
            foreach (var p in localPaths)
                if (p != null && p.Length >= 2) baked.Add(new LocalPath { points = p });
        }

        private void OnDrawGizmosSelected()
        {
            if (!HasBaked) return;
            Gizmos.color = Color.cyan;
            foreach (var lp in baked)
            {
                if (lp?.points == null) continue;
                for (int i = 0; i < lp.points.Length - 1; i++)
                    Gizmos.DrawLine(transform.TransformPoint(lp.points[i]),
                                    transform.TransformPoint(lp.points[i + 1]));
            }
        }
    }
}
