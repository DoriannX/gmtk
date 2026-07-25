using UnityEngine;
using UnityEngine.UIElements;
using Core;

namespace UI
{
    // A CABLER COTE UNITY :
    // - Creer un GameObject "PauseUI" avec un composant UIDocument.
    // - Assigner le Source Asset du UIDocument sur UI/UXML/PauseMenu.uxml.
    // - Poser ce script (PauseController) sur le meme GameObject.
    // - S'assurer qu'un GameManager existe dans la scene.
    [RequireComponent(typeof(UIDocument))]
    public class PauseController : MonoBehaviour
    {
        private VisualElement pausePanel;
        private Button firstButton;

        private void OnEnable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;

            pausePanel = root.Q<VisualElement>("pause-panel") ?? root;

            var resumeButton = root.Q<Button>("resume-button");
            var quitToMenuButton = root.Q<Button>("quit-to-menu-button");
            firstButton = resumeButton;

            if (resumeButton != null) resumeButton.clicked += OnResumeClicked;
            if (quitToMenuButton != null) quitToMenuButton.clicked += OnQuitToMenuClicked;

            SetPanelVisible(GameManager.Instance != null && GameManager.Instance.CurrentState == GameState.Paused);
        }

        private void OnDisable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;

            var resumeButton = root.Q<Button>("resume-button");
            var quitToMenuButton = root.Q<Button>("quit-to-menu-button");

            if (resumeButton != null) resumeButton.clicked -= OnResumeClicked;
            if (quitToMenuButton != null) quitToMenuButton.clicked -= OnQuitToMenuClicked;
        }

        private void Update()
        {
            if (!Core.InputActions.GetPausePressed()) return;
            if (GameManager.Instance == null) return;

            if (GameManager.Instance.CurrentState == GameState.Playing)
            {
                GameManager.Instance.PauseGame();
                SetPanelVisible(true);
            }
            else if (GameManager.Instance.CurrentState == GameState.Paused)
            {
                GameManager.Instance.ResumeGame();
                SetPanelVisible(false);
            }
        }

        private void OnResumeClicked()
        {
            if (GameManager.Instance == null) return;
            GameManager.Instance.ResumeGame();
            SetPanelVisible(false);
        }

        private void OnQuitToMenuClicked()
        {
            if (GameManager.Instance == null) return;
            GameManager.Instance.ReturnToMenu();
            SetPanelVisible(false);
        }

        private void SetPanelVisible(bool visible)
        {
            if (pausePanel == null) return;
            pausePanel.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

            // Sans element focus, la manette n'a rien d'ou naviguer. Differe d'une
            // frame : un element en display:none ne peut pas prendre le focus.
            if (visible && firstButton != null)
                pausePanel.schedule.Execute(() => firstButton.Focus());
        }
    }
}
