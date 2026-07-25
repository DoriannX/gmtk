using UnityEngine;

namespace Gameplay.City
{
    // RACINE + REGLAGES du pinceau de ville. Composant d'AUTEUR : rien ne tourne au runtime, il
    // ne sert qu'a ancrer le conteneur des objets peints et a serialiser les reglages avec la
    // scene (meme parti-pris que CityBuilder et ZooBuilder).
    //
    // Pas de mode a choisir. Le placement est un CHAMP : pour un point donne, sa distance a la
    // route decide tout. Contre la chaussee -> front de rue aligne, facade vers la rue. Plus
    // loin -> voies arriere, puis grille de coeur d'ilot. Aucune route a portee -> grille pure.
    // Le pinceau ne fait que REVELER un plan de ville deja calcule, ce qui rend la peinture
    // idempotente : repasser au meme endroit ne pose rien de plus.
    //
    // Le contenu peint vit sous l'enfant "Peints" et n'a AUCUN hideFlags : contrairement au
    // "Generated" de RoadNetwork (DontSave | NotEditable, entierement derive), c'est du contenu
    // d'auteur qui doit etre sauve, selectionnable et editable a la main. Ne pas "corriger".
    public class CityBrush : MonoBehaviour
    {
        public const string ContainerName = "Peints";

        public CityPalette palette;

        [Tooltip("Batiments : peint la ville au sol, le placement se decide tout seul. " +
                 "Props : saupoudre sur les batiments DEJA peints (toits, facades). " +
                 "Raccourci B.")]
        public BrushLayer layer = BrushLayer.Batiments;

        // Bornes du rayon, partagees avec les raccourcis de l'outil pour qu'inspecteur et
        // molette ne se contredisent jamais.
        public const float MinRadius = 4f;
        public const float MaxRadius = 500f;

        [Header("Pinceau")]
        [Tooltip("Rayon du disque, en metres. Molette (avec Maj) pendant la peinture, ou [ et ], " +
                 "ou - et =.")]
        [Range(MinRadius, MaxRadius)] public float radius = 28f;
        [Tooltip("Distance entre deux passes, en part du rayon. Petit = suit la souris de pres.")]
        [Range(0.05f, 1f)] public float strokeStep = 0.25f;
        [Tooltip("Graine du plan de ville. Meme graine = meme ville. Raccourci R pour regrainer.")]
        public int seed = 12345;
        [Tooltip("Garde-fou : au-dela, le trait s'arrete et previent. Monte-le si tu peins au " +
                 "gros pinceau, un disque de 300 m contient facilement un millier de batiments.")]
        public int maxPerStroke = 1500;

        [Header("Front de rue")]
        [Tooltip("Recul depuis le BORD de la route. La demi-chaussee est ajoutee automatiquement.")]
        public float setback = 1.2f;
        [Tooltip("Nombre de voies de batiments en profondeur depuis chaque trottoir. 1 = juste le " +
                 "front de rue. 2-3 = vrais ilots epais.")]
        [Range(1, 4)] public int lanes = 2;
        [Tooltip("Profondeur reservee a une voie. En dessous de la profondeur du plus gros " +
                 "batiment, les voies se chevauchent et le rejet en jette une partie.")]
        public float laneDepth = 17f;
        [Tooltip("Ruelle entre deux voies.")]
        public float laneGap = 2.5f;
        [Tooltip("Espace minimum entre deux facades voisines. Monte-le pour aerer.")]
        public float gapMin = 0.2f;
        [Tooltip("Rallonge aleatoire de l'espace. C'est LUI qui donne l'irregularite lisible.")]
        public float gapRange = 0.9f;
        [Tooltip("Probabilite de sauter une place -> terrain vague.")]
        [Range(0f, 0.5f)] public float holeChance = 0.06f;
        public float holeSize = 7f;

        [Header("Coeur d'ilot")]
        [Tooltip("Pas de la grille qui remplit tout ce qui est hors de portee des voies. " +
                 "Baisse-le pour densifier.")]
        [Range(8f, 60f)] public float gridSpacing = 15f;
        [Tooltip("Desordre de la grille, en part du pas. 0 = damier militaire.")]
        [Range(0f, 0.45f)] public float gridJitter = 0.28f;
        [Tooltip("Nombre d'essais par case quand le premier batiment ne rentre pas (autre " +
                 "position, autre prefab). C'est ce qui empeche une collision de creuser un " +
                 "trou definitif. Monte-le pour densifier encore.")]
        [Range(1, 8)] public int gridTries = 4;
        [Tooltip("Probabilite de laisser une case vide -> cours, parkings, respiration.")]
        [Range(0f, 0.8f)] public float gridHoleChance = 0.10f;
        [Tooltip("Au-dela de cette distance a la route, les batiments de coeur d'ilot ne " +
                 "s'alignent plus sur elle et prennent les axes du monde.")]
        public float alignRange = 60f;

        // Le cote rigolo. Principe : PEU de jitter, BEAUCOUP de variete. Un gros bruit de
        // yaw/recul se lit comme de la geometrie cassee, pas comme du charme -- l'irregularite
        // qui marche vient des largeurs, des espacements et des trous ci-dessus.
        [Header("Cabossage")]
        [Range(0f, 20f)] public float yawJitter = 5f;
        [Range(0f, 0.4f)] public float scaleJitter = 0.14f;
        [Tooltip("Penche legerement le batiment, en degres.")]
        [Range(0f, 5f)] public float tiltJitter = 1f;
        [Tooltip("Enfonce le batiment dans le sol (jamais ne le souleve) : evite le jour sous " +
                 "les coins quand il est penche ou le sol pas plat.")]
        [Range(0f, 0.5f)] public float sinkMax = 0.15f;

        // Pas d'habillage automatique, et c'est deliberé : les prefabs de batiments arrivent
        // DEJA habilles. L'extraction garde tous les enfants de chaque groupe du FBX, donc
        // Immeuble_01/02/03 embarquent leurs 7 props (clim, escalier, enseigne, porte de
        // garage...) et Immeuble_neon_01/02/03 les leurs, aux positions de l'artiste. Semer des
        // props par-dessus faisait double emploi la ou c'etait deja bon, et posait des blocs de
        // travers sur les 5 tours batiment_02..06, qui n'en ont pas.
        // La couche Props ci-dessous reste pour les retouches a la main.
        [Header("Props (couche manuelle)")]
        [Tooltip("Props par metre carre de surface visee.")]
        [Range(0.002f, 0.2f)] public float propDensity = 0.03f;

        [Header("Sol")]
        [Tooltip("Plan de secours quand le rayon ne touche aucun collider.")]
        public float groundHeight = 0f;

        public Transform Container => transform.Find(ContainerName);
    }
}
