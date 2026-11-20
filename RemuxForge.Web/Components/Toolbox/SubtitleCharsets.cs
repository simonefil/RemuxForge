using RemuxForge.Core.Localization;
using RemuxForge.Core.Toolbox.AddTracks;
using System.Collections.Generic;

namespace RemuxForge.Web.Components.Toolbox
{
    /// <summary>
    /// Codifiche dei sottotitoli di testo offerte dalle tendine delle card, UTF-8 per prima
    /// </summary>
    public static class SubtitleCharsets
    {
        #region Variabili statiche

        /// <summary>
        /// Codifiche offerte per i sottotitoli di testo che non sono UTF-8, con il nome che mkvmerge accetta per --sub-charset e la
        /// chiave della regione
        /// </summary>
        private static readonly KeyValuePair<string, string>[] s_subtitleCharsets = new KeyValuePair<string, string>[]
        {
            new KeyValuePair<string, string>("windows-1252", "web.toolbox.addTracks.charsetWestern"),
            new KeyValuePair<string, string>("ISO-8859-1", "web.toolbox.addTracks.charsetWestern"),
            new KeyValuePair<string, string>("ISO-8859-15", "web.toolbox.addTracks.charsetWesternEuro"),
            new KeyValuePair<string, string>("windows-1250", "web.toolbox.addTracks.charsetCentral"),
            new KeyValuePair<string, string>("ISO-8859-2", "web.toolbox.addTracks.charsetCentral"),
            new KeyValuePair<string, string>("windows-1251", "web.toolbox.addTracks.charsetCyrillic"),
            new KeyValuePair<string, string>("KOI8-R", "web.toolbox.addTracks.charsetRussian"),
            new KeyValuePair<string, string>("windows-1253", "web.toolbox.addTracks.charsetGreek"),
            new KeyValuePair<string, string>("ISO-8859-7", "web.toolbox.addTracks.charsetGreek"),
            new KeyValuePair<string, string>("windows-1254", "web.toolbox.addTracks.charsetTurkish"),
            new KeyValuePair<string, string>("ISO-8859-9", "web.toolbox.addTracks.charsetTurkish"),
            new KeyValuePair<string, string>("windows-1257", "web.toolbox.addTracks.charsetBaltic"),
            new KeyValuePair<string, string>("windows-1255", "web.toolbox.addTracks.charsetHebrew"),
            new KeyValuePair<string, string>("windows-1256", "web.toolbox.addTracks.charsetArabic"),
            new KeyValuePair<string, string>("windows-874", "web.toolbox.addTracks.charsetThai"),
            new KeyValuePair<string, string>("windows-1258", "web.toolbox.addTracks.charsetVietnamese"),
            new KeyValuePair<string, string>("Shift_JIS", "web.toolbox.addTracks.charsetJapanese"),
            new KeyValuePair<string, string>("EUC-JP", "web.toolbox.addTracks.charsetJapanese"),
            new KeyValuePair<string, string>("GB18030", "web.toolbox.addTracks.charsetChineseSimplified"),
            new KeyValuePair<string, string>("Big5", "web.toolbox.addTracks.charsetChineseTraditional"),
            new KeyValuePair<string, string>("EUC-KR", "web.toolbox.addTracks.charsetKorean")
        };

        #endregion

        #region Proprietà

        /// <summary>
        /// Codifiche della tendina, nella lingua dell'interfaccia: il nome per mkvmerge e l'etichetta con regione e nome
        /// </summary>
        public static List<KeyValuePair<string, string>> Choices
        {
            get
            {
                List<KeyValuePair<string, string>> choices = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>(AddTracksService.DEFAULT_SUBTITLE_CHARSET, AddTracksService.DEFAULT_SUBTITLE_CHARSET)
                };

                foreach (KeyValuePair<string, string> charset in s_subtitleCharsets)
                    choices.Add(new KeyValuePair<string, string>(charset.Key, AppText.T(charset.Value) + " (" + charset.Key + ")"));

                return choices;
            }
        }

        #endregion
    }
}
