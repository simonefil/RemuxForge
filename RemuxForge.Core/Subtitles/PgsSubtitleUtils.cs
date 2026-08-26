using System;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Utility binarie condivise per sottotitoli PGS/SUP
    /// </summary>
    internal static class PgsSubtitleUtils
    {
        #region Costanti

        /// <summary>
        /// Dimensione header packet SUP: magic, PTS, DTS, tipo segmento e lunghezza
        /// </summary>
        public const int SUP_PACKET_HEADER_SIZE = 13;

        /// <summary>
        /// Segment type Object Definition Segment
        /// </summary>
        public const int SEGMENT_OBJECT = 0x15;

        /// <summary>
        /// Segment type Presentation Composition Segment
        /// </summary>
        public const int SEGMENT_PRESENTATION = 0x16;

        /// <summary>
        /// Segment type Window Definition Segment
        /// </summary>
        public const int SEGMENT_WINDOW = 0x17;

        /// <summary>
        /// Segment type End Of Display Set
        /// </summary>
        public const int SEGMENT_END = 0x80;

        /// <summary>
        /// Lunghezza massima di un run RLE PGS
        /// </summary>
        private const int MAX_RLE_RUN_LENGTH = 0x3fff;

        /// <summary>
        /// Lunghezza massima payload segmento ODS
        /// </summary>
        private const int MAX_ODS_SEGMENT_LENGTH = 65535;

        /// <summary>
        /// Header payload del primo segmento ODS
        /// </summary>
        private const int ODS_FIRST_PAYLOAD_HEADER_SIZE = 11;

        /// <summary>
        /// Header payload dei segmenti ODS continuation
        /// </summary>
        private const int ODS_CONTINUATION_PAYLOAD_HEADER_SIZE = 4;

        /// <summary>
        /// Lunghezza massima object_data_length ODS su 24 bit
        /// </summary>
        private const int MAX_ODS_OBJECT_DATA_LENGTH = 0xffffff;

        #endregion

        #region Metodi pubblici - Packet SUP

        /// <summary>
        /// Legge e valida la lunghezza di un packet SUP
        /// </summary>
        /// <param name="data">Buffer SUP</param>
        /// <param name="pos">Offset packet</param>
        /// <param name="packetLength">Lunghezza packet completa</param>
        /// <returns>True se il packet è valido</returns>
        public static bool TryGetPacketLength(byte[] data, int pos, out int packetLength)
        {
            int size;
            packetLength = 0;
            if (pos + SUP_PACKET_HEADER_SIZE > data.Length || data[pos] != (byte)'P' || data[pos + 1] != (byte)'G')
            {
                return false;
            }

            size = ReadUInt16BigEndian(data, pos + 11);
            packetLength = SUP_PACKET_HEADER_SIZE + size;
            return pos + packetLength <= data.Length;
        }

        /// <summary>
        /// Legge un unsigned 16 bit big-endian
        /// </summary>
        /// <param name="data">Buffer dati</param>
        /// <param name="offset">Offset lettura</param>
        /// <returns>Valore letto</returns>
        public static int ReadUInt16BigEndian(byte[] data, int offset)
        {
            return (data[offset] << 8) | data[offset + 1];
        }

        /// <summary>
        /// Scrive un unsigned 16 bit big-endian
        /// </summary>
        /// <param name="data">Buffer dati</param>
        /// <param name="offset">Offset scrittura</param>
        /// <param name="value">Valore da scrivere</param>
        public static void WriteUInt16BigEndian(byte[] data, int offset, int value)
        {
            data[offset] = (byte)((value >> 8) & 0xff);
            data[offset + 1] = (byte)(value & 0xff);
        }

        /// <summary>
        /// Legge un unsigned 32 bit big-endian
        /// </summary>
        /// <param name="data">Buffer dati</param>
        /// <param name="offset">Offset lettura</param>
        /// <returns>Valore letto</returns>
        public static uint ReadUInt32BigEndian(byte[] data, int offset)
        {
            return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
        }

        /// <summary>
        /// Scrive un unsigned 32 bit big-endian
        /// </summary>
        /// <param name="data">Buffer dati</param>
        /// <param name="offset">Offset scrittura</param>
        /// <param name="value">Valore da scrivere</param>
        public static void WriteUInt32BigEndian(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)((value >> 24) & 0xff);
            data[offset + 1] = (byte)((value >> 16) & 0xff);
            data[offset + 2] = (byte)((value >> 8) & 0xff);
            data[offset + 3] = (byte)(value & 0xff);
        }

        /// <summary>
        /// Legge un unsigned 24 bit big-endian
        /// </summary>
        /// <param name="data">Buffer dati</param>
        /// <param name="offset">Offset lettura</param>
        /// <returns>Valore letto</returns>
        public static int ReadUInt24BigEndian(byte[] data, int offset)
        {
            return (data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2];
        }

        /// <summary>
        /// Scrive un unsigned 24 bit big-endian
        /// </summary>
        /// <param name="data">Buffer dati</param>
        /// <param name="offset">Offset scrittura</param>
        /// <param name="value">Valore da scrivere</param>
        public static void WriteUInt24BigEndian(byte[] data, int offset, int value)
        {
            data[offset] = (byte)((value >> 16) & 0xff);
            data[offset + 1] = (byte)((value >> 8) & 0xff);
            data[offset + 2] = (byte)(value & 0xff);
        }

        #endregion

        #region Metodi pubblici - Oggetti ODS

        /// <summary>
        /// Raccoglie definizioni oggetto ODS complete presenti nel display-set
        /// </summary>
        /// <param name="data">Buffer SUP completo</param>
        /// <param name="start">Offset iniziale display-set</param>
        /// <param name="end">Offset finale display-set</param>
        /// <param name="report">Report aggiornato</param>
        /// <param name="objects">Oggetti completi per object id</param>
        /// <returns>True se gli ODS del display-set sono coerenti</returns>
        public static bool CollectDisplaySetObjectDefinitions(byte[] data, int start, int end, PgsSubtitleCanvasRewriteReport report, out Dictionary<int, PgsObjectDefinition> objects)
        {
            Dictionary<int, PgsObjectAssemblyState> partials = new Dictionary<int, PgsObjectAssemblyState>();
            int pos = start;
            int packetLength;

            objects = new Dictionary<int, PgsObjectDefinition>();

            // Scorre i packet del display-set cercando solo segmenti ODS
            while (pos < end && TryGetPacketLength(data, pos, out packetLength) && pos + packetLength <= end)
            {
                if (data[pos + 10] == SEGMENT_OBJECT)
                {
                    if (!ReadObjectSegment(data, pos, partials, objects, report))
                    {
                        return false;
                    }
                }

                pos += packetLength;
            }

            // Se restano partial aperti, il display-set contiene ODS frammentati non completati
            if (partials.Count > 0)
            {
                report.ErrorMessage = "ODS PGS frammentato incompleto";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Costruisce packet SUP ODS completi per una definizione oggetto
        /// </summary>
        /// <param name="definition">Definizione oggetto</param>
        /// <param name="packets">Packet SUP ODS prodotti</param>
        /// <param name="errorMessage">Errore di scrittura</param>
        /// <returns>True se packet costruiti</returns>
        public static bool BuildObjectDefinitionPackets(PgsObjectDefinition definition, out List<byte[]> packets, out string errorMessage)
        {
            int objectDataLength;
            int firstDataLength;
            int offset;
            int remaining;
            int dataLength;

            packets = new List<byte[]>();
            errorMessage = "";

            // Valida l'header SUP originale usato come base per i nuovi packet
            if (definition == null || definition.FirstPacketHeader == null || definition.FirstPacketHeader.Length != SUP_PACKET_HEADER_SIZE)
            {
                errorMessage = "ODS PGS header originale mancante";
                return false;
            }

            objectDataLength = 4 + definition.RleData.Length;
            if (objectDataLength > MAX_ODS_OBJECT_DATA_LENGTH)
            {
                errorMessage = "ODS PGS object_data_length oltre 24 bit";
                return false;
            }

            // Caso semplice: tutto l'oggetto entra in un singolo segmento ODS
            if (ODS_FIRST_PAYLOAD_HEADER_SIZE + definition.RleData.Length <= MAX_ODS_SEGMENT_LENGTH)
            {
                packets.Add(BuildFirstObjectPacket(definition, 0, definition.RleData.Length, 0xc0));
                return true;
            }

            // Caso frammentato: primo segmento con flag first, poi continuation fino al flag last
            firstDataLength = MAX_ODS_SEGMENT_LENGTH - ODS_FIRST_PAYLOAD_HEADER_SIZE;
            packets.Add(BuildFirstObjectPacket(definition, 0, firstDataLength, 0x80));
            offset = firstDataLength;
            remaining = definition.RleData.Length - offset;
            while (remaining > 0)
            {
                dataLength = remaining > MAX_ODS_SEGMENT_LENGTH - ODS_CONTINUATION_PAYLOAD_HEADER_SIZE ? MAX_ODS_SEGMENT_LENGTH - ODS_CONTINUATION_PAYLOAD_HEADER_SIZE : remaining;
                remaining -= dataLength;
                packets.Add(BuildObjectContinuationPacket(definition, offset, dataLength, remaining == 0 ? 0x40 : 0x00));
                offset += dataLength;
            }

            return true;
        }


        #endregion

        #region Metodi pubblici - Bitmap RLE

        /// <summary>
        /// Decodifica il RLE di un oggetto PGS in bitmap palette-indexed
        /// </summary>
        /// <param name="definition">Definizione oggetto ODS</param>
        /// <param name="bitmap">Bitmap decodificata</param>
        /// <param name="errorMessage">Errore di decoding</param>
        /// <param name="warnings">Warning non fatali</param>
        /// <returns>True se decoding riuscito</returns>
        public static bool DecodeObjectBitmap(PgsObjectDefinition definition, out PgsSubtitleBitmap bitmap, out string errorMessage, out int warnings)
        {
            byte[] pixels;
            int pos = 0;
            int x = 0;
            int y = 0;
            int first;
            int code;
            int runLength;
            int color;

            bitmap = null;
            errorMessage = "";
            warnings = 0;

            // Valida metadati ODS minimi prima di allocare la bitmap
            if (definition == null || definition.Width <= 0 || definition.Height <= 0 || definition.RleData == null)
            {
                errorMessage = "ODS PGS non valido per decode";
                return false;
            }

            pixels = new byte[definition.Width * definition.Height];

            // Decodifica il flusso RLE riga per riga nello spazio bitmap dichiarato dall'ODS
            while (pos < definition.RleData.Length && y < definition.Height)
            {
                first = definition.RleData[pos++];

                // Byte non nullo: singolo pixel con indice palette esplicito
                if (first != 0)
                {
                    if (!WriteDecodedRun(pixels, definition.Width, definition.Height, ref x, ref y, 1, first, ref warnings, out errorMessage))
                    {
                        return false;
                    }
                    continue;
                }

                if (pos >= definition.RleData.Length)
                {
                    errorMessage = "RLE PGS escape finale incompleto";
                    return false;
                }

                code = definition.RleData[pos++];

                // 00 00 chiude la riga corrente
                if (code == 0)
                {
                    EndDecodedLine(pixels, definition.Width, definition.Height, ref x, ref y, ref warnings);
                    continue;
                }

                // Run trasparente corto
                if ((code & 0xc0) == 0x00)
                {
                    runLength = code & 0x3f;
                    color = 0;
                }

                // Run trasparente lungo
                else if ((code & 0xc0) == 0x40)
                {
                    if (pos >= definition.RleData.Length)
                    {
                        errorMessage = "RLE PGS run trasparente lungo incompleto";
                        return false;
                    }

                    runLength = ((code & 0x3f) << 8) | definition.RleData[pos++];
                    color = 0;
                }

                // Run colore corto
                else if ((code & 0xc0) == 0x80)
                {
                    if (pos >= definition.RleData.Length)
                    {
                        errorMessage = "RLE PGS run colore corto incompleto";
                        return false;
                    }

                    runLength = code & 0x3f;
                    color = definition.RleData[pos++];
                }

                // Run colore lungo
                else
                {
                    if (pos + 1 >= definition.RleData.Length)
                    {
                        errorMessage = "RLE PGS run colore lungo incompleto";
                        return false;
                    }

                    runLength = ((code & 0x3f) << 8) | definition.RleData[pos++];
                    color = definition.RleData[pos++];
                }

                if (runLength <= 0)
                {
                    errorMessage = "RLE PGS run nullo";
                    return false;
                }

                if (!WriteDecodedRun(pixels, definition.Width, definition.Height, ref x, ref y, runLength, color, ref warnings, out errorMessage))
                {
                    return false;
                }
            }

            // Alcuni encoder omettono EOL finale dopo l'ultimo pixel dell'ultima riga
            if (y == definition.Height - 1 && x == definition.Width)
            {
                y++;
            }
            else if (y < definition.Height && x > 0)
            {
                EndDecodedLine(pixels, definition.Width, definition.Height, ref x, ref y, ref warnings);
            }

            // La bitmap deve essere interamente coperta dopo normalizzazione righe
            if (y < definition.Height)
            {
                errorMessage = "RLE PGS termina prima della bitmap";
                return false;
            }

            if (pos < definition.RleData.Length)
            {
                warnings++;
            }

            bitmap = new PgsSubtitleBitmap(definition.Width, definition.Height, pixels);
            return true;
        }

        /// <summary>
        /// Codifica una bitmap palette-indexed in RLE PGS
        /// </summary>
        /// <param name="bitmap">Bitmap da codificare</param>
        /// <returns>Dati RLE</returns>
        public static byte[] EncodeBitmapRle(PgsSubtitleBitmap bitmap)
        {
            List<byte> result = new List<byte>();
            int rowStart;
            int x;
            int runLength;
            byte color;

            // Codifica ogni riga e chiude sempre con EOL PGS
            for (int y = 0; y < bitmap.Height; y++)
            {
                rowStart = y * bitmap.Width;
                x = 0;
                while (x < bitmap.Width)
                {
                    color = bitmap.Pixels[rowStart + x];
                    runLength = 1;

                    // Accorpa pixel consecutivi con lo stesso indice palette
                    while (x + runLength < bitmap.Width &&
                        bitmap.Pixels[rowStart + x + runLength] == color &&
                        runLength < MAX_RLE_RUN_LENGTH)
                    {
                        runLength++;
                    }

                    WriteEncodedRun(result, color, runLength);
                    x += runLength;
                }

                result.Add(0x00);
                result.Add(0x00);
            }

            return result.ToArray();
        }

        /// <summary>
        /// Scala una bitmap PGS palette-indexed senza modificare la palette
        /// </summary>
        /// <param name="input">Bitmap input</param>
        /// <param name="outputWidth">Larghezza output</param>
        /// <param name="outputHeight">Altezza output</param>
        /// <param name="warnings">Warning non fatali</param>
        /// <returns>Bitmap scalata</returns>
        public static PgsSubtitleBitmap ScaleBitmap(PgsSubtitleBitmap input, int outputWidth, int outputHeight, out int warnings)
        {
            warnings = 0;

            // Nessuno scaling: restituisce una copia per non condividere il buffer
            if (input.Width == outputWidth && input.Height == outputHeight)
            {
                byte[] copy = new byte[input.Pixels.Length];
                Array.Copy(input.Pixels, copy, input.Pixels.Length);
                return new PgsSubtitleBitmap(outputWidth, outputHeight, copy);
            }

            // Upscale: nearest-neighbor per preservare indici palette senza introdurre colori
            if (outputWidth >= input.Width && outputHeight >= input.Height)
            {
                return ScaleBitmapNearest(input, outputWidth, outputHeight);
            }

            // Downscale: majority vote sull'area sorgente per ridurre aliasing sugli indici palette
            warnings++;
            return ScaleBitmapMajority(input, outputWidth, outputHeight);
        }

        #endregion

        #region Metodi privati - Assembly ODS

        /// <summary>
        /// Legge un segmento ODS e aggiorna lo stato di assembly
        /// </summary>
        /// <param name="data">Buffer SUP completo</param>
        /// <param name="packetStart">Offset iniziale packet ODS</param>
        /// <param name="partials">Oggetti parziali in assembly</param>
        /// <param name="objects">Oggetti completi raccolti</param>
        /// <param name="report">Report aggiornato durante la lettura</param>
        /// <returns>True se il segmento è stato letto</returns>
        private static bool ReadObjectSegment(
            byte[] data,
            int packetStart,
            Dictionary<int, PgsObjectAssemblyState> partials,
            Dictionary<int, PgsObjectDefinition> objects,
            PgsSubtitleCanvasRewriteReport report)
        {
            int segmentLength = ReadUInt16BigEndian(data, packetStart + 11);
            int payload = packetStart + SUP_PACKET_HEADER_SIZE;
            int objectId;
            int version;
            int flags;
            bool first;
            bool last;
            PgsObjectAssemblyState state;
            int dataOffset;
            int dataLength;

            // Valida header segmento e legge identità/flag ODS
            if (segmentLength < 4 || payload + segmentLength > data.Length)
            {
                report.ErrorMessage = "ODS PGS troppo corto";
                return false;
            }

            objectId = ReadUInt16BigEndian(data, payload);
            version = data[payload + 2];
            flags = data[payload + 3];
            first = (flags & 0x80) != 0;
            last = (flags & 0x40) != 0;

            // Primo frammento: apre un nuovo oggetto e include dimensioni bitmap
            if (first)
            {
                if (!StartObject(data, packetStart, segmentLength, payload, objectId, version, partials, out state, report))
                {
                    return false;
                }

                dataOffset = payload + ODS_FIRST_PAYLOAD_HEADER_SIZE;
                dataLength = segmentLength - ODS_FIRST_PAYLOAD_HEADER_SIZE;
            }

            // Frammento successivo: prosegue un oggetto già aperto con stessa versione
            else
            {
                if (!partials.TryGetValue(objectId, out state))
                {
                    report.ErrorMessage = "ODS PGS continuation senza first segment";
                    return false;
                }

                if (state.Version != version)
                {
                    report.ErrorMessage = "ODS PGS continuation con versione incoerente";
                    return false;
                }

                dataOffset = payload + ODS_CONTINUATION_PAYLOAD_HEADER_SIZE;
                dataLength = segmentLength - ODS_CONTINUATION_PAYLOAD_HEADER_SIZE;
            }

            // Aggiunge il payload RLE al buffer dell'oggetto
            if (!AppendObjectData(state, data, dataOffset, dataLength, report))
            {
                return false;
            }

            // Ultimo frammento: completa l'oggetto e lo rende disponibile al rewriter
            if (last)
            {
                if (!CompleteObject(state, objects, report))
                {
                    return false;
                }

                partials.Remove(objectId);
            }
            else
            {
                partials[objectId] = state;
            }

            return true;
        }

        /// <summary>
        /// Inizializza assembly di un oggetto ODS
        /// </summary>
        /// <param name="data">Buffer SUP completo</param>
        /// <param name="packetStart">Offset iniziale packet ODS</param>
        /// <param name="segmentLength">Lunghezza segmento ODS</param>
        /// <param name="payload">Offset payload ODS</param>
        /// <param name="objectId">Object id PGS</param>
        /// <param name="version">Versione oggetto PGS</param>
        /// <param name="partials">Oggetti parziali in assembly</param>
        /// <param name="state">Stato oggetto inizializzato</param>
        /// <param name="report">Report aggiornato durante la lettura</param>
        /// <returns>True se il primo frammento è valido</returns>
        private static bool StartObject(byte[] data, int packetStart, int segmentLength, int payload, int objectId, int version, Dictionary<int, PgsObjectAssemblyState> partials, out PgsObjectAssemblyState state, PgsSubtitleCanvasRewriteReport report)
        {
            int objectDataLength;

            state = null;

            // Il primo segmento deve contenere object_data_length e dimensioni bitmap
            if (segmentLength < ODS_FIRST_PAYLOAD_HEADER_SIZE)
            {
                report.ErrorMessage = "ODS PGS first segment troppo corto";
                return false;
            }

            // object_data_length include i 4 byte width/height oltre al payload RLE
            objectDataLength = ReadUInt24BigEndian(data, payload + 4);
            if (objectDataLength < 4)
            {
                report.ErrorMessage = "ODS PGS object_data_length non valido";
                return false;
            }

            state = new PgsObjectAssemblyState();
            state.ObjectId = objectId;
            state.Version = version;
            state.ExpectedRleLength = objectDataLength - 4;
            state.Width = ReadUInt16BigEndian(data, payload + 7);
            state.Height = ReadUInt16BigEndian(data, payload + 9);
            state.FirstPacketHeader = new byte[SUP_PACKET_HEADER_SIZE];
            Array.Copy(data, packetStart, state.FirstPacketHeader, 0, SUP_PACKET_HEADER_SIZE);

            // Dimensioni nulle rendono impossibile decodifica e validazione bounds
            if (state.Width <= 0 || state.Height <= 0)
            {
                report.ErrorMessage = "ODS PGS dimensione oggetto non valida";
                return false;
            }

            // Due first segment con stesso object id nella stessa assembly sarebbero ambigui
            if (partials.ContainsKey(objectId))
            {
                report.ErrorMessage = "ODS PGS first segment duplicato";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Aggiunge bytes RLE allo stato oggetto
        /// </summary>
        /// <param name="state">Stato oggetto in assembly</param>
        /// <param name="data">Buffer SUP completo</param>
        /// <param name="dataOffset">Offset dati RLE da copiare</param>
        /// <param name="dataLength">Numero byte RLE da copiare</param>
        /// <param name="report">Report aggiornato durante la lettura</param>
        /// <returns>True se il payload è stato aggiunto</returns>
        private static bool AppendObjectData(PgsObjectAssemblyState state, byte[] data, int dataOffset, int dataLength, PgsSubtitleCanvasRewriteReport report)
        {
            if (dataLength < 0 || dataOffset + dataLength > data.Length)
            {
                report.ErrorMessage = "ODS PGS payload fuori buffer";
                return false;
            }

            if (state.RleData.Length + dataLength > state.ExpectedRleLength)
            {
                report.ErrorMessage = "ODS PGS RLE oltre object_data_length";
                return false;
            }

            state.RleData.Write(data, dataOffset, dataLength);
            return true;
        }

        /// <summary>
        /// Completa un oggetto ricostruito
        /// </summary>
        /// <param name="state">Stato oggetto in assembly</param>
        /// <param name="objects">Oggetti completi raccolti</param>
        /// <param name="report">Report aggiornato durante la lettura</param>
        /// <returns>True se l'oggetto è completo</returns>
        private static bool CompleteObject(PgsObjectAssemblyState state, Dictionary<int, PgsObjectDefinition> objects, PgsSubtitleCanvasRewriteReport report)
        {
            PgsObjectDefinition result;

            // Il payload ricostruito deve combaciare esattamente con object_data_length
            if (state.RleData.Length != state.ExpectedRleLength)
            {
                report.ErrorMessage = "ODS PGS RLE incompleto";
                return false;
            }

            result = new PgsObjectDefinition(state.ObjectId, state.Version, state.Width, state.Height, state.RleData.ToArray(), state.FirstPacketHeader);
            objects[result.ObjectId] = result;
            return true;
        }

        #endregion

        #region Metodi privati - Scrittura ODS


        /// <summary>
        /// Costruisce il primo packet ODS
        /// </summary>
        /// <param name="definition">Definizione oggetto da scrivere</param>
        /// <param name="dataOffset">Offset RLE sorgente</param>
        /// <param name="dataLength">Numero byte RLE da scrivere</param>
        /// <param name="flags">Flag ODS da impostare</param>
        /// <returns>Packet ODS first segment</returns>
        private static byte[] BuildFirstObjectPacket(PgsObjectDefinition definition, int dataOffset, int dataLength, int flags)
        {
            byte[] packet = CreateObjectPacket(definition.FirstPacketHeader, ODS_FIRST_PAYLOAD_HEADER_SIZE + dataLength);
            int payload = SUP_PACKET_HEADER_SIZE;

            // Header primo ODS: object id, versione, flag, lunghezza oggetto e dimensioni bitmap
            WriteUInt16BigEndian(packet, payload, definition.ObjectId);
            packet[payload + 2] = (byte)definition.Version;
            packet[payload + 3] = (byte)flags;
            WriteUInt24BigEndian(packet, payload + 4, 4 + definition.RleData.Length);
            WriteUInt16BigEndian(packet, payload + 7, definition.Width);
            WriteUInt16BigEndian(packet, payload + 9, definition.Height);
            if (dataLength > 0)
            {
                Array.Copy(definition.RleData, dataOffset, packet, payload + ODS_FIRST_PAYLOAD_HEADER_SIZE, dataLength);
            }

            return packet;
        }

        /// <summary>
        /// Costruisce un packet ODS continuation
        /// </summary>
        /// <param name="definition">Definizione oggetto da scrivere</param>
        /// <param name="dataOffset">Offset RLE sorgente</param>
        /// <param name="dataLength">Numero byte RLE da scrivere</param>
        /// <param name="flags">Flag ODS da impostare</param>
        /// <returns>Packet ODS continuation</returns>
        private static byte[] BuildObjectContinuationPacket(PgsObjectDefinition definition, int dataOffset, int dataLength, int flags)
        {
            byte[] packet = CreateObjectPacket(definition.FirstPacketHeader, ODS_CONTINUATION_PAYLOAD_HEADER_SIZE + dataLength);
            int payload = SUP_PACKET_HEADER_SIZE;

            // Header continuation ODS: object id, versione e flag di frammentazione
            WriteUInt16BigEndian(packet, payload, definition.ObjectId);
            packet[payload + 2] = (byte)definition.Version;
            packet[payload + 3] = (byte)flags;
            if (dataLength > 0)
            {
                Array.Copy(definition.RleData, dataOffset, packet, payload + ODS_CONTINUATION_PAYLOAD_HEADER_SIZE, dataLength);
            }

            return packet;
        }

        /// <summary>
        /// Crea un packet SUP con header copiato e lunghezza aggiornata
        /// </summary>
        /// <param name="originalHeader">Header SUP originale da preservare</param>
        /// <param name="segmentLength">Lunghezza nuovo segmento ODS</param>
        /// <returns>Packet SUP vuoto con header aggiornato</returns>
        private static byte[] CreateObjectPacket(byte[] originalHeader, int segmentLength)
        {
            byte[] packet = new byte[SUP_PACKET_HEADER_SIZE + segmentLength];

            // Mantiene magic/timing originali e aggiorna tipo segmento e lunghezza payload
            Array.Copy(originalHeader, packet, SUP_PACKET_HEADER_SIZE);
            packet[10] = SEGMENT_OBJECT;
            WriteUInt16BigEndian(packet, 11, segmentLength);
            return packet;
        }

        #endregion

        #region Metodi privati - Decode RLE

        /// <summary>
        /// Scrive un run decodificato nella bitmap output
        /// </summary>
        /// <param name="pixels">Buffer pixel output</param>
        /// <param name="width">Larghezza bitmap</param>
        /// <param name="height">Altezza bitmap</param>
        /// <param name="x">Coordinata X corrente</param>
        /// <param name="y">Coordinata Y corrente</param>
        /// <param name="runLength">Lunghezza run da scrivere</param>
        /// <param name="color">Indice palette da scrivere</param>
        /// <param name="warnings">Warning non fatali aggiornati</param>
        /// <param name="errorMessage">Errore in caso di run invalido</param>
        /// <returns>True se il run è stato scritto</returns>
        private static bool WriteDecodedRun(byte[] pixels, int width, int height, ref int x, ref int y, int runLength, int color, ref int warnings, out string errorMessage)
        {
            errorMessage = "";
            if (x >= width)
            {
                y++;
                x = 0;
                warnings++;
            }

            if (y >= height || x + runLength > width)
            {
                errorMessage = "RLE PGS run fuori riga";
                return false;
            }

            for (int i = 0; i < runLength; i++)
            {
                pixels[(y * width) + x + i] = (byte)color;
            }

            x += runLength;
            return true;
        }

        /// <summary>
        /// Chiude una riga RLE decodificata
        /// </summary>
        /// <param name="pixels">Buffer pixel output</param>
        /// <param name="width">Larghezza bitmap</param>
        /// <param name="height">Altezza bitmap</param>
        /// <param name="x">Coordinata X corrente</param>
        /// <param name="y">Coordinata Y corrente</param>
        /// <param name="warnings">Warning non fatali aggiornati</param>
        private static void EndDecodedLine(byte[] pixels, int width, int height, ref int x, ref int y, ref int warnings)
        {
            if (y >= height)
            {
                warnings++;
                return;
            }

            if (x < width)
            {
                while (x < width)
                {
                    pixels[(y * width) + x] = 0;
                    x++;
                }

                warnings++;
            }

            y++;
            x = 0;
        }

        #endregion

        #region Metodi privati - Encode RLE

        /// <summary>
        /// Scrive un run nel formato RLE PGS
        /// </summary>
        /// <param name="result">Buffer RLE output</param>
        /// <param name="color">Indice palette del run</param>
        /// <param name="runLength">Lunghezza run da scrivere</param>
        private static void WriteEncodedRun(List<byte> result, byte color, int runLength)
        {
            int remaining = runLength;
            int part;

            while (remaining > 0)
            {
                part = remaining > MAX_RLE_RUN_LENGTH ? MAX_RLE_RUN_LENGTH : remaining;

                // Pixel singolo non trasparente: codifica compatta senza escape
                if (color != 0 && part == 1)
                {
                    result.Add(color);
                }

                // Run trasparente: escape 00 + lunghezza corta/lunga
                else if (color == 0)
                {
                    result.Add(0x00);
                    if (part < 0x40)
                    {
                        result.Add((byte)part);
                    }
                    else
                    {
                        result.Add((byte)(0x40 | ((part >> 8) & 0x3f)));
                        result.Add((byte)(part & 0xff));
                    }
                }

                // Run colore: escape 00 + lunghezza corta/lunga + indice palette
                else
                {
                    result.Add(0x00);
                    if (part < 0x40)
                    {
                        result.Add((byte)(0x80 | part));
                        result.Add(color);
                    }
                    else
                    {
                        result.Add((byte)(0xc0 | ((part >> 8) & 0x3f)));
                        result.Add((byte)(part & 0xff));
                        result.Add(color);
                    }
                }

                remaining -= part;
            }
        }

        #endregion

        #region Metodi privati - Scaling bitmap

        /// <summary>
        /// Scala con nearest-neighbor
        /// </summary>
        /// <param name="input">Bitmap input</param>
        /// <param name="outputWidth">Larghezza output</param>
        /// <param name="outputHeight">Altezza output</param>
        /// <returns>Bitmap scalata</returns>
        private static PgsSubtitleBitmap ScaleBitmapNearest(PgsSubtitleBitmap input, int outputWidth, int outputHeight)
        {
            byte[] output = new byte[outputWidth * outputHeight];
            int sourceX;
            int sourceY;

            // Mappa ogni pixel output sul pixel sorgente più vicino
            for (int y = 0; y < outputHeight; y++)
            {
                sourceY = Math.Min(input.Height - 1, (int)(((y + 0.5) * input.Height) / outputHeight));
                for (int x = 0; x < outputWidth; x++)
                {
                    sourceX = Math.Min(input.Width - 1, (int)(((x + 0.5) * input.Width) / outputWidth));
                    output[(y * outputWidth) + x] = input.Pixels[(sourceY * input.Width) + sourceX];
                }
            }

            return new PgsSubtitleBitmap(outputWidth, outputHeight, output);
        }

        /// <summary>
        /// Scala scegliendo il colore prevalente dell'area sorgente
        /// </summary>
        /// <param name="input">Bitmap input</param>
        /// <param name="outputWidth">Larghezza output</param>
        /// <param name="outputHeight">Altezza output</param>
        /// <returns>Bitmap scalata</returns>
        private static PgsSubtitleBitmap ScaleBitmapMajority(PgsSubtitleBitmap input, int outputWidth, int outputHeight)
        {
            byte[] output = new byte[outputWidth * outputHeight];
            int[] counts = new int[256];
            int xStart;
            int xEnd;
            int yStart;
            int yEnd;

            // Per ogni pixel output calcola il rettangolo sorgente coperto
            for (int y = 0; y < outputHeight; y++)
            {
                yStart = (int)Math.Floor(y * input.Height / (double)outputHeight);
                yEnd = Math.Max(yStart + 1, (int)Math.Ceiling((y + 1) * input.Height / (double)outputHeight));
                yEnd = Math.Min(yEnd, input.Height);
                for (int x = 0; x < outputWidth; x++)
                {
                    Array.Clear(counts, 0, counts.Length);
                    xStart = (int)Math.Floor(x * input.Width / (double)outputWidth);
                    xEnd = Math.Max(xStart + 1, (int)Math.Ceiling((x + 1) * input.Width / (double)outputWidth));
                    xEnd = Math.Min(xEnd, input.Width);

                    // Sceglie l'indice palette più presente nell'area sorgente
                    output[(y * outputWidth) + x] = SelectMajorityColor(input, xStart, xEnd, yStart, yEnd, counts);
                }
            }

            return new PgsSubtitleBitmap(outputWidth, outputHeight, output);
        }

        /// <summary>
        /// Seleziona l'indice palette prevalente in un rettangolo sorgente
        /// </summary>
        /// <param name="input">Bitmap input</param>
        /// <param name="xStart">X iniziale area sorgente inclusiva</param>
        /// <param name="xEnd">X finale area sorgente esclusiva</param>
        /// <param name="yStart">Y iniziale area sorgente inclusiva</param>
        /// <param name="yEnd">Y finale area sorgente esclusiva</param>
        /// <param name="counts">Buffer contatori palette riutilizzato</param>
        /// <returns>Indice palette prevalente</returns>
        private static byte SelectMajorityColor(PgsSubtitleBitmap input, int xStart, int xEnd, int yStart, int yEnd, int[] counts)
        {
            int color;
            int bestColor = 0;
            int bestCount = -1;

            // Conta gli indici palette presenti nel rettangolo sorgente
            for (int y = yStart; y < yEnd; y++)
            {
                for (int x = xStart; x < xEnd; x++)
                {
                    color = input.Pixels[(y * input.Width) + x];
                    counts[color]++;
                }
            }

            // Preferisce un colore non trasparente in caso di pareggio con trasparente
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] > bestCount || (counts[i] == bestCount && bestColor == 0 && i != 0))
                {
                    bestColor = i;
                    bestCount = counts[i];
                }
            }

            return (byte)bestColor;
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Stato temporaneo di assembly ODS
        /// </summary>
        private class PgsObjectAssemblyState
        {
            /// <summary>
            /// Object id
            /// </summary>
            public int ObjectId { get; set; }

            /// <summary>
            /// Versione oggetto
            /// </summary>
            public int Version { get; set; }

            /// <summary>
            /// Larghezza bitmap
            /// </summary>
            public int Width { get; set; }

            /// <summary>
            /// Altezza bitmap
            /// </summary>
            public int Height { get; set; }

            /// <summary>
            /// Lunghezza RLE attesa
            /// </summary>
            public int ExpectedRleLength { get; set; }

            /// <summary>
            /// Header SUP del primo packet
            /// </summary>
            public byte[] FirstPacketHeader { get; set; }

            /// <summary>
            /// Buffer RLE
            /// </summary>
            public MemoryStream RleData { get; private set; }

            /// <summary>
            /// Costruttore
            /// </summary>
            public PgsObjectAssemblyState()
            {
                this.FirstPacketHeader = new byte[0];
                this.RleData = new MemoryStream();
            }
        }

        #endregion
    }
}
