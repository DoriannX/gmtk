using System.Collections.Generic;
using UnityEngine;

namespace Gameplay.City
{
    // ATTENTION : valeurs serialisees par INDICE dans CityPalette.asset. Toute nouvelle couche
    // s'AJOUTE a la fin, jamais au milieu, sinon les entrees existantes changent de couche en
    // silence.
    //
    // Batiments et Props sont les deux couches d'origine. Props reste ce qu'elle a toujours ete :
    // le saupoudrage sur les surfaces DEJA PEINTES (toits, facades). Les deux nouvelles couvrent
    // le mobilier de RUE, qui ne se pose ni comme un batiment ni comme une cheminee :
    //   PropsRue -> aligne sur le trottoir, pose par CityPropsSeeder (un bouton, pas un geste :
    //               un lampadaire tous les 22 m est entierement determine par la route) ;
    //   PropsSol -> disperse au sol, pose au pinceau (une benne dans une ruelle, c'est un choix).
    public enum BrushLayer { Batiments, Props, PropsRue, PropsSol }

    // Comment un prop se colle a la surface qu'il touche.
    public enum PropAlign
    {
        Sol,      // debout, on ignore la normale (poubelle, distributeur)
        Normale,  // couche sur la surface (bloc technique de toit)
        Mur,      // plaque le DOS contre la surface (enseigne, porte de garage)
    }


    // PALETTE DE VILLE : la seule donnee du pinceau qui doit survivre d'une scene a l'autre.
    //
    // Pourquoi un ScriptableObject alors que le projet n'en avait aucun : le facadeYaw est une
    // metadonnee PAR PREFAB, devinee une fois a l'extraction puis corrigee a la main, et relue
    // par toutes les scenes. Un MonoBehaviour serait duplique par scene et divergerait en
    // silence ; un simple scan de dossier ne peut rien stocker du tout. Le garde-fou contre le
    // pourrissement de la liste est le bouton "Rescanner le dossier" de l'inspecteur.
    [CreateAssetMenu(fileName = "CityPalette", menuName = "GMTK/Palette de ville")]
    public class CityPalette : ScriptableObject
    {
        [System.Serializable]
        public class Entry
        {
            public GameObject prefab;
            public BrushLayer layer = BrushLayer.Batiments;
            public bool enabled = true;

            [Tooltip("Poids du tirage. 0 = jamais tire (equivaut a decocher).")]
            [Range(0f, 8f)] public float weight = 1f;

            [Tooltip("Yaw LOCAL (deg) vers lequel regarde la FACADE quand le prefab est a yaw 0. " +
                     "Devine a l'import (centroide de detail, arrondi a 90 deg), a corriger avec " +
                     "la boussole de l'inspecteur.")]
            [Range(0f, 360f)] public float facadeYaw;

            [Tooltip("Mesure figee a l'extraction. x = largeur de facade, y = hauteur, " +
                     "z = profondeur. Pivot du prefab : centre en XZ, base a y = 0.")]
            public Vector3 footprint = Vector3.one;

            [Tooltip("Couche Props uniquement : comment le prop se colle a la surface visee.")]
            public PropAlign align = PropAlign.Normale;

            [Tooltip("Couche PropsRue uniquement : distance entre deux exemplaires le long du " +
                     "trottoir, en metres. Proche de la largeur du prop = pose BOUT A BOUT " +
                     "(barriere, rambarde) ; grande = mobilier ponctuel (lampadaire, abribus).")]
            public float spacing = 8f;

            [Tooltip("Couche PropsRue uniquement : recul depuis le BORD EXTERIEUR de l'emprise " +
                     "routiere, en metres. C'est ce qui range le trottoir en profondeur : petit " +
                     "= adosse aux batiments (abribus, banc, panneau), grand = au bord du " +
                     "caniveau (poteau, barriere, lampadaire). Le trottoir du kit fait 3.75 m, " +
                     "donc l'intervalle utile va de ~0.5 a ~3.4.")]
            public float inset = 1f;

            // Un prop pose bout a bout forme une ligne continue, un prop ponctuel se seme. Le
            // seeder a besoin de distinguer les deux, et c'est DEDUIT au lieu d'etre un champ de
            // plus : si l'espacement demande vaut a peu pres la largeur du prop, c'est qu'on veut
            // une ligne. Le facteur 1.35 laisse la place a un petit jeu entre elements sans
            // basculer un lampadaire (spacing 22 pour 1.12 de large) du mauvais cote.
            // ponytail: regle deduite, un champ explicite si un prop large doit se semer serre
            public bool Continu => spacing <= Mathf.Max(0.3f, footprint.x) * 1.35f;

            [Tooltip("Variation d'echelle propre a cette entree, EN PLUS du reglage du pinceau.")]
            [Range(0f, 0.5f)] public float scaleJitter = 0.12f;

            [Tooltip("Couche PropsRue uniquement : ce prop se grinde. Le seeder regroupe les " +
                     "grindables sous UN conteneur portant un GrindRailSource -- separes, chacun " +
                     "ne ferait qu'un rail de sa propre longueur et la moto decrocherait entre " +
                     "deux ; ensemble, le recollage en fait une ligne continue.")]
            public bool grindable;

            [Tooltip("Trace de la devinette de facade, nom d'origine dans le FBX, etc.")]
            public string note;

            public bool Usable => prefab != null && enabled && weight > 0f;
        }

        public List<Entry> entries = new List<Entry>();

        public float TotalWeight(BrushLayer layer)
        {
            float t = 0f;
            foreach (var e in entries)
                if (e.layer == layer && e.Usable) t += e.weight;
            return t;
        }

        // Tirage pondere DETERMINISTE : `u` dans [0,1) vient du hash de placement, pas de
        // Random. C'est ce qui permet a une rangee de rester identique d'un repaint a l'autre.
        public Entry Pick(BrushLayer layer, float u)
        {
            float total = TotalWeight(layer);
            if (total <= 0f) return null;

            float target = Mathf.Clamp01(u) * total;
            float acc = 0f;
            Entry last = null;
            foreach (var e in entries)
            {
                if (e.layer != layer || !e.Usable) continue;
                last = e;
                acc += e.weight;
                if (target < acc) return e;
            }
            return last;   // filet contre l'arrondi flottant quand u tend vers 1
        }

        public int CountUsable(BrushLayer layer)
        {
            int n = 0;
            foreach (var e in entries)
                if (e.layer == layer && e.Usable) n++;
            return n;
        }

        public Entry Find(GameObject prefab)
        {
            foreach (var e in entries)
                if (e.prefab == prefab) return e;
            return null;
        }
    }
}
