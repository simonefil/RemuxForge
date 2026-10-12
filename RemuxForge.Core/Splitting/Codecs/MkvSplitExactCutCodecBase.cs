using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting.Cutters;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Splitting.Codecs
{
    /// <summary>
    /// Codec che si taglia al fotogramma ricodificando testa e coda con lo stesso codec: encoder, range di qualità e velocità
    /// della tabella e argomenti ffmpeg della ricodifica
    /// </summary>
    public abstract class MkvSplitExactCutCodecBase : MkvSplitVideoCodecBase
    {
        #region Proprietà

        /// <summary>
        /// Encoder ffmpeg della ricodifica
        /// </summary>
        public string Encoder { get; init; }

        /// <summary>
        /// Nome tecnico del parametro di qualità (CRF o q)
        /// </summary>
        public string QualityName { get; init; }

        /// <summary>
        /// Qualità predefinita
        /// </summary>
        public int QualityDefault { get; init; }

        /// <summary>
        /// Qualità minima ammessa
        /// </summary>
        public int QualityMin { get; init; }

        /// <summary>
        /// Qualità massima ammessa
        /// </summary>
        public int QualityMax { get; init; }

        /// <summary>
        /// Parametro di velocità dell'encoder, null se l'encoder non lo prevede
        /// </summary>
        public MkvSplitEncodeSpeedBase Speed { get; init; }

        /// <summary>
        /// Muxer ffmpeg delle parti ricodificate o copiate, da passare a -f
        /// </summary>
        public abstract string RawMuxer { get; }

        /// <summary>
        /// Filtro di bitstream ffmpeg che riporta in banda i parameter set del codec private, per la clip copiata che in un montaggio
        /// segue una clip tagliata. Per default nessuno: il video copiato li porta già in banda ai keyframe, oppure non ne ha
        /// </summary>
        public virtual string InBandParameterSetsFilter => "";

        /// <summary>
        /// Opzione ffmpeg con cui l'encoder riceve la qualità
        /// </summary>
        protected abstract string QualityOption { get; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Costruisce gli argomenti dell'encoder per testa e coda ricodificate: qualità e velocità dalle opzioni del lavoro,
        /// oppure i default del codec; pix_fmt e colori dal sorgente
        /// </summary>
        /// <param name="p">Parametri video del sorgente</param>
        /// <param name="options">Opzioni Split del lavoro</param>
        /// <returns>Argomenti ffmpeg dell'encoder</returns>
        public List<string> BuildHeadEncodeArgs(MkvSplitVideoParams p, MkvSplitOptions options)
        {
            int quality = options.EncodeQuality ?? this.QualityDefault;
            string speed = string.IsNullOrWhiteSpace(options.EncodeSpeed) ? this.Speed?.Default : options.EncodeSpeed.Trim().ToLowerInvariant();
            List<string> args = new List<string> { "-c:v", this.SelectEncoder(p), this.QualityOption, quality.ToString(CultureInfo.InvariantCulture) };

            this.AddEncodeArgs(args, p, speed);
            return args;
        }

        /// <summary>
        /// Crea la strategia del taglio al fotogramma del metodo del codec: il piano ne legge il costo della testa, l'esecuzione la
        /// prepara una volta e taglia con lei le clip ricodificate
        /// </summary>
        /// <returns>Strategia nuova, legata al codec</returns>
        public abstract MkvSplitExactCutterBase CreateCutter();

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Encoder della ricodifica per il sorgente indicato. Per default quello della tabella
        /// </summary>
        /// <param name="p">Parametri video del sorgente</param>
        /// <returns>Nome dell'encoder ffmpeg</returns>
        protected virtual string SelectEncoder(MkvSplitVideoParams p)
        {
            return this.Encoder;
        }

        /// <summary>
        /// Aggiunge gli argomenti dell'encoder che seguono encoder e qualità
        /// </summary>
        /// <param name="args">Argomenti in costruzione</param>
        /// <param name="p">Parametri video del sorgente</param>
        /// <param name="speed">Velocità richiesta o predefinita, null se l'encoder non la prevede</param>
        protected abstract void AddEncodeArgs(List<string> args, MkvSplitVideoParams p, string speed);

        /// <summary>
        /// Aggiunge pix_fmt e colori del sorgente; per x264 e x265 i colori vanno anche nei parametri dell'encoder
        /// </summary>
        /// <param name="args">Argomenti in costruzione</param>
        /// <param name="p">Parametri video del sorgente</param>
        /// <param name="defaultPixFmt">pix_fmt quando il sorgente non lo dichiara</param>
        /// <param name="encParams">Parametri dell'encoder che ricevono anche i colori, null se l'encoder non li prevede</param>
        protected static void AddPictureArgs(List<string> args, MkvSplitVideoParams p, string defaultPixFmt, List<string> encParams)
        {
            args.Add("-pix_fmt"); args.Add(p.PixFmt != null ? p.PixFmt : defaultPixFmt);
            if (p.ColorSpace != null)
            {
                args.Add("-colorspace"); args.Add(p.ColorSpace);
                encParams?.Add("colormatrix=" + p.ColorSpace);
            }
            if (p.ColorPrimaries != null)
            {
                args.Add("-color_primaries"); args.Add(p.ColorPrimaries);
                encParams?.Add("colorprim=" + p.ColorPrimaries);
            }
            if (p.ColorTransfer != null)
            {
                args.Add("-color_trc"); args.Add(p.ColorTransfer);
                encParams?.Add("transfer=" + p.ColorTransfer);
            }
            if (p.ColorRange != null)
            {
                args.Add("-color_range"); args.Add(p.ColorRange);
                encParams?.Add("range=" + (p.ColorRange == "pc" ? "full" : "limited"));
            }
        }

        #endregion
    }
}
