using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting.Codecs;
using System;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Splitting
{
    /// <summary>
    /// Video di un sorgente come lo giudica lo Split: codec, scansione e, da questi, se si taglia al fotogramma o solo al keyframe
    /// e perché. Lo dà l'analisi o la lettura del file originale; il motivo si scrive nella lingua corrente a ogni lettura
    /// </summary>
    public sealed class MkvSplitVideoProfile
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="codecName">Codec come lo riporta ffprobe, null se non rilevato</param>
        /// <param name="scanType">Scansione letta da MediaInfo, vuota se non dichiarata</param>
        public MkvSplitVideoProfile(string codecName, VideoScanType scanType)
        {
            this.CodecName = codecName;
            this.ScanType = scanType;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Codec come lo riporta ffprobe, null se non rilevato
        /// </summary>
        public string CodecName { get; }

        /// <summary>
        /// Scansione letta da MediaInfo, vuota se non dichiarata (P4: progressivo)
        /// </summary>
        public VideoScanType ScanType { get; }

        /// <summary>
        /// Codec della tabella con la ricodifica di testa e coda, anche su video interlacciato: dà la strategia e il costo della
        /// testa. Null se il codec si taglia solo al keyframe, è fuori tabella o non è rilevato
        /// </summary>
        public MkvSplitExactCutCodecBase ExactCutCodec => MkvSplitCodecCapabilities.FindExactCut(this.CodecName);

        /// <summary>
        /// Motivo localizzato del solo taglio al keyframe, vuoto se il taglio al fotogramma è disponibile: codec senza ricodifica
        /// oppure video interlacciato, che non ha un fotogramma da ricodificare al giunto (D07)
        /// </summary>
        public string KeyframeOnlyReason => this.ExactCutCodec == null ? AppText.F("split.unsupportedVideoCodec", MkvSplitCodecCapabilities.GetDisplayName(this.CodecName))
            : this.ScanType.IsInterlaced ? AppText.F("split.interlacedKeyframeOnly", this.ScanType) : "";

        /// <summary>
        /// True se il video si può tagliare al fotogramma, cioè con la ricodifica della testa
        /// </summary>
        public bool IsExactCutAvailable => this.KeyframeOnlyReason.Length == 0;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Legge codec e scansione dal file originale, con ffprobe e MediaInfo, senza normalizzarlo
        /// </summary>
        /// <param name="file">File da leggere</param>
        /// <returns>Profilo del file, con il codec null se ffprobe non lo legge</returns>
        public static MkvSplitVideoProfile Read(string file)
        {
            string codec;
            try
            {
                codec = MkvSplitExternalTools.Instance.GetVideoParams(file).CodecName;
            }
            catch (InvalidOperationException)
            {
                codec = null;
            }
            return new MkvSplitVideoProfile(codec, MkvSplitExternalTools.Instance.GetVideoScanType(file));
        }

        /// <summary>
        /// Verifica che i file di un batch abbiano lo stesso codec video e lo stesso tipo di scansione, progressivo o
        /// interlacciato (D14, P4). Legge i file originali come il wizard per un file singolo, senza normalizzare
        /// </summary>
        /// <param name="files">File del batch</param>
        /// <returns>Profilo del primo file ed errore con i file diversi da lui</returns>
        public static MkvSplitBatchVideoCheck CheckBatch(List<string> files)
        {
            List<string> different = new List<string>();
            MkvSplitVideoProfile first = new MkvSplitVideoProfile(null, new VideoScanType(""));

            for (int i = 0; i < files.Count; i++)
            {
                MkvSplitVideoProfile profile = Read(files[i]);
                if (i == 0)
                    first = profile;
                else if (profile.CodecName != first.CodecName || profile.ScanType.IsInterlaced != first.ScanType.IsInterlaced)
                    different.Add(profile.DescribeForBatch(files[i]));
            }

            return new MkvSplitBatchVideoCheck(first, different.Count == 0 ? "" : AppText.F("split.batch.videoMismatch", first.DescribeForBatch(files[0]), string.Join("; ", different)));
        }

        /// <summary>
        /// Indica se un fotogramma è un punto di taglio senza ricodifica: keyframe chiuso oppure, su video interlacciato
        /// che non si può ricodificare (D07), keyframe di GOP aperto con i soli B iniziali, che il taglio perde (D25)
        /// </summary>
        /// <param name="frame">Fotogramma del confine, in ordine di presentazione</param>
        /// <returns>True se mkvmerge può tagliare sul fotogramma</returns>
        public bool IsCopyCutKeyframe(MkvSplitFrameInfo frame)
        {
            return frame.Key && (!frame.OpenGop || (frame.LeadingFrames > 0 && this.ScanType.IsInterlaced));
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Nome del file con codec e tipo di scansione, per l'errore del batch non omogeneo
        /// </summary>
        /// <param name="file">File del batch</param>
        /// <returns>Descrizione localizzata</returns>
        private string DescribeForBatch(string file)
        {
            return AppText.F("split.batch.videoFile", Path.GetFileName(file), this.CodecName != null ? MkvSplitCodecCapabilities.GetDisplayName(this.CodecName) : "?",
                this.ScanType.IsInterlaced ? AppText.F("split.batch.interlaced", this.ScanType) : AppText.T("split.batch.progressive"));
        }

        #endregion
    }

    /// <summary>
    /// Esito della verifica del video di un batch: il profilo del primo file e l'errore con i file diversi
    /// </summary>
    public sealed class MkvSplitBatchVideoCheck
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="profile">Profilo del primo file</param>
        /// <param name="mismatch">Errore localizzato con i file diversi dal primo, vuoto se il batch è omogeneo</param>
        public MkvSplitBatchVideoCheck(MkvSplitVideoProfile profile, string mismatch)
        {
            this.Profile = profile;
            this.Mismatch = mismatch;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Profilo del primo file, comune a tutto il batch se omogeneo
        /// </summary>
        public MkvSplitVideoProfile Profile { get; }

        /// <summary>
        /// Errore localizzato con i file diversi dal primo, vuoto se il batch è omogeneo
        /// </summary>
        public string Mismatch { get; }

        #endregion
    }
}
