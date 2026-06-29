using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Utility per sottotitoli VobSub IDX/SUB
    /// </summary>
    internal static class VobSubSubtitleUtils
    {
        #region Metodi pubblici - IDX

        /// <summary>
        /// Prova a leggere timestamp e filepos da una riga IDX
        /// </summary>
        /// <param name="line">Riga IDX</param>
        /// <param name="timestampMs">Timestamp in millisecondi</param>
        /// <param name="filePosition">Posizione nel file SUB</param>
        /// <returns>True se la riga contiene una entry valida</returns>
        public static bool TryParseEntryLine(string line, out long timestampMs, out long filePosition)
        {
            int timestampIndex;
            int valueStart;
            int commaIndex;
            string timestamp;
            int filePosIndex;
            int filePosStart;
            int filePosEnd;
            string filePosValue;

            timestampMs = 0;
            filePosition = 0;

            // Una entry valida deve contenere prima timestamp e poi filepos nella stessa riga
            timestampIndex = line.IndexOf("timestamp:", StringComparison.OrdinalIgnoreCase);
            if (timestampIndex < 0)
            {
                return false;
            }

            valueStart = timestampIndex + "timestamp:".Length;
            commaIndex = line.IndexOf(",", valueStart, StringComparison.Ordinal);
            if (commaIndex < 0)
            {
                return false;
            }

            // Il timestamp termina alla prima virgola, prima dei campi aggiuntivi IDX
            timestamp = line.Substring(valueStart, commaIndex - valueStart).Trim();
            if (!TryParseTimestamp(timestamp, out timestampMs))
            {
                return false;
            }

            filePosIndex = line.IndexOf("filepos:", commaIndex, StringComparison.OrdinalIgnoreCase);
            if (filePosIndex < 0)
            {
                return false;
            }

            // filepos e' esadecimale e puo' essere seguito da altri token non gestiti
            filePosStart = filePosIndex + "filepos:".Length;
            while (filePosStart < line.Length && line[filePosStart] == ' ')
            {
                filePosStart++;
            }

            filePosEnd = filePosStart;
            while (filePosEnd < line.Length && IsHexChar(line[filePosEnd]))
            {
                filePosEnd++;
            }

            if (filePosEnd <= filePosStart)
            {
                return false;
            }

            filePosValue = line.Substring(filePosStart, filePosEnd - filePosStart);
            return long.TryParse(filePosValue, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out filePosition);
        }

        /// <summary>
        /// Riscrive una riga IDX con timestamp e filepos aggiornati
        /// </summary>
        /// <param name="line">Riga originale</param>
        /// <param name="timestampMs">Nuovo timestamp</param>
        /// <param name="filePosition">Nuovo filepos</param>
        /// <returns>Riga aggiornata</returns>
        public static string RewriteEntryLine(string line, long timestampMs, long filePosition)
        {
            int timestampIndex = line.IndexOf("timestamp:", StringComparison.OrdinalIgnoreCase);
            int valueStart = timestampIndex + "timestamp:".Length;
            int commaIndex = line.IndexOf(",", valueStart, StringComparison.Ordinal);
            int filePosIndex = line.IndexOf("filepos:", commaIndex, StringComparison.OrdinalIgnoreCase);
            int filePosStart = filePosIndex + "filepos:".Length;
            int filePosEnd;
            StringBuilder result;
            
            // Mantiene struttura e spazi della riga originale sostituendo solo timestamp e filepos
            while (filePosStart < line.Length && line[filePosStart] == ' ')
            {
                filePosStart++;
            }

            filePosEnd = filePosStart;
            while (filePosEnd < line.Length && IsHexChar(line[filePosEnd]))
            {
                filePosEnd++;
            }

            result = new StringBuilder();
            result.Append(line.Substring(0, valueStart));
            result.Append(" ");
            result.Append(FormatTimestamp(timestampMs));
            result.Append(line.Substring(commaIndex, filePosStart - commaIndex));
            result.Append(filePosition.ToString("x9", CultureInfo.InvariantCulture));

            // Preserva eventuali token successivi a filepos non gestiti direttamente
            result.Append(line.Substring(filePosEnd));
            return result.ToString();
        }

        #endregion

        #region Metodi pubblici - SUB/SPU

        /// <summary>
        /// Riscrive un blocco SUB contenente una SPU DVD
        /// </summary>
        /// <param name="block">Blocco SUB originale</param>
        /// <param name="transform">Trasformazione canvas</param>
        /// <param name="palette">Palette IDX RGB a 16 colori, se presente</param>
        /// <param name="rewrittenBlock">Blocco SUB riscritto</param>
        /// <param name="areasRewritten">Display area riscritte</param>
        /// <param name="bitmapsDecoded">Bitmap decodificate</param>
        /// <param name="bitmapsScaled">Bitmap scalate</param>
        /// <param name="bitmapsEncoded">Bitmap ricodificate</param>
        /// <param name="errorMessage">Errore</param>
        /// <returns>True se il blocco e' stato riscritto</returns>
        public static bool TryRewriteSubtitleBlock(
            byte[] block,
            SubtitleCanvasTransform transform,
            int[] palette,
            out byte[] rewrittenBlock,
            out int areasRewritten,
            out int bitmapsDecoded,
            out int bitmapsScaled,
            out int bitmapsEncoded,
            out string errorMessage)
        {
            VobSubSpuInfo info;
            byte[] rawSpu;
            byte[] newSpu;

            rewrittenBlock = null;
            areasRewritten = 0;
            bitmapsDecoded = 0;
            bitmapsScaled = 0;
            bitmapsEncoded = 0;
            errorMessage = "";

            if (!TryFindSpu(block, out info, out errorMessage))
            {
                return false;
            }

            rawSpu = new byte[info.SpuSize];
            Array.Copy(block, info.SpuOffset, rawSpu, 0, rawSpu.Length);
            if (!TryRewriteRawSpu(rawSpu, transform, palette, out newSpu, out areasRewritten, out bitmapsDecoded, out bitmapsScaled, out bitmapsEncoded, out errorMessage))
            {
                return false;
            }

            rewrittenBlock = ReplaceSpuInBlock(block, info.SpuOffset, info.SpuSize, newSpu, out errorMessage);
            return rewrittenBlock != null;
        }

        #endregion

        #region Metodi privati - IDX

        /// <summary>
        /// Converte un timestamp IDX in millisecondi
        /// </summary>
        /// <param name="value">Timestamp IDX testuale</param>
        /// <param name="ms">Millisecondi letti</param>
        /// <returns>True se il timestamp è valido</returns>
        private static bool TryParseTimestamp(string value, out long ms)
        {
            string[] parts = value.Split(new char[] { ':', '.' });
            int h;
            int m;
            int s;
            int milli;

            ms = 0;
            if (parts.Length < 4)
            {
                return false;
            }

            if (!int.TryParse(parts[0], out h) || !int.TryParse(parts[1], out m) || !int.TryParse(parts[2], out s) || !int.TryParse(parts[3], out milli))
            {
                return false;
            }

            ms = (((h * 60L) + m) * 60L + s) * 1000L + milli;
            return true;
        }

        /// <summary>
        /// Formatta millisecondi nel formato timestamp IDX
        /// </summary>
        /// <param name="ms">Millisecondi da formattare</param>
        /// <returns>Timestamp IDX formattato</returns>
        private static string FormatTimestamp(long ms)
        {
            long h;
            long m;
            long s;
            long milli;

            if (ms < 0)
            {
                ms = 0;
            }

            h = ms / 3600000L;
            ms %= 3600000L;
            m = ms / 60000L;
            ms %= 60000L;
            s = ms / 1000L;
            milli = ms % 1000L;
            return h.ToString("00", CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture) + ":" + s.ToString("00", CultureInfo.InvariantCulture) + ":" + milli.ToString("000", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Indica se il carattere appartiene a un valore esadecimale filepos
        /// </summary>
        /// <param name="value">Carattere da verificare</param>
        /// <returns>True se il carattere e' esadecimale</returns>
        private static bool IsHexChar(char value)
        {
            return (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f') || (value >= 'A' && value <= 'F');
        }

        #endregion

        #region Metodi privati - SPU rewrite

        /// <summary>
        /// Cerca una SPU DVD valida dentro il blocco SUB
        /// </summary>
        /// <param name="block">Blocco SUB da analizzare</param>
        /// <param name="info">Informazioni SPU trovate</param>
        /// <param name="errorMessage">Errore in caso di blocco non trasformabile</param>
        /// <returns>True se una SPU valida e' stata trovata</returns>
        private static bool TryFindSpu(byte[] block, out VobSubSpuInfo info, out string errorMessage)
        {
            int maxScan = Math.Max(0, Math.Min(block.Length - 4, 4096));

            info = null;
            errorMessage = "";

            // I blocchi .sub possono contenere header PES prima della SPU raw: cerca una SPU valida in modo prudente
            for (int offset = 0; offset <= maxScan; offset++)
            {
                if (TryParseSpu(block, offset, block.Length - offset, out info, false, out errorMessage))
                {
                    return true;
                }

                // Se il parser ha trovato una SPU parziale/non trasformabile non continua a cercare falsi positivi successivi
                if (errorMessage.Length > 0)
                {
                    return false;
                }
            }

            errorMessage = "SPU VobSub non trovata nel blocco SUB";
            return false;
        }

        /// <summary>
        /// Riscrive una SPU raw, scalando bitmap e command table
        /// </summary>
        /// <param name="rawSpu">SPU raw da riscrivere</param>
        /// <param name="transform">Trasformazione canvas da applicare</param>
        /// <param name="palette">Palette IDX RGB a 16 colori, se presente</param>
        /// <param name="output">SPU raw riscritta</param>
        /// <param name="areasRewritten">Numero SET_DAREA riscritti</param>
        /// <param name="bitmapsDecoded">Numero bitmap decodificate</param>
        /// <param name="bitmapsScaled">Numero bitmap scalate</param>
        /// <param name="bitmapsEncoded">Numero bitmap ricodificate</param>
        /// <param name="errorMessage">Errore in caso di rewrite fallito</param>
        /// <returns>True se la SPU e' stata riscritta</returns>
        private static bool TryRewriteRawSpu(byte[] rawSpu, SubtitleCanvasTransform transform, int[] palette, out byte[] output, out int areasRewritten, out int bitmapsDecoded, out int bitmapsScaled, out int bitmapsEncoded, out string errorMessage)
        {
            VobSubSpuInfo info;
            VobSubBitmap bitmap;
            VobSubBitmap scaledBitmap;
            byte[] topRle;
            byte[] bottomRle;
            byte[] commandTable;
            int newX;
            int newY;
            int newWidth;
            int newHeight;
            int newCommandOffset;

            output = null;
            areasRewritten = 0;
            bitmapsDecoded = 0;
            bitmapsScaled = 0;
            bitmapsEncoded = 0;
            errorMessage = "";

            if (!TryParseSpu(rawSpu, 0, rawSpu.Length, out info, true, out errorMessage))
            {
                return false;
            }

            // Trasforma SET_DAREA nello spazio canvas finale e valida subito i bounds
            newX = transform.MapX(info.X);
            newY = transform.MapY(info.Y);
            newWidth = transform.MapWidth(info.Width);
            newHeight = transform.MapHeight(info.Height);
            if (newX < 0 || newY < 0 || newWidth <= 0 || newHeight <= 0 ||
                newX + newWidth > transform.OutputCanvasWidth ||
                newY + newHeight > transform.OutputCanvasHeight)
            {
                errorMessage = "SET_DAREA VobSub fuori canvas";
                return false;
            }

            // Offset-only: non tocca bitmap/RLE, riscrive solo command table e coordinate
            if (!transform.RequiresScaling)
            {
                commandTable = BuildCommandTable(rawSpu, info, 0, info.TopFieldOffset, info.BottomFieldOffset, newX, newY, newWidth, newHeight, out areasRewritten, out errorMessage);
                if (commandTable == null)
                {
                    return false;
                }

                output = new byte[rawSpu.Length];
                Array.Copy(rawSpu, output, rawSpu.Length);
                Array.Copy(commandTable, 0, output, info.CommandOffset, commandTable.Length);
                return true;
            }

            // Resize reale: decodifica i due field interlacciati in una bitmap completa
            if (!DecodeBitmap(rawSpu, info, out bitmap, out errorMessage))
            {
                return false;
            }
            bitmapsDecoded++;

            // Scala in spazio palette-aware quando SET_COLOR/SET_CONTR e palette IDX sono disponibili
            scaledBitmap = transform.RequiresScaling ? ScaleBitmap(bitmap, newWidth, newHeight, info, palette) : bitmap;
            if (transform.RequiresScaling)
            {
                bitmapsScaled++;
            }

            // Ricodifica top/bottom field e ricostruisce command table con i nuovi offset RLE
            topRle = EncodeFieldRle(scaledBitmap, 0);
            bottomRle = EncodeFieldRle(scaledBitmap, 1);
            bitmapsEncoded++;

            newCommandOffset = 4 + topRle.Length + bottomRle.Length;
            commandTable = BuildCommandTable(rawSpu, info, newCommandOffset - info.CommandOffset, 4, 4 + topRle.Length, newX, newY, newWidth, newHeight, out areasRewritten, out errorMessage);
            if (commandTable == null)
            {
                return false;
            }

            output = new byte[newCommandOffset + commandTable.Length];
            WriteUInt16BigEndian(output, 0, output.Length);
            WriteUInt16BigEndian(output, 2, newCommandOffset);
            Array.Copy(topRle, 0, output, 4, topRle.Length);
            Array.Copy(bottomRle, 0, output, 4 + topRle.Length, bottomRle.Length);
            Array.Copy(commandTable, 0, output, newCommandOffset, commandTable.Length);
            return true;
        }

        /// <summary>
        /// Sostituisce la SPU nel blocco SUB e aggiorna lunghezze PES semplici
        /// </summary>
        /// <param name="block">Blocco SUB originale</param>
        /// <param name="spuOffset">Offset SPU nel blocco</param>
        /// <param name="oldSpuSize">Lunghezza SPU originale</param>
        /// <param name="newSpu">SPU riscritta</param>
        /// <param name="errorMessage">Errore in caso di sostituzione fallita</param>
        /// <returns>Blocco SUB riscritto, null se non aggiornabile</returns>
        private static byte[] ReplaceSpuInBlock(byte[] block, int spuOffset, int oldSpuSize, byte[] newSpu, out string errorMessage)
        {
            byte[] result;
            int diff = newSpu.Length - oldSpuSize;

            errorMessage = "";
            result = new byte[block.Length + diff];
            Array.Copy(block, 0, result, 0, spuOffset);
            Array.Copy(newSpu, 0, result, spuOffset, newSpu.Length);
            Array.Copy(block, spuOffset + oldSpuSize, result, spuOffset + newSpu.Length, block.Length - spuOffset - oldSpuSize);

            // Se la SPU cambia lunghezza, prova ad aggiornare il contenitore PES semplice che la precede
            if (diff != 0 && !TryUpdateContainingPesLength(result, spuOffset, diff, out errorMessage))
            {
                return null;
            }

            return result;
        }

        /// <summary>
        /// Aggiorna una PES private stream length se il blocco la contiene
        /// </summary>
        /// <param name="block">Blocco SUB riscritto</param>
        /// <param name="payloadOffset">Offset payload SPU</param>
        /// <param name="diff">Delta lunghezza SPU</param>
        /// <param name="errorMessage">Errore in caso di PES non aggiornabile</param>
        /// <returns>True se la lunghezza PES e' coerente o non necessaria</returns>
        private static bool TryUpdateContainingPesLength(byte[] block, int payloadOffset, int diff, out string errorMessage)
        {
            int pesStart = -1;
            int length;
            int newLength;

            errorMessage = "";

            // Cerca all'indietro il packet PES private stream che contiene la SPU
            for (int i = payloadOffset; i >= 0 && i >= payloadOffset - 4096; i--)
            {
                if (i + 6 <= block.Length && block[i] == 0x00 && block[i + 1] == 0x00 && block[i + 2] == 0x01 && block[i + 3] == 0xbd)
                {
                    pesStart = i;
                    break;
                }
            }

            if (pesStart < 0)
            {
                return true;
            }

            // Lunghezza zero significa PES unbounded: non c'e' niente da aggiornare
            length = ReadUInt16BigEndian(block, pesStart + 4);
            if (length == 0)
            {
                return true;
            }

            // La lunghezza PES resta a 16 bit, quindi un resize troppo grande non e' muxabile in sicurezza
            newLength = length + diff;
            if (newLength <= 0 || newLength > 0xffff)
            {
                errorMessage = "PES VobSub troppo grande dopo rewrite";
                return false;
            }

            WriteUInt16BigEndian(block, pesStart + 4, newLength);
            return true;
        }

        #endregion

        #region Metodi privati - SPU parse

        /// <summary>
        /// Parse SPU e command table
        /// </summary>
        /// <param name="data">Buffer in cui cercare la SPU</param>
        /// <param name="offset">Offset candidato SPU</param>
        /// <param name="availableLength">Lunghezza disponibile dal candidato</param>
        /// <param name="info">Informazioni SPU lette</param>
        /// <param name="strict">True per errore esplicito sui comandi non supportati</param>
        /// <param name="errorMessage">Errore in caso di SPU non trasformabile</param>
        /// <returns>True se la SPU e' valida e trasformabile</returns>
        private static bool TryParseSpu(byte[] data, int offset, int availableLength, out VobSubSpuInfo info, bool strict, out string errorMessage)
        {
            int spuSize;
            int commandOffset;

            info = null;
            errorMessage = "";
            if (availableLength < 10 || offset + 4 > data.Length)
            {
                return false;
            }

            // I primi due uint16 definiscono lunghezza SPU e offset della command table
            spuSize = ReadUInt16BigEndian(data, offset);
            commandOffset = ReadUInt16BigEndian(data, offset + 2);
            if (spuSize <= 4 || commandOffset < 4 || commandOffset >= spuSize || spuSize > availableLength || offset + spuSize > data.Length)
            {
                return false;
            }

            // Header SPU valido: da qui in poi vengono raccolte le coordinate e gli offset RLE necessari al rewrite
            info = new VobSubSpuInfo();
            info.SpuOffset = offset;
            info.SpuSize = spuSize;
            info.CommandOffset = commandOffset;
            if (!ParseCommandTable(data, offset, spuSize, commandOffset, info, strict, out errorMessage))
            {
                info = null;
                return false;
            }

            // Senza SET_DAREA e SET_DSPXA non conosciamo posizione né pixel data, quindi il blocco non è trasformabile
            if (!info.HasDisplayArea || !info.HasPixelOffsets)
            {
                info = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Parse command table SPU
        /// </summary>
        /// <param name="data">Buffer SPU sorgente</param>
        /// <param name="spuOffset">Offset SPU nel buffer</param>
        /// <param name="spuSize">Lunghezza SPU</param>
        /// <param name="commandOffset">Offset command table relativo alla SPU</param>
        /// <param name="info">Informazioni SPU da popolare</param>
        /// <param name="strict">True per errore esplicito sui comandi non supportati</param>
        /// <param name="errorMessage">Errore in caso di command table non trasformabile</param>
        /// <returns>True se la command table è stata parseata</returns>
        private static bool ParseCommandTable(byte[] data, int spuOffset, int spuSize, int commandOffset, VobSubSpuInfo info, bool strict, out string errorMessage)
        {
            int currentOffset = commandOffset;
            int sequenceStart;
            int nextOffset;
            int commandPos;
            int command;
            int commandLength;
            int guard = 0;
            int x1;
            int x2;
            int y1;
            int y2;

            errorMessage = "";

            // Una SPU puo' contenere piu' command sequence concatenate tramite nextOffset
            while (currentOffset >= 4 && currentOffset < spuSize && guard < 64)
            {
                guard++;
                sequenceStart = spuOffset + currentOffset;
                if (sequenceStart + 4 > spuOffset + spuSize)
                {
                    return false;
                }

                info.SequenceOffsets.Add(currentOffset);
                nextOffset = ReadUInt16BigEndian(data, sequenceStart + 2);
                commandPos = sequenceStart + 4;

                // Scorre i comandi della sequence preservando quelli noti e bloccando varianti non riscrivibili
                while (commandPos < spuOffset + spuSize)
                {
                    command = data[commandPos];
                    commandPos++;
                    if (command == 0xff)
                    {
                        break;
                    }

                    commandLength = GetCommandLength(command);
                    if (commandLength < 0 || commandPos + commandLength > spuOffset + spuSize)
                    {
                        if (strict || info.HasDisplayArea || info.HasPixelOffsets)
                        {
                            errorMessage = "comando SPU VobSub non trasformabile: 0x" + command.ToString("x2", CultureInfo.InvariantCulture);
                        }
                        return false;
                    }

                    // SET_DAREA contiene il rettangolo di display da mappare sul canvas finale
                    if (command == 0x05)
                    {
                        ReadDisplayArea(data, commandPos, out x1, out x2, out y1, out y2);
                        info.X = x1;
                        info.X2 = x2;
                        info.Y = y1;
                        info.Y2 = y2;
                        info.DisplayAreaCommandOffsets.Add(commandPos - spuOffset);
                        info.HasDisplayArea = true;
                    }

                    // SET_COLOR mappa i quattro indici 2-bit ai colori della palette IDX
                    else if (command == 0x03)
                    {
                        ReadNibbleMap(data, commandPos, info.ColorIndexes);
                        info.HasColorIndexes = true;
                    }

                    // SET_CONTR assegna l'alpha/contrasto dei quattro indici 2-bit
                    else if (command == 0x04)
                    {
                        ReadNibbleMap(data, commandPos, info.ContrastValues);
                        info.HasContrastValues = true;
                    }

                    // SET_DSPXA contiene gli offset dei due field RLE da aggiornare dopo ricodifica
                    else if (command == 0x06)
                    {
                        info.TopFieldOffset = ReadUInt16BigEndian(data, commandPos);
                        info.BottomFieldOffset = ReadUInt16BigEndian(data, commandPos + 2);
                        info.PixelOffsetCommandOffsets.Add(commandPos - spuOffset);
                        info.HasPixelOffsets = true;
                    }

                    commandPos += commandLength;
                }

                // nextOffset invalido o non progressivo chiude la catena delle sequence
                if (nextOffset <= currentOffset || nextOffset >= spuSize)
                {
                    break;
                }

                currentOffset = nextOffset;
            }

            return true;
        }

        /// <summary>
        /// Restituisce la lunghezza payload di un comando SPU
        /// </summary>
        /// <param name="command">Codice comando SPU</param>
        /// <returns>Lunghezza payload, -1 se non supportato</returns>
        private static int GetCommandLength(int command)
        {
            switch (command)
            {
                case 0x00:
                case 0x01:
                case 0x02:
                    return 0;
                case 0x03:
                case 0x04:
                    return 2;
                case 0x05:
                    return 6;
                case 0x06:
                    return 4;
                default:
                    return -1;
            }
        }

        /// <summary>
        /// Ricostruisce command table con offset aggiornati
        /// </summary>
        /// <param name="rawSpu">SPU raw originale</param>
        /// <param name="info">Informazioni SPU parseate</param>
        /// <param name="delta">Delta offset command table</param>
        /// <param name="topOffset">Nuovo offset top field</param>
        /// <param name="bottomOffset">Nuovo offset bottom field</param>
        /// <param name="x">Nuova X display area</param>
        /// <param name="y">Nuova Y display area</param>
        /// <param name="width">Nuova larghezza display area</param>
        /// <param name="height">Nuova altezza display area</param>
        /// <param name="areasRewritten">Numero SET_DAREA riscritti</param>
        /// <param name="errorMessage">Errore in caso di tabella non ricostruibile</param>
        /// <returns>Command table ricostruita, null se fallita</returns>
        private static byte[] BuildCommandTable(byte[] rawSpu, VobSubSpuInfo info, int delta, int topOffset, int bottomOffset, int x, int y, int width, int height, out int areasRewritten, out string errorMessage)
        {
            byte[] result = new byte[info.SpuSize - info.CommandOffset];
            int sequenceOffset;
            int relativeSequenceOffset;
            int nextOffset;

            areasRewritten = 0;
            errorMessage = "";
            Array.Copy(rawSpu, info.CommandOffset, result, 0, result.Length);

            // Aggiorna i puntatori alla command sequence successiva quando la tabella si sposta
            for (int i = 0; i < info.SequenceOffsets.Count; i++)
            {
                sequenceOffset = info.SequenceOffsets[i];
                relativeSequenceOffset = sequenceOffset - info.CommandOffset;
                nextOffset = ReadUInt16BigEndian(result, relativeSequenceOffset + 2);
                if (nextOffset >= info.CommandOffset && nextOffset < info.SpuSize)
                {
                    WriteUInt16BigEndian(result, relativeSequenceOffset + 2, nextOffset + delta);
                }
            }

            // Riscrive tutti i SET_DAREA incontrati con il nuovo rettangolo display
            for (int i = 0; i < info.DisplayAreaCommandOffsets.Count; i++)
            {
                WriteDisplayArea(result, info.DisplayAreaCommandOffsets[i] - info.CommandOffset, x, x + width - 1, y, y + height - 1);
                areasRewritten++;
            }

            // Aggiorna SET_DSPXA con i nuovi offset top/bottom field
            for (int i = 0; i < info.PixelOffsetCommandOffsets.Count; i++)
            {
                WriteUInt16BigEndian(result, info.PixelOffsetCommandOffsets[i] - info.CommandOffset, topOffset);
                WriteUInt16BigEndian(result, info.PixelOffsetCommandOffsets[i] - info.CommandOffset + 2, bottomOffset);
            }

            return result;
        }

        #endregion

        #region Metodi privati - RLE bitmap

        /// <summary>
        /// Decodifica bitmap interlacciata SPU 2-bit
        /// </summary>
        /// <param name="rawSpu">SPU raw da decodificare</param>
        /// <param name="info">Informazioni SPU parseate</param>
        /// <param name="bitmap">Bitmap decodificata</param>
        /// <param name="errorMessage">Errore in caso di decode fallito</param>
        /// <returns>True se la bitmap e' stata decodificata</returns>
        private static bool DecodeBitmap(byte[] rawSpu, VobSubSpuInfo info, out VobSubBitmap bitmap, out string errorMessage)
        {
            byte[] pixels = new byte[info.Width * info.Height];

            bitmap = null;
            errorMessage = "";

            // Top e bottom field sono memorizzati separatamente ma ricostruiscono righe alternate della bitmap
            if (!DecodeField(rawSpu, info.TopFieldOffset, info.CommandOffset, info.Width, (info.Height + 1) / 2, pixels, 0, out errorMessage))
            {
                return false;
            }
            if (!DecodeField(rawSpu, info.BottomFieldOffset, info.CommandOffset, info.Width, info.Height / 2, pixels, 1, out errorMessage))
            {
                return false;
            }

            bitmap = new VobSubBitmap(info.Width, info.Height, pixels);
            return true;
        }

        /// <summary>
        /// Decodifica un field RLE
        /// </summary>
        /// <param name="data">Buffer SPU</param>
        /// <param name="offset">Offset field RLE</param>
        /// <param name="end">Offset finale leggibile</param>
        /// <param name="width">Larghezza bitmap</param>
        /// <param name="rows">Numero righe del field</param>
        /// <param name="pixels">Buffer pixel output</param>
        /// <param name="firstRow">Prima riga output del field</param>
        /// <param name="errorMessage">Errore in caso di decode fallito</param>
        /// <returns>True se il field è stato decodificato</returns>
        private static bool DecodeField(byte[] data, int offset, int end, int width, int rows, byte[] pixels, int firstRow, out string errorMessage)
        {
            NibbleReader reader = new NibbleReader(data, offset, end);
            int color;
            int run;
            int x;
            int y;
            int rowIndex;

            errorMessage = "";
            for (y = 0; y < rows; y++)
            {
                x = 0;
                rowIndex = firstRow + (y * 2);

                // Ogni riga viene riempita da run 2-bit fino alla larghezza dichiarata da SET_DAREA
                while (x < width)
                {
                    if (!reader.TryReadRun(width - x, out run, out color))
                    {
                        errorMessage = "RLE VobSub non decodificabile";
                        return false;
                    }

                    for (int i = 0; i < run && x < width; i++)
                    {
                        pixels[(rowIndex * width) + x] = (byte)color;
                        x++;
                    }
                }

                // Ogni field RLE viene riallineato a byte a fine riga
                reader.AlignByte();
            }

            return true;
        }

        /// <summary>
        /// Scala bitmap SPU 2-bit
        /// </summary>
        /// <param name="input">Bitmap input</param>
        /// <param name="outputWidth">Larghezza output</param>
        /// <param name="outputHeight">Altezza output</param>
        /// <param name="info">Informazioni SPU parseate</param>
        /// <param name="palette">Palette IDX RGB a 16 colori, se presente</param>
        /// <returns>Bitmap scalata</returns>
        private static VobSubBitmap ScaleBitmap(VobSubBitmap input, int outputWidth, int outputHeight, VobSubSpuInfo info, int[] palette)
        {
            if (CanScaleWithPalette(info, palette))
            {
                return ScaleBitmapWithPalette(input, outputWidth, outputHeight, info, palette);
            }

            return ScaleBitmapByCoverage(input, outputWidth, outputHeight);
        }

        /// <summary>
        /// Verifica se la SPU dispone dei dati colore necessari per il resampling premoltiplicato
        /// </summary>
        /// <param name="info">Informazioni SPU parseate</param>
        /// <param name="palette">Palette IDX RGB</param>
        /// <returns>True se il resampling palette-aware e' applicabile</returns>
        private static bool CanScaleWithPalette(VobSubSpuInfo info, int[] palette)
        {
            if (info == null || palette == null || palette.Length < 16 || !info.HasColorIndexes || !info.HasContrastValues)
            {
                return false;
            }

            for (int i = 0; i < 4; i++)
            {
                if (info.ColorIndexes[i] < 0 || info.ColorIndexes[i] >= 16 || info.ContrastValues[i] < 0 || info.ContrastValues[i] > 15)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Scala la bitmap usando area sampling e quantizzazione sui quattro colori SPU effettivi
        /// </summary>
        /// <param name="input">Bitmap input</param>
        /// <param name="outputWidth">Larghezza output</param>
        /// <param name="outputHeight">Altezza output</param>
        /// <param name="info">Informazioni SPU parseate</param>
        /// <param name="palette">Palette IDX RGB</param>
        /// <returns>Bitmap scalata</returns>
        private static VobSubBitmap ScaleBitmapWithPalette(VobSubBitmap input, int outputWidth, int outputHeight, VobSubSpuInfo info, int[] palette)
        {
            byte[] output = new byte[outputWidth * outputHeight];
            VobSubResampleColor[] colors = BuildVobSubResampleColors(info, palette);
            double sourceX0;
            double sourceX1;
            double sourceY0;
            double sourceY1;

            // Media i pixel sorgenti in RGBA premoltiplicato e ricade su uno dei quattro indici SPU
            for (int y = 0; y < outputHeight; y++)
            {
                sourceY0 = y * input.Height / (double)outputHeight;
                sourceY1 = (y + 1) * input.Height / (double)outputHeight;
                for (int x = 0; x < outputWidth; x++)
                {
                    sourceX0 = x * input.Width / (double)outputWidth;
                    sourceX1 = (x + 1) * input.Width / (double)outputWidth;
                    output[(y * outputWidth) + x] = QuantizeVobSubArea(input, colors, sourceX0, sourceX1, sourceY0, sourceY1);
                }
            }

            return new VobSubBitmap(outputWidth, outputHeight, output);
        }

        /// <summary>
        /// Scala la bitmap usando solo la copertura pesata degli indici 2-bit
        /// </summary>
        /// <param name="input">Bitmap input</param>
        /// <param name="outputWidth">Larghezza output</param>
        /// <param name="outputHeight">Altezza output</param>
        /// <returns>Bitmap scalata</returns>
        private static VobSubBitmap ScaleBitmapByCoverage(VobSubBitmap input, int outputWidth, int outputHeight)
        {
            byte[] output = new byte[outputWidth * outputHeight];
            double[] weights = new double[4];
            double sourceX0;
            double sourceX1;
            double sourceY0;
            double sourceY1;

            // Fallback senza palette: usa comunque overlap reale, evitando il majority count a rettangoli interi
            for (int y = 0; y < outputHeight; y++)
            {
                sourceY0 = y * input.Height / (double)outputHeight;
                sourceY1 = (y + 1) * input.Height / (double)outputHeight;
                for (int x = 0; x < outputWidth; x++)
                {
                    sourceX0 = x * input.Width / (double)outputWidth;
                    sourceX1 = (x + 1) * input.Width / (double)outputWidth;
                    output[(y * outputWidth) + x] = SelectCoverageColor(input, sourceX0, sourceX1, sourceY0, sourceY1, weights);
                }
            }

            return new VobSubBitmap(outputWidth, outputHeight, output);
        }

        /// <summary>
        /// Costruisce i quattro colori SPU premoltiplicati
        /// </summary>
        /// <param name="info">Informazioni SPU parseate</param>
        /// <param name="palette">Palette IDX RGB</param>
        /// <returns>Colori per indice 2-bit</returns>
        private static VobSubResampleColor[] BuildVobSubResampleColors(VobSubSpuInfo info, int[] palette)
        {
            VobSubResampleColor[] result = new VobSubResampleColor[4];

            // Ogni valore bitmap 0..3 punta a un colore IDX e a un alpha/contrast 0..15
            for (int i = 0; i < result.Length; i++)
            {
                int rgb = palette[info.ColorIndexes[i]];
                int alpha = info.ContrastValues[i] * 17;
                result[i] = new VobSubResampleColor((rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff, alpha);
            }

            return result;
        }

        /// <summary>
        /// Calcola un pixel output palette-aware
        /// </summary>
        /// <param name="input">Bitmap input</param>
        /// <param name="colors">Colori premoltiplicati</param>
        /// <param name="sourceX0">X sorgente iniziale</param>
        /// <param name="sourceX1">X sorgente finale</param>
        /// <param name="sourceY0">Y sorgente iniziale</param>
        /// <param name="sourceY1">Y sorgente finale</param>
        /// <returns>Indice 2-bit output</returns>
        private static byte QuantizeVobSubArea(VobSubBitmap input, VobSubResampleColor[] colors, double sourceX0, double sourceX1, double sourceY0, double sourceY1)
        {
            int xStart = Math.Max(0, (int)Math.Floor(sourceX0));
            int xEnd = Math.Min(input.Width, (int)Math.Ceiling(sourceX1));
            int yStart = Math.Max(0, (int)Math.Floor(sourceY0));
            int yEnd = Math.Min(input.Height, (int)Math.Ceiling(sourceY1));
            double sumR = 0.0;
            double sumG = 0.0;
            double sumB = 0.0;
            double sumAlpha = 0.0;
            double totalWeight = 0.0;

            // Accumula RGBA premoltiplicato sui pixel sorgenti coperti
            for (int y = yStart; y < yEnd; y++)
            {
                double yWeight = Math.Min(sourceY1, y + 1.0) - Math.Max(sourceY0, y);
                if (yWeight <= 0.0)
                {
                    continue;
                }

                for (int x = xStart; x < xEnd; x++)
                {
                    double xWeight = Math.Min(sourceX1, x + 1.0) - Math.Max(sourceX0, x);
                    if (xWeight <= 0.0)
                    {
                        continue;
                    }

                    double weight = xWeight * yWeight;
                    VobSubResampleColor color = colors[input.Pixels[(y * input.Width) + x] & 0x03];
                    sumR += color.PremultipliedR * weight;
                    sumG += color.PremultipliedG * weight;
                    sumB += color.PremultipliedB * weight;
                    sumAlpha += color.Alpha * weight;
                    totalWeight += weight;
                }
            }

            if (totalWeight <= 0.0)
            {
                return 0;
            }

            return FindNearestVobSubIndex(colors, sumR / totalWeight, sumG / totalWeight, sumB / totalWeight, sumAlpha / totalWeight);
        }

        /// <summary>
        /// Seleziona un indice 2-bit in base alla copertura pesata
        /// </summary>
        /// <param name="input">Bitmap input</param>
        /// <param name="sourceX0">X sorgente iniziale</param>
        /// <param name="sourceX1">X sorgente finale</param>
        /// <param name="sourceY0">Y sorgente iniziale</param>
        /// <param name="sourceY1">Y sorgente finale</param>
        /// <param name="weights">Buffer pesi riutilizzato</param>
        /// <returns>Indice 2-bit output</returns>
        private static byte SelectCoverageColor(VobSubBitmap input, double sourceX0, double sourceX1, double sourceY0, double sourceY1, double[] weights)
        {
            int xStart = Math.Max(0, (int)Math.Floor(sourceX0));
            int xEnd = Math.Min(input.Width, (int)Math.Ceiling(sourceX1));
            int yStart = Math.Max(0, (int)Math.Floor(sourceY0));
            int yEnd = Math.Min(input.Height, (int)Math.Ceiling(sourceY1));
            int bestColor = 0;
            double bestWeight = -1.0;

            Array.Clear(weights, 0, weights.Length);
            for (int y = yStart; y < yEnd; y++)
            {
                double yWeight = Math.Min(sourceY1, y + 1.0) - Math.Max(sourceY0, y);
                if (yWeight <= 0.0)
                {
                    continue;
                }

                for (int x = xStart; x < xEnd; x++)
                {
                    double xWeight = Math.Min(sourceX1, x + 1.0) - Math.Max(sourceX0, x);
                    if (xWeight > 0.0)
                    {
                        weights[input.Pixels[(y * input.Width) + x] & 0x03] += xWeight * yWeight;
                    }
                }
            }

            // A parita' preferisce un indice non zero per non cancellare bordi sottili
            for (int i = 0; i < weights.Length; i++)
            {
                if (weights[i] > bestWeight || (Math.Abs(weights[i] - bestWeight) < 0.000001 && bestColor == 0 && i != 0))
                {
                    bestColor = i;
                    bestWeight = weights[i];
                }
            }

            return (byte)bestColor;
        }

        /// <summary>
        /// Trova l'indice SPU piu' vicino al colore target
        /// </summary>
        /// <param name="colors">Colori premoltiplicati</param>
        /// <param name="targetR">Rosso premoltiplicato target</param>
        /// <param name="targetG">Verde premoltiplicato target</param>
        /// <param name="targetB">Blu premoltiplicato target</param>
        /// <param name="targetAlpha">Alpha target</param>
        /// <returns>Indice 2-bit piu' vicino</returns>
        private static byte FindNearestVobSubIndex(VobSubResampleColor[] colors, double targetR, double targetG, double targetB, double targetAlpha)
        {
            int bestIndex = 0;
            double bestDistance = double.MaxValue;

            for (int i = 0; i < colors.Length; i++)
            {
                double dr = colors[i].PremultipliedR - targetR;
                double dg = colors[i].PremultipliedG - targetG;
                double db = colors[i].PremultipliedB - targetB;
                double da = colors[i].Alpha - targetAlpha;
                double distance = (dr * dr) + (dg * dg) + (db * db) + (da * da * 4.0);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            return (byte)bestIndex;
        }

        /// <summary>
        /// Ricodifica un field RLE
        /// </summary>
        /// <param name="bitmap">Bitmap da ricodificare</param>
        /// <param name="firstRow">Prima riga del field da ricodificare</param>
        /// <returns>Field RLE codificato</returns>
        private static byte[] EncodeFieldRle(VobSubBitmap bitmap, int firstRow)
        {
            NibbleWriter writer = new NibbleWriter();
            int runLength;
            byte color;
            byte nextColor;

            // Ricodifica solo le righe del field richiesto: top=pari, bottom=dispari
            for (int y = firstRow; y < bitmap.Height; y += 2)
            {
                int x = 0;
                while (x < bitmap.Width)
                {
                    color = bitmap.Pixels[(y * bitmap.Width) + x];
                    runLength = 1;

                    // Accorpa pixel consecutivi con lo stesso indice 2-bit
                    while (x + runLength < bitmap.Width && bitmap.Pixels[(y * bitmap.Width) + x + runLength] == color)
                    {
                        runLength++;
                    }

                    // L'ultimo run di riga puo' usare il codice fill-to-end del formato DVD SPU
                    while (runLength > 0)
                    {
                        nextColor = color;
                        if (runLength == bitmap.Width - x)
                        {
                            writer.WriteFill(nextColor);
                            x += runLength;
                            runLength = 0;
                        }
                        else
                        {
                            int chunk = Math.Min(runLength, 255);
                            writer.WriteRun(chunk, nextColor);
                            x += chunk;
                            runLength -= chunk;
                        }
                    }
                }

                // Le righe RLE dei field DVD sono byte-aligned
                writer.AlignByte();
            }

            return writer.ToArray();
        }

        #endregion

        #region Metodi privati - Binary helpers

        /// <summary>
        /// Legge quattro nibble command-table in ordine indice pixel 0..3
        /// </summary>
        /// <param name="data">Buffer command table</param>
        /// <param name="offset">Offset payload comando</param>
        /// <param name="values">Array destinazione da quattro valori</param>
        private static void ReadNibbleMap(byte[] data, int offset, int[] values)
        {
            int packed = ReadUInt16BigEndian(data, offset);

            // Il payload DVD SPU serializza i valori dal pixel 3 al pixel 0 nei quattro nibble
            values[3] = (packed >> 12) & 0x0f;
            values[2] = (packed >> 8) & 0x0f;
            values[1] = (packed >> 4) & 0x0f;
            values[0] = packed & 0x0f;
        }

        /// <summary>
        /// Legge SET_DAREA
        /// </summary>
        /// <param name="data">Buffer command table</param>
        /// <param name="offset">Offset payload SET_DAREA</param>
        /// <param name="x1">X iniziale letta</param>
        /// <param name="x2">X finale letta</param>
        /// <param name="y1">Y iniziale letta</param>
        /// <param name="y2">Y finale letta</param>
        private static void ReadDisplayArea(byte[] data, int offset, out int x1, out int x2, out int y1, out int y2)
        {
            x1 = (data[offset] << 4) | (data[offset + 1] >> 4);
            x2 = ((data[offset + 1] & 0x0f) << 8) | data[offset + 2];
            y1 = (data[offset + 3] << 4) | (data[offset + 4] >> 4);
            y2 = ((data[offset + 4] & 0x0f) << 8) | data[offset + 5];
        }

        /// <summary>
        /// Scrive SET_DAREA
        /// </summary>
        /// <param name="data">Buffer command table</param>
        /// <param name="offset">Offset payload SET_DAREA</param>
        /// <param name="x1">X iniziale da scrivere</param>
        /// <param name="x2">X finale da scrivere</param>
        /// <param name="y1">Y iniziale da scrivere</param>
        /// <param name="y2">Y finale da scrivere</param>
        private static void WriteDisplayArea(byte[] data, int offset, int x1, int x2, int y1, int y2)
        {
            data[offset] = (byte)((x1 >> 4) & 0xff);
            data[offset + 1] = (byte)(((x1 & 0x0f) << 4) | ((x2 >> 8) & 0x0f));
            data[offset + 2] = (byte)(x2 & 0xff);
            data[offset + 3] = (byte)((y1 >> 4) & 0xff);
            data[offset + 4] = (byte)(((y1 & 0x0f) << 4) | ((y2 >> 8) & 0x0f));
            data[offset + 5] = (byte)(y2 & 0xff);
        }

        /// <summary>
        /// Legge un uint16 big-endian
        /// </summary>
        /// <param name="data">Buffer sorgente</param>
        /// <param name="offset">Offset valore</param>
        /// <returns>Valore uint16 letto come int</returns>
        private static int ReadUInt16BigEndian(byte[] data, int offset)
        {
            return (data[offset] << 8) | data[offset + 1];
        }

        /// <summary>
        /// Scrive un uint16 big-endian
        /// </summary>
        /// <param name="data">Buffer destinazione</param>
        /// <param name="offset">Offset valore</param>
        /// <param name="value">Valore da scrivere</param>
        private static void WriteUInt16BigEndian(byte[] data, int offset, int value)
        {
            data[offset] = (byte)((value >> 8) & 0xff);
            data[offset + 1] = (byte)(value & 0xff);
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Informazioni SPU parse
        /// </summary>
        private sealed class VobSubSpuInfo
        {
            /// <summary>
            /// Costruttore
            /// </summary>
            public VobSubSpuInfo()
            {
                this.SequenceOffsets = new List<int>();
                this.DisplayAreaCommandOffsets = new List<int>();
                this.PixelOffsetCommandOffsets = new List<int>();
                this.ColorIndexes = new int[4];
                this.ContrastValues = new int[4];
            }

            /// <summary>
            /// Offset SPU nel blocco input
            /// </summary>
            public int SpuOffset { get; set; }

            /// <summary>
            /// Dimensione SPU dichiarata
            /// </summary>
            public int SpuSize { get; set; }

            /// <summary>
            /// Offset command table relativo alla SPU
            /// </summary>
            public int CommandOffset { get; set; }

            /// <summary>
            /// True se la command table contiene SET_DAREA
            /// </summary>
            public bool HasDisplayArea { get; set; }

            /// <summary>
            /// True se la command table contiene SET_DSPXA
            /// </summary>
            public bool HasPixelOffsets { get; set; }

            /// <summary>
            /// True se la command table contiene SET_COLOR
            /// </summary>
            public bool HasColorIndexes { get; set; }

            /// <summary>
            /// True se la command table contiene SET_CONTR
            /// </summary>
            public bool HasContrastValues { get; set; }

            /// <summary>
            /// Coordinata sinistra display area
            /// </summary>
            public int X { get; set; }

            /// <summary>
            /// Coordinata destra display area
            /// </summary>
            public int X2 { get; set; }

            /// <summary>
            /// Coordinata superiore display area
            /// </summary>
            public int Y { get; set; }

            /// <summary>
            /// Coordinata inferiore display area
            /// </summary>
            public int Y2 { get; set; }

            /// <summary>
            /// Larghezza display area
            /// </summary>
            public int Width { get { return this.X2 - this.X + 1; } }

            /// <summary>
            /// Altezza display area
            /// </summary>
            public int Height { get { return this.Y2 - this.Y + 1; } }

            /// <summary>
            /// Offset RLE campo pari/superiore
            /// </summary>
            public int TopFieldOffset { get; set; }

            /// <summary>
            /// Offset RLE campo dispari/inferiore
            /// </summary>
            public int BottomFieldOffset { get; set; }

            /// <summary>
            /// Indici colore palette per pixel value 0..3
            /// </summary>
            public int[] ColorIndexes { get; private set; }

            /// <summary>
            /// Alpha/contrast per pixel value 0..3
            /// </summary>
            public int[] ContrastValues { get; private set; }

            /// <summary>
            /// Offset sequenze command table
            /// </summary>
            public List<int> SequenceOffsets { get; private set; }

            /// <summary>
            /// Offset comandi SET_DAREA da riscrivere
            /// </summary>
            public List<int> DisplayAreaCommandOffsets { get; private set; }

            /// <summary>
            /// Offset comandi SET_DSPXA da riscrivere
            /// </summary>
            public List<int> PixelOffsetCommandOffsets { get; private set; }
        }

        /// <summary>
        /// Bitmap VobSub decodificata
        /// </summary>
        /// <param name="Width">Larghezza bitmap</param>
        /// <param name="Height">Altezza bitmap</param>
        /// <param name="Pixels">Pixel palette-indexed in ordine row-major</param>
        private sealed record VobSubBitmap(int Width, int Height, byte[] Pixels);

        /// <summary>
        /// Colore VobSub premoltiplicato per resampling bitmap
        /// </summary>
        private readonly struct VobSubResampleColor
        {
            /// <summary>
            /// Crea il colore premoltiplicato
            /// </summary>
            /// <param name="red">Rosso</param>
            /// <param name="green">Verde</param>
            /// <param name="blue">Blu</param>
            /// <param name="alpha">Opacita'</param>
            public VobSubResampleColor(double red, double green, double blue, double alpha)
            {
                double alphaFactor = alpha / 255.0;
                this.PremultipliedR = red * alphaFactor;
                this.PremultipliedG = green * alphaFactor;
                this.PremultipliedB = blue * alphaFactor;
                this.Alpha = alpha;
            }

            /// <summary>
            /// Rosso premoltiplicato
            /// </summary>
            public double PremultipliedR { get; }

            /// <summary>
            /// Verde premoltiplicato
            /// </summary>
            public double PremultipliedG { get; }

            /// <summary>
            /// Blu premoltiplicato
            /// </summary>
            public double PremultipliedB { get; }

            /// <summary>
            /// Alpha non premoltiplicata
            /// </summary>
            public double Alpha { get; }
        }

        /// <summary>
        /// Lettore nibble per RLE SPU
        /// </summary>
        private sealed class NibbleReader
        {
            /// <summary>
            /// Dati SPU sorgente
            /// </summary>
            private readonly byte[] _data;

            /// <summary>
            /// Posizione nibble finale esclusiva
            /// </summary>
            private readonly int _endNibble;

            /// <summary>
            /// Posizione nibble corrente
            /// </summary>
            private int _nibble;

            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="data">Dati SPU sorgente</param>
            /// <param name="offset">Offset byte iniziale</param>
            /// <param name="end">Offset byte finale esclusivo</param>
            public NibbleReader(byte[] data, int offset, int end)
            {
                this._data = data;
                this._nibble = offset * 2;
                this._endNibble = end * 2;
            }

            /// <summary>
            /// Legge un run RLE VobSub
            /// </summary>
            /// <param name="remainingWidth">Pixel residui sulla riga</param>
            /// <param name="run">Lunghezza run letta</param>
            /// <param name="color">Indice palette letto</param>
            /// <returns>True se il run è valido</returns>
            public bool TryReadRun(int remainingWidth, out int run, out int color)
            {
                int value = 0;
                int threshold;
                int nibble;

                color = 0;
                run = 0;

                // Il codice SPU usa prefissi a nibble progressivi: 1, 2, 3 o 4 nibble a seconda del valore letto
                for (threshold = 1; value < threshold && threshold <= 0x40; threshold <<= 2)
                {
                    if (!this.TryReadNibble(out nibble))
                    {
                        return false;
                    }

                    value = (value << 4) | nibble;
                }

                color = value & 0x03;
                run = value < 4 ? remainingWidth : value >> 2;
                return run > 0 && run <= remainingWidth;
            }

            /// <summary>
            /// Allinea la lettura al byte successivo
            /// </summary>
            public void AlignByte()
            {
                if ((this._nibble & 1) != 0)
                {
                    this._nibble++;
                }
            }

            /// <summary>
            /// Legge un nibble dal flusso RLE
            /// </summary>
            /// <param name="value">Nibble letto</param>
            /// <returns>True se il nibble era disponibile</returns>
            private bool TryReadNibble(out int value)
            {
                int byteIndex;

                value = 0;
                if (this._nibble >= this._endNibble)
                {
                    return false;
                }

                byteIndex = this._nibble / 2;
                value = (this._nibble & 1) == 0 ? (this._data[byteIndex] >> 4) & 0x0f : this._data[byteIndex] & 0x0f;
                this._nibble++;
                return true;
            }
        }

        /// <summary>
        /// Writer nibble per RLE SPU
        /// </summary>
        private sealed class NibbleWriter
        {
            /// <summary>
            /// Byte prodotti dal writer
            /// </summary>
            private readonly List<byte> _bytes = new List<byte>();

            /// <summary>
            /// True se il prossimo nibble va scritto nella parte alta del byte
            /// </summary>
            private bool _highNibble = true;

            /// <summary>
            /// Scrive un run RLE
            /// </summary>
            /// <param name="run">Lunghezza run</param>
            /// <param name="color">Indice palette</param>
            public void WriteRun(int run, int color)
            {
                int value = (run << 2) | (color & 0x03);

                // Sceglie la codifica piu' corta capace di rappresentare la lunghezza del run
                if (run <= 3)
                {
                    this.WriteCode(value, 1);
                }
                else if (run <= 15)
                {
                    this.WriteCode(value, 2);
                }
                else if (run <= 63)
                {
                    this.WriteCode(value, 3);
                }
                else
                {
                    this.WriteCode(value, 4);
                }
            }

            /// <summary>
            /// Scrive un run di riempimento fino a fine riga
            /// </summary>
            /// <param name="color">Indice palette</param>
            public void WriteFill(int color)
            {
                this.WriteCode(color & 0x03, 4);
            }

            /// <summary>
            /// Allinea la scrittura al byte successivo
            /// </summary>
            public void AlignByte()
            {
                if (!this._highNibble)
                {
                    this.WriteNibble(0);
                }
            }

            /// <summary>
            /// Restituisce i byte RLE prodotti
            /// </summary>
            /// <returns>Array RLE allineato a byte</returns>
            public byte[] ToArray()
            {
                this.AlignByte();
                return this._bytes.ToArray();
            }

            /// <summary>
            /// Scrive un codice RLE usando un numero fissato di nibble
            /// </summary>
            /// <param name="value">Valore da serializzare</param>
            /// <param name="nibbleCount">Numero nibble da scrivere</param>
            private void WriteCode(int value, int nibbleCount)
            {
                for (int shift = (nibbleCount - 1) * 4; shift >= 0; shift -= 4)
                {
                    this.WriteNibble((value >> shift) & 0x0f);
                }
            }

            /// <summary>
            /// Scrive un singolo nibble
            /// </summary>
            /// <param name="value">Valore nibble</param>
            private void WriteNibble(int value)
            {
                if (this._highNibble)
                {
                    this._bytes.Add((byte)((value & 0x0f) << 4));
                }
                else
                {
                    this._bytes[this._bytes.Count - 1] |= (byte)(value & 0x0f);
                }

                this._highNibble = !this._highNibble;
            }
        }

        #endregion
    }
}
