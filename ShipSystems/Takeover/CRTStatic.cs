// CRTStatic.cs — shared CRT channel-change frame and audio generators. The
// takeover's transition and the quota rewards ceremony's restore burst play
// the same white/snow/black sequence, so the generators live here rather
// than in either presentation (same split as TypewriterAudio).

using UnityEngine;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    internal static class CRTStatic
    {
        internal static Texture2D CreateSolidTexture(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        internal static Texture2D CreateNoiseTexture(int width, int height)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point; // sharp pixels — authentic CRT static
            var pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                float v = Random.value;
                pixels[i] = new Color(v, v, v, 1f);
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        // Short white-noise burst — the crackle of a TV between channels.
        internal static AudioClip CreateBurstClip()
        {
            int sampleRate = 44100;
            int samples    = (int)(sampleRate * 0.35f);
            float[] data   = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                float env = 1f;
                if (i > samples * 0.75f)
                    env = 1f - ((float)(i - samples * 0.75f) / (samples * 0.25f));
                data[i] = (Random.value * 2f - 1f) * 0.45f * env;
            }
            var clip = AudioClip.Create("Y4NGZ_Static", samples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
