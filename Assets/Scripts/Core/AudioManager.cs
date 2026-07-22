using UnityEngine;
using System.Collections;

namespace Core
{
    [RequireComponent(typeof(AudioSource))]
    public class AudioManager : MonoBehaviour
    {
        public static AudioManager Instance { get; private set; }

        [SerializeField] private AudioSource musicSourceA;
        [SerializeField] private AudioSource musicSourceB;
        [SerializeField] private AudioSource sfxSource;
        [SerializeField] private float crossfadeDuration = 1f;

        [Range(0f, 1f)] [SerializeField] private float musicVolume = 1f;
        [Range(0f, 1f)] [SerializeField] private float sfxVolume = 1f;

        private AudioSource activeMusicSource;
        private AudioSource inactiveMusicSource;
        private Coroutine crossfadeRoutine;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            // Deux AudioSource dediees a la musique pour permettre le crossfade,
            // une troisieme pour les SFX en one-shot.
            if (musicSourceA == null || musicSourceB == null || sfxSource == null)
            {
                var sources = GetComponents<AudioSource>();
                Debug.LogWarning("[AudioManager] AudioSources non assignees dans l'inspecteur, verifier le cablage.");
            }

            activeMusicSource = musicSourceA;
            inactiveMusicSource = musicSourceB;

            if (activeMusicSource != null) activeMusicSource.volume = musicVolume;
            if (inactiveMusicSource != null) inactiveMusicSource.volume = 0f;
            if (sfxSource != null) sfxSource.volume = sfxVolume;
        }

        public void PlayMusic(AudioClip clip)
        {
            if (clip == null || activeMusicSource == null || inactiveMusicSource == null) return;

            if (crossfadeRoutine != null) StopCoroutine(crossfadeRoutine);
            crossfadeRoutine = StartCoroutine(CrossfadeTo(clip));
        }

        private IEnumerator CrossfadeTo(AudioClip clip)
        {
            inactiveMusicSource.clip = clip;
            inactiveMusicSource.volume = 0f;
            inactiveMusicSource.Play();

            float t = 0f;
            float startActiveVolume = activeMusicSource.volume;

            while (t < crossfadeDuration)
            {
                t += Time.deltaTime;
                float ratio = Mathf.Clamp01(t / crossfadeDuration);
                activeMusicSource.volume = Mathf.Lerp(startActiveVolume, 0f, ratio);
                inactiveMusicSource.volume = Mathf.Lerp(0f, musicVolume, ratio);
                yield return null;
            }

            activeMusicSource.Stop();

            var swap = activeMusicSource;
            activeMusicSource = inactiveMusicSource;
            inactiveMusicSource = swap;
        }

        public void PlaySFX(AudioClip clip)
        {
            if (clip == null || sfxSource == null) return;
            sfxSource.PlayOneShot(clip, sfxVolume);
        }

        public void SetMusicVolume(float volume)
        {
            musicVolume = Mathf.Clamp01(volume);
            if (activeMusicSource != null) activeMusicSource.volume = musicVolume;
        }

        public void SetSFXVolume(float volume)
        {
            sfxVolume = Mathf.Clamp01(volume);
            if (sfxSource != null) sfxSource.volume = sfxVolume;
        }
    }
}
