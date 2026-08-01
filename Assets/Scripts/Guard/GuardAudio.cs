using UnityEngine;
using Fusion;

// Reusable voice for any guard archetype. Owns the clips + the networked bark RPC so every client hears it.
// Decoupled from any specific FSM: the brain asks for a generic BarkType (not a state), so the dog reuses
// this with its own clips. It's a NetworkBehaviour because RPCs must live on one.
public class GuardAudio : NetworkBehaviour
{
    public enum BarkType { Alert, Search, Chase, Caught, GiveUp } //generic categories any guard maps its own states onto

    [SerializeField] private AudioSource voiceSource;   //3D source on the guard
    [SerializeField] private AudioClip[] alertSounds;   //"huh? who's there?"
    [SerializeField] private AudioClip[] searchSounds;  //"I know you're in here"
    [SerializeField] private AudioClip[] chaseSounds;   //"HEY! GET OUT!"
    [SerializeField] private AudioClip[] caughtSounds;  //"GOTCHA!"
    [SerializeField] private AudioClip[] giveUpSounds;  //"musta been nothin'"

    [SerializeField] private float barkCooldown = 1.5f; //min seconds between barks
    private float barkCooldownTimer;

    private void Awake()
    {
        //his voice is the single most important sound in the game to place correctly - "he's in the next room" versus
        //"he's in THIS room" is the whole decision you're making when you hear him
        AudioOcclusion.Attach(voiceSource);
    }

    private AudioClip[] ClipsFor(BarkType type)
    {
        switch (type)
        {
            case BarkType.Alert:  return alertSounds;
            case BarkType.Search: return searchSounds;
            case BarkType.Chase:  return chaseSounds;
            case BarkType.Caught: return caughtSounds;
            case BarkType.GiveUp: return giveUpSounds;
            default: return null;
        }
    }

    private void Update() //cooldown just rate-limits the authority's Bark calls, so a local tick is fine
    {
        if (barkCooldownTimer > 0f)
        {
            barkCooldownTimer -= Time.deltaTime;
        }
    }

    public void Bark(BarkType type) //called by the brain on the state authority
    {
        if (barkCooldownTimer > 0f)
        {
            return;
        }
        AudioClip[] clips = ClipsFor(type);
        if (clips != null && clips.Length > 0)
        {
            int index = Random.Range(0, clips.Length); //chosen once on the authority so everyone plays the same clip
            barkCooldownTimer = barkCooldown;
            RPC_PlayBark(index, type);
        }
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_PlayBark(int index, BarkType type)
    {
        AudioClip[] clips = ClipsFor(type);
        if (voiceSource != null && clips != null && index >= 0 && index < clips.Length)
        {
            voiceSource.Stop();              //cut any bark still playing so two voices never overlap
            voiceSource.clip = clips[index];
            voiceSource.Play();
        }
    }
}
