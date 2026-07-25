using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Gameplay
{
    // Petite "IA" sociale du pieton : de temps en temps il pense tout seul,
    // ou s'il y a un autre pieton assez proche, ils echangent quelques repliques.
    // Affichage via SpeechBubble (ajoute automatiquement si absent).
    [RequireComponent(typeof(SpeechBubble))]
    [RequireComponent(typeof(Pedestrian))]
    public class PedestrianChatter : MonoBehaviour
    {
        [Header("Timing")]
        [SerializeField] private Vector2 quietDuration = new Vector2(4f, 10f); // silence entre deux idees
        [SerializeField] private float lineDuration = 2.6f;                    // duree d'affichage d'une replique

        [Header("Meme six seven")]
        [SerializeField, Range(0f, 1f)] private float sixSevenChance = 0.15f;   // proba a chaque idee (0.15 = surtout pensees/convos)
        [SerializeField] private float sixSevenDuration = 3f;                   // duree du geste + bulle
        [SerializeField] private float talkRadius = 7f;                        // distance max pour aller discuter
        [SerializeField] private float faceToFaceDist = 1.5f;                  // distance de discussion
        [SerializeField] private float approachTimeout = 10f;                  // abandon si trop long

        [Header("Repliques (modifiable dans l'inspecteur)")]
        [SerializeField] private string[] thoughts =
        {
            "J'ai oublie d'eteindre le four ?",
            "Belle journee pour marcher en rond.",
            "Ou est-ce que j'allais deja...",
            "J'aurais du prendre le bus.",
            "Ce camion roule bizarrement, non ?",
            "Croissant ou pain au chocolat...",
            "Mes pieds me portent, mais vers quoi ?",
        };
        [SerializeField] private string[] starters =
        {
            "Salut ! Ca va ?",
            "T'as vu ce camion ?!",
            "Il va pleuvoir tu crois ?",
            "Toujours a trainer ici toi !",
            "T'as pas vu mon chat ?",
        };
        [SerializeField] private string[] replies =
        {
            "Ouais ouais, tranquille.",
            "M'en parle pas !",
            "Aucune idee, je fais que passer.",
            "Ha ! Elle est bien bonne.",
            "Faut que je file, a plus !",
            "Si tu le dis...",
        };

        private static readonly List<PedestrianChatter> All = new();

        private SpeechBubble bubble;
        private Pedestrian pedestrian;
        private float quietTimer;
        private bool inConversation;

        private void Awake()
        {
            bubble = GetComponent<SpeechBubble>();
            pedestrian = GetComponent<Pedestrian>();
        }
        private void OnEnable() { All.Add(this); ResetQuietTimer(); }
        private void OnDisable() => All.Remove(this);

        private void Update()
        {
            // au volant : le pieton est cache, ses pensees passent par la voiture (CarChatter)
            if (inConversation || bubble.IsShowing || pedestrian.IsPanicking || pedestrian.IsDriving) return;
            quietTimer -= Time.deltaTime;
            if (quietTimer > 0f) return;

            if (Random.value < sixSevenChance) { SixSeven(); return; }

            var partner = FindFreePartner();
            if (partner != null && Random.value > 0.4f)
                StartCoroutine(Conversation(partner));
            else
                Think();
        }

        private void Think()
        {
            bubble.Show(Pick(thoughts), isThought: true, lineDuration);
            ResetQuietTimer();
        }

        private void SixSeven()
        {
            bubble.Show("SIIIX SEVEEEN !", isThought: false, sixSevenDuration);
            pedestrian.SixSeven(sixSevenDuration);
            ResetQuietTimer();
        }

        // Debug runtime : force le geste, ignore l'etat social (pour iterer sur le feel)
        public void ForceSixSeven()
        {
            bubble.Show("SIIIX SEVEEEN !", isThought: false, sixSevenDuration);
            pedestrian.SixSeven(sixSevenDuration);
            ResetQuietTimer();
        }

        // ponytail: bouton debug a l'ecran, dessine une seule fois, declenche tout le monde
        private void OnGUI()
        {
            if (All.Count == 0 || All[0] != this) return;
            if (GUI.Button(new Rect(12, 12, 190, 46), "6-7 (debug)"))
                foreach (var c in All) c.ForceSixSeven();
        }

        private IEnumerator Conversation(PedestrianChatter other)
        {
            inConversation = true;
            other.inConversation = true;

            // 1. l'autre s'arrete, celui qui veut parler marche vers lui
            other.pedestrian.StandAndWait();
            bool arrived = false;
            pedestrian.WalkTo(other.transform.position, faceToFaceDist, () => arrived = true);
            float timeout = approachTimeout;
            while (!arrived && timeout > 0f)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }
            if (!arrived)
            {
                EndConversation(other); // bloque en route -> on laisse tomber
                yield break;
            }

            // 2. l'autre reagit : face a face
            pedestrian.FaceTowards(other.transform.position);
            other.pedestrian.FaceTowards(transform.position);
            yield return new WaitForSeconds(0.4f);

            // 3. echange, moustache en furie pour celui qui parle
            // (le confrere peut se faire ecraser en route -> on baile si detruit)
            if (other == null) { inConversation = false; yield break; }
            Say(this, Pick(starters));
            yield return new WaitForSeconds(lineDuration + 0.3f);
            if (other == null) { inConversation = false; yield break; }
            Say(other, Pick(other.replies));
            yield return new WaitForSeconds(lineDuration + 0.3f);
            // 50% : une derniere relance pour faire vivant
            if (Random.value > 0.5f)
            {
                Say(this, Pick(replies));
                yield return new WaitForSeconds(lineDuration);
            }

            EndConversation(other);
        }

        private void Say(PedestrianChatter who, string line)
        {
            who.bubble.Show(line, isThought: false, lineDuration);
            who.pedestrian.MustacheFrenzy(lineDuration);
        }

        private void EndConversation(PedestrianChatter other)
        {
            pedestrian.ResumeWander();
            inConversation = false;
            ResetQuietTimer();
            if (other == null) return; // confrere ecrase pendant l'echange
            other.pedestrian.ResumeWander();
            other.inConversation = false;
            other.ResetQuietTimer();
        }

        private PedestrianChatter FindFreePartner()
        {
            foreach (var p in All)
            {
                if (p == this || p.inConversation || p.bubble.IsShowing) continue;
                if ((p.transform.position - transform.position).sqrMagnitude <= talkRadius * talkRadius)
                    return p;
            }
            return null;
        }

        // Pensee au hasard, transmise a la voiture quand ce pieton conduit.
        public string RandomThought() => Pick(thoughts);

        private void ResetQuietTimer() => quietTimer = Random.Range(quietDuration.x, quietDuration.y);
        private static string Pick(string[] pool) => pool[Random.Range(0, pool.Length)];
    }
}
