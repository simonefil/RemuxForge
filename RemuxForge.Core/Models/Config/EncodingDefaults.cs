namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Valori di default e liste dropdown hardcoded per i codec di encoding
    /// </summary>
    public static class EncodingDefaults
    {
        #region Codec disponibili

        /// <summary>
        /// Lista codec supportati
        /// </summary>
        public static readonly string[] CODECS = new string[]
        {
            "libx264", "libx265", "libsvtav1"
        };

        #endregion

        #region Modalità rate control

        /// <summary>
        /// Modalità rate control per x264/x265
        /// </summary>
        public static readonly string[] RATE_MODES_X26X = new string[]
        {
            "crf", "bitrate"
        };

        /// <summary>
        /// Modalità rate control per svtav1
        /// </summary>
        public static readonly string[] RATE_MODES_SVTAV1 = new string[]
        {
            "crf", "qp", "bitrate"
        };

        #endregion

        #region libx264

        /// <summary>
        /// Preset x264
        /// </summary>
        public static readonly string[] X264_PRESETS = new string[]
        {
            "ultrafast", "superfast", "veryfast", "faster", "fast",
            "medium", "slow", "slower", "veryslow", "placebo"
        };

        /// <summary>
        /// Tune x264
        /// </summary>
        public static readonly string[] X264_TUNES = new string[]
        {
            "default", "film", "animation", "grain", "stillimage",
            "psnr", "ssim", "zerolatency", "fastdecode"
        };

        /// <summary>
        /// Profili x264
        /// </summary>
        public static readonly string[] X264_PROFILES = new string[]
        {
            "default", "baseline", "main", "high", "high10", "high422", "high444"
        };

        /// <summary>
        /// Bit depth x264
        /// </summary>
        public static readonly string[] X264_BIT_DEPTHS = new string[]
        {
            "8-bit: yuv420p",
            "10-bit: yuv420p10le",
            "8-bit 422: yuv422p",
            "8-bit 444: yuv444p",
            "10-bit 422: yuv422p10le",
            "10-bit 444: yuv444p10le"
        };

        /// <summary>
        /// CRF di default x264
        /// </summary>
        public const int X264_CRF_DEFAULT = 23;

        /// <summary>
        /// CRF minimo x264
        /// </summary>
        public const int X264_CRF_MIN = 0;

        /// <summary>
        /// CRF massimo x264
        /// </summary>
        public const int X264_CRF_MAX = 51;

        #endregion

        #region libx265

        /// <summary>
        /// Preset x265
        /// </summary>
        public static readonly string[] X265_PRESETS = new string[]
        {
            "ultrafast", "superfast", "veryfast", "faster", "fast",
            "medium", "slow", "slower", "veryslow", "placebo"
        };

        /// <summary>
        /// Tune x265
        /// </summary>
        public static readonly string[] X265_TUNES = new string[]
        {
            "default", "psnr", "ssim", "grain", "zerolatency", "fastdecode", "animation"
        };

        /// <summary>
        /// Profili x265
        /// </summary>
        public static readonly string[] X265_PROFILES = new string[]
        {
            "default", "main", "main10", "mainstillpicture", "msp",
            "main-intra", "main10-intra",
            "main444-8", "main444-intra", "main444-stillpicture",
            "main422-10", "main422-10-intra",
            "main444-10", "main444-10-intra",
            "main12", "main12-intra",
            "main422-12", "main422-12-intra",
            "main444-12", "main444-12-intra",
            "main444-16-intra", "main444-16-stillpicture"
        };

        /// <summary>
        /// Bit depth x265
        /// </summary>
        public static readonly string[] X265_BIT_DEPTHS = new string[]
        {
            "8-bit: yuv420p",
            "10-bit: yuv420p10le",
            "12-bit: yuv420p12le",
            "8-bit 422: yuv422p",
            "8-bit 444: yuv444p",
            "10-bit 422: yuv422p10le",
            "10-bit 444: yuv444p10le",
            "12-bit 422: yuv422p12le",
            "12-bit 444: yuv444p12le"
        };

        /// <summary>
        /// CRF di default x265
        /// </summary>
        public const int X265_CRF_DEFAULT = 28;

        /// <summary>
        /// CRF minimo x265
        /// </summary>
        public const int X265_CRF_MIN = 0;

        /// <summary>
        /// CRF massimo x265
        /// </summary>
        public const int X265_CRF_MAX = 51;

        #endregion

        #region libsvtav1

        /// <summary>
        /// Preset svtav1 (0 = lento/migliore, 13 = veloce/peggiore)
        /// </summary>
        public static readonly string[] SVTAV1_PRESETS = new string[]
        {
            "0", "1", "2", "3", "4", "5", "6",
            "7", "8", "9", "10", "11", "12", "13"
        };

        /// <summary>
        /// Tune svtav1
        /// </summary>
        public static readonly string[] SVTAV1_TUNES = new string[]
        {
            "0 - VQ (Psychovisual)", "1 - PSNR", "2 - SSIM"
        };

        /// <summary>
        /// Bit depth svtav1
        /// </summary>
        public static readonly string[] SVTAV1_BIT_DEPTHS = new string[]
        {
            "8-bit: yuv420p",
            "10-bit: yuv420p10le"
        };

        /// <summary>
        /// CRF di default svtav1
        /// </summary>
        public const int SVTAV1_CRF_DEFAULT = 35;

        /// <summary>
        /// CRF minimo svtav1
        /// </summary>
        public const int SVTAV1_CRF_MIN = 0;

        /// <summary>
        /// CRF massimo svtav1
        /// </summary>
        public const int SVTAV1_CRF_MAX = 63;

        /// <summary>
        /// Film grain minimo svtav1
        /// </summary>
        public const int SVTAV1_FILM_GRAIN_MIN = 0;

        /// <summary>
        /// Film grain massimo svtav1
        /// </summary>
        public const int SVTAV1_FILM_GRAIN_MAX = 50;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Restituisce i preset per il codec specificato
        /// </summary>
        /// <param name="codec">Nome codec</param>
        /// <returns>Array di preset</returns>
        public static string[] GetPresets(string codec)
        {
            string[] result = X265_PRESETS;

            if (codec == "libx264") { result = X264_PRESETS; }
            else if (codec == "libx265") { result = X265_PRESETS; }
            else if (codec == "libsvtav1") { result = SVTAV1_PRESETS; }

            return result;
        }

        /// <summary>
        /// Restituisce le opzioni tune per il codec specificato
        /// </summary>
        /// <param name="codec">Nome codec</param>
        /// <returns>Array di tune</returns>
        public static string[] GetTunes(string codec)
        {
            string[] result = X265_TUNES;

            if (codec == "libx264") { result = X264_TUNES; }
            else if (codec == "libx265") { result = X265_TUNES; }
            else if (codec == "libsvtav1") { result = SVTAV1_TUNES; }

            return result;
        }

        /// <summary>
        /// Restituisce i profili per il codec specificato
        /// </summary>
        /// <param name="codec">Nome codec</param>
        /// <returns>Array di profili, vuoto per svtav1</returns>
        public static string[] GetProfiles(string codec)
        {
            string[] result = new string[0];

            if (codec == "libx264") { result = X264_PROFILES; }
            else if (codec == "libx265") { result = X265_PROFILES; }

            return result;
        }

        /// <summary>
        /// Restituisce le opzioni bit depth per il codec specificato
        /// </summary>
        /// <param name="codec">Nome codec</param>
        /// <returns>Array di bit depth</returns>
        public static string[] GetBitDepths(string codec)
        {
            string[] result = X265_BIT_DEPTHS;

            if (codec == "libx264") { result = X264_BIT_DEPTHS; }
            else if (codec == "libx265") { result = X265_BIT_DEPTHS; }
            else if (codec == "libsvtav1") { result = SVTAV1_BIT_DEPTHS; }

            return result;
        }

        /// <summary>
        /// Restituisce le modalità rate control per il codec specificato
        /// </summary>
        /// <param name="codec">Nome codec</param>
        /// <returns>Array di modalità</returns>
        public static string[] GetRateModes(string codec)
        {
            string[] result = RATE_MODES_X26X;

            if (codec == "libsvtav1") { result = RATE_MODES_SVTAV1; }

            return result;
        }

        /// <summary>
        /// Restituisce il CRF di default per il codec specificato
        /// </summary>
        /// <param name="codec">Nome codec</param>
        /// <returns>Valore CRF default</returns>
        public static int GetDefaultCrf(string codec)
        {
            int result = X265_CRF_DEFAULT;

            if (codec == "libx264") { result = X264_CRF_DEFAULT; }
            else if (codec == "libx265") { result = X265_CRF_DEFAULT; }
            else if (codec == "libsvtav1") { result = SVTAV1_CRF_DEFAULT; }

            return result;
        }

        /// <summary>
        /// Restituisce il CRF minimo per il codec specificato
        /// </summary>
        /// <param name="codec">Nome codec</param>
        /// <returns>Valore CRF minimo</returns>
        public static int GetMinCrf(string codec)
        {
            int result = X265_CRF_MIN;

            if (codec == "libx264") { result = X264_CRF_MIN; }
            else if (codec == "libx265") { result = X265_CRF_MIN; }
            else if (codec == "libsvtav1") { result = SVTAV1_CRF_MIN; }

            return result;
        }

        /// <summary>
        /// Restituisce il CRF massimo per il codec specificato
        /// </summary>
        /// <param name="codec">Nome codec</param>
        /// <returns>Valore CRF massimo</returns>
        public static int GetMaxCrf(string codec)
        {
            int result = X265_CRF_MAX;

            if (codec == "libx264") { result = X264_CRF_MAX; }
            else if (codec == "libx265") { result = X265_CRF_MAX; }
            else if (codec == "libsvtav1") { result = SVTAV1_CRF_MAX; }

            return result;
        }

        /// <summary>
        /// Indica se il codec supporta il campo Profile
        /// </summary>
        /// <param name="codec">Nome codec</param>
        /// <returns>True se supporta profile</returns>
        public static bool HasProfile(string codec)
        {
            bool result = false;

            if (codec == "libx264" || codec == "libx265") { result = true; }

            return result;
        }

        /// <summary>
        /// Indica se il codec supporta film grain
        /// </summary>
        /// <param name="codec">Nome codec</param>
        /// <returns>True se supporta film grain</returns>
        public static bool HasFilmGrain(string codec)
        {
            bool result = false;

            if (codec == "libsvtav1") { result = true; }

            return result;
        }

        /// <summary>
        /// Indica se il codec supporta multi-pass in modalità bitrate
        /// </summary>
        /// <param name="codec">Nome codec</param>
        /// <returns>True se supporta multi-pass</returns>
        public static bool HasMultiPass(string codec)
        {
            bool result = false;

            if (codec == "libx264" || codec == "libx265") { result = true; }

            return result;
        }

        #endregion
    }
}
