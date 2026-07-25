using UnityEngine;

namespace Gameplay
{
    // Silhouette d'oiseau minimale (2 ailes en V, corps implicite) pour les
    // oiseaux lointains/cosmetiques. Pivots "WingL"/"WingR" nommes -> battables
    // par le code appelant. Partage par BirdFlock et AmbientBirds.
    public static class ProcBird
    {
        public static GameObject Build(Material mat)
        {
            var root = new GameObject("Bird");
            Wing(root.transform, "WingL", 1f, mat);
            Wing(root.transform, "WingR", -1f, mat);
            return root;
        }

        // Cherche une piece par nom en profondeur (les modeles FBX imbriquent
        // souvent WingL/WingR sous un "Body"). Null tolere.
        public static Transform FindDeep(Transform root, string n)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == n) return t;
            return null;
        }

        private static void Wing(Transform parent, string n, float sign, Material mat)
        {
            var pivot = new GameObject(n).transform;
            pivot.SetParent(parent, false);
            var mesh = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(mesh.GetComponent<Collider>());
            mesh.GetComponent<MeshRenderer>().sharedMaterial = mat;
            mesh.transform.SetParent(pivot, false);
            mesh.transform.localScale = new Vector3(1.2f, 0.06f, 0.5f);
            mesh.transform.localPosition = new Vector3(sign * 0.7f, 0, 0); // aile decalee : pivot = epaule
        }
    }
}
