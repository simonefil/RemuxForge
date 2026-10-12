using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Splitting.Codecs
{
    /// <summary>
    /// Codec Annex B (H.264, H.265): flusso di NAL con start code, ricodifica all-intra con x264/x265 e parameter set come NAL
    /// dedicati
    /// </summary>
    public abstract class MkvSplitAnnexBCodecBase : MkvSplitByteCodecBase
    {
        #region Proprietà

        /// <summary>
        /// Qualità come CRF
        /// </summary>
        protected override string QualityOption => "-crf";

        /// <summary>
        /// Opzione ffmpeg dei parametri dell'encoder (-x264-params, -x265-params)
        /// </summary>
        protected abstract string ParamsOption { get; }

        /// <summary>
        /// pix_fmt della ricodifica quando il sorgente non lo dichiara
        /// </summary>
        protected abstract string DefaultPixFmt { get; }

        /// <summary>
        /// Tipi di NAL dei parameter set, tutti necessari prima del corpo copiato
        /// </summary>
        protected abstract int[] ParameterSetTypes { get; }

        /// <summary>
        /// Prefisso dell'intestazione prima dei parameter set, vuoto se non serve
        /// </summary>
        protected abstract byte[] HeaderPrefix { get; }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Aggiunge preset, intervallo dei keyframe a 1 senza B-frame, pix_fmt e colori, ripetuti nei parametri dell'encoder
        /// </summary>
        /// <param name="args">Argomenti in costruzione</param>
        /// <param name="p">Parametri video del sorgente</param>
        /// <param name="speed">Preset richiesto o predefinito</param>
        protected override void AddEncodeArgs(List<string> args, MkvSplitVideoParams p, string speed)
        {
            List<string> encParams = new List<string> { "keyint=1", "bframes=0" };

            args.Add("-" + this.Speed.Name); args.Add(speed);
            AddPictureArgs(args, p, this.DefaultPixFmt, encParams);
            args.Add(this.ParamsOption);
            args.Add(string.Join(":", encParams));
        }

        /// <summary>
        /// Indica se il prefisso arriva al primo NAL di un fotogramma
        /// </summary>
        /// <param name="data">Prefisso del flusso grezzo</param>
        /// <returns>True se il prefisso contiene un NAL di fotogramma</returns>
        protected override bool ReachesFirstPicture(byte[] data)
        {
            return this.FindNals(data).Exists(nal => this.IsPictureNal(nal.type));
        }

        /// <summary>
        /// Scrive il prefisso dell'intestazione e i NAL dei parameter set, fino ad averli tutti
        /// </summary>
        /// <param name="data">Prefisso del flusso grezzo</param>
        /// <param name="outputFile">File di output con l'intestazione</param>
        /// <exception cref="InvalidOperationException">Manca almeno un parameter set</exception>
        protected override void WriteParameterSets(byte[] data, string outputFile)
        {
            byte[] prefix = this.HeaderPrefix;
            int[] required = this.ParameterSetTypes;
            HashSet<int> found = new HashSet<int>();
            FileStream outF = null;

            try
            {
                outF = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None);
                outF.Write(prefix, 0, prefix.Length);
                foreach ((int start, int end, int type) nal in this.FindNals(data))
                {
                    if (Array.IndexOf(required, nal.type) >= 0)
                    {
                        outF.Write(data, nal.start, nal.end - nal.start);
                        found.Add(nal.type);
                    }

                    if (found.Count == required.Length) { break; }
                }
            }
            finally
            {
                if (outF != null) { outF.Dispose(); }
            }

            if (found.Count != required.Length)
            {
                throw new InvalidOperationException(AppText.T("split.exec.noParameterSets"));
            }
        }

        /// <summary>
        /// Tipo del NAL dal primo byte del suo header
        /// </summary>
        /// <param name="header">Primo byte dell'header del NAL</param>
        /// <returns>Tipo del NAL</returns>
        protected abstract int NalType(byte header);

        /// <summary>
        /// Indica se il tipo di NAL porta un fotogramma
        /// </summary>
        /// <param name="nalType">Tipo del NAL</param>
        /// <returns>True per un NAL di fotogramma</returns>
        protected abstract bool IsPictureNal(int nalType);

        #endregion

        #region Metodi privati

        /// <summary>
        /// Trova i NAL Annex B nel buffer, includendo lo start code nel range restituito
        /// </summary>
        /// <param name="data">Flusso grezzo</param>
        /// <returns>Inizio, fine esclusiva e tipo di ogni NAL</returns>
        private List<(int start, int end, int type)> FindNals(byte[] data)
        {
            List<(int start, int end, int type)> nals = new List<(int, int, int)>();
            List<int> starts = new List<int>();
            int pos = 0;
            int sc;

            while (pos < data.Length - 3)
            {
                sc = StartCodeLength(data, pos);
                if (sc > 0)
                {
                    starts.Add(pos);
                    pos += sc;
                }
                else
                {
                    pos++;
                }
            }

            for (int i = 0; i < starts.Count; i++)
            {
                int start = starts[i];
                int end = (i + 1 < starts.Count) ? starts[i + 1] : data.Length;
                int payload = start + StartCodeLength(data, start);
                if (payload >= end) { continue; }

                nals.Add((start, end, this.NalType(data[payload])));
            }
            return nals;
        }

        /// <summary>
        /// Lunghezza dello start code Annex B nella posizione indicata
        /// </summary>
        /// <param name="data">Flusso grezzo</param>
        /// <param name="offset">Posizione da controllare</param>
        /// <returns>3 o 4 se nella posizione c'è uno start code, altrimenti 0</returns>
        private static int StartCodeLength(byte[] data, int offset)
        {
            if (offset + 3 <= data.Length && data[offset] == 0 && data[offset + 1] == 0 && data[offset + 2] == 1)
            {
                return 3;
            }
            if (offset + 4 <= data.Length && data[offset] == 0 && data[offset + 1] == 0 && data[offset + 2] == 0 && data[offset + 3] == 1)
            {
                return 4;
            }
            return 0;
        }

        #endregion
    }
}
