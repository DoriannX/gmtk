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
        [SerializeField] private QuestArrow arrow;
        [SerializeField] private float hideDelay = 1.2f;   // laisse voir le "OK !" avant de ranger la zone

        public UnityEvent onPackagePicked;
        public UnityEvent onDelivered;

        public bool HasPackage { get; private set; }
        public bool Delivered { get; private set; }

        private void Awake()
        {
            if (dropoff != null) dropoff.gameObject.SetActive(false);
            if (arrow != null) arrow.SetTarget(null);   // aucun guidage avant d'avoir le paquet
        }

        private void OnEnable()
        {
            if (pickup != null) pickup.onCompleted.AddListener(Pick);
            if (dropoff != null) dropoff.onCompleted.AddListener(Deliver);
        }

        private void OnDisable()
        {
            if (pickup != null) pickup.onCompleted.RemoveListener(Pick);
            if (dropoff != null) dropoff.onCompleted.RemoveListener(Deliver);
            DOTween.Kill(this);
        }

        private void Pick()
        {
            if (HasPackage) return;
            HasPackage = true;

            dropoff.gameObject.SetActive(true);   // la zone de livraison jaillit (pop dans QuestZone)
            arrow.SetTarget(dropoff);
            Retire(pickup);
            onPackagePicked?.Invoke();
        }

        private void Deliver()
        {
            HasPackage = false;
            Delivered = true;
            arrow.SetTarget(null);
            Retire(dropoff);
            onDelivered?.Invoke();
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
