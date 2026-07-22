using System;
using UnityEngine.SceneManagement;

namespace Core
{
    // Wrapper statique : pas de fade/transition implemente ici, juste des points
    // d'accroche (BeforeSceneLoad / AfterSceneLoad) pour brancher une UI de loading plus tard.
    public static class SceneLoader
    {
        public static event Action<string> BeforeSceneLoad;
        public static event Action<string> AfterSceneLoad;

        public static void LoadScene(string sceneName)
        {
            BeforeSceneLoad?.Invoke(sceneName);
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.LoadScene(sceneName);
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            AfterSceneLoad?.Invoke(scene.name);
        }
    }
}
