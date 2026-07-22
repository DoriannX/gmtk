using UnityEngine;
using UnityEngine.UIElements;
using Core;

namespace UI
{
    // A CABLER COTE UNITY :
    // - Creer un GameObject "GameOverUI" avec un composant UIDocument.
    // - Assigner le Source Asset du UIDocument sur UI/UXML/GameOver.uxml.
    // - Poser ce script (GameOverController) sur le meme GameObject.
    // - S'assurer qu'un GameManager existe dans la scene.
    [RequireComponent(typeof(UIDocument))]
    public class GameOverController : MonoBehaviour
    {
        private VisualElement gameOverPanel;

        private void OnEnable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            gameOverPanel = root.Q<VisualElement>("gameover-panel") ?? root;

            var retryButton = root.Q<Button>("retry-button");
            var menuButton = root.Q<Button>("menu-button");

            if (retryButton != null) retryButton.clicked += OnRetryClicked;
            if (menuButton != null) menuButton.clicked += OnMenuClicked;

            if (GameManager.Instance != null)
            {
                GameManager.Instance.OnGameStateChanged += HandleGameStateChanged;
                SetPanelVisible(GameManager.Instance.CurrentState == GameState.GameOver);
            }
            else
            {
                SetPanelVisible(false);
            }
        }

        private void OnDisable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;

            var retryButton = root.Q<Button>("retry-button");
            var menuButton = root.Q<Button>("menu-button");

            if (retryButton != null) retryButton.clicked -= OnRetryClicked;
            if (menuButton != null) menuButton.clicked -= OnMenuClicked;

            if (GameManager.Instance != null) GameManager.Instance.OnGameStateChanged -= HandleGameStateChanged;
        }

        private void HandleGameStateChanged(GameState newState)
        {
            SetPanelVisible(newState == GameState.GameOver);
        }

        private void OnRetryClicked()
        {
            if (GameManager.Instance != null) GameManager.Instance.StartGame();
        }

        private void OnMenuClicked()
        {
            if (GameManager.Instance != null) GameManager.Instance.ReturnToMenu();
        }

        private void SetPanelVisible(bool visible)
        {
            if (gameOverPanel == null) return;
            gameOverPanel.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}
