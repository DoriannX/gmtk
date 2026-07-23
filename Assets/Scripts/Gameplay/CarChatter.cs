using UnityEngine;

namespace Gameplay
{
    // Une voiture vide ne pense pas : elle est garee, c'est un objet. Quand un
    // pieton la conduit, ce sont SES pensees (PedestrianChatter) qui s'affichent
    // sur la bulle de la voiture. Fallback sur les lignes locales si le
    // conducteur n'a pas de chatter.
    [RequireComponent(typeof(SpeechBubble))]
    [RequireComponent(typeof(AICarController))]
    public class CarChatter : MonoBehaviour
    {
        [Header("Timing")]
        [SerializeField] private Vector2 quietDuration = new Vector2(6f, 14f); // silence entre deux pensees
        [SerializeField] private float lineDuration = 2.6f;
        [SerializeField, Range(0f, 1f)] private float thoughtChance = 0.7f;    // sinon rien ce cycle

        [Header("Pensees (modifiable dans l'inspecteur)")]
        [SerializeField] private string[] thoughts =
        {
            "Encore ce feu rouge...",
            "Un peu de trafic aujourd'hui.",
            "Ou est-ce que j'allais deja ?",
            "Cette route est a moi.",
            "J'aurais du prendre l'autoroute.",
            "Doucement, doucement.",
            "Ce pieton m'a regarde bizarrement.",
            "Plein d'essence ? ... j'espere.",
            "Vroum vroum.",
        };

        private SpeechBubble bubble;
        private AICarController car;
        private float quietTimer;

        private void Awake()
        {
            bubble = GetComponent<SpeechBubble>();
            car = GetComponent<AICarController>();
        }
        private void OnEnable() => ResetTimer();

        private void Update()
        {
            if (!car.Occupied) { ResetTimer(); return; } // vide = pas de pensee (c'est la caisse)
            if (bubble.IsShowing) return;                // bulle occupee (pensee ou klaxon du controller)
            quietTimer -= Time.deltaTime;
            if (quietTimer > 0f) return;

            ResetTimer();
            if (Random.value < thoughtChance)
                bubble.Show(DriverThought(), isThought: true, lineDuration);
        }

        // Pensee du pieton au volant, sinon ligne locale de secours.
        private string DriverThought()
        {
            var d = car.Driver;
            if (d != null)
            {
                var chat = d.GetComponent<PedestrianChatter>();
                if (chat != null) return chat.RandomThought();
            }
            return thoughts[Random.Range(0, thoughts.Length)];
        }

        private void ResetTimer() => quietTimer = Random.Range(quietDuration.x, quietDuration.y);
    }
}
