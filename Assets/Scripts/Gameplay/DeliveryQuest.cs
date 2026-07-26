using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.Events;

namespace Gameplay
{
    // Quete de livreur : rester dans la zone de RETRAIT pour prendre le paquet, puis dans la
    // zone de LIVRAISON pour le deposer.
    // - la zone de retrait n'est PAS pistee par la fleche (on la trouve a l'oeil)
    // - la zone de livraison n'existe pas tant que le paquet n'est pas pris
    // - la fleche ne guide qu'une fois le paquet en main
    public class DeliveryQuest : MonoBehaviour
    {
        [SerializeField] private QuestZone pickup;
        [SerializeField] private QuestZone dropoff;
        [SerializeField] private float hideDelay = 1.2f;   // laisse voir le "OK !" avant de ranger la zone
        [SerializeField] private int pickupReward = 60;    // points au retrait
        [SerializeField] private int deliveryReward = 320; // points a la livraison

        public UnityEvent onPackagePicked;
        public UnityEvent onDelivered;

        public bool HasPackage { get; private set; }
        public bool Delivered { get; private set; }

        private void Awake()
        {
            if (dropoff != null) dropoff.gameObject.SetActive(false);
            ReleaseArrow();   // aucun guidage avant d'avoir le paquet
        }

        // La fleche est unique et partagee entre les livraisons : on ne la libere QUE si elle
        // pointe encore sur notre depot. Sinon une quete effacerait le guidage d'une autre.
        private void ReleaseArrow()
        {
            var arrow = QuestArrow.Instance;   // spawnee a la demande, rien a brancher en scene
            if (arrow.Target == dropoff) arrow.SetTarget(null);
        }

        private void OnEnable()
        {
            if (pickup != null) pickup.onCompleted.AddListener(Pick);
            if (dropoff != null) dropoff.onCompleted.AddListener(Deliver);
            if (!All.Contains(this)) All.Add(this);
            RefreshBlocks();
        }

        private void OnDisable()
        {
            if (pickup != null) pickup.onCompleted.RemoveListener(Pick);
            if (dropoff != null) dropoff.onCompleted.RemoveListener(Deliver);
            All.Remove(this);
            RefreshBlocks();
            DOTween.Kill(this);
        }

        // On ne porte qu'UN colis a la fois : des qu'une course est en cours, tous les points
        // de retrait deviennent des murs. Recalcule global plutot que "bloque/debloque" par
        // paire : avec plusieurs livraisons, un depot en debloquerait pendant qu'une autre
        // course tourne encore.
        private static readonly List<DeliveryQuest> All = new();

        // DisableDomainReload : la liste survit au Stop/Play. On repart d'une liste vide a
        // chaque session, en plus de la purge paresseuse ci-dessous. Cf RunEnd.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            All.Clear();
            tricks = null;   // pointerait sur le TrickSystem detruit de la partie precedente
        }

        // Meme cause : les quetes detruites de la partie precedente restent dans la liste si
        // OnDisable n'a pas tourne. Sans purge, Total les compte et le HUD annonce "3 / 6".
        private static void Prune()
        {
            for (int i = All.Count - 1; i >= 0; i--)
                if (All[i] == null) All.RemoveAt(i);
        }

        // Une course "en cours" doit avoir un depot VIVANT. Si le depot a disparu (scene
        // rechargee a moitie, objet detruit), la quete est perdue : la compter bloquerait
        // tous les points de retrait pour le reste de la partie, sans aucun moyen d'en sortir.
        public static bool AnyInProgress
        {
            get
            {
                Prune();
                foreach (var q in All)
                    if (q != null && q.HasPackage && q.dropoff != null && q.dropoff.gameObject.activeInHierarchy)
                        return true;
                return false;
            }
        }

        // LE COMPTEUR DE VICTOIRE. Les quetes sont posees a la main en scene : Total est
        // simplement leur nombre, Remaining descend a chaque livraison et ne remonte jamais.
        // Remaining == 0 -> partie gagnee (cf RunEnd).
        public static int Total { get { Prune(); return All.Count; } }

        public static int Remaining
        {
            get
            {
                int n = 0;
                foreach (var q in All) if (q != null && !q.Delivered) n++;
                return n;
            }
        }

        public static int DeliveredCount => Total - Remaining;

        private static void RefreshBlocks()
        {
            bool busy = AnyInProgress;
            foreach (var q in All)
                if (q != null && q.pickup != null)
                    // JAMAIS le point de la course en cours : le joueur est dedans quand il
                    // vient de prendre le colis, le solidifier l'enfermerait / l'ejecterait.
                    // Il disparait de lui-meme apres le flash de validation.
                    q.pickup.SetBusy(busy && !q.HasPackage);
        }

        private void Pick()
        {
            if (HasPackage || Delivered) return;
            // ON NE PORTE QU'UN COLIS. Le blocage des autres points de retrait est VISUEL
            // (un mur qui met une frame a se fermer, et jamais autour du joueur) : deux zones
            // de retrait qui se chevauchent peuvent donc se valider dans la meme frame.
            // Sans ce garde-fou, deux courses partent d'un coup, une seule est livree, et
            // celle qui reste bloque AnyInProgress a vie -> plus aucun retrait possible pour
            // le reste de la partie. On REARME le point au passage : sans ca il resterait
            // valide (once = true) et serait perdu pour de bon -> victoire inatteignable.
            if (AnyInProgress) { if (pickup != null) pickup.Rearm(); return; }
            HasPackage = true;

            dropoff.gameObject.SetActive(true);   // la zone de livraison jaillit (pop dans QuestZone)
            QuestArrow.Instance.SetTarget(dropoff);
            Retire(pickup);
            RefreshBlocks();          // les autres points se ferment
            Award("COLIS PRIS", pickupReward);
            onPackagePicked?.Invoke();
        }

        private void Deliver()
        {
            HasPackage = false;
            Delivered = true;
            ReleaseArrow();
            Retire(dropoff);
            RefreshBlocks();          // course terminee : les autres points rouvrent
            Award("LIVRAISON", deliveryReward);
            onDelivered?.Invoke();
        }

        // Les gains passent par la CHAINE de figures, pas par ScoreGauge.Add : ScoreGauge suit
        // deja TrickSystem.Total par delta, crediter les deux compterait les points deux fois.
        // Effet voulu : livrer en plein combo est multiplie (jusqu'a x10) et relance la fenetre.
        // Contrepartie assumee : se planter apres la livraison fait perdre la chaine, donc les
        // points -- mais JAMAIS le colis, le compteur de victoire ne recule pas.
        private static TrickSystem tricks;

        private static void Award(string label, int pts)
        {
            if (tricks == null) tricks = FindAnyObjectByType<TrickSystem>();
            if (tricks != null) tricks.AwardChain(label, pts, true);
            else if (ScoreGauge.Instance != null) ScoreGauge.Instance.Add(pts);   // secours : pas de TrickSystem en scene
        }

        // On range la zone APRES le flash de validation, sinon le feedback disparait dans la frame.
        private void Retire(QuestZone z)
        {
            if (z == null) return;
            DOVirtual.DelayedCall(hideDelay, () => { if (z != null) z.gameObject.SetActive(false); })
                .SetTarget(this);
        }
    }
}
