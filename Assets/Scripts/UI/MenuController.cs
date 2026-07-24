using UnityEngine;
using UnityEngine.UIElements;
using Core;

namespace UI
{
    // A CABLER COTE UNITY :
    // - Creer un GameObject "MainMenuUI" avec un composant UIDocument.
    // - Assigner le Source Asset du UIDocument sur UI/UXML/MainMenu.uxml.
    // - Poser ce script (MenuController) sur le meme GameObject.
    // - S'assurer qu'un GameManager existe dans la scene (ou dans une scene de boot persistante).
    [RequireComponent(typeof(UIDocument))]
    public class MenuController : MonoBehaviour
    {
        private void OnEnable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;

            var playButton = root.Q<Button>("play-button");
            var quitButton = root.Q<Button>("quit-button");

            if (playButton != null) playButton.clicked += OnPlayClicked;
            if (quitButton != null) quitButton.clicked += OnQuitClicked;

            // Sans element focus, la manette n'a rien d'ou naviguer : Navigate/Submit
            // du panel UI Toolkit partent du focus courant. Differe d'une frame, le
            // panel n'est pas encore attache au moment du OnEnable.
            if (playButton != null) root.schedule.Execute(() => playButton.Focus());
        }

        private void OnDisable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;

            var playButton = root.Q<Button>("play-button");
            var quitButton = root.Q<Button>("quit-button");

            if (playButton != null) playButton.clicked -= OnPlayClicked;
            if (quitButton != null) quitButton.clicked -= OnQuitClicked;
        }

        private void OnPlayClicked()
        {
            if (GameManager.Instance != null) GameManager.Instance.StartGame();
        }

        private void OnQuitClicked()
        {
            Application.Quit();
        }
    }
}
