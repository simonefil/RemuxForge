using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace RemuxForge.Core.Infrastructure
{
    /// <summary>
    /// Risultato di un'esecuzione processo
    /// </summary>
    public class ProcessResult
    {
        /// <summary>
        /// Codice di uscita del processo (-1 se errore o timeout)
        /// </summary>
        public int ExitCode;

        /// <summary>
        /// Output standard del processo
        /// </summary>
        public string Stdout;

        /// <summary>
        /// Output errore del processo
        /// </summary>
        public string Stderr;

        /// <summary>
        /// Costruttore
        /// </summary>
        public ProcessResult()
        {
            this.ExitCode = -1;
            this.Stdout = "";
            this.Stderr = "";
        }
    }

    /// <summary>
    /// Risultato di un'esecuzione processo con stdout binario
    /// </summary>
    public class ProcessBinaryResult
    {
        /// <summary>
        /// Codice di uscita del processo (-1 se errore o timeout)
        /// </summary>
        public int ExitCode;

        /// <summary>
        /// Output errore del processo
        /// </summary>
        public string Stderr;

        /// <summary>
        /// Costruttore
        /// </summary>
        public ProcessBinaryResult()
        {
            this.ExitCode = -1;
            this.Stderr = "";
        }
    }

    /// <summary>
    /// Esecuzione centralizzata di processi esterni con lettura parallela stdout/stderr
    /// </summary>
    public static class ProcessRunner
    {
        #region Variabili statiche

        /// <summary>
        /// Callback opzionale per richiesta stop cooperativa
        /// </summary>
        private static Func<bool> s_stopRequestedCallback;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Imposta callback globale per richiesta stop cooperativa
        /// </summary>
        /// <param name="callback">Callback stop o null</param>
        public static void SetStopRequestedCallback(Func<bool> callback)
        {
            s_stopRequestedCallback = callback;
        }

        /// <summary>
        /// Esegue un processo e cattura stdout e stderr come testo
        /// </summary>
        /// <param name="fileName">Percorso dell'eseguibile</param>
        /// <param name="arguments">Argomenti del processo</param>
        /// <param name="timeoutMs">Timeout in millisecondi, 0 = nessun timeout</param>
        /// <returns>Risultato con exit code, stdout e stderr</returns>
        public static ProcessResult Run(string fileName, string[] arguments, int timeoutMs = 0)
        {
            ProcessResult result = new ProcessResult();
            Process proc = null;
            string stdout = "";
            string stderr = "";
            StreamReader stdoutReader;
            StreamReader stderrReader;
            try
            {
                proc = new Process();
                SetupStartInfo(proc, fileName);

                // Argomenti via ArgumentList per encoding corretto su Linux (UTF-8)
                for (int i = 0; i < arguments.Length; i++)
                {
                    proc.StartInfo.ArgumentList.Add(arguments[i]);
                }

                proc.Start();

                stdoutReader = proc.StandardOutput;
                stderrReader = proc.StandardError;

                // Legge stdout e stderr in parallelo per prevenire deadlock
                // Il timeout deve governare il processo, non le letture bloccanti delle pipe
                Thread stdoutThread = new Thread(() => { stdout = stdoutReader.ReadToEnd(); });
                Thread stderrThread = new Thread(() => { stderr = stderrReader.ReadToEnd(); });
                stdoutThread.Start();
                stderrThread.Start();

                if (!WaitForExitOrStop(proc, timeoutMs))
                {
                    KillProcessTree(proc);
                    stdoutThread.Join(5000);
                    stderrThread.Join(5000);
                    result.ExitCode = -1;
                    result.Stdout = stdout;
                    result.Stderr = stderr;
                    return result;
                }

                stdoutThread.Join(5000);
                stderrThread.Join(5000);

                result.ExitCode = proc.ExitCode;
                result.Stdout = stdout;
                result.Stderr = stderr;
            }
            catch (Exception ex)
            {
                result.Stderr = "Eccezione durante l'esecuzione di " + fileName + ": " + ex.Message;
            }
            finally
            {
                if (proc != null) { proc.Dispose(); }
            }

            return result;
        }

        /// <summary>
        /// Esegue un processo leggendo stderr riga per riga per il progresso in tempo reale
        /// </summary>
        /// <param name="fileName">Percorso dell'eseguibile</param>
        /// <param name="arguments">Lista argomenti</param>
        /// <param name="onStderrLine">Callback invocato per ogni riga di stderr</param>
        /// <returns>Codice di uscita del processo</returns>
        public static int RunWithProgress(string fileName, List<string> arguments, Action<string> onStderrLine)
        {
            int exitCode = -1;
            Process proc = null;
            Thread stdoutThread;
            Thread stderrThread;
            StreamReader stdoutReader;
            StreamReader stderrReader;
            try
            {
                proc = new Process();
                SetupStartInfo(proc, fileName);

                // Argomenti via ArgumentList
                for (int i = 0; i < arguments.Count; i++)
                {
                    proc.StartInfo.ArgumentList.Add(arguments[i]);
                }

                proc.Start();
                stdoutReader = proc.StandardOutput;
                stderrReader = proc.StandardError;

                // Leggi stdout in thread separato per evitare deadlock
                // Pipe chiuse/disposte sono attese se il processo termina durante lo stop
                stdoutThread = new Thread(() =>
                {
                    try
                    {
                        stdoutReader.ReadToEnd();
                    }
                    catch (IOException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });
                stdoutThread.Start();

                stderrThread = new Thread(() =>
                {
                    try
                    {
                        string line;
                        while ((line = stderrReader.ReadLine()) != null)
                        {
                            if (onStderrLine != null && !string.IsNullOrEmpty(line))
                            {
                                onStderrLine(line);
                            }
                        }
                    }
                    catch (IOException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });
                stderrThread.Start();

                if (!WaitForExitOrStop(proc, 0))
                {
                    KillProcessTree(proc);
                }

                stdoutThread.Join(5000);
                stderrThread.Join(5000);
                if (proc.HasExited) { exitCode = proc.ExitCode; }
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.General, LogLevel.Warning, "Errore esecuzione " + fileName + ": " + ex.Message);
            }
            finally
            {
                if (proc != null) { proc.Dispose(); }
            }

            return exitCode;
        }

        /// <summary>
        /// Esegue un processo leggendo stdout riga per riga e accumulando stderr
        /// </summary>
        /// <param name="fileName">Percorso dell'eseguibile</param>
        /// <param name="arguments">Lista argomenti</param>
        /// <param name="onStdoutLine">Callback invocato per ogni riga di stdout</param>
        /// <param name="timeoutMs">Timeout in millisecondi, 0 = nessun timeout</param>
        /// <returns>Risultato con exit code e stderr</returns>
        public static ProcessResult RunWithStdoutLines(string fileName, IEnumerable<string> arguments, Action<string> onStdoutLine, int timeoutMs = 0)
        {
            ProcessResult result = new ProcessResult();
            Process proc = null;
            Thread stdoutThread;
            Thread stderrThread;
            StringBuilder stderr = new StringBuilder();
            StreamReader stdoutReader;
            StreamReader stderrReader;
            try
            {
                proc = new Process();
                SetupStartInfo(proc, fileName);

                foreach (string arg in arguments)
                {
                    proc.StartInfo.ArgumentList.Add(arg);
                }

                proc.Start();
                stdoutReader = proc.StandardOutput;
                stderrReader = proc.StandardError;

                stdoutThread = new Thread(() =>
                {
                    try
                    {
                        string line;
                        while ((line = stdoutReader.ReadLine()) != null)
                        {
                            if (onStdoutLine != null)
                            {
                                onStdoutLine(line);
                            }
                        }
                    }
                    catch (IOException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });

                stderrThread = new Thread(() =>
                {
                    try
                    {
                        stderr.Append(stderrReader.ReadToEnd());
                    }
                    catch (IOException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });

                stdoutThread.Start();
                stderrThread.Start();

                if (!WaitForExitOrStop(proc, timeoutMs))
                {
                    KillProcessTree(proc);
                }

                stdoutThread.Join(5000);
                stderrThread.Join(5000);

                if (proc.HasExited)
                {
                    result.ExitCode = proc.ExitCode;
                }
                result.Stderr = stderr.ToString();
            }
            catch (Exception ex)
            {
                result.Stderr = "Eccezione durante l'esecuzione di " + fileName + ": " + ex.Message;
            }
            finally
            {
                if (proc != null) { proc.Dispose(); }
            }

            return result;
        }

        /// <summary>
        /// Esegue un processo scartando l'output, con supporto timeout e kill
        /// </summary>
        /// <param name="fileName">Percorso dell'eseguibile</param>
        /// <param name="arguments">Argomenti del processo</param>
        /// <param name="timeoutMs">Timeout in millisecondi, 0 = nessun timeout</param>
        /// <returns>Codice di uscita del processo, -1 se timeout o errore</returns>
        public static int RunDiscardOutput(string fileName, string[] arguments, int timeoutMs = 0)
        {
            int exitCode = -1;
            Process proc = null;
            StreamReader stdoutReader;
            StreamReader stderrReader;
            try
            {
                proc = new Process();
                SetupStartInfo(proc, fileName);

                // Argomenti via ArgumentList
                for (int i = 0; i < arguments.Length; i++)
                {
                    proc.StartInfo.ArgumentList.Add(arguments[i]);
                }

                proc.Start();
                stdoutReader = proc.StandardOutput;
                stderrReader = proc.StandardError;

                // Svuota stdout e stderr in thread separati per prevenire deadlock
                // Pipe chiuse/disposte sono attese se il processo termina durante lo stop
                Thread stdoutThread = new Thread(() =>
                {
                    try
                    {
                        stdoutReader.ReadToEnd();
                    }
                    catch (IOException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });
                Thread stderrThread = new Thread(() =>
                {
                    try
                    {
                        stderrReader.ReadToEnd();
                    }
                    catch (IOException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });
                stdoutThread.Start();
                stderrThread.Start();

                if (WaitForExitOrStop(proc, timeoutMs))
                {
                    exitCode = proc.ExitCode;
                }
                else
                {
                    KillProcessTree(proc);
                }

                // Attendi thread con timeout per evitare hang
                stdoutThread.Join(5000);
                stderrThread.Join(5000);
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.General, LogLevel.Warning, "Errore esecuzione " + fileName + ": " + ex.Message);
            }
            finally
            {
                if (proc != null) { proc.Dispose(); }
            }

            return exitCode;
        }

        /// <summary>
        /// Esegue un processo leggendo stdout binario a blocchi e stderr come testo
        /// </summary>
        /// <param name="fileName">Percorso dell'eseguibile</param>
        /// <param name="arguments">Argomenti del processo</param>
        /// <param name="onStdoutBytes">Callback invocato per ogni blocco stdout letto</param>
        /// <param name="timeoutMs">Timeout in millisecondi, 0 = nessun timeout</param>
        /// <returns>Risultato con exit code e stderr</returns>
        public static ProcessBinaryResult RunBinaryStdout(string fileName, string[] arguments, Action<byte[], int> onStdoutBytes, int timeoutMs = 0)
        {
            ProcessBinaryResult result = new ProcessBinaryResult();
            Process proc = null;
            Thread stdoutThread;
            Thread stderrThread;
            StringBuilder stderr = new StringBuilder();
            Exception stdoutException = null;
            Stream stdoutStream;
            StreamReader stderrReader;
            bool completed;
            try
            {
                proc = new Process();
                SetupStartInfo(proc, fileName);

                for (int i = 0; i < arguments.Length; i++)
                {
                    proc.StartInfo.ArgumentList.Add(arguments[i]);
                }

                proc.Start();
                stdoutStream = proc.StandardOutput.BaseStream;
                stderrReader = proc.StandardError;

                stdoutThread = new Thread(() =>
                {
                    byte[] buffer = new byte[32768];
                    int bytesRead;
                    try
                    {
                        while ((bytesRead = stdoutStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (onStdoutBytes != null)
                            {
                                onStdoutBytes(buffer, bytesRead);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        stdoutException = ex;
                    }
                });

                stderrThread = new Thread(() =>
                {
                    try
                    {
                        stderr.Append(stderrReader.ReadToEnd());
                    }
                    catch (IOException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });

                stdoutThread.Start();
                stderrThread.Start();

                completed = WaitForExitOrStop(proc, timeoutMs);
                if (!completed)
                {
                    KillProcessTree(proc);
                }

                stdoutThread.Join(5000);
                stderrThread.Join(5000);

                if (proc.HasExited)
                {
                    result.ExitCode = proc.ExitCode;
                }

                result.Stderr = stderr.ToString();
                if (!completed)
                {
                    if (!string.IsNullOrEmpty(result.Stderr))
                    {
                        result.Stderr = result.Stderr + Environment.NewLine;
                    }

                    result.Stderr = result.Stderr + "Processo interrotto per timeout o richiesta stop";
                    result.ExitCode = -1;
                }
                if (stdoutException != null)
                {
                    result.ExitCode = -1;
                    result.Stderr = result.Stderr + "Errore lettura stdout binario: " + stdoutException.Message;
                }
            }
            catch (Exception ex)
            {
                result.Stderr = "Eccezione durante l'esecuzione di " + fileName + ": " + ex.Message;
            }
            finally
            {
                if (proc != null) { proc.Dispose(); }
            }

            return result;
        }

        /// <summary>
        /// Splitta argomenti composti contenenti spazi in token singoli,
        /// preservando path con separatori directory (/ o \)
        /// </summary>
        /// <param name="args">Array di argomenti potenzialmente composti</param>
        /// <returns>Array con argomenti individuali</returns>
        public static string[] SplitCompoundArgs(string[] args)
        {
            List<string> result = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Contains(" ") && !args[i].Contains("/") && !args[i].Contains("\\"))
                {
                    // Argomento composto - splitta in token singoli
                    string[] subArgs = args[i].Split(' ');
                    for (int j = 0; j < subArgs.Length; j++)
                    {
                        string trimmed = subArgs[j].Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                        {
                            result.Add(trimmed);
                        }
                    }
                }
                else
                {
                    result.Add(args[i]);
                }
            }

            return result.ToArray();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Configura le impostazioni comuni di avvio processo
        /// </summary>
        /// <param name="proc">Processo da configurare</param>
        /// <param name="fileName">Percorso dell'eseguibile</param>
        private static void SetupStartInfo(Process proc, string fileName)
        {
            proc.StartInfo.FileName = fileName;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.RedirectStandardError = true;
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            proc.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        }

        /// <summary>
        /// Attende terminazione, timeout o richiesta stop
        /// </summary>
        private static bool WaitForExitOrStop(Process proc, int timeoutMs)
        {
            DateTime start = DateTime.UtcNow;

            while (!proc.HasExited)
            {
                if (IsStopRequested())
                {
                    return false;
                }

                if (timeoutMs > 0 && (DateTime.UtcNow - start).TotalMilliseconds >= timeoutMs)
                {
                    return false;
                }

                proc.WaitForExit(200);
            }

            return true;
        }

        /// <summary>
        /// True se lo stop cooperativo è richiesto
        /// </summary>
        private static bool IsStopRequested()
        {
            return s_stopRequestedCallback != null && s_stopRequestedCallback();
        }

        /// <summary>
        /// Termina un processo esterno e i figli quando supportato
        /// </summary>
        private static void KillProcessTree(Process proc)
        {
            try
            {
                proc.Kill(true);
            }
            catch (Exception ex)
            {
                try
                {
                    proc.Kill();
                }
                catch (Exception fallbackEx)
                {
                    ConsoleHelper.Write(LogSection.General, LogLevel.Debug, "Kill processo fallita: " + ex.Message + " / " + fallbackEx.Message);
                }
            }
        }

        #endregion
    }
}
