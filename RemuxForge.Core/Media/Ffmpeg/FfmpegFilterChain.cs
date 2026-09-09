using RemuxForge.Core.Models;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Media.Ffmpeg
{
    /// <summary>
    /// Compone la catena passata a -vf dalle decodifiche di analisi.
    /// Le decodifiche che si confrontano fra loro devono montare gli stessi anelli negli stessi
    /// termini, o i fotogrammi che arrivano alle misure non sono più gli stessi
    /// </summary>
    public class FfmpegFilterChain
    {
        #region Costanti

        /// <summary>
        /// Copia il piano di luminanza senza swscale, che su otto bit rende un pixel diverso fra
        /// SIMD x86 e percorso scalare ARM. Il formato va fissato prima, o l'accelerazione hardware
        /// consegna un formato di GPU che extractplanes non sa negoziare
        /// </summary>
        private const string LUMA_PLANE = "format=yuv420p,extractplanes=y";

        /// <summary>
        /// Variante a dieci bit per le ancore SIFT, che vivono sui gradienti fini: ridurre la
        /// profondità prima della scala toglie punti caratteristici. I segnali dHash stanno a otto
        /// bit, come le soglie che li giudicano
        /// </summary>
        private const string LUMA_PLANE_FULL_DEPTH = "format=yuv420p10le,extractplanes=y";

        /// <summary>
        /// Espande l'intervallo televisivo a scala piena con una tabella esatta, prima della scala:
        /// senza, il nero varrebbe sedici invece di zero e le soglie di EditAnalysisProfile sono
        /// tarate sullo zero. Serve solo dove la luminanza incontra una soglia, non dove i
        /// fotogrammi si confrontano fra loro
        /// </summary>
        private const string LUMA_FULL_RANGE = "lut=y='clip((val-16)*255/219,0,255)'";

        /// <summary>
        /// Disattiva l'arrotondamento veloce della SIMD x86, che scosta i pixel di uno rispetto al
        /// percorso scalare di ARM
        /// </summary>
        private const string SCALE_FLAGS = "flags=bicubic+accurate_rnd";

        #endregion

        #region Campi privati

        /// <summary>
        /// Anelli nell'ordine in cui il chiamante li ha montati
        /// </summary>
        private readonly List<string> _filters;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore che apre una catena vuota
        /// </summary>
        public FfmpegFilterChain()
        {
            this._filters = new List<string>();
        }

        #endregion

        #region Metodi statici

        /// <summary>
        /// Indica se la configurazione dichiara bordi da ritagliare, per chi deve scegliere fra
        /// questo ritaglio e un altro
        /// </summary>
        /// <param name="cropPx">Ritaglio nel formato sinistra:destra:alto:basso</param>
        /// <returns>True quando almeno un bordo è diverso da zero</returns>
        public static bool HasAnalysisCrop(string cropPx)
        {
            return Options.TryParseAnalysisCropPx(cropPx, out int left, out int right, out int top, out int bottom) &&
                (left != 0 || right != 0 || top != 0 || bottom != 0);
        }

        #endregion

        #region Metodi pubblici - Selezione

        /// <summary>
        /// Ritaglia i bordi dichiarati dalla configurazione di analisi, se ce ne sono
        /// </summary>
        /// <param name="cropPx">Ritaglio nel formato sinistra:destra:alto:basso</param>
        /// <returns>La catena stessa</returns>
        public FfmpegFilterChain AnalysisCrop(string cropPx)
        {
            if (!HasAnalysisCrop(cropPx))
                return this;
            Options.TryParseAnalysisCropPx(cropPx, out int left, out int right, out int top, out int bottom);
            this._filters.Add("crop=iw-" + left.ToString(CultureInfo.InvariantCulture) + "-" + right.ToString(CultureInfo.InvariantCulture) +
                ":ih-" + top.ToString(CultureInfo.InvariantCulture) + "-" + bottom.ToString(CultureInfo.InvariantCulture) +
                ":" + left.ToString(CultureInfo.InvariantCulture) + ":" + top.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        /// <summary>
        /// Ritaglia al rapporto quattro terzi intorno al centro
        /// </summary>
        /// <returns>La catena stessa</returns>
        public FfmpegFilterChain FourThreeCrop()
        {
            this._filters.Add("crop=ih*4/3:ih");
            return this;
        }

        /// <summary>
        /// Campiona a frequenza fissa
        /// </summary>
        /// <param name="fps">Fotogrammi al secondo</param>
        /// <returns>La catena stessa</returns>
        public FfmpegFilterChain SampleFps(int fps)
        {
            this._filters.Add("fps=" + fps.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        /// <summary>
        /// Campiona alla frequenza richiesta arrotondando al fotogramma più vicino
        /// </summary>
        /// <param name="fps">Fotogrammi al secondo</param>
        /// <returns>La catena stessa</returns>
        public FfmpegFilterChain TargetFps(double fps)
        {
            this._filters.Add("fps=fps=" + fps.ToString("R", CultureInfo.InvariantCulture) + ":round=near");
            return this;
        }

        /// <summary>
        /// Tiene un fotogramma ogni intervallo, misurato sul tempo di presentazione
        /// </summary>
        /// <param name="seconds">Intervallo fra due fotogrammi tenuti</param>
        /// <returns>La catena stessa</returns>
        public FfmpegFilterChain SampleInterval(double seconds)
        {
            this._filters.Add("select='isnan(prev_selected_t)+gte(t-prev_selected_t\\," + seconds.ToString("R", CultureInfo.InvariantCulture) + ")'");
            return this;
        }

        #endregion

        #region Metodi pubblici - Luminanza

        /// <summary>
        /// Porta il fotogramma sul piano di luminanza a otto bit
        /// </summary>
        /// <returns>La catena stessa</returns>
        public FfmpegFilterChain LumaPlane()
        {
            this._filters.Add(LUMA_PLANE);
            return this;
        }

        /// <summary>
        /// Porta il fotogramma sul piano di luminanza a dieci bit
        /// </summary>
        /// <returns>La catena stessa</returns>
        public FfmpegFilterChain LumaPlaneFullDepth()
        {
            this._filters.Add(LUMA_PLANE_FULL_DEPTH);
            return this;
        }

        /// <summary>
        /// Espande la luminanza a scala piena
        /// </summary>
        /// <returns>La catena stessa</returns>
        public FfmpegFilterChain FullRange()
        {
            this._filters.Add(LUMA_FULL_RANGE);
            return this;
        }

        /// <summary>
        /// Chiude sul formato grigio a otto bit atteso dalle misure
        /// </summary>
        /// <returns>La catena stessa</returns>
        public FfmpegFilterChain Gray()
        {
            this._filters.Add("format=gray");
            return this;
        }

        #endregion

        #region Metodi pubblici - Geometria

        /// <summary>
        /// Riporta i pixel a quadrati usando il rapporto dichiarato dal contenitore
        /// </summary>
        /// <returns>La catena stessa</returns>
        public FfmpegFilterChain PixelAspectScale()
        {
            this._filters.Add("scale=iw*sar:ih:" + SCALE_FLAGS);
            return this;
        }

        /// <summary>
        /// Variante che tiene la larghezza pari e riazzera il rapporto dichiarato
        /// </summary>
        /// <returns>La catena stessa</returns>
        public FfmpegFilterChain PixelAspectScaleEven()
        {
            this._filters.Add("scale=w='trunc(iw*sar/2)*2':h=ih:" + SCALE_FLAGS);
            this._filters.Add("setsar=1");
            return this;
        }

        /// <summary>
        /// Ritaglia il quadrato centrale
        /// </summary>
        /// <returns>La catena stessa</returns>
        public FfmpegFilterChain CentralSquare()
        {
            this._filters.Add("crop=min(iw\\,ih):min(iw\\,ih):(iw-min(iw\\,ih))/2:(ih-min(iw\\,ih))/2");
            return this;
        }

        /// <summary>
        /// Stringe sul centro e sposta in verticale, restando dentro il fotogramma
        /// </summary>
        /// <param name="zoom">Frazione di lato conservata</param>
        /// <param name="verticalShift">Spostamento verticale in frazioni di altezza</param>
        /// <returns>La catena stessa</returns>
        public FfmpegFilterChain Zoom(double zoom, double verticalShift)
        {
            string zoomText = zoom.ToString("0.######", CultureInfo.InvariantCulture);
            string shiftText = verticalShift.ToString("0.######", CultureInfo.InvariantCulture);
            this._filters.Add("crop=iw*" + zoomText + ":ih*" + zoomText + ":" +
                "(iw-iw*" + zoomText + ")/2:" +
                "max(0\\,min(ih-ih*" + zoomText + "\\,(ih-ih*" + zoomText + ")/2+" + shiftText + "*ih))");
            return this;
        }

        /// <summary>
        /// Ritaglia il viewport attivo espresso in frazioni del fotogramma
        /// </summary>
        /// <param name="left">Bordo sinistro</param>
        /// <param name="top">Bordo superiore</param>
        /// <param name="right">Bordo destro</param>
        /// <param name="bottom">Bordo inferiore</param>
        /// <returns>La catena stessa</returns>
        public FfmpegFilterChain NormalizedViewport(double left, double top, double right, double bottom)
        {
            string leftText = left.ToString("0.########", CultureInfo.InvariantCulture);
            string topText = top.ToString("0.########", CultureInfo.InvariantCulture);
            string widthText = (right - left).ToString("0.########", CultureInfo.InvariantCulture);
            string heightText = (bottom - top).ToString("0.########", CultureInfo.InvariantCulture);
            this._filters.Add("crop=iw*" + widthText + ":ih*" + heightText + ":iw*" + leftText + ":ih*" + topText);
            return this;
        }

        /// <summary>
        /// Porta il fotogramma al quadrato di analisi mediando i pixel che spariscono
        /// </summary>
        /// <param name="side">Lato in pixel</param>
        /// <returns>La catena stessa</returns>
        public FfmpegFilterChain ResizeArea(int side)
        {
            string sideText = side.ToString(CultureInfo.InvariantCulture);
            this._filters.Add("scale=" + sideText + ":" + sideText + ":flags=area+accurate_rnd");
            return this;
        }

        /// <summary>
        /// Porta il fotogramma alla risoluzione richiesta interpolando
        /// </summary>
        /// <param name="resolution">Risoluzione nel formato larghezza:altezza</param>
        /// <returns>La catena stessa</returns>
        public FfmpegFilterChain ResizeBicubic(string resolution)
        {
            this._filters.Add("scale=" + resolution + ":" + SCALE_FLAGS);
            return this;
        }

        #endregion

        #region Metodi pubblici - Chiusura

        /// <summary>
        /// Chiede a ffmpeg di stampare i tempi di presentazione sulla diagnostica
        /// </summary>
        /// <returns>La catena stessa</returns>
        public FfmpegFilterChain ShowInfo()
        {
            this._filters.Add("showinfo");
            return this;
        }

        /// <summary>
        /// Unisce gli anelli nella forma attesa da -vf
        /// </summary>
        /// <returns>Catena separata da virgole</returns>
        public string Build()
        {
            return string.Join(",", this._filters);
        }

        #endregion
    }
}
