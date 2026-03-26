using System;
using UnityEngine;

public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance;

    private AudioSource audioSource;

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
            return;
        }

        audioSource = GetComponent<AudioSource>();
    }

    public void Play(AudioClip clip)
    {
        if (clip == null)
        {
            Debug.LogError("AudioManager: clip is null!");
            return;
        }
        if (audioSource == null)
        {
            Debug.LogError("AudioManager: AudioSource is null!");
            return;
        }

        Debug.Log($"Playing: {clip.name}");
        audioSource.PlayOneShot(clip);
    }
}