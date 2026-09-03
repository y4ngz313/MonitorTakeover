// TypewriterAudio.cs — shared procedural typewriter tick. The takeover
// dialogue and the quota rewards ceremony play the same click, so the clip
// generator lives here rather than in either presentation.

using UnityEngine;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    internal static class TypewriterAudio
    {
        // Very short click/tick — a single-cycle pulse that sounds like a
        // teletype or old CRT terminal printing a character.
        internal static AudioClip CreateTick()
        {
            int sampleRate = 44100;
            int samples    = (int)(sampleRate * 0.02f); // 20 ms — short but audible
            float[] data   = new float[samples];

            for (int i = 0; i < samples; i++)
            {
                float t = (float)i / samples;
                // Sharp attack, fast decay — sounds like a relay click
                float env = Mathf.Exp(-t * 35f);
                // Mix a mid-frequency tick (1200 Hz) with a noise transient
                float tone  = Mathf.Sin(i * 2f * Mathf.PI * 1200f / sampleRate);
                float noise = Random.value * 2f - 1f;
                data[i] = (tone * 0.7f + noise * 0.3f) * env * 0.95f;
            }

            var clip = AudioClip.Create("Y4NGZ_Tick", samples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
