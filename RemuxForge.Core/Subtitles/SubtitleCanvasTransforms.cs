using System;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Trasformazione geometrica comune per rewrite canvas sottotitoli
    /// </summary>
    internal class SubtitleCanvasTransform
    {
        #region Costruttore

        /// <summary>
        /// Inizializza la componente geometrica residua all'identità
        /// </summary>
        public SubtitleCanvasTransform()
        {
            this.GeometryScaleX = 1.0;
            this.GeometryScaleY = 1.0;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Larghezza storage input
        /// </summary>
        public int InputCanvasWidth { get; set; }

        /// <summary>
        /// Altezza storage input
        /// </summary>
        public int InputCanvasHeight { get; set; }

        /// <summary>
        /// Larghezza storage output
        /// </summary>
        public int OutputCanvasWidth { get; set; }

        /// <summary>
        /// Altezza storage output
        /// </summary>
        public int OutputCanvasHeight { get; set; }

        /// <summary>
        /// Larghezza display input
        /// </summary>
        public int InputDisplayWidth { get; set; }

        /// <summary>
        /// Altezza display input
        /// </summary>
        public int InputDisplayHeight { get; set; }

        /// <summary>
        /// Larghezza display output
        /// </summary>
        public int OutputDisplayWidth { get; set; }

        /// <summary>
        /// Altezza display output
        /// </summary>
        public int OutputDisplayHeight { get; set; }

        /// <summary>
        /// Crop sinistro input
        /// </summary>
        public int InputCropLeft { get; set; }

        /// <summary>
        /// Crop destro input
        /// </summary>
        public int InputCropRight { get; set; }

        /// <summary>
        /// Crop superiore input
        /// </summary>
        public int InputCropTop { get; set; }

        /// <summary>
        /// Crop inferiore input
        /// </summary>
        public int InputCropBottom { get; set; }

        /// <summary>
        /// Crop sinistro output
        /// </summary>
        public int OutputCropLeft { get; set; }

        /// <summary>
        /// Crop destro output
        /// </summary>
        public int OutputCropRight { get; set; }

        /// <summary>
        /// Crop superiore output
        /// </summary>
        public int OutputCropTop { get; set; }

        /// <summary>
        /// Crop inferiore output
        /// </summary>
        public int OutputCropBottom { get; set; }

        /// <summary>
        /// Larghezza area attiva input
        /// </summary>
        public int InputActiveWidth
        {
            get { return this.InputCanvasWidth - this.InputCropLeft - this.InputCropRight; }
        }

        /// <summary>
        /// Altezza area attiva input
        /// </summary>
        public int InputActiveHeight
        {
            get { return this.InputCanvasHeight - this.InputCropTop - this.InputCropBottom; }
        }

        /// <summary>
        /// Larghezza area attiva output
        /// </summary>
        public int OutputActiveWidth
        {
            get { return this.OutputCanvasWidth - this.OutputCropLeft - this.OutputCropRight; }
        }

        /// <summary>
        /// Altezza area attiva output
        /// </summary>
        public int OutputActiveHeight
        {
            get { return this.OutputCanvasHeight - this.OutputCropTop - this.OutputCropBottom; }
        }

        /// <summary>
        /// Scala orizzontale active-area
        /// </summary>
        public double ResolutionScaleX
        {
            get { return this.InputActiveWidth > 0 ? this.OutputActiveWidth / (double)this.InputActiveWidth : 0.0; }
        }

        /// <summary>
        /// Scala verticale active-area
        /// </summary>
        public double ResolutionScaleY
        {
            get { return this.InputActiveHeight > 0 ? this.OutputActiveHeight / (double)this.InputActiveHeight : 0.0; }
        }

        /// <summary>
        /// Scala orizzontale residua misurata fra le aree attive
        /// </summary>
        public double GeometryScaleX { get; set; }

        /// <summary>
        /// Scala verticale residua misurata fra le aree attive
        /// </summary>
        public double GeometryScaleY { get; set; }

        /// <summary>
        /// Traslazione orizzontale in frazioni dell'area attiva output
        /// </summary>
        public double GeometryTranslateX { get; set; }

        /// <summary>
        /// Traslazione verticale in frazioni dell'area attiva output
        /// </summary>
        public double GeometryTranslateY { get; set; }

        /// <summary>
        /// Scala orizzontale effettiva canvas per geometria
        /// </summary>
        public double ScaleX
        {
            get { return this.ResolutionScaleX * this.GeometryScaleX; }
        }

        /// <summary>
        /// Scala verticale effettiva canvas per geometria
        /// </summary>
        public double ScaleY
        {
            get { return this.ResolutionScaleY * this.GeometryScaleY; }
        }

        /// <summary>
        /// True se la trasformazione richiede scaling dimensionale
        /// </summary>
        public bool RequiresScaling
        {
            get { return Math.Abs(this.ScaleX - 1.0) > 0.000001 || Math.Abs(this.ScaleY - 1.0) > 0.000001; }
        }

        /// <summary>
        /// True se la trasformazione richiede decoding/scaling/encoding di bitmap sottotitoli
        /// </summary>
        public bool RequiresBitmapScaling
        {
            get { return this.RequiresScaling; }
        }

        /// <summary>
        /// Offset X nel caso senza scaling
        /// </summary>
        public int OffsetX
        {
            get { return this.MapX(0); }
        }

        /// <summary>
        /// Offset Y nel caso senza scaling
        /// </summary>
        public int OffsetY
        {
            get { return this.MapY(0); }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Mappa una coordinata X input nello spazio output
        /// </summary>
        /// <param name="x">Coordinata input</param>
        /// <returns>Coordinata output</returns>
        public int MapX(int x)
        {
            return this.RoundToInt(this.MapX((double)x));
        }

        /// <summary>
        /// Mappa una coordinata X input decimale nello spazio output
        /// </summary>
        /// <param name="x">Coordinata input</param>
        /// <returns>Coordinata output</returns>
        public double MapX(double x)
        {
            return ((x - this.InputCropLeft) * this.ScaleX) + this.OutputCropLeft + (this.OutputActiveWidth * this.GeometryTranslateX);
        }

        /// <summary>
        /// Mappa una coordinata Y input nello spazio output
        /// </summary>
        /// <param name="y">Coordinata input</param>
        /// <returns>Coordinata output</returns>
        public int MapY(int y)
        {
            return this.RoundToInt(this.MapY((double)y));
        }

        /// <summary>
        /// Mappa una coordinata Y input decimale nello spazio output
        /// </summary>
        /// <param name="y">Coordinata input</param>
        /// <returns>Coordinata output</returns>
        public double MapY(double y)
        {
            return ((y - this.InputCropTop) * this.ScaleY) + this.OutputCropTop + (this.OutputActiveHeight * this.GeometryTranslateY);
        }

        /// <summary>
        /// Scala una larghezza
        /// </summary>
        /// <param name="width">Larghezza input</param>
        /// <returns>Larghezza output</returns>
        public int MapWidth(int width)
        {
            return Math.Max(1, this.RoundToInt(width * this.ScaleX));
        }

        /// <summary>
        /// Scala un'altezza
        /// </summary>
        /// <param name="height">Altezza input</param>
        /// <returns>Altezza output</returns>
        public int MapHeight(int height)
        {
            return Math.Max(1, this.RoundToInt(height * this.ScaleY));
        }

        /// <summary>
        /// Mappa la coordinata X di un oggetto sottotitolo
        /// </summary>
        /// <param name="x">Coordinata X input</param>
        /// <returns>Coordinata X output</returns>
        public int MapObjectX(int x)
        {
            return this.MapX(x);
        }

        /// <summary>
        /// Mappa la coordinata Y di un oggetto sottotitolo
        /// </summary>
        /// <param name="y">Coordinata Y input</param>
        /// <returns>Coordinata Y output</returns>
        public int MapObjectY(int y)
        {
            return this.MapY(y);
        }

        /// <summary>
        /// Scala la larghezza di un oggetto sottotitolo
        /// </summary>
        /// <param name="width">Larghezza input</param>
        /// <returns>Larghezza output</returns>
        public int MapObjectWidth(int width)
        {
            return this.MapWidth(width);
        }

        /// <summary>
        /// Scala l'altezza di un oggetto sottotitolo
        /// </summary>
        /// <param name="height">Altezza input</param>
        /// <returns>Altezza output</returns>
        public int MapObjectHeight(int height)
        {
            return this.MapHeight(height);
        }

        /// <summary>
        /// Crea una trasformazione equivalente per uno spazio coordinate input diverso dal canvas video
        /// </summary>
        /// <param name="inputWidth">Larghezza spazio coordinate input</param>
        /// <param name="inputHeight">Altezza spazio coordinate input</param>
        /// <param name="outputWidth">Larghezza spazio coordinate output</param>
        /// <param name="outputHeight">Altezza spazio coordinate output</param>
        /// <returns>Trasformazione nello spazio coordinate richiesto</returns>
        public SubtitleCanvasTransform CreateCoordinateTransform(int inputWidth, int inputHeight, int outputWidth, int outputHeight)
        {
            SubtitleCanvasTransform result = new SubtitleCanvasTransform();

            // Crea un nuovo transform nello spazio coordinate del sottotitolo, non nello storage video originale
            result.InputCanvasWidth = inputWidth;
            result.InputCanvasHeight = inputHeight;
            result.OutputCanvasWidth = outputWidth;
            result.OutputCanvasHeight = outputHeight;
            result.InputDisplayWidth = inputWidth;
            result.InputDisplayHeight = inputHeight;
            result.OutputDisplayWidth = outputWidth;
            result.OutputDisplayHeight = outputHeight;
            result.GeometryScaleX = this.GeometryScaleX;
            result.GeometryScaleY = this.GeometryScaleY;
            result.GeometryTranslateX = this.GeometryTranslateX;
            result.GeometryTranslateY = this.GeometryTranslateY;

            // Il crop resta quello video, ma viene proporzionato allo spazio PlayRes/LayoutRes del formato testuale
            result.InputCropLeft = this.ScaleCropValue(this.InputCropLeft, this.InputCanvasWidth, inputWidth);
            result.InputCropRight = this.ScaleCropValue(this.InputCropRight, this.InputCanvasWidth, inputWidth);
            result.InputCropTop = this.ScaleCropValue(this.InputCropTop, this.InputCanvasHeight, inputHeight);
            result.InputCropBottom = this.ScaleCropValue(this.InputCropBottom, this.InputCanvasHeight, inputHeight);
            result.OutputCropLeft = this.ScaleCropValue(this.OutputCropLeft, this.OutputCanvasWidth, outputWidth);
            result.OutputCropRight = this.ScaleCropValue(this.OutputCropRight, this.OutputCanvasWidth, outputWidth);
            result.OutputCropTop = this.ScaleCropValue(this.OutputCropTop, this.OutputCanvasHeight, outputHeight);
            result.OutputCropBottom = this.ScaleCropValue(this.OutputCropBottom, this.OutputCanvasHeight, outputHeight);
            return result;
        }

        /// <summary>
        /// Risolve coordinate e dimensioni di una window WDS nel canvas finale
        /// </summary>
        /// <param name="x">Coordinata X originale</param>
        /// <param name="y">Coordinata Y originale</param>
        /// <param name="width">Larghezza originale</param>
        /// <param name="height">Altezza originale</param>
        /// <param name="deltaX">Delta X locale del display-set</param>
        /// <param name="deltaY">Delta Y locale del display-set</param>
        /// <param name="newX">Coordinata X risolta</param>
        /// <param name="newY">Coordinata Y risolta</param>
        /// <param name="newWidth">Larghezza risolta</param>
        /// <param name="newHeight">Altezza risolta</param>
        public void ResolveWindowRect(int x, int y, int width, int height, int deltaX, int deltaY, out int newX, out int newY, out int newWidth, out int newHeight)
        {
            // WDS full-canvas sul lang diventa full-canvas sul source senza passare da active area/crop
            if (x == 0 && y == 0 && width == this.InputCanvasWidth && height == this.InputCanvasHeight)
            {
                newX = 0;
                newY = 0;
                newWidth = this.OutputCanvasWidth;
                newHeight = this.OutputCanvasHeight;
                return;
            }

            // Alcuni flussi hanno già WDS allineata al canvas output: preservala per evitare doppio scaling
            if (x == 0 && y == 0 && width == this.OutputCanvasWidth && height == this.OutputCanvasHeight)
            {
                newX = 0;
                newY = 0;
                newWidth = this.OutputCanvasWidth;
                newHeight = this.OutputCanvasHeight;
                return;
            }

            // WDS tight o parziale: applica mapping affine e il clamp locale del display-set
            newX = this.MapX(x) + deltaX;
            newY = this.MapY(y) + deltaY;
            newWidth = this.MapWidth(width);
            newHeight = this.MapHeight(height);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Scala un crop quando lo spazio coordinate non coincide con il canvas video
        /// </summary>
        /// <param name="value">Valore crop originale</param>
        /// <param name="sourceSize">Dimensione canvas sorgente</param>
        /// <param name="targetSize">Dimensione coordinate target</param>
        /// <returns>Valore crop scalato</returns>
        private int ScaleCropValue(int value, int sourceSize, int targetSize)
        {
            if (value == 0 || sourceSize <= 0 || targetSize <= 0)
            {
                return 0;
            }

            return this.RoundToInt(value * (targetSize / (double)sourceSize));
        }

        /// <summary>
        /// Arrotonda in modo stabile una coordinata
        /// </summary>
        /// <param name="value">Valore da arrotondare</param>
        /// <returns>Intero arrotondato away-from-zero</returns>
        private int RoundToInt(double value)
        {
            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        #endregion
    }
}
