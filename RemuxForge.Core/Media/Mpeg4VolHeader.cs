using System;

namespace RemuxForge.Core.Media
{
    /// <summary>
    /// Video Object Layer di MPEG-4 Part 2 letto dai dati privati della traccia video: quant_type e matrici di quantizzazione
    /// effettive. I campi precedenti si leggono solo per arrivarci, secondo la sintassi di ISO/IEC 14496-2 §6.2.3
    /// </summary>
    public sealed class Mpeg4VolHeader
    {
        #region Variabili statiche

        /// <summary>
        /// Matrice intra predefinita di MPEG-4 Part 2 (ISO/IEC 14496-2), in ordine raster come ff_mpeg4_default_intra_matrix di libavcodec
        /// </summary>
        private static readonly int[] s_defaultIntraMatrix = new int[]
        {
             8, 17, 18, 19, 21, 23, 25, 27,
            17, 18, 19, 21, 23, 25, 27, 28,
            20, 21, 22, 23, 24, 26, 28, 30,
            21, 22, 23, 24, 26, 28, 30, 32,
            22, 23, 24, 26, 28, 30, 32, 35,
            23, 24, 26, 28, 30, 32, 35, 38,
            25, 26, 28, 30, 32, 35, 38, 41,
            27, 28, 30, 32, 35, 38, 41, 45
        };

        /// <summary>
        /// Matrice non intra predefinita di MPEG-4 Part 2 (ISO/IEC 14496-2), in ordine raster come ff_mpeg4_default_non_intra_matrix di libavcodec
        /// </summary>
        private static readonly int[] s_defaultNonIntraMatrix = new int[]
        {
            16, 17, 18, 19, 20, 21, 22, 23,
            17, 18, 19, 20, 21, 22, 23, 24,
            18, 19, 20, 21, 22, 23, 24, 25,
            19, 20, 21, 22, 23, 24, 26, 27,
            20, 21, 22, 23, 25, 26, 27, 28,
            21, 22, 23, 24, 26, 27, 28, 30,
            22, 23, 24, 26, 27, 28, 30, 31,
            23, 24, 25, 27, 28, 30, 31, 33
        };

        /// <summary>
        /// Posizione raster di ogni coefficiente nell'ordine zigzag con cui il VOL scrive le matrici caricate, come ff_zigzag_direct di libavcodec
        /// </summary>
        private static readonly int[] s_zigzag = new int[]
        {
             0,  1,  8, 16,  9,  2,  3, 10,
            17, 24, 32, 25, 18, 11,  4,  5,
            12, 19, 26, 33, 40, 48, 41, 34,
            27, 20, 13,  6,  7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36,
            29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46,
            53, 60, 61, 54, 47, 55, 62, 63
        };

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore sui campi letti
        /// </summary>
        /// <param name="quantType">quant_type del VOL: 0 quantizzazione H.263, 1 quantizzazione MPEG</param>
        /// <param name="intraMatrix">Matrice intra effettiva in ordine raster, null con quant_type 0</param>
        /// <param name="nonIntraMatrix">Matrice non intra effettiva in ordine raster, null con quant_type 0</param>
        private Mpeg4VolHeader(int quantType, int[] intraMatrix, int[] nonIntraMatrix)
        {
            this.QuantType = quantType;
            this.IntraMatrix = intraMatrix;
            this.NonIntraMatrix = nonIntraMatrix;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// quant_type del VOL: 0 quantizzazione H.263, 1 quantizzazione MPEG con le matrici
        /// </summary>
        public int QuantType { get; }

        /// <summary>
        /// Matrice intra effettiva in ordine raster: la predefinita, oppure quella caricata nel VOL. Null con quant_type 0
        /// </summary>
        public int[] IntraMatrix { get; }

        /// <summary>
        /// Matrice non intra effettiva in ordine raster: la predefinita, oppure quella caricata nel VOL. Null con quant_type 0
        /// </summary>
        public int[] NonIntraMatrix { get; }

        /// <summary>
        /// Firma della quantizzazione: quant_type e, con la quantizzazione MPEG, le matrici effettive. Una matrice caricata uguale
        /// alla predefinita ha la stessa firma della predefinita non caricata
        /// </summary>
        public string QuantizationSignature => "quant_type=" + this.QuantType
            + (this.QuantType == 1 ? ";intra=" + string.Join(",", this.IntraMatrix) + ";inter=" + string.Join(",", this.NonIntraMatrix) : "");

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Legge il VOL dai dati privati della traccia video
        /// </summary>
        /// <param name="hex">Dati privati della traccia video con MPEG-4 Part 2 nativo, in esadecimale come li scrive mkvmerge</param>
        /// <returns>VOL letto, null se manca, se il layer non ha texture rettangolare o in scala di grigi nella forma attesa, oppure
        /// se i dati finiscono prima della quantizzazione</returns>
        public static Mpeg4VolHeader Parse(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0)
                return null;

            // Start code del VOL: 00 00 01 20..2F
            byte[] data = Convert.FromHexString(hex);
            int volStart = MpegStartCode.Find(data, 0, data.Length, code => (code & 0xF0) == 0x20);
            if (volStart < 0)
                return null;
            BitReader reader = new BitReader(data, volStart + 4);

            // random_accessible_vol, video_object_type_indication
            reader.Read(1);
            reader.Read(8);
            int verid = 1;
            if (reader.Read(1) == 1)
            {
                verid = reader.Read(4);
                reader.Read(3);
            }
            // aspect_ratio_info, con par_width e par_height se esteso
            if (reader.Read(4) == 15)
                reader.Read(16);
            // vol_control_parameters: chroma_format, low_delay, vbv_parameters
            if (reader.Read(1) == 1)
            {
                reader.Read(3);
                if (reader.Read(1) == 1)
                {
                    reader.Read(15); reader.Read(1); reader.Read(15); reader.Read(1); reader.Read(15); reader.Read(1);
                    reader.Read(3); reader.Read(11); reader.Read(1); reader.Read(15); reader.Read(1);
                }
            }
            // video_object_layer_shape: 0 rettangolare, 2 solo binaria, 3 scala di grigi
            int shape = reader.Read(2);
            if (shape == 3 && verid != 1)
                reader.Read(4);
            reader.Read(1);
            int resolution = reader.Read(16);
            reader.Read(1);
            if (reader.Read(1) == 1)
            {
                int incrementBits = 1;
                while ((1 << incrementBits) < resolution)
                    incrementBits++;
                reader.Read(incrementBits);
            }
            // Senza texture o in scala di grigi la quantizzazione non segue nella forma attesa
            if (shape == 2 || shape == 3)
                return null;
            if (shape == 0)
            {
                reader.Read(1); reader.Read(13); reader.Read(1); reader.Read(13); reader.Read(1);
            }
            // interlaced, obmc_disable, sprite_enable
            reader.Read(1);
            reader.Read(1);
            int sprite = reader.Read(verid == 1 ? 1 : 2);
            if (sprite == 1 || sprite == 2)
            {
                if (sprite != 2)
                {
                    reader.Read(13); reader.Read(1); reader.Read(13); reader.Read(1); reader.Read(13); reader.Read(1); reader.Read(13); reader.Read(1);
                }
                reader.Read(6); reader.Read(2); reader.Read(1);
                if (sprite != 2)
                    reader.Read(1);
            }
            if (verid != 1 && shape != 0)
                reader.Read(1);
            // not_8_bit: quant_precision e bits_per_pixel
            if (reader.Read(1) == 1)
            {
                reader.Read(4);
                reader.Read(4);
            }

            // quant_type e, con la quantizzazione MPEG, le matrici effettive intra e non intra: si parte dalla predefinita e
            // i valori caricati, in ordine zigzag e chiusi da uno zero, la sostituiscono ripetendo l'ultimo fino al 64° come
            // libavcodec
            int quantType = reader.Read(1);
            int[][] matrices = new int[2][];
            if (quantType == 1)
            {
                for (int matrix = 0; matrix < 2; matrix++)
                {
                    int[] effective = (int[])(matrix == 0 ? s_defaultIntraMatrix : s_defaultNonIntraMatrix).Clone();
                    if (reader.Read(1) == 1)
                    {
                        int last = 0;
                        int k = 0;
                        for (; k < 64; k++)
                        {
                            int value = reader.Read(8);
                            if (value == 0)
                                break;
                            last = value;
                            effective[s_zigzag[k]] = value;
                        }
                        for (; k < 64; k++)
                            effective[s_zigzag[k]] = last;
                    }
                    matrices[matrix] = effective;
                }
            }
            return reader.Overrun ? null : new Mpeg4VolHeader(quantType, matrices[0], matrices[1]);
        }

        #endregion

        #region Tipi annidati

        /// <summary>
        /// Lettore di bit dal più significativo, oltre la fine dei dati restituisce zero e lo segnala
        /// </summary>
        private sealed class BitReader
        {
            #region Variabili di classe

            /// <summary>
            /// Dati sorgente
            /// </summary>
            private readonly byte[] _data;

            /// <summary>
            /// Posizione corrente in bit
            /// </summary>
            private long _position;

            #endregion

            #region Costruttore

            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="data">Dati sorgente</param>
            /// <param name="offset">Offset in byte del primo bit da leggere</param>
            public BitReader(byte[] data, int offset)
            {
                this._data = data;
                this._position = offset * 8L;
            }

            #endregion

            #region Proprietà

            /// <summary>
            /// Vero se una lettura ha superato la fine dei dati
            /// </summary>
            public bool Overrun { get; private set; }

            #endregion

            #region Metodi pubblici

            /// <summary>
            /// Legge un campo senza segno
            /// </summary>
            /// <param name="count">Numero di bit del campo</param>
            /// <returns>Valore letto, zero se i dati sono finiti</returns>
            public int Read(int count)
            {
                int value = 0;
                for (int b = 0; b < count; b++)
                {
                    if (this._position >= this._data.Length * 8L)
                    {
                        this.Overrun = true;
                        return 0;
                    }
                    value = (value << 1) | ((this._data[this._position >> 3] >> (7 - (int)(this._position & 7))) & 1);
                    this._position++;
                }
                return value;
            }

            #endregion
        }

        #endregion
    }
}
