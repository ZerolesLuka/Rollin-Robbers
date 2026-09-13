using UnityEngine;

// A faulty bulb. Put this on the Light itself and it wobbles dimmer and brighter, and every so often cuts out for a
// split second. Purely cosmetic and local - every client runs its own flicker, and nobody can tell theirs is out of step.
//
// It only ever touches INTENSITY, never enabled. Lights.cs switches a zone on and off by toggling enabled, so as long as
// this stays off that field the light switch keeps full control: a flicker can dim a lit bulb, but it can never switch
// a dark room back on.
[RequireComponent(typeof(Light))]
public class FlickeringLight : MonoBehaviour
{
    [SerializeField, Range(0f, 1f)] private float dimAmount = 0.85f;            //how far the steady wobble dips, as a fraction of the authored brightness
    [SerializeField] private float wobbleSpeed = 6f;                            //how fast it wobbles. higher = more nervous
    [SerializeField] private float wobbleContrast = 2.5f;                       //stretches the noise toward its extremes. 1 = raw Perlin, which hovers near the middle and barely reads
    [SerializeField] private float dropoutChancePerSecond = 0.5f;               //how often it cuts out, on average per second. lower than a flicker would be, because each one is a real stretch of dark
    [SerializeField] private float dropoutMinimumSeconds = 0.4f;                //shortest blackout. long enough to register as the room going DARK, not a blink
    [SerializeField] private float dropoutMaximumSeconds = 0.6f;                //longest blackout
    [SerializeField, Range(0f, 1f)] private float dropoutBrightness = 0f;       //fully off during a blackout. relative to the bulb's own intensity (1.3 on the house bulbs), so any bulb works

    private Light bulb;
    private float baseIntensity;   //whatever the light was authored at, so the flicker scales it rather than replacing it
    private float dropoutTimer;    //seconds left on the current cut-out, 0 when there isn't one
    private float noiseOffset;     //random start point in the noise, so two faulty bulbs in one house don't flicker in unison

    private void Awake()
    {
        bulb = GetComponent<Light>();
        baseIntensity = bulb.intensity;
        noiseOffset = Random.Range(0f, 1000f);
    }

    private void Update()
    {
        //the switch owns on/off. if the zone is dark there's nothing to flicker - and writing intensity on a disabled
        //light would be harmless, but skipping it keeps the rule obvious
        if (!bulb.enabled)
        {
            return;
        }

        //mid cut-out: hold it almost dark until the timer runs out
        if (dropoutTimer > 0f)
        {
            dropoutTimer -= Time.deltaTime;
            bulb.intensity = baseIntensity * dropoutBrightness;
            return;
        }

        //roll for a new cut-out. multiplied by deltaTime so the chance is per SECOND, not per frame - otherwise a faster
        //machine would flicker more
        if (Random.value < dropoutChancePerSecond * Time.deltaTime)
        {
            dropoutTimer = Random.Range(dropoutMinimumSeconds, dropoutMaximumSeconds);
            return;
        }

        //PERLIN, NOT RANDOM. a fresh random value every frame reads as static on a TV; noise drifts smoothly between
        //values, which is what a bad connection in a bulb actually looks like
        float noise = Mathf.PerlinNoise(Time.time * wobbleSpeed + noiseOffset, 0f);

        //CONTRAST. raw Perlin spends almost all its time between about 0.3 and 0.7, so the first version barely dipped
        //at all. pushing it away from 0.5 and clamping makes it sit near full and near dim far more often, which is
        //what actually reads as a bulb struggling
        float stretchedNoise = Mathf.Clamp01((noise - 0.5f) * wobbleContrast + 0.5f);
        bulb.intensity = baseIntensity * (1f - dimAmount * stretchedNoise);
    }
}
