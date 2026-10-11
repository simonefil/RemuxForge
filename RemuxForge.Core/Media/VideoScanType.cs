using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemuxForge.Core.Media
{
    /// <summary>
    /// Scansione della traccia video come la dichiara MediaInfo (campo ScanType)
    /// </summary>
    /// <remarks>
    /// Il pulldown morbido è dichiarato Progressive con ScanType_Original interlacciato: è progressivo.
    /// Il campo assente (tipico di AV1 e VP9) vale progressivo (P4). In JSON si scrive come la stringa MediaInfo.
    /// </remarks>
    [JsonConverter(typeof(VideoScanTypeJsonConverter))]
    public readonly struct VideoScanType : IEquatable<VideoScanType>
    {
        #region Variabili di classe

        /// <summary>
        /// Valore MediaInfo, null nel valore di default della struttura
        /// </summary>
        private readonly string _value;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="value">Valore MediaInfo ScanType, vuoto o null se non dichiarato</param>
        public VideoScanType(string value)
        {
            this._value = value ?? "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Valore MediaInfo ScanType, vuoto se non dichiarato
        /// </summary>
        public string Value => this._value ?? "";

        /// <summary>
        /// True per Interlaced, MBAFF, PAFF e Mixed (D14)
        /// </summary>
        public bool IsInterlaced => this.Value == "Interlaced" || this.Value == "MBAFF" || this.Value == "PAFF" || this.Value == "Mixed";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Valore MediaInfo, come compare nei messaggi
        /// </summary>
        /// <returns>Valore MediaInfo ScanType, vuoto se non dichiarato</returns>
        public override string ToString()
        {
            return this.Value;
        }

        /// <summary>
        /// Uguaglianza sul valore MediaInfo
        /// </summary>
        /// <param name="other">Scansione da confrontare</param>
        /// <returns>True se i valori coincidono</returns>
        public bool Equals(VideoScanType other)
        {
            return string.Equals(this.Value, other.Value, StringComparison.Ordinal);
        }

        /// <summary>
        /// Uguaglianza sul valore MediaInfo
        /// </summary>
        /// <param name="obj">Oggetto da confrontare</param>
        /// <returns>True se è una scansione con lo stesso valore</returns>
        public override bool Equals(object obj)
        {
            return obj is VideoScanType other && this.Equals(other);
        }

        /// <summary>
        /// Hash del valore MediaInfo
        /// </summary>
        /// <returns>Hash ordinale del valore</returns>
        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(this.Value);
        }

        #endregion
    }

    /// <summary>
    /// Serializza la scansione come la stringa MediaInfo, la stessa forma del campo string che sostituisce
    /// </summary>
    public sealed class VideoScanTypeJsonConverter : JsonConverter<VideoScanType>
    {
        #region Metodi pubblici

        /// <summary>
        /// Legge la stringa MediaInfo
        /// </summary>
        /// <param name="reader">Lettore JSON</param>
        /// <param name="typeToConvert">Tipo richiesto</param>
        /// <param name="options">Opzioni del serializzatore</param>
        /// <returns>Scansione letta, vuota per null</returns>
        public override VideoScanType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new VideoScanType(reader.TokenType == JsonTokenType.Null ? "" : reader.GetString());
        }

        /// <summary>
        /// Scrive la stringa MediaInfo
        /// </summary>
        /// <param name="writer">Scrittore JSON</param>
        /// <param name="value">Scansione da scrivere</param>
        /// <param name="options">Opzioni del serializzatore</param>
        public override void Write(Utf8JsonWriter writer, VideoScanType value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.Value);
        }

        #endregion
    }
}
